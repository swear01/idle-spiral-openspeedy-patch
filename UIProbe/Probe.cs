using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install()
        {
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"0Harmony.dll"));
            Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.UIProbe").GetMethod("Install").Invoke(null,null);
        }
        public static void Remove(){Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.UIProbe").GetMethod("Remove").Invoke(null,null);}
    }
    public static class UIProbe
    {
        private const string Owner="local.idlespiral.ui.observer";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly Dictionary<MethodBase,Result> Results=new Dictionary<MethodBase,Result>();
        private static readonly MethodInfo Frame=AccessTools.Method(typeof(GameSystem),"Update");
        private static ulong start;
        private sealed class Result {public long Count;public ulong Total,Maximum;}
        [DllImport("kernelbase.dll",ExactSpelling=true)]private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}
        private static string Output=>Path.Combine(Path.GetDirectoryName(typeof(UIProbe).Assembly.Location),"ui-profile.txt");
        public static void Install()
        {
            start=Now();Results.Clear();
            foreach(var type in new[]{typeof(LineDraw),typeof(SpiralVertexes),typeof(CreateSpirals),typeof(QedProofRenderer),typeof(BattlePolarCurveRenderer),
                typeof(UpdateBattleRenderer),typeof(PolarCurveSlider),typeof(A_TotalTheta),typeof(BattleUI_BattleView),typeof(DesignRenderManager)})
                foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                    if(method.Name=="Update" || method.Name=="LateUpdate" || method.Name=="FixedUpdate" || method.Name=="NotifyAchievements" ||
                        method.Name=="ApplyAllyProgress" || method.Name=="ApplyEnemyProgress" || method.Name=="PresentGraph")
                    {
                        Results.Add(method,new Result());
                        Harmony.Patch(method,prefix:new HarmonyMethod(typeof(UIProbe),nameof(Begin)),postfix:new HarmonyMethod(typeof(UIProbe),nameof(End)));
                    }
            Harmony.Patch(Frame,postfix:new HarmonyMethod(typeof(UIProbe),nameof(Check)));
            File.WriteAllText(Output,"state=scheduled\n");
        }
        private static void Begin(out ulong __state){__state=Now();}
        private static void End(MethodBase __originalMethod,ulong __state)
        {
            var result=Results[__originalMethod];ulong ticks=Now()-__state;
            result.Count++;result.Total+=ticks;result.Maximum=Math.Max(result.Maximum,ticks);
        }
        private static void Check()
        {
            var elapsed=Now()-start;if(elapsed<150000000)return;
            Remove();
            var text=new StringBuilder("state=completed\nrealSeconds="+(elapsed/1e7).ToString("R",CultureInfo.InvariantCulture)+"\nobserverRemoved=true\n");
            foreach(var entry in Results.OrderByDescending(r=>r.Value.Total))
                text.AppendFormat(CultureInfo.InvariantCulture,"method={0}.{1}; count={2}; totalMilliseconds={3:R}; maxMilliseconds={4:R}\n",
                    entry.Key.DeclaringType.Name,entry.Key.Name,entry.Value.Count,entry.Value.Total/10000.0,entry.Value.Maximum/10000.0);
            File.WriteAllText(Output,text.ToString());
        }
        public static void Remove(){foreach(var method in Results.Keys.Concat(new[]{Frame}))Harmony.Unpatch(method,HarmonyPatchType.All,Owner);}
    }
}
