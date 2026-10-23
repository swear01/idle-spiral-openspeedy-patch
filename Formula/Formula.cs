using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BreakInfinity;
using HarmonyLib;

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
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.TheoryGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader")
                .GetMethod("AutoInstall").Invoke(null,null);
            Install();
        }
        private static void Invoke(string method)
        {
            try
            {
                Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
                Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.FormulaGuard").GetMethod(method).Invoke(null,null);
                File.Delete(Path.Combine(DirectoryPath,"formula-error.txt"));
            }
            catch(Exception error){File.WriteAllText(Path.Combine(DirectoryPath,"formula-error.txt"),error.ToString());throw;}
        }
    }
    public static class FormulaGuard
    {
        private const string Owner="local.idlespiral.openspeedy.formula";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo[] Targets={AccessTools.Method(typeof(Formula),"CalculateDecayCurve"),
            AccessTools.Method(typeof(Formula),"CalculateLogDecayCurve"),AccessTools.Method(typeof(Formula),"CalculateRootDecayCurve")};
        [ThreadStatic] private static Entry[] cache;
        private static bool installed;
        private static long hits,misses;
        private static string DirectoryPath=>Path.GetDirectoryName(typeof(FormulaGuard).Assembly.Location);
        private struct Key
        {
            public long M0,E0,M1,E1,M2,E2,M3,E3,Log;
            public int Kind;
            public bool Valid;
            public Key(int kind,BigDouble x,BigDouble threshold,BigDouble a,BigDouble b,double log)
            {
                Kind=kind;Valid=true;M0=Bits(x.Mantissa);E0=x.Exponent;M1=Bits(threshold.Mantissa);E1=threshold.Exponent;
                M2=Bits(a.Mantissa);E2=a.Exponent;M3=Bits(b.Mantissa);E3=b.Exponent;Log=Bits(log);
            }
            public int Slot
            {
                get
                {
                    unchecked
                    {
                        long h=Kind*397L;
                        h=(h^M0)*1099511628211L;h=(h^E0)*1099511628211L;
                        h=(h^M1)*1099511628211L;h=(h^E1)*1099511628211L;
                        h=(h^M2)*1099511628211L;h=(h^E2)*1099511628211L;
                        h=(h^M3)*1099511628211L;h=(h^E3)*1099511628211L;h=(h^Log)*1099511628211L;
                        h^=h>>32;h^=h>>16;h^=h>>8;return (int)h&511;
                    }
                }
            }
            public bool Same(Key other)=>Valid && other.Valid && Kind==other.Kind && M0==other.M0 && E0==other.E0 && M1==other.M1 && E1==other.E1 &&
                M2==other.M2 && E2==other.E2 && M3==other.M3 && E3==other.E3 && Log==other.Log;
        }
        private struct Entry {public Key Key;public BigDouble Value;}
        private static long Bits(double value)=>BitConverter.DoubleToInt64Bits(value);
        public static void Install()
        {
            if(installed)return;
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(typeof(Formula).Assembly.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=
                    "9e665da9e2b44503d98db50557dfefb5cd43c892ab47b8a7b8995535b3784a3e")throw new InvalidOperationException("Unsupported formula assembly.");
            try
            {
                for(int i=0;i<Targets.Length;i++) Harmony.Patch(Targets[i],prefix:new HarmonyMethod(typeof(FormulaGuard),new[]{nameof(Decay),nameof(Log),nameof(Root)}[i]),
                    postfix:new HarmonyMethod(typeof(FormulaGuard),nameof(Store)));
                installed=true;Status();
            }
            catch {Remove();throw;}
        }
        public static void Remove()
        {
            foreach(var target in Targets) Harmony.Unpatch(target,HarmonyPatchType.All,Owner);
            installed=false;Status();
        }
        private static bool Lookup(Key key,ref BigDouble result)
        {
            if(cache==null)cache=new Entry[512];
            var entry=cache[key.Slot];
            if(entry.Key.Same(key)){hits++;result=entry.Value;return false;}
            misses++;return true;
        }
        private static bool Decay(BigDouble __0,BigDouble __1,BigDouble __2,BigDouble __3,ref BigDouble __result,out Key __state)
        {
            __state=default(Key);if(__0<=__1)return true;
            __state=new Key(0,__0,__1,__2,__3,0);return Lookup(__state,ref __result);
        }
        private static bool Log(BigDouble __0,BigDouble __1,BigDouble __2,BigDouble __3,double __4,ref BigDouble __result,out Key __state)
        {
            __state=default(Key);if(__0<=__1)return true;
            __state=new Key(1,__0,__1,__2,__3,__4);return Lookup(__state,ref __result);
        }
        private static bool Root(BigDouble __0,BigDouble __1,BigDouble __2,ref BigDouble __result,out Key __state)
        {
            __state=default(Key);if(__0<=__1)return true;
            __state=new Key(2,__0,__1,__2,default(BigDouble),0);return Lookup(__state,ref __result);
        }
        private static void Store(BigDouble __result,Key __state,bool __runOriginal)
        {
            if(__runOriginal && __state.Valid) cache[__state.Slot]=new Entry{Key=__state,Value=__result};
        }
        public static void Status()
        {
            File.WriteAllText(Path.Combine(DirectoryPath,"formula-status.txt"),"state="+(installed?"running":"removed")+
                "\nversion=1-exact-pure-formulas\ncacheEntriesPerThread=512\nhits="+hits+"\nmisses="+misses+"\n");
        }
        [DllImport("kernelbase.dll",ExactSpelling=true)] private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}
        private static BigDouble Evaluate(int i)
        {
            BigDouble x=i%71==0?BigDouble.NaN:i%73==0?BigDouble.PositiveInfinity:(i%4000)*0.271;
            BigDouble threshold=i%29, a=i%7==0?0:0.73, b=i%11==0?0:1.07;
            if(i%3==0)return Formula.CalculateDecayCurve(x,threshold,a,b);
            if(i%3==1)return Formula.CalculateLogDecayCurve(x,threshold,a,b,i%5==0?10:Math.E);
            return Formula.CalculateRootDecayCurve(x,threshold,i%7==0?3:2.5);
        }
        private static double Benchmark()
        {
            ulong start=Now();BigDouble sum=0;
            for(int i=0;i<100000;i++)sum+=Formula.CalculateLogDecayCurve(1000+i%24,200);
            if(sum==0)throw new InvalidOperationException("Empty benchmark.");
            return (Now()-start)/10000.0;
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Isolated host only.");
            const int count=20000;
            var expected=Enumerable.Range(0,count).Select(Capture).ToArray();
            Benchmark();double original=Benchmark();Install();
            try
            {
                for(int pass=0;pass<2;pass++)for(int i=0;i<count;i++)
                {
                    var actual=Capture(i);
                    if(actual.Error!=expected[i].Error || (actual.Error==null && (actual.Value.Exponent!=expected[i].Value.Exponent || Bits(actual.Value.Mantissa)!=Bits(expected[i].Value.Mantissa))))
                        throw new InvalidOperationException("Formula mismatch at "+i);
                }
                Benchmark();double optimized=Benchmark();
                if(hits==0 || misses==0 || cache.Length!=512)throw new InvalidOperationException("Cache coverage failed.");
                File.WriteAllText(Path.Combine(DirectoryPath,"formula-selftest.txt"),string.Format(CultureInfo.InvariantCulture,
                    "PASS: 40000 result/exception comparisons against original three formulas; bit-exact returned values; defaults, changed inputs, NaN/infinity, collisions/eviction; bounded per-thread cache.\noriginalHotSetMilliseconds={0:R}\noptimizedHotSetMilliseconds={1:R}\nhits={2}\nmisses={3}\n",original,optimized,hits,misses));
            }
            finally {Remove();}
        }
        private struct Outcome {public BigDouble Value;public Type Error;}
        private static Outcome Capture(int i)
        {
            try{return new Outcome{Value=Evaluate(i)};}
            catch(ArithmeticException error){return new Outcome{Error=error.GetType()};}
        }
    }
}
