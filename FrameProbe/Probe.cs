using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install()
        {
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"0Harmony.dll"));
            Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.FrameProbe").GetMethod("Install").Invoke(null,null);
        }
        public static void Remove() { Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.FrameProbe").GetMethod("Remove").Invoke(null,null); }
    }
    public static class FrameProbe
    {
#if STABILITY_SAMPLE
        private const string Owner="local.idlespiral.frame.observer300";
        private const ulong WindowTicks=3000000000;
        private const string OutputName="frame-observation-300.txt";
#elif LONG_SAMPLE
        private const string Owner="local.idlespiral.frame.observer60";
        private const ulong WindowTicks=600000000;
        private const string OutputName="frame-observation-60.txt";
#else
        private const string Owner="local.idlespiral.frame.observer";
        private const ulong WindowTicks=150000000;
        private const string OutputName="frame-observation.txt";
#endif
        private static readonly Harmony Patch=new Harmony(Owner);
        private static readonly string Output=Path.Combine(Path.GetDirectoryName(typeof(FrameProbe).Assembly.Location),OutputName);
        private static readonly MethodInfo Target=AccessTools.Method(typeof(GameSystem),"Update");
        private static readonly List<double> Periods=new List<double>();
        private static ulong start,previous;
        private static double updateSeconds,gameSeconds;
        private static long frames;
        [DllImport("kernelbase.dll",ExactSpelling=true)] private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var time);return time;}
        public static void Install()
        {
            start=previous=0; updateSeconds=gameSeconds=0; frames=0; Periods.Clear();
            File.WriteAllText(Output,"state=scheduled\n");
            Patch.Patch(Target,prefix:new HarmonyMethod(typeof(FrameProbe),nameof(Begin)),postfix:new HarmonyMethod(typeof(FrameProbe),nameof(End)));
        }
        public static void Remove(){Patch.Unpatch(Target,HarmonyPatchType.All,Owner);}
        private static void Begin(out ulong __state){__state=Now();}
        private static void End(GameSystem __instance,ulong __state)
        {
            var now=Now();
            if(start==0){start=previous=__state;return;}
            Periods.Add((__state-previous)/10000000.0); previous=__state;
            frames++; updateSeconds+=(now-__state)/10000000.0; gameSeconds+=__instance.AdmittedGameSimulationDelta;
            if(now-start<WindowTicks)return;
            Remove();
            Periods.Sort();
            double elapsed=(now-start)/10000000.0;
            File.WriteAllText(Output,string.Format(CultureInfo.InvariantCulture,
                "state=completed\nrealSeconds={0:R}\nframes={1}\nrealFps={2:R}\nmeanUpdateMilliseconds={3:R}\nmeanOutsideUpdateMilliseconds={4:R}\np95FrameMilliseconds={5:R}\nmaxFrameMilliseconds={6:R}\ngameMultiplier={7:R}\nobserverRemoved=true\n",
                elapsed,frames,frames/elapsed,updateSeconds/frames*1000,(elapsed-updateSeconds)/frames*1000,
                Periods[(int)((Periods.Count-1)*0.95)]*1000,Periods[Periods.Count-1]*1000,gameSeconds/elapsed));
        }
    }
}
