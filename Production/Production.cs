using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using BreakInfinity;
using HarmonyLib;
using IdleLibrary;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install(){Invoke("Install");}
        public static void Remove(){Invoke("Remove");}
        public static void Test(){Invoke("Test");}
        private static void Invoke(string name)
        {
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"0Harmony.dll"));
            try { typeof(Production).GetMethod(name).Invoke(null,null); }
            catch(Exception e){File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"production-error.txt"),e.ToString());throw;}
        }
    }
    public static class Production
    {
        private const string Owner="local.idlespiral.openspeedy.production";
        private static readonly bool Audit=false;
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo Number=AccessTools.PropertyGetter(typeof(Parameter),"Number");
        private static readonly MethodInfo[] Scopes={AccessTools.Method(typeof(DistanceDomain),"CalculateIncrementPerSecond"),
            AccessTools.Method(typeof(R),"CalculateIncrementPerSecond"),AccessTools.Method(typeof(ReccurenceMultiplier),"Multiply")};
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static Dictionary<Parameter,BigDouble> cache;
        private static long scopes,hits,misses,mismatches;
        private static bool installed;
        public static void Install()
        {
            if(installed)return;
            using(var sha=SHA256.Create()) using(var stream=File.OpenRead(typeof(Parameter).Assembly.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=
                    "caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b")throw new InvalidOperationException("Unsupported production assembly.");
            try
            {
                foreach(var scope in Scopes)Harmony.Patch(scope,prefix:new HarmonyMethod(typeof(Production),nameof(Begin)),finalizer:new HarmonyMethod(typeof(Production),nameof(End)));
                Harmony.Patch(Number,prefix:new HarmonyMethod(typeof(Production),nameof(Read)),postfix:new HarmonyMethod(typeof(Production),nameof(Store)));
                installed=true;Status("installed");
            }
            catch{Remove();throw;}
        }
        private static void Begin(out bool __state)
        {
            __state=true;
            if(depth++==0){if(cache==null)cache=new Dictionary<Parameter,BigDouble>(128);else cache.Clear();scopes++;}
        }
        private static void End(bool __state)
        {
            if(!__state || --depth!=0)return;
            cache.Clear();
            if((scopes&4095)==0)Status("running");
        }
        private static bool Read(Parameter __instance,ref BigDouble __result,out bool __state)
        {
            __state=false;
            if(depth==0)return true;
            if(cache.TryGetValue(__instance,out var value))
            {
                hits++;
                if(Audit)return true;
                __result=value;return false;
            }
            misses++;__state=true;return true;
        }
        private static void Store(Parameter __instance,BigDouble __result,bool __state)
        {
            if(depth==0)return;
            if(__state)cache[__instance]=__result;
            else if(Audit && cache.TryGetValue(__instance,out var old) && !old.Equals(__result))mismatches++;
        }
        public static void Remove()
        {
            foreach(var target in Scopes.Concat(new[]{Number}))Harmony.Unpatch(target,HarmonyPatchType.All,Owner);
            installed=false;Status("removed");
        }
        private static void Status(string state)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Production).Assembly.Location),"production-status.txt"),
                "state="+state+"\naudit="+Audit+"\nscopes="+scopes+"\nhits="+hits+"\nmisses="+misses+"\nmismatches="+mismatches+"\ncacheCrossesResourceWrites=false\n");
        }
        private sealed class Fixture
        {
            public double Dynamic;
            public bool Fail;
            public readonly Parameter Input=new Parameter(2,"input");
            public readonly Parameter Shared=new Parameter(1,"shared");
            public readonly DistanceDomain Distance;
            public readonly DTO Data;
            public Fixture(bool r)
            {
                Data=(DTO)FormatterServices.GetUninitializedObject(typeof(DTO));
                Data.bigDTO=(BigDTO)FormatterServices.GetUninitializedObject(typeof(BigDTO));
                Data.bigDTO.r=new CurrencyDTO();
                Distance=r?(DistanceDomain)new R(Data):new DistanceDomain();
                Input.produceMultiplier.RegisterMultiplier(new MultiplierInfo(()=>Dynamic,MultiplierType.add));
                Input.multiplier.RegisterMultiplier(new MultiplierInfo(()=>{if(Fail)throw new InvalidOperationException("fixture");return Dynamic;},MultiplierType.add));
                Shared.multiplier.RegisterMultiplier(new MultiplierInfo(()=>Input.Number*Input.Number+Distance.Number,MultiplierType.add));
                Shared.cappedExponentialMultiplier.RegisterMultiplier(new MultiplierInfo(()=>0.2,MultiplierType.add));
                Shared.cappedExponentialOrderCap.RegisterMultiplier(new MultiplierInfo(()=>3,MultiplierType.add));
                Distance.DistanceIncrementPerSecondMultiplier.RegisterMultiplier(new MultiplierInfo(()=>Shared.Number*Input.Number+Shared.Number,MultiplierType.add));
                Distance.IndependentDistanceIncrementPerSecondMultiplier.RegisterMultiplier(new MultiplierInfo(()=>Input.Number+Shared.Number,MultiplierType.add));
            }
            public void Change(int i)
            {
                Dynamic=0.1+i%53;
                Data.challengeKind=i%3==0?ChallengeKind.Logarithm:default(ChallengeKind);
                Data.li_defeated_while_log=i%37;
                Input.ProduceBySecond(0.001);
            }
            public BigDouble[] Snapshot()=>new[]{Distance.Number,Distance.TotalNumber,Distance.MaxNumber,Distance.MaxRPSReached};
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Isolated host only.");
            const int count=10000;
            var expected=new BigDouble[2][][];
            for(int type=0;type<2;type++)
            {
                var baseline=new Fixture(type==1);expected[type]=new BigDouble[count][];
                for(int i=0;i<count;i++){baseline.Change(i);baseline.Distance.ProduceBySecond(0.001);expected[type][i]=baseline.Snapshot();}
            }
            Install();Install();
            for(int type=0;type<2;type++)
            {
                var candidate=new Fixture(type==1);
                for(int i=0;i<count;i++)
                {
                    candidate.Change(i);candidate.Distance.ProduceBySecond(0.001);
                    if(!candidate.Snapshot().SequenceEqual(expected[type][i]))throw new InvalidOperationException("Production state mismatch at "+type+":"+i);
                }
                candidate.Fail=true;
                try{candidate.Distance.IncrementPerSecond();throw new Exception("Expected exception.");}
                catch(InvalidOperationException e){if(e.Message!="fixture")throw;}
                if(depth!=0 || cache.Count!=0)throw new InvalidOperationException("Exception scope leaked.");
            }
            var parameter=new Parameter(3,"input");
            for(int i=0;i<10000;i++)
            {
                parameter.ProduceBySecond(0.02);
                Begin(out var outer);
                try
                {
                    var repeated=parameter.Number;
                    Begin(out var inner);
                    try{if(!parameter.Number.Equals(repeated))throw new InvalidOperationException("Nested scope changed value.");}
                    finally{End(inner);}
                    if(!parameter.Number.Equals(repeated))throw new InvalidOperationException("Repeated value changed.");
                }
                finally{End(outer);}
                if(depth!=0 || cache.Count!=0)throw new InvalidOperationException("Scope leaked.");
            }
            if(hits<10000 || mismatches!=0)throw new InvalidOperationException("Cache hooks not exercised.");
            Remove();
            if(Scopes.Concat(new[]{Number}).Any(m=>HarmonyLib.Harmony.GetPatchInfo(m)?.Owners.Contains(Owner)==true))throw new InvalidOperationException("Removal failed.");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Production).Assembly.Location),"production-selftest.txt"),"PASS: exact original/patched four-field production state for 20000 steps; R logarithmic challenge and changing inputs; capped exponents; dependency on just-produced currency; exception cleanup; nested scopes; 10000 scope boundaries; own hook removal. Full gameplay needs live validation.\n");
        }
    }
}
