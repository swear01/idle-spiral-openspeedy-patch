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
using IdleSystem.Analytics;
using UnityEngine;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        private static string DirectoryPath=>Path.GetDirectoryName(typeof(Loader).Assembly.Location);
        public static void Install(){Invoke("Install");}
        public static void Remove(){Invoke("Remove");}
        public static void Test(){Invoke("Test");}
        public static void AutoInstall()
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.TheoryGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader").GetMethod("AutoInstall").Invoke(null,null);
            Install();
        }
        private static void Invoke(string name)
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
            try{typeof(Housekeeping).GetMethod(name).Invoke(null,null);File.Delete(Path.Combine(DirectoryPath,"housekeeping-error.txt"));}
            catch(Exception e){File.WriteAllText(Path.Combine(DirectoryPath,"housekeeping-error.txt"),e.ToString());throw;}
        }
    }
    public static class Housekeeping
    {
        private const string Owner="local.idlespiral.openspeedy.housekeeping";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly string DirectoryPath=Path.GetDirectoryName(typeof(Housekeeping).Assembly.Location);
        private static readonly MethodInfo Frame=AccessTools.Method(typeof(GameSystem),"Update");
        private static readonly MethodInfo Steam=AccessTools.Method(typeof(SteamManager),"Update");
        private static readonly MethodInfo Init=AccessTools.Method(typeof(IdleAnalytics),"Init");
        private static readonly MethodInfo WatchdogInstall=AccessTools.Method(typeof(MainThreadWatchdog),"Install");
        private static readonly MethodInfo Cadence=AccessTools.Method(typeof(PcFrameRateController),"ApplyResolvedCadence");
        private static readonly List<IdleAnalytics> Disabled=new List<IdleAnalytics>();
        private static MethodInfo stepBudget,runFrame;
        private static Func<double> readFactor;
        private static Func<int> originalBudget;
        private static MethodInfo adaptiveBudget;
        private static bool installed,applied;
        private static volatile bool requested;
        private static ulong nextPoll,nextStatus;
        private static long polls,skipped;
        private static double maximumSteamMilliseconds;
        private static bool watchdogWasRunning;
        private static string watchdogPath;
        [DllImport("kernelbase.dll",ExactSpelling=true)]private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}
        private static bool Due(ulong now,ref ulong next)
        {
            if(now<next)return false;
            next=now+500000;return true;
        }
        public static void Install()
        {
            if(installed){requested=true;return;}
            Verify(typeof(GameSystem).Assembly,"a67ee1d53fc2247d738ff2166f1a5e217bcb3fdebc3dfebf0a21393b8e5fbfbb");
            Verify(typeof(IdleAnalytics).Assembly,"1e1c233c358ffa6475269d6d86b0c8c460ac0dc651bd0a765599242bad684029");
            Verify(Assembly.Load("com.rlabrecque.steamworks.net"),"0b7d5a5fc171bd89b663291dc83002a5f69e7124882d2cb1f075295057030058");
            var runtime=Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.SpeedPatch.SteamGuard.dll"));
            Verify(runtime,"dbfeebdc66091f27f63320971cd3f369b3fd66928fc6a66af336425e44df635f");
            stepBudget=AccessTools.Method(runtime.GetType("IdleSpiralSpeedPatch.Patch"),"StepBudget");
            runFrame=AccessTools.Method(stepBudget.DeclaringType,"RunFrame");
            readFactor=(Func<double>)Delegate.CreateDelegate(typeof(Func<double>),AccessTools.Method(stepBudget.DeclaringType,"ReadFactor"));
            var adaptive=Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.AdaptiveGuard.dll")).GetType("IdleSpiralSpeedPatch.Adaptive");
            adaptiveBudget=AccessTools.Method(adaptive,"CurrentBudgetTicks");
            originalBudget=(Func<int>)Delegate.CreateDelegate(typeof(Func<int>),adaptiveBudget);
            if(PatchProcessor.GetOriginalInstructions(stepBudget).Count(i=>i.LoadsConstant(128.0))!=1 ||
                PatchProcessor.GetOriginalInstructions(runFrame).Count(i=>i.Calls(stepBudget))!=1)
                throw new InvalidOperationException("Unexpected bounded step cap.");
            Now();
            try
            {
                Harmony.Patch(runFrame,transpiler:new HarmonyMethod(typeof(Housekeeping),nameof(RaiseCap)){priority=Priority.Last});
                Harmony.Patch(Steam,prefix:new HarmonyMethod(typeof(Housekeeping),nameof(BeforeSteam)),postfix:new HarmonyMethod(typeof(Housekeeping),nameof(AfterSteam)));
                Harmony.Patch(Init,prefix:new HarmonyMethod(typeof(Housekeeping),nameof(SkipAnalytics)));
                Harmony.Patch(WatchdogInstall,prefix:new HarmonyMethod(typeof(Housekeeping),nameof(SkipAnalytics)));
                Harmony.Patch(Cadence,prefix:new HarmonyMethod(typeof(Housekeeping),nameof(KeepFocusedCadence)));
                requested=installed=true;
                Harmony.Patch(Frame,postfix:new HarmonyMethod(typeof(Housekeeping),nameof(OnFrame)));
                Status("scheduled");
            }
            catch{Unpatch();throw;}
        }
        private static IEnumerable<CodeInstruction> RaiseCap(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if(instruction.Calls(stepBudget))instruction.operand=AccessTools.Method(typeof(Housekeeping),nameof(StepBudget));
                if(instruction.Calls(adaptiveBudget))instruction.operand=AccessTools.Method(typeof(Housekeeping),nameof(FrameBudget));
                yield return instruction;
            }
        }
        private static int StepBudget(double factor){return (int)Math.Min(512,Math.Max(4,Math.Ceiling(4*factor)));}
        private static int FrameBudget(){return readFactor()>=100?400000:originalBudget();}
        private static bool SkipAnalytics(){return !requested;}
        // The game caps unfocused windows at 20 FPS, which at high factors means 50+ game seconds per frame; keep the saved rate.
        private static void KeepFocusedCadence(ref bool focused){focused=true;}
        private static bool BeforeSteam(out ulong __state)
        {
            var now=Now();__state=0;
            if(!Due(now,ref nextPoll)){skipped++;return false;}
            __state=now;polls++;return true;
        }
        private static void AfterSteam(ulong __state)
        {
            if(__state!=0)maximumSteamMilliseconds=Math.Max(maximumSteamMilliseconds,(Now()-__state)/10000.0);
        }
        private static void OnFrame()
        {
            try
            {
                if(!requested)
                {
                    Unpatch();
                    foreach(var item in Disabled)if(item!=null)item.enabled=true;
                    Disabled.Clear();
                    if(watchdogWasRunning)MainThreadWatchdog.Install(watchdogPath);
                    applied=false;Status("removed");return;
                }
                if(!applied)
                {
                    foreach(var item in Resources.FindObjectsOfTypeAll<IdleAnalytics>())
                        if(item.enabled){item.StopAllCoroutines();item.enabled=false;Disabled.Add(item);}
                    watchdogWasRunning=(bool)AccessTools.Field(typeof(MainThreadWatchdog),"_running").GetValue(null);
                    watchdogPath=(string)AccessTools.Field(typeof(MainThreadWatchdog),"_hangFilePath").GetValue(null);
                    MainThreadWatchdog.Stop();applied=true;
                }
                var now=Now();
                if(now>=nextStatus){nextStatus=now+50000000;Status("running");}
            }
            catch(Exception e)
            {
                requested=false;
                File.WriteAllText(Path.Combine(DirectoryPath,"housekeeping-error.txt"),e.ToString());
            }
        }
        public static void Remove(){requested=false;Status("restore-scheduled");}
        private static void Unpatch()
        {
            foreach(var method in new[]{Frame,Steam,Init,WatchdogInstall,Cadence,runFrame})if(method!=null)Harmony.Unpatch(method,HarmonyPatchType.All,Owner);
            installed=false;
        }
        private static void Status(string state)
        {
            File.WriteAllText(Path.Combine(DirectoryPath,"housekeeping-status.txt"),string.Format(CultureInfo.InvariantCulture,
                "state={0}\nversion=3-background-cadence\npid={1}\nanalyticsComponentsPaused={2}\nwatchdogStopApplied={3}\nsteamCallbackTargetHz=20\nsteamPolls={4}\nsteamSkippedFrames={5}\nmaximumSteamMilliseconds={6:R}\nmaximumSteps=512\nhighSpeedSimulationBudgetMilliseconds=40\nbackgroundFrameCap=saved\n",
                state,Process.GetCurrentProcess().Id,Disabled.Count,applied,polls,skipped,maximumSteamMilliseconds));
        }
        private static void Verify(Assembly assembly,string expected)
        {
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(assembly.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=expected)
                    throw new InvalidOperationException("Unsupported assembly: "+assembly.GetName().Name);
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Isolated host only.");
            ulong next=0;
            for(ulong time=0;time<10000000;time+=10000)
                if(Due(time,ref next)!=(time%500000==0))throw new InvalidOperationException("Real-time callback cadence.");
            Install();Install();
            var rewritten=RaiseCap(new[]{new CodeInstruction(OpCodes.Call,adaptiveBudget)}).Single();
            if(!rewritten.Calls(AccessTools.Method(typeof(Housekeeping),nameof(FrameBudget))))throw new InvalidOperationException("Adaptive time budget substitution.");
            Func<double,int> budget=StepBudget;
            foreach(var pair in new[]{Tuple.Create(1.0,4),Tuple.Create(20.0,80),Tuple.Create(100.0,400),Tuple.Create(1000.0,512)})
                if(budget(pair.Item1)!=pair.Item2)throw new InvalidOperationException("Bounded step cap mismatch.");
            var runtime=stepBudget.DeclaringType;
            var run=AccessTools.Method(runtime,"RunFrame");
            var clock=new FixedStepSimulationClock();clock.AddElapsed(2,10);
            var args=new object[]{clock,1000.0,(Action)(()=>{}),(Func<ulong>)(()=>0),0.0,false,0.0};
            if((int)run.Invoke(null,args)!=512 || clock.BacklogSeconds<9 || !(bool)args[5])throw new InvalidOperationException("Hard cap or debt conservation.");
            clock=new FixedStepSimulationClock();clock.AddElapsed(2,10);ulong fake=0;int steps=0;
            args=new object[]{clock,1000.0,(Action)(()=>{steps++;fake+=100000;}),(Func<ulong>)(()=>fake),0.0,false,0.0};
            run.Invoke(null,args);
            if(steps!=1 || Math.Abs(clock.BacklogSeconds-19.98)>1e-8)throw new InvalidOperationException("Time-budget yield changed.");
            var adaptive=adaptiveBudget.DeclaringType;
            adaptive.GetMethod("Install").Invoke(null,null);
            try
            {
                readFactor=()=>100;
                clock=new FixedStepSimulationClock();clock.AddElapsed(2,10);fake=0;steps=0;
                args=new object[]{clock,100.0,(Action)(()=>{steps++;fake+=100000;}),(Func<ulong>)(()=>fake),0.0,false,0.0};
                run.Invoke(null,args);
                if(steps!=4 || Math.Abs(clock.BacklogSeconds-19.92)>1e-8 || !(bool)args[5])throw new InvalidOperationException("Combined 40 ms budget or debt conservation.");
            }
            finally{adaptive.GetMethod("Remove").Invoke(null,null);}
            Remove();Unpatch();
            if(new[]{Frame,Steam,Init,WatchdogInstall,Cadence,runFrame}.Any(m=>HarmonyLib.Harmony.GetPatchInfo(m)?.Owners.Contains(Owner)==true))throw new InvalidOperationException("Removal failed.");
            Status("tested");
            File.WriteAllText(Path.Combine(DirectoryPath,"housekeeping-selftest.txt"),"PASS: supported hashes; idempotent hooks; 20 Hz real-time cadence; low/high speed caps; 512-step cap with conserved debt; timed yield after slow step; own hook removal. Analytics pause requires live validation.\n");
        }
    }
}
