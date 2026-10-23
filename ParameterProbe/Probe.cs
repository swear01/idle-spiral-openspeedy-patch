using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using IdleLibrary;
namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install(){Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"0Harmony.dll"));ParameterProbe.Install();}
        public static void Remove(){ParameterProbe.Remove();}
    }
    public static class ParameterProbe
    {
        private const string Owner="local.idlespiral.parameter.observer2";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo Advance=AccessTools.Method(typeof(IdleSpiralSystem),"Advance");
        private static readonly MethodInfo Frame=AccessTools.Method(typeof(GameSystem),"Update");
        private static readonly MethodInfo Number=AccessTools.PropertyGetter(typeof(Parameter),"Number");
        private sealed class Result{public string Name;public ulong Ticks;public long Count;}
        private static readonly Dictionary<Parameter,Result> Results=new Dictionary<Parameter,Result>();
        private static readonly Dictionary<MethodBase,Result> Methods=new Dictionary<MethodBase,Result>();
        private static bool sample;
        private static long steps,samples;
        private static ulong start;
        private static string initial;
        private static string Output=>Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"parameter-profile.txt");
        [DllImport("kernelbase.dll",ExactSpelling=true)]private static extern void QueryUnbiasedInterruptTimePrecise(out ulong value);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}
        public static void Install()
        {
            start=0;steps=samples=0;sample=false;Results.Clear();Methods.Clear();
            Harmony.Patch(Advance,prefix:new HarmonyMethod(typeof(ParameterProbe),nameof(BeginStep)),finalizer:new HarmonyMethod(typeof(ParameterProbe),nameof(EndStep)));
            Harmony.Patch(Frame,postfix:new HarmonyMethod(typeof(ParameterProbe),nameof(Check)));
            File.WriteAllText(Output,"state=scheduled\n");
        }
        public static void Remove(){foreach(var method in Methods.Keys.Concat(new MethodBase[]{Advance,Number,Frame}))Harmony.Unpatch(method,HarmonyPatchType.All,Owner);}
        private static void BeginStep(){sample=(++steps%127)==0;if(sample)samples++;}
        private static void EndStep(){sample=false;}
        private static void Begin(out ulong __state){__state=sample?Now():0;}
        private static void End(Parameter __instance,ulong __state)
        {
            if(__state==0)return;
            if(Results.TryGetValue(__instance,out var result)){result.Ticks+=Now()-__state;result.Count++;}
        }
        private static void EndMethod(MethodBase __originalMethod,ulong __state)
        {
            if(__state==0)return;
            var result=Methods[__originalMethod];result.Ticks+=Now()-__state;result.Count++;
        }
        private static void Add(Delegate value,string label)
        {
            if(value==null || Methods.ContainsKey(value.Method))return;
            Methods[value.Method]=new Result{Name=label+":"+value.Method.DeclaringType.FullName+"."+value.Method.Name};
            Harmony.Patch(value.Method,prefix:new HarmonyMethod(typeof(ParameterProbe),nameof(Begin)),postfix:new HarmonyMethod(typeof(ParameterProbe),nameof(EndMethod)));
        }
        private static void Check(GameSystem __instance)
        {
            var now=Now();
            if(start==0)
            {
                start=now;var container=__instance.idleSystem.parameterContainer;
                foreach(ParameterContainer.ParametersKind kind in Enum.GetValues(typeof(ParameterContainer.ParametersKind)))
                    Results[container.GetParameter(kind)]=new Result{Name=kind.ToString()};
                initial="n="+container.GetParameter(ParameterContainer.ParametersKind.n).Number.ToString()+"\nm="+container.GetParameter(ParameterContainer.ParametersKind.m).Number.ToString()+"\n";
                var a=container.GetParameter(ParameterContainer.ParametersKind.a);
                foreach(var multiplier in new[]{a.multiplier,a.independentMultiplier,a.exponentialMultiplier,a.cappedExponentialMultiplier,a.cappedExponentialOrderCap})
                    foreach(var suffix in new[]{"add","mul"})
                    {
                        var array=(Array)AccessTools.Field(typeof(Multiplier),"_"+suffix+"Multipliers").GetValue(multiplier);
                        int count=(int)AccessTools.Field(typeof(Multiplier),"_"+suffix+"MultiplierCount").GetValue(multiplier);
                        for(int i=0;i<count;i++)
                        {
                            var entry=array.GetValue(i);var type=entry.GetType();
                            string label=(string)AccessTools.Field(type,"Key").GetValue(entry);
                            Add((Delegate)AccessTools.Field(type,"Value").GetValue(entry),label);
                            Add((Delegate)AccessTools.Field(type,"ConditionalTrigger").GetValue(entry),label+":trigger");
                            var level=AccessTools.Field(type,"LevelValue").GetValue(entry);
                            if(level!=null)Add((Delegate)AccessTools.Property(level.GetType(),"multiplierWithLevel").GetValue(level,null),label);
                        }
                    }
                return;
            }
            if(now-start<150000000)return;
            Remove();var text=new StringBuilder(initial+"state=completed\nsamples="+samples+"\nobserverRemoved=true\n");
            foreach(var entry in Methods.Values.OrderByDescending(r=>r.Ticks))
                text.AppendFormat(CultureInfo.InvariantCulture,"method={0}; calls={1}; inclusiveMicrosecondsPerStep={2:R}\n",entry.Name,entry.Count,entry.Ticks/(double)samples/10);
            File.WriteAllText(Output,text.ToString());
        }
    }
}
