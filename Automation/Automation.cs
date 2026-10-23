using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
            try{typeof(AutomationGuard).GetMethod(name).Invoke(null,null);File.Delete(Path.Combine(DirectoryPath,"automation-error.txt"));}
            catch(Exception e){File.WriteAllText(Path.Combine(DirectoryPath,"automation-error.txt"),e.ToString());throw;}
        }
    }
    // The four automation controllers tick once per 1 s of Time.realtimeSinceStartup, which ClockIsolation keeps at 1x.
    // Feed them a clock that runs at the OpenSpeedy factor (still at most one tick per frame, as with accelerated native
    // clocks), and keep the prestige checkpoint saves they trigger to one per 5 real seconds.
    public static class AutomationGuard
    {
        private const string Owner="local.idlespiral.openspeedy.automation";
        private const string RuntimeHash="dbfeebdc66091f27f63320971cd3f369b3fd66928fc6a66af336425e44df635f";
        private const ulong CheckpointIntervalTicks=50000000;
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly string DirectoryPath=Path.GetDirectoryName(typeof(AutomationGuard).Assembly.Location);
        private static readonly Type[] Controllers={typeof(UpgradeAutomationController),typeof(PrestigeAutomationController),typeof(SoftPrestigeAutomationController),typeof(TornadoPrestigeAutomationController)};
        private static Func<double> readFactor;
        private static bool installed,inAutomation;
        private static double lastReal=double.NaN,scaled;
        private static ulong lastCheckpoint;
        private static long frames,autoBuyRuns,checkpoints,deferredCheckpoints;
        [DllImport("kernelbase.dll",ExactSpelling=true)]private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}

        public static void Install()
        {
            if(installed)return;
            var runtime=Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.SpeedPatch.SteamGuard.dll"));
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(runtime.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=RuntimeHash)
                    throw new InvalidOperationException("Unsupported simulation runtime.");
            readFactor=(Func<double>)Delegate.CreateDelegate(typeof(Func<double>),AccessTools.Method(runtime.GetType("IdleSpiralSpeedPatch.Patch"),"ReadFactor"));
            try
            {
                foreach(var controller in Controllers)
                    Harmony.Patch(AccessTools.Method(controller,"Tick",new[]{typeof(double)}),
                        prefix:new HarmonyMethod(typeof(AutomationGuard),nameof(BeforeTick)),finalizer:new HarmonyMethod(typeof(AutomationGuard),nameof(AfterTick)));
                Harmony.Patch(AccessTools.Method(typeof(UpgradeAutomationController),"RunAutomation"),postfix:new HarmonyMethod(typeof(AutomationGuard),nameof(CountAutoBuy)));
                Harmony.Patch(AccessTools.Method(typeof(PrestigeCheckpoint),"TrySave"),prefix:new HarmonyMethod(typeof(AutomationGuard),nameof(BeforeCheckpoint)));
                installed=true;Status("installed",1);
            }
            catch{Harmony.UnpatchAll(Owner);throw;}
        }
        public static void Remove(){Harmony.UnpatchAll(Owner);installed=false;Status("removed",1);}

        private static void BeforeTick(ref double realtimeSeconds)
        {
            double factor=readFactor();
            if(realtimeSeconds!=lastReal)
            {
                scaled=double.IsNaN(lastReal)?realtimeSeconds:scaled+Math.Max(0,realtimeSeconds-lastReal)*factor;
                lastReal=realtimeSeconds;
                if((++frames&1023)==1)Status("running",factor);
            }
            realtimeSeconds=scaled;
            inAutomation=true;
        }
        private static void CountAutoBuy(){autoBuyRuns++;}
        private static Exception AfterTick(Exception __exception){inAutomation=false;return __exception;}
        private static bool BeforeCheckpoint(ref bool __result)
        {
            if(!inAutomation)return true;
            var now=Now();
            if(lastCheckpoint!=0&&now-lastCheckpoint<CheckpointIntervalTicks){deferredCheckpoints++;__result=true;return false;}
            lastCheckpoint=now;checkpoints++;return true;
        }
        private static void Status(string state,double factor)
        {
            try
            {
                File.WriteAllText(Path.Combine(DirectoryPath,"automation-status.txt"),string.Format(CultureInfo.InvariantCulture,
                    "state={0}\nfactor={1:R}\nframes={2}\nscaledSeconds={3:R}\nautoBuyRuns={4}\ncheckpoints={5}\ndeferredCheckpoints={6}\n",
                    state,factor,frames,scaled,autoBuyRuns,checkpoints,deferredCheckpoints));
            }
            catch{}
        }
    }
}
