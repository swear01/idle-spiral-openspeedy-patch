using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        private static string DirectoryPath => Path.GetDirectoryName(typeof(Loader).Assembly.Location);
        public static void Install() { Invoke("Install"); }
        public static void Remove() { Invoke("Remove"); }
        public static void Test() { Invoke("Test"); }
        public static void AutoInstall()
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.AuthGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader")
                .GetMethod("AutoInstall").Invoke(null,null);
            Install();
        }
        private static void Invoke(string method)
        {
            try
            {
                Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
                Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.Adaptive").GetMethod(method).Invoke(null,null);
            }
            catch(Exception error)
            {
                File.WriteAllText(Path.Combine(DirectoryPath,"adaptive-error.txt"),error.ToString());
                throw;
            }
        }
    }

    public static class Adaptive
    {
        private const string Owner="local.idlespiral.openspeedy.adaptive";
        private const string RuntimeHash="dbfeebdc66091f27f63320971cd3f369b3fd66928fc6a66af336425e44df635f";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly string DirectoryPath=Path.GetDirectoryName(typeof(Adaptive).Assembly.Location);
        private static readonly Budget Controller=new Budget();
        private static MethodInfo RunFrame, SimulationFrame;
        private static Func<double> ReadFactor;
        private static bool installed;
        private static int currentBudget=80000;
        private static ulong lastStart,lastDuration,firstStart;
        private static double outsideTicks,simulatedSeconds,gameSeconds,lastMilliseconds,maxMilliseconds;
        private static long frames;
        [DllImport("kernelbase.dll",ExactSpelling=true)] private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var time);return time;}

        private sealed class Budget
        {
            private int requested=80000;
            public int Choose(double factor,double backlog,double outside)
            {
                if(factor<=1){requested=80000;return requested;}
                if(backlog>Math.Max(0.08,factor*0.08)) requested=Math.Min(160000,requested+2500);
                else if(backlog<factor*0.02) requested=Math.Max(80000,requested-1250);
                int headroom=(int)Math.Max(80000,Math.Min(160000,400000-outside));
                return Math.Min(requested,headroom);
            }
        }

        public static int CurrentBudgetTicks(){return currentBudget;}
        public static void Install()
        {
            if(installed)return;
            var runtime=Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.SpeedPatch.SteamGuard.dll"));
            using(var sha=SHA256.Create())
            using(var stream=File.OpenRead(runtime.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=RuntimeHash)
                    throw new InvalidOperationException("Unsupported bounded simulation runtime.");
            var type=runtime.GetType("IdleSpiralSpeedPatch.Patch");
            RunFrame=AccessTools.Method(type,"RunFrame");
            SimulationFrame=AccessTools.Method(type,"RunSimulationFrame");
            ReadFactor=(Func<double>)Delegate.CreateDelegate(typeof(Func<double>),AccessTools.Method(type,"ReadFactor"));
            if(RunFrame==null || SimulationFrame==null || PatchProcessor.GetOriginalInstructions(RunFrame).Count(IsBudget)!=1)
                throw new InvalidOperationException("Unexpected simulation time budget structure.");
            if(new[]{RunFrame,SimulationFrame}.Any(m=>HarmonyLib.Harmony.GetPatchInfo(m)?.Owners.Contains(Owner)==true))
                throw new InvalidOperationException("Remove another adaptive version first.");
            Now();
            try
            {
                Harmony.Patch(RunFrame,transpiler:new HarmonyMethod(typeof(Adaptive),nameof(Rewrite)));
                Harmony.Patch(SimulationFrame,prefix:new HarmonyMethod(typeof(Adaptive),nameof(Begin)),postfix:new HarmonyMethod(typeof(Adaptive),nameof(End)));
                installed=true;
                WriteStatus("installed",0,0);
                File.Delete(Path.Combine(DirectoryPath,"adaptive-error.txt"));
            }
            catch {Remove();throw;}
        }
        private static bool IsBudget(CodeInstruction instruction){return instruction.opcode==OpCodes.Ldc_I4 && instruction.operand is int value && value==80000;}
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if(IsBudget(instruction))
                {
                    instruction.opcode=OpCodes.Call;
                    instruction.operand=AccessTools.Method(typeof(Adaptive),nameof(CurrentBudgetTicks));
                }
                yield return instruction;
            }
        }
        private static void Begin(FixedStepSimulationClock __1)
        {
            ulong now=Now();
            if(firstStart==0)firstStart=now;
            if(lastStart!=0 && now>=lastStart && now-lastStart<10000000)
            {
                ulong period=now-lastStart;
                double outside=period>lastDuration ? period-lastDuration : 0;
                outsideTicks=outsideTicks==0 ? outside : outsideTicks*0.9+outside*0.1;
            }
            else outsideTicks=0;
            currentBudget=Controller.Choose(ReadFactor(),__1.BacklogSeconds,outsideTicks);
            lastStart=now;
        }
        private static void End(GameSystem __0,FixedStepSimulationClock __1)
        {
            lastDuration=Now()-lastStart;
            lastMilliseconds=lastDuration/10000.0;
            maxMilliseconds=Math.Max(maxMilliseconds,lastMilliseconds);
            simulatedSeconds+=__0.AdmittedSimulationDelta;
            gameSeconds+=__0.AdmittedGameSimulationDelta;
            frames++;
            if((frames&127)==0)WriteStatus("running",ReadFactor(),__1.BacklogSeconds);
        }
        private static void WriteStatus(string state,double factor,double backlog)
        {
            try
            {
                double realSeconds=firstStart==0 ? 0 : (Now()-firstStart)/10000000.0;
                File.WriteAllText(Path.Combine(DirectoryPath,"adaptive-status.txt"),string.Format(CultureInfo.InvariantCulture,
                    "state={0}\nversion=1-adaptive-budget\npid={1}\nframes={2}\nfactor={3:R}\nframeBudgetMilliseconds={4:R}\nminimumBudgetMilliseconds=8\nmaximumBudgetMilliseconds=16\nsoftFrameTargetMilliseconds=40\nmeanOutsideSimulationMilliseconds={5:R}\nlastSimulationMilliseconds={6:R}\nmaxSimulationMilliseconds={7:R}\nbacklogSeconds={8:R}\nrealSeconds={9:R}\nsimulatedSeconds={10:R}\ngameSeconds={11:R}\n",
                    state,Process.GetCurrentProcess().Id,frames,factor,currentBudget/10000.0,outsideTicks/10000.0,
                    lastMilliseconds,maxMilliseconds,backlog,realSeconds,simulatedSeconds,gameSeconds));
            }
            catch(IOException){}
            catch(UnauthorizedAccessException){}
        }
        public static void Remove()
        {
            if(RunFrame!=null)Harmony.Unpatch(RunFrame,HarmonyPatchType.Transpiler,Owner);
            if(SimulationFrame!=null)Harmony.Unpatch(SimulationFrame,HarmonyPatchType.All,Owner);
            installed=false;
            WriteStatus("removed",0,0);
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Use only the isolated Mono host.");
            var budget=new Budget();
            for(int i=0;i<100;i++)Require(budget.Choose(42,40,100000)<=160000,"maximum adaptive budget");
            Require(budget.Choose(42,40,100000)==160000,"queue pressure grows budget");
            Require(budget.Choose(42,40,310000)==90000,"outside work limits simulation headroom");
            Require(budget.Choose(42,40,900000)==80000,"heavy outside work restores baseline cap");
            for(int i=0;i<100;i++)budget.Choose(42,0,0);
            Require(budget.Choose(42,0,0)==80000,"empty queue returns to baseline");
            Require(budget.Choose(1,40,0)==80000,"disabled speed returns to baseline");
            Install();
            try
            {
                Require(!PatchProcessor.GetCurrentInstructions(RunFrame).Any(IsBudget),"constant replaced");
                currentBudget=160000;
                var clock=new FixedStepSimulationClock(); clock.AddElapsed(2,1);
                ulong ticks=0;
                var args=new object[]{clock,42.0,(Action)(()=>ticks+=30000),(Func<ulong>)(()=>ticks),null,null,null};
                int count=(int)RunFrame.Invoke(null,args);
                Require(count==6 && (bool)args[5] && (double)args[6]==18,"six slow steps then cooperative yield");
                Require(Math.Abs(clock.BacklogSeconds-1.88)<1e-8,"unexecuted time preserved");
                int total=count;
                while(clock.BacklogSeconds+1e-9>=0.02)total+=(int)RunFrame.Invoke(null,args);
                Require(total==100,"yield resume conserves steps");
                var stress=new FixedStepSimulationClock();
                var admit=AccessTools.Method(RunFrame.DeclaringType,"AdmitElapsed");
                double overflow=0;int steps=0;
                for(int i=0;i<10000;i++)
                {
                    overflow+=(double)admit.Invoke(null,new object[]{stress,0.4,1.0,1000.0});
                    args=new object[]{stress,1000.0,(Action)(()=>ticks+=10000),(Func<ulong>)(()=>ticks),null,null,null};
                    steps+=(int)RunFrame.Invoke(null,args);
                    Require(stress.BacklogSeconds<=40+1e-8,"bounded stress backlog");
                }
                Require(steps==160000 && Math.Abs(4000-steps*0.02-overflow-stress.BacklogSeconds)<1e-6,"10000 frame overload conservation");
                currentBudget=80000; ticks=0;
                var baseline=new FixedStepSimulationClock();baseline.AddElapsed(1,1);
                args=new object[]{baseline,42.0,(Action)(()=>ticks+=30000),(Func<ulong>)(()=>ticks),null,null,null};
                Require((int)RunFrame.Invoke(null,args)==3,"eight millisecond baseline retained");
            }
            finally {Remove();}
            Require(HarmonyLib.Harmony.GetPatchInfo(RunFrame)?.Owners.Contains(Owner)!=true,"adaptive hook removed");
            File.WriteAllText(Path.Combine(DirectoryPath,"adaptive-selftest.txt"),"PASS: adaptive growth/decay; 8-16ms bounds; outside-work headroom; disabled baseline; exact budget callsite; 16ms cooperative yield/resume; 10000-frame overload conservation; own hook removal.\n");
        }
        private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Adaptive test failed: "+message);}
    }
}
