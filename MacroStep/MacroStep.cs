using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        private static string DirectoryPath=>Path.GetDirectoryName(typeof(Loader).Assembly.Location);
        public static void Install(){Invoke("Install");}
        public static void Remove(){Invoke("Remove");}
        public static void AutoInstall(){Install();}
        private static void Invoke(string name)
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
            try{typeof(MacroStep).GetMethod(name).Invoke(null,null);File.Delete(Path.Combine(DirectoryPath,"macrostep-error.txt"));}
            catch(Exception e){File.WriteAllText(Path.Combine(DirectoryPath,"macrostep-error.txt"),e.ToString());throw;}
        }
    }
    // Above 100x, advances k fixed 0.02 s steps per IdleSpiralSystem.Advance call (k = ceil(factor/100)),
    // keeping ~5000 Advance calls per real second. Per-call game time stays within the game's own
    // Offline Boost size (20 x 0.02 s), so boost mode itself falls back to k = 1.
    public static class MacroStep
    {
        private const string Owner="local.idlespiral.openspeedy.macrostep";
        private const string RuntimeHash="dbfeebdc66091f27f63320971cd3f369b3fd66928fc6a66af336425e44df635f";
        private const double StepSeconds=0.02;
        private const double MaximumCallSeconds=0.4;
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly string DirectoryPath=Path.GetDirectoryName(typeof(MacroStep).Assembly.Location);
        private const double BacklogCapSeconds=40.0;
        private static MethodInfo runFrame,simulationFrame,stepClosure,admitElapsed;
        private static Func<double> readFactor;
        private static bool installed;
        private static int chunk=1,current=1,frameCalls,frameSteps;
        private static long frames,calls,steps;
        private static double lastFactor;

        public static void Install()
        {
            if(installed)return;
            var runtime=Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.SpeedPatch.SteamGuard.dll"));
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(runtime.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=RuntimeHash)
                    throw new InvalidOperationException("Unsupported simulation runtime.");
            var patch=runtime.GetType("IdleSpiralSpeedPatch.Patch");
            runFrame=AccessTools.Method(patch,"RunFrame");
            simulationFrame=AccessTools.Method(patch,"RunSimulationFrame");
            admitElapsed=AccessTools.Method(patch,"AdmitElapsed");
            readFactor=(Func<double>)Delegate.CreateDelegate(typeof(Func<double>),AccessTools.Method(patch,"ReadFactor"));
            var advance=AccessTools.Method(typeof(IdleSpiralSystem),"Advance");
            stepClosure=patch.GetNestedTypes(BindingFlags.NonPublic|BindingFlags.Public)
                .SelectMany(t=>t.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly))
                .Single(m=>PatchProcessor.GetOriginalInstructions(m).Any(i=>i.Calls(advance)));
            var drain=AccessTools.Method(typeof(FixedStepSimulationClock),"Drain");
            var step=PatchProcessor.GetOriginalInstructions(stepClosure);
            var frame=PatchProcessor.GetOriginalInstructions(simulationFrame);
            if(PatchProcessor.GetOriginalInstructions(runFrame).Count(i=>i.Calls(drain))!=1 ||
                step.Count(IsStepSingle)!=2 || step.Count(IsStepDouble)!=1 ||
                frame.Count(IsStepSingle)!=1 || frame.Count(IsStepDouble)!=1 ||
                PatchProcessor.GetOriginalInstructions(admitElapsed).Count(IsBacklogCap)!=1)
                throw new InvalidOperationException("Unexpected simulation step structure.");
            try
            {
                Harmony.Patch(runFrame,transpiler:new HarmonyMethod(typeof(MacroStep),nameof(RewriteDrain)));
                Harmony.Patch(stepClosure,transpiler:new HarmonyMethod(typeof(MacroStep),nameof(RewriteStep)));
                Harmony.Patch(simulationFrame,prefix:new HarmonyMethod(typeof(MacroStep),nameof(BeginFrame)),
                    transpiler:new HarmonyMethod(typeof(MacroStep),nameof(RewriteFrame)));
                Harmony.Patch(admitElapsed,transpiler:new HarmonyMethod(typeof(MacroStep),nameof(RewriteBacklogCap)));
                installed=true;Status("installed");
            }
            catch{Harmony.UnpatchAll(Owner);throw;}
        }
        public static void Remove()
        {
            Harmony.UnpatchAll(Owner);
            installed=false;chunk=current=1;Status("removed");
        }
        private static bool IsStepSingle(CodeInstruction i)=>i.opcode==OpCodes.Ldc_R4&&(float)i.operand==(float)StepSeconds;
        private static bool IsStepDouble(CodeInstruction i)=>i.opcode==OpCodes.Ldc_R8&&(double)i.operand==StepSeconds;
        private static bool IsBacklogCap(CodeInstruction i)=>i.opcode==OpCodes.Ldc_R8&&(double)i.operand==BacklogCapSeconds;
        // The runtime bounds admitted debt at 40 game seconds (0.4 real seconds at 100x); scale it with k so the real-time window stays the same.
        private static IEnumerable<CodeInstruction> RewriteBacklogCap(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if(IsBacklogCap(instruction))yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(MacroStep),nameof(BacklogCap))).MoveLabelsFrom(instruction);
                else yield return instruction;
            }
        }
        private static double BacklogCap()=>BacklogCapSeconds*chunk;
        private static IEnumerable<CodeInstruction> RewriteDrain(IEnumerable<CodeInstruction> instructions)
        {
            var drain=AccessTools.Method(typeof(FixedStepSimulationClock),"Drain");
            foreach(var instruction in instructions)
            {
                if(instruction.Calls(drain)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(MacroStep),nameof(Drain));}
                yield return instruction;
            }
        }
        // Inside one step: TickManager.Scale(0.02f), AdmitGameDelta(.., 0.02) and Advance(.., 0.02f) all cover the drained span.
        private static IEnumerable<CodeInstruction> RewriteStep(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if(IsStepSingle(instruction))yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(MacroStep),nameof(SpanSingle))).MoveLabelsFrom(instruction);
                else if(IsStepDouble(instruction))yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(MacroStep),nameof(SpanDouble))).MoveLabelsFrom(instruction);
                else yield return instruction;
            }
        }
        // RunSimulationFrame reports steps * 0.02 as the frame's base delta (raid battles use it); report drained steps instead.
        private static IEnumerable<CodeInstruction> RewriteFrame(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if(IsStepSingle(instruction))yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(MacroStep),nameof(FrameSpanSingle))).MoveLabelsFrom(instruction);
                else if(IsStepDouble(instruction))yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(MacroStep),nameof(FrameSpanDouble))).MoveLabelsFrom(instruction);
                else yield return instruction;
            }
        }
        private static void BeginFrame()
        {
            frameCalls=frameSteps=0;
            double factor=readFactor();
            int wanted=factor>100?(int)Math.Ceiling(factor/100):1;
            if(wanted>1)
            {
                double perStep=TickManager.Scale((float)StepSeconds);
                int cap=perStep>0?(int)Math.Floor(MaximumCallSeconds/perStep+1e-6):1;
                wanted=Math.Max(1,Math.Min(wanted,cap));
            }
            chunk=wanted;lastFactor=factor;
            if((++frames&255)==1)Status("running");
        }
        private static int Drain(FixedStepSimulationClock clock,int maxSteps)
        {
            int drained=clock.Drain(chunk);
            current=Math.Max(1,drained);
            if(drained>0){frameCalls++;frameSteps+=drained;calls++;steps+=drained;}
            return drained;
        }
        private static float SpanSingle()=>(float)(current*StepSeconds);
        private static double SpanDouble()=>current*StepSeconds;
        private static double Ratio()=>frameCalls==0?1:(double)frameSteps/frameCalls;
        private static float FrameSpanSingle()=>(float)(StepSeconds*Ratio());
        private static double FrameSpanDouble()=>StepSeconds*Ratio();
        private static void Status(string state)
        {
            try
            {
                File.WriteAllText(Path.Combine(DirectoryPath,"macrostep-status.txt"),string.Format(CultureInfo.InvariantCulture,
                    "state={0}\nfactor={1:R}\nchunk={2}\nframes={3}\nadvanceCalls={4}\nfixedSteps={5}\nmeanStepsPerCall={6:R}\nmaximumSecondsPerCall={7:R}\n",
                    state,lastFactor,chunk,frames,calls,steps,calls==0?0:(double)steps/calls,MaximumCallSeconds));
            }
            catch{}
        }
    }
}
