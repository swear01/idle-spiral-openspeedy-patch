using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install()
        {
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"0Harmony.dll"));
            Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.PacingProbe").GetMethod("Install").Invoke(null,null);
        }
        public static void Remove(){Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.PacingProbe").GetMethod("Remove").Invoke(null,null);}
    }
    public static class PacingProbe
    {
        private const string Owner="local.idlespiral.pacing.observer";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo Update=AccessTools.Method(typeof(GameSystem),"Update");
        private static readonly MethodInfo Cadence=AccessTools.Method(AccessTools.TypeByName("PcFrameRateController"),"ApplyResolvedCadence");
        private static string Output=>Path.Combine(Path.GetDirectoryName(typeof(PacingProbe).Assembly.Location),"pacing-observation.txt");
        private static ulong start,first;
        private static long frames;
        private static double games;
        private static int originalTarget;
        private static bool requested;
        private static string initial;
        [DllImport("kernelbase.dll",ExactSpelling=true)]private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}
        public static void Install()
        {
            requested=true;start=first=0;frames=0;games=0;
            Harmony.Patch(Cadence,prefix:new HarmonyMethod(typeof(PacingProbe),nameof(AllowCadence)));
            Harmony.Patch(Update,postfix:new HarmonyMethod(typeof(PacingProbe),nameof(Frame)));
            File.WriteAllText(Output,"state=scheduled\n");
        }
        private static bool AllowCadence(){return !requested;}
        public static void Remove(){requested=false;}
        private static void Frame(GameSystem __instance)
        {
            ulong now=Now();
            if(!requested){Restore();return;}
            if(start==0)
            {
                start=first=now;originalTarget=Application.targetFrameRate;
                initial=string.Format(CultureInfo.InvariantCulture,"originalTargetFrameRate={0}\noriginalVsync={1}\nfocused={2}\nrunInBackground={3}\ntimeScale={4:R}\ndeltaTime={5:R}\nunscaledDeltaTime={6:R}\nfixedDeltaTime={7:R}\nmaximumDeltaTime={8:R}\n",
                    originalTarget,QualitySettings.vSyncCount,Application.isFocused,Application.runInBackground,Time.timeScale,Time.deltaTime,Time.unscaledDeltaTime,Time.fixedDeltaTime,Time.maximumDeltaTime);
                Application.targetFrameRate=-1;
                return;
            }
            frames++;games+=__instance.AdmittedGameSimulationDelta;
            if(now-start<150000000)return;
            requested=false;Restore();
            File.WriteAllText(Output,initial+string.Format(CultureInfo.InvariantCulture,"state=completed\nrealSeconds={0:R}\nframes={1}\nrealFps={2:R}\ngameMultiplier={3:R}\noriginalTargetRestored=true\nobserverRemoved=true\n",(now-start)/1e7,frames,frames/((now-start)/1e7),games/((now-start)/1e7)));
        }
        private static void Restore()
        {
            if(start!=0)Application.targetFrameRate=originalTarget;
            Harmony.Unpatch(Cadence,HarmonyPatchType.All,Owner);Harmony.Unpatch(Update,HarmonyPatchType.All,Owner);
        }
    }
}
