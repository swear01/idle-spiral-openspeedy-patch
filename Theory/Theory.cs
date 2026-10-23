using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using BreakInfinity;
using HarmonyLib;
using IdleLibrary;
using Theory;

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
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.RenderGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader")
                .GetMethod("AutoInstall").Invoke(null,null);
            Install();
        }
        private static void Invoke(string method)
        {
            try
            {
                Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
                Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.TheoryGuard").GetMethod(method).Invoke(null,null);
                File.Delete(Path.Combine(DirectoryPath,"theory-error.txt"));
            }
            catch(Exception error){File.WriteAllText(Path.Combine(DirectoryPath,"theory-error.txt"),error.ToString());throw;}
        }
    }
    public static class TheoryGuard
    {
        private const string Owner="local.idlespiral.openspeedy.theory";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo Update=AccessTools.Method(typeof(TheoryContainer),"UpdateTheories");
        private static readonly MethodInfo Number=AccessTools.PropertyGetter(typeof(Parameter),"Number");
        private static readonly List<MethodInfo> Targets=new List<MethodInfo>();
        private static string DirectoryPath=>Path.GetDirectoryName(typeof(TheoryGuard).Assembly.Location);
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static Parameter progress,addition;
        [ThreadStatic] private static BigDouble progressValue,additionValue;
        [ThreadStatic] private static bool hasProgress,hasAddition;
        private static bool installed;
        private static long hits,misses;
        public static void Install()
        {
            if(installed)return;
            using(var sha=SHA256.Create())
            using(var stream=File.OpenRead(typeof(TheoryContainer).Assembly.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=
                    "caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b")
                    throw new InvalidOperationException("Unsupported theory assembly.");
            Targets.Clear();
            foreach(var nested in new[]{typeof(TheoryContainer)}.Concat(typeof(TheoryContainer).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)))
                foreach(var method in nested.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
                    if(method.Name.StartsWith("<ApplyProduceLogic>") && method.ReturnType==typeof(BigDouble))
                    {
                        var instructions=PatchProcessor.GetOriginalInstructions(method);
                        if(instructions.Count(i=>i.Calls(Number))==1 && instructions.Any(i=>i.LoadsConstant((long)ParameterContainer.ParametersKind.study_progress) ||
                            i.LoadsConstant((long)ParameterContainer.ParametersKind.study_progress_add))) Targets.Add(method);
                    }
            if(Targets.Count!=2)throw new InvalidOperationException("Unexpected shared theory multiplier delegates.");
            try
            {
                Harmony.Patch(Update,prefix:new HarmonyMethod(typeof(TheoryGuard),nameof(Begin)),finalizer:new HarmonyMethod(typeof(TheoryGuard),nameof(End)));
                foreach(var method in Targets) Harmony.Patch(method,transpiler:new HarmonyMethod(typeof(TheoryGuard),nameof(Rewrite)));
                installed=true;
                Status("installed");
            }
            catch {Remove();throw;}
        }
        public static void Remove()
        {
            foreach(var method in Targets.Concat(new[]{Update})) Harmony.Unpatch(method,HarmonyPatchType.All,Owner);
            installed=false;
            Status("removed");
        }
        private static void Begin(ParameterContainer ___parameter,out bool __state)
        {
            __state=true;
            if(++depth!=1)return;
            progress=___parameter.GetParameter(ParameterContainer.ParametersKind.study_progress);
            addition=___parameter.GetParameter(ParameterContainer.ParametersKind.study_progress_add);
            hasProgress=hasAddition=false;
        }
        private static void End(bool __state)
        {
            if(!__state)return;
            if(--depth!=0)return;
            progress=addition=null;hasProgress=hasAddition=false;
        }
        public static BigDouble ReadNumber(Parameter parameter)
        {
            if(depth!=1)return parameter.Number;
            if(ReferenceEquals(parameter,progress))
            {
                if(hasProgress){hits++;return progressValue;}
                misses++;progressValue=parameter.Number;hasProgress=true;return progressValue;
            }
            if(ReferenceEquals(parameter,addition))
            {
                if(hasAddition){hits++;return additionValue;}
                misses++;additionValue=parameter.Number;hasAddition=true;return additionValue;
            }
            return parameter.Number;
        }
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if(instruction.Calls(Number))
                {
                    instruction.opcode=OpCodes.Call;
                    instruction.operand=AccessTools.Method(typeof(TheoryGuard),nameof(ReadNumber));
                }
                yield return instruction;
            }
        }
        public static void Status(){Status(installed?"running":"removed");}
        private static void Status(string state)
        {
            File.WriteAllText(Path.Combine(DirectoryPath,"theory-status.txt"),"state="+state+"\nversion=1-per-update-shared-theory-inputs\nhits="+hits+
                "\nmisses="+misses+"\ncacheCrossesSimulationSteps=false\n");
        }
        [DllImport("kernelbase.dll",ExactSpelling=true)] private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}
        private sealed class Fixture
        {
            public readonly DTO Data=(DTO)FormatterServices.GetUninitializedObject(typeof(DTO));
            public readonly ParameterContainer Parameters=(ParameterContainer)FormatterServices.GetUninitializedObject(typeof(ParameterContainer));
            public readonly TheoryContainer Container;
            public double Dynamic=1;
            public int Evaluations;
            public Fixture()
            {
                Data.bigDTO=(BigDTO)FormatterServices.GetUninitializedObject(typeof(BigDTO));
                Data.bigDTO.theoryDatas=Enumerable.Range(0,10).Select(i=>new TheoryData{level=i+1,canProgress=true}).ToArray();
                Data.bigDTO.theory_materials=new BigDouble[10];Data.bigDTO.currencyConsumed=new BigDouble[10];
                Data.TheoryUpgradeLevels=new long[10];
                var parameters=Enumerable.Range(0,Enum.GetValues(typeof(ParameterContainer.ParametersKind)).Length)
                    .Select(i=>new Parameter(i==79?1:0,"fixture-"+i)).ToArray();
                AccessTools.Field(typeof(ParameterContainer),"parameters").SetValue(Parameters,parameters);
                parameters[79].multiplier.RegisterMultiplier(new MultiplierInfo(()=>{Evaluations++;return Math.Sqrt(Dynamic)+1;},MultiplierType.mul));
                parameters[92].multiplier.RegisterMultiplier(new MultiplierInfo(()=>Dynamic*0.03,MultiplierType.add));
                Container=new TheoryContainer(Data,Parameters);
            }
            public void Change(int step)
            {
                Dynamic=1+step%97;
                Data.bigDTO.theoryDatas[0].level=1+step%50;
                Data.bigDTO.theoryDatas[0].canProgress=step%3!=0;
                Data.bigDTO.theory_materials[step%10]=step*0.7;
                Data.TheoryUpgradeLevels[step%10]=step%13;
            }
            public BigDouble[] Snapshot()=>Data.bigDTO.theoryDatas.Select(t=>t.exp).ToArray();
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Isolated host only.");
            const int count=10000;
            var baseline=new Fixture();var expected=new BigDouble[count][];
            ulong started=Now();
            for(int i=0;i<count;i++){baseline.Change(i);baseline.Container.UpdateTheories(i%2==0?0.02f:0.031f);expected[i]=baseline.Snapshot();}
            double originalMilliseconds=(Now()-started)/10000.0;
            Install();
            try
            {
                var candidate=new Fixture();started=Now();
                for(int i=0;i<count;i++)
                {
                    candidate.Change(i);candidate.Container.UpdateTheories(i%2==0?0.02f:0.031f);
                    var actual=candidate.Snapshot();
                    for(int j=0;j<actual.Length;j++)
                        if(actual[j].Exponent!=expected[i][j].Exponent || BitConverter.DoubleToInt64Bits(actual[j].Mantissa)!=BitConverter.DoubleToInt64Bits(expected[i][j].Mantissa))
                            throw new InvalidOperationException("Theory mismatch at "+i+"/"+j);
                }
                double optimizedMilliseconds=(Now()-started)/10000.0;
                if(candidate.Evaluations!=count || baseline.Evaluations<=candidate.Evaluations*8)throw new InvalidOperationException("Shared evaluation count failed.");
                candidate.Dynamic=135;
                var parameter=candidate.Parameters.GetParameter(ParameterContainer.ParametersKind.study_progress);
                if(ReadNumber(parameter)!=parameter.Number || depth!=0)throw new InvalidOperationException("Cache escaped update scope.");
                parameter.multiplier.RegisterMultiplier(new MultiplierInfo(()=>throw new InvalidOperationException("fixture-fault"),MultiplierType.add),"fixture-fault");
                try {candidate.Container.UpdateTheories();throw new Exception("Expected fixture fault.");}
                catch(InvalidOperationException error){if(error.Message!="fixture-fault")throw;}
                if(depth!=0 || progress!=null || addition!=null)throw new InvalidOperationException("Exception cleanup failed.");
                File.WriteAllText(Path.Combine(DirectoryPath,"theory-selftest.txt"),string.Format(CultureInfo.InvariantCulture,
                    "PASS: 10000 steps / 100000 bit-exact EXP comparisons against original; changing levels/materials/common input; paused first theory; mixed time steps; cache lifetime; exception cleanup.\noriginalMilliseconds={0:R}\noptimizedMilliseconds={1:R}\noriginalCommonEvaluations={2}\noptimizedCommonEvaluations={3}\n",originalMilliseconds,optimizedMilliseconds,baseline.Evaluations,count));
            }
            finally {Remove();}
        }
    }
}
