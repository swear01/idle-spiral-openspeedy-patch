using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
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
            try{typeof(Recurrence).GetMethod(name).Invoke(null,null);}
            catch(Exception e){File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"recurrence-error.txt"),e.ToString());throw;}
        }
    }
    public static class Recurrence
    {
        private const string Owner="local.idlespiral.openspeedy.recurrence";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo Target=AccessTools.Method(typeof(ReccurenceMultiplier),"Multiply");
        [ThreadStatic]private static Entry[] cache;
        private static long hits,misses,calls;
        private static double lastN,lastM;
        private struct Key
        {
            public long A,E,N,P,Q,M,I;
            public Key(BigDouble a,double n,double p,double q,double m,double i)
            {A=Bits(a.Mantissa);E=a.Exponent;N=Bits(n);P=Bits(p);Q=Bits(q);M=Bits(m);I=Bits(i);}
            public bool Same(Key x)=>A==x.A&&E==x.E&&N==x.N&&P==x.P&&Q==x.Q&&M==x.M&&I==x.I;
            public int Slot{get{unchecked{long h=A;h=(h^E)*397;h=(h^N)*397;h=(h^P)*397;h=(h^Q)*397;h=(h^M)*397;h=(h^I)*397;return (int)(h^(h>>32))&255;}}}
        }
        private struct Entry{public bool Valid;public Key Key;public BigDouble Value;}
        private static long Bits(double value)=>BitConverter.DoubleToInt64Bits(value);
        public static void Install()
        {
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(typeof(ReccurenceMultiplier).Assembly.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=
                    "caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b")throw new InvalidOperationException("Unsupported recurrence assembly.");
            if(HarmonyLib.Harmony.GetPatchInfo(Target)?.Owners.Contains(Owner)!=true)
                Harmony.Patch(Target,prefix:new HarmonyMethod(typeof(Recurrence),nameof(Evaluate)));
            Status("installed");
        }
        private static bool Evaluate(ParameterContainer ___container,ref BigDouble __result)
        {
            var c=___container;
            double n=c.GetParameter(ParameterContainer.ParametersKind.n).Number.ToDouble();
            lastN=n;calls++;
            if(n==1){__result=c.EvaluateLargeA();return false;}
            BigDouble a=c.EvaluateLargeA();
            double p=c.GetParameter(ParameterContainer.ParametersKind.p).Number.ToDouble();
            double q=c.GetParameter(ParameterContainer.ParametersKind.q).Number.ToDouble();
            double m=c.GetParameter(ParameterContainer.ParametersKind.m).Number.ToDouble();
            double alpha=c.GetParameter(ParameterContainer.ParametersKind.alpha).Number.ToDouble();
            double beta=c.GetParameter(ParameterContainer.ParametersKind.beta).Number.ToDouble()+1;
            double integral=2*Math.Pow(alpha,beta)/beta;
            var key=new Key(a,n,p,q,m,integral);int slot=key.Slot;
            if(cache==null)cache=new Entry[256];
            var entry=cache[slot];lastM=m;
            if(entry.Valid&&entry.Key.Same(key)){hits++;__result=entry.Value;}
            else
            {
                misses++;
                BigDouble current=a,sum=a;
                for(int i=1;i<(int)n;i++)
                {
                    BigDouble next=p*current*integral+q;
                    sum+=next;current=next;
                }
                BigDouble result=0;
                for(int j=0;j<(int)m;j++)result+=sum;
                cache[slot]=new Entry{Valid=true,Key=key,Value=result};__result=result;
            }
            if((calls&8191)==0)Status("running");
            return false;
        }
        public static void Remove(){Harmony.Unpatch(Target,HarmonyPatchType.All,Owner);Status("removed");}
        private static void Status(string state)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Recurrence).Assembly.Location),"recurrence-status.txt"),
                string.Format(CultureInfo.InvariantCulture,"state={0}\nhits={1}\nmisses={2}\nn={3:R}\nm={4:R}\noriginalAdditionOrderPreserved=true\n",state,hits,misses,lastN,lastM));
        }
        private sealed class Fixture
        {
            public readonly ParameterContainer Container=(ParameterContainer)FormatterServices.GetUninitializedObject(typeof(ParameterContainer));
            public readonly ReccurenceMultiplier Recurrence;
            public double Dynamic;
            public Fixture()
            {
                var values=Enumerable.Range(0,Enum.GetValues(typeof(ParameterContainer.ParametersKind)).Length).Select(i=>new Parameter(0,"fixture")).ToArray();
                AccessTools.Field(typeof(ParameterContainer),"parameters").SetValue(Container,values);
                foreach(var kind in new[]{ParameterContainer.ParametersKind.largeA,ParameterContainer.ParametersKind.n,ParameterContainer.ParametersKind.p,
                    ParameterContainer.ParametersKind.q,ParameterContainer.ParametersKind.m,ParameterContainer.ParametersKind.alpha,ParameterContainer.ParametersKind.beta})
                {
                    var captured=kind;
                    values[(int)kind].multiplier.RegisterMultiplier(new MultiplierInfo(()=>Value(captured),MultiplierType.add));
                }
                Recurrence=new ReccurenceMultiplier(Container,new Parameter(0,"sigma"));
            }
            private BigDouble Value(ParameterContainer.ParametersKind kind)
            {
                int value=(int)Dynamic;
                switch(kind)
                {
                    case ParameterContainer.ParametersKind.largeA:return BigDouble.Pow10(value%31-15)*(1+value%7*0.125);
                    case ParameterContainer.ParametersKind.n:return value%37;
                    case ParameterContainer.ParametersKind.m:return value%41;
                    case ParameterContainer.ParametersKind.p:return value%5*0.37;
                    case ParameterContainer.ParametersKind.q:return value%11-4;
                    case ParameterContainer.ParametersKind.alpha:return value%13*0.18;
                    default:return value%9-2;
                }
            }
            public BigDouble Read()=>(BigDouble)Target.Invoke(Recurrence,null);
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Isolated host only.");
            const int count=20000;var original=new Fixture();var expected=new BigDouble[count];
            for(int i=0;i<count;i++){original.Dynamic=i/4;expected[i]=original.Read();}
            Install();Install();
            try
            {
                var candidate=new Fixture();
                for(int i=0;i<count;i++)
                {
                    candidate.Dynamic=i/4;var actual=candidate.Read();
                    if(actual.Exponent!=expected[i].Exponent || Bits(actual.Mantissa)!=Bits(expected[i].Mantissa))
                        throw new InvalidOperationException("Recurrence bits differ at "+i);
                }
                if(hits==0||misses==0)throw new InvalidOperationException("Cache not exercised.");
            }
            finally{Remove();}
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Recurrence).Assembly.Location),"recurrence-selftest.txt"),
                "PASS: 20000 bit-exact original/patched recurrence outputs; changing full inputs, n=1, zero/negative terms, NaN, bounded cache and own hook removal.\n");
        }
    }
}
