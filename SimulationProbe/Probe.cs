using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
            Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.SimulationProbe").GetMethod("Install").Invoke(null,null);
        }
        public static void Remove() { Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.SimulationProbe").GetMethod("Remove").Invoke(null,null); }
    }
    public static class SimulationProbe
    {
        private const string Owner="local.idlespiral.simulation.observer3";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo Advance=AccessTools.Method(typeof(IdleSpiralSystem),"Advance");
        private static readonly MethodInfo Update=AccessTools.Method(typeof(GameSystem),"Update");
        private static readonly MethodInfo Theta=AccessTools.Method(typeof(A_TotalTheta),"NotifyAchievements");
        private static readonly List<string> Names=new List<string>();
        private static readonly List<MethodInfo> Targets=new List<MethodInfo>();
        private static readonly ulong[] Starts=new ulong[128],Ticks=new ulong[128];
        private static readonly long[] Calls=new long[128];
        private static bool sample;
        private static long steps,samples,thetaCalls;
        private static double firstTheta,lastTheta,gameSeconds;
        private static ulong start,total,advanceStart;
        private static string initial;
        private static string Output=>Path.Combine(Path.GetDirectoryName(typeof(SimulationProbe).Assembly.Location),"simulation-profile.txt");
        [DllImport("kernelbase.dll",ExactSpelling=true)] private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var time);return time;}
        public static void Install()
        {
            steps=samples=thetaCalls=0;start=total=0;sample=false;gameSeconds=firstTheta=lastTheta=0;
            Array.Clear(Ticks,0,Ticks.Length);Array.Clear(Calls,0,Calls.Length);
            Names.Clear();Targets.Clear();initial="";
            Targets.Add(Advance);
            Harmony.Patch(Advance,prefix:new HarmonyMethod(typeof(SimulationProbe),nameof(Begin)),postfix:new HarmonyMethod(typeof(SimulationProbe),nameof(End)),
                transpiler:new HarmonyMethod(typeof(SimulationProbe),nameof(Rewrite)));
            foreach(var method in new[]{AccessTools.Method(typeof(IdleSpiralSystem),"UpdateCurrencyBySecond"),AccessTools.Method(typeof(ReccurenceMultiplier),"UpdateValue")})
            {
                Targets.Add(method);
                Harmony.Patch(method,transpiler:new HarmonyMethod(typeof(SimulationProbe),nameof(Rewrite)));
            }
            Harmony.Patch(Update,postfix:new HarmonyMethod(typeof(SimulationProbe),nameof(Frame)));
            Harmony.Patch(Theta,postfix:new HarmonyMethod(typeof(SimulationProbe),nameof(ObserveTheta)));
            File.WriteAllText(Output,"state=scheduled\n");
        }
        public static void Remove()
        {
            foreach(var method in Targets.Concat(new[]{Update,Theta})) Harmony.Unpatch(method,HarmonyPatchType.All,Owner);
        }
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if((instruction.opcode==OpCodes.Call || instruction.opcode==OpCodes.Callvirt) && instruction.operand is MethodInfo method &&
                    method.DeclaringType.Assembly==typeof(IdleSpiralSystem).Assembly)
                {
                    int index=Names.Count;
                    if(index>=Starts.Length) throw new InvalidOperationException("Too many stages.");
                    Names.Add(method.DeclaringType.Name+"."+method.Name);
                    var first=new CodeInstruction(OpCodes.Ldc_I4,index);
                    first.labels.AddRange(instruction.labels);instruction.labels.Clear();
                    first.blocks.AddRange(instruction.blocks.Where(b=>b.blockType!=ExceptionBlockType.EndExceptionBlock));
                    instruction.blocks.RemoveAll(b=>b.blockType!=ExceptionBlockType.EndExceptionBlock);
                    yield return first;
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SimulationProbe),nameof(StageBegin)));
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Ldc_I4,index);
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SimulationProbe),nameof(StageEnd)));
                }
                else yield return instruction;
            }
        }
        private static void Begin(){sample=(++steps%127)==0;if(sample){samples++;advanceStart=Now();}}
        private static void End(){if(sample)total+=Now()-advanceStart;sample=false;}
        public static void StageBegin(int index){if(sample)Starts[index]=Now();}
        public static void StageEnd(int index){if(sample){Ticks[index]+=Now()-Starts[index];Calls[index]++;}}
        private static void ObserveTheta(double __0){if(thetaCalls++==0)firstTheta=__0;lastTheta=__0;}
        private static void Frame(GameSystem __instance)
        {
            ulong now=Now();if(start==0)
            {
                start=now;
                var draw=UnityEngine.Object.FindAnyObjectByType<LineDraw>(UnityEngine.FindObjectsInactive.Include);
                initial="currencyUpdateStepCount="+__instance.idleSystem.CurrencyUpdateStepCount+
                    "\nlineDrawActive="+(draw!=null && draw.isActiveAndEnabled)+
                    "\nmainSpiralSuppressed="+(CreateSpirals.Instance!=null && CreateSpirals.Instance.IsRenderingSuppressed)+"\n";
                return;
            }
            gameSeconds+=__instance.AdmittedGameSimulationDelta;
            if(now-start<150000000)return;
            Remove();
            var result=new StringBuilder();
            result.Append(initial);
            result.AppendFormat(CultureInfo.InvariantCulture,"state=completed\nrealSeconds={0:R}\nsteps={1}\nsamples={2}\nmeanSampledStepMicroseconds={3:R}\ngameMultiplier={4:R}\nthetaNotifications={5}\nfirstTheta={6:R}\nlastTheta={7:R}\nobserverRemoved=true\n",
                (now-start)/1e7,steps,samples,total/(double)Math.Max(1,samples)/10,gameSeconds/((now-start)/1e7),thetaCalls,firstTheta,lastTheta);
            foreach(int index in Enumerable.Range(0,Names.Count).OrderByDescending(i=>Ticks[i]))
                result.AppendFormat(CultureInfo.InvariantCulture,"stage={0}; calls={1}; meanMicrosecondsPerSample={2:R}\n",Names[index],Calls[index],Ticks[index]/(double)Math.Max(1,samples)/10);
            File.WriteAllText(Output,result.ToString());
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="IdleSpiral.FormulaGuard" || a.GetName().Name=="IdleSpiral.TheoryGuard"))
            {
                var type=assembly.GetTypes().First(t=>t.Name=="FormulaGuard" || t.Name=="TheoryGuard");
                type.GetMethod("Status",Type.EmptyTypes).Invoke(null,null);
            }
        }
    }
}
