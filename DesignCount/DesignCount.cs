using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;
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
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.HousekeepingGuard2.dll")).GetType("IdleSpiralSpeedPatch.Loader").GetMethod("AutoInstall").Invoke(null,null);
            Install();
        }
        private static void Invoke(string name)
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
            try{typeof(DesignCount).GetMethod(name).Invoke(null,null);File.Delete(Path.Combine(DirectoryPath,"designcount-error.txt"));}
            catch(Exception e){File.WriteAllText(Path.Combine(DirectoryPath,"designcount-error.txt"),e.ToString());throw;}
        }
    }
    public static class DesignCount
    {
        private const string Owner="local.idlespiral.openspeedy.designcount";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly MethodInfo Target=AccessTools.Method(typeof(DLCManager),"CountVisibleUnlockedDesignsForRuntime",
            new[]{typeof(bool[]),typeof(string),typeof(bool),typeof(bool),typeof(bool),typeof(RuntimePlatform)});
        [ThreadStatic]private static Entry cache;
        private static long hits,misses;
        private sealed class Entry
        {
            public bool[] Flags;
            public string Identifier;
            public bool Development,Editor,Entitlement;
            public RuntimePlatform Platform;
            public int Value;
        }
        public static void Install()
        {
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(typeof(DLCManager).Assembly.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=
                    "caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b")throw new InvalidOperationException("Unsupported design-count assembly.");
            if(HarmonyLib.Harmony.GetPatchInfo(Target)?.Owners.Contains(Owner)!=true)
                Harmony.Patch(Target,prefix:new HarmonyMethod(typeof(DesignCount),nameof(Lookup)),postfix:new HarmonyMethod(typeof(DesignCount),nameof(Store)));
            Status("installed");
        }
        private static bool Same(bool[] values,bool[] saved)
        {
            if(values==null || saved==null)return values==null&&saved==null;
            if(values.Length!=saved.Length)return false;
            for(int i=0;i<values.Length;i++)if(values[i]!=saved[i])return false;
            return true;
        }
        private static bool Lookup(bool[] __0,string __1,bool __2,bool __3,bool __4,RuntimePlatform __5,ref int __result,out bool __state)
        {
            var entry=cache;
            __state=entry==null || entry.Identifier!=__1 || entry.Development!=__2 || entry.Editor!=__3 || entry.Entitlement!=__4 || entry.Platform!=__5 || !Same(__0,entry.Flags);
            if(__state){misses++;return true;}
            __result=entry.Value;hits++;
            if((hits&65535)==0)Status("running");
            return false;
        }
        private static void Store(bool[] __0,string __1,bool __2,bool __3,bool __4,RuntimePlatform __5,int __result,bool __state,bool __runOriginal)
        {
            if(!__state || !__runOriginal)return;
            cache=new Entry{Flags=__0==null?null:(bool[])__0.Clone(),Identifier=__1,Development=__2,Editor=__3,Entitlement=__4,Platform=__5,Value=__result};
        }
        public static void Remove(){Harmony.Unpatch(Target,HarmonyPatchType.All,Owner);Status("removed");}
        private static void Status(string state)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(DesignCount).Assembly.Location),"designcount-status.txt"),
                "state="+state+"\nhits="+hits+"\nmisses="+misses+"\ncompleteBooleanArrayCompared=true\nentitlementQueriesPreserved=true\n");
        }
        private static object[] Input(int i)
        {
            int value=i/4;var flags=value%17==0?null:new bool[value%5==0?7:Enum.GetValues(typeof(DesignID)).Length+3];
            if(flags!=null)for(int j=0;j<flags.Length;j++)flags[j]=(value+j)%3!=0;
            return new object[]{flags,value%7==0?null:value%5==0?"qa.fixture":"com.idlesystem.idlespiral",value%2==0,value%3==0,value%5==0,
                value%2==0?RuntimePlatform.WindowsPlayer:RuntimePlatform.Android};
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Isolated host only.");
            const int count=20000;var expected=new int[count];
            for(int i=0;i<count;i++)expected[i]=(int)Target.Invoke(null,Input(i));
            Install();Install();
            try
            {
                for(int i=0;i<count;i++)if((int)Target.Invoke(null,Input(i))!=expected[i])throw new InvalidOperationException("Design count differs at "+i);
                var input=Input(9);var flags=(bool[])input[0];Target.Invoke(null,input);long previous=misses;
                flags[0]=!flags[0];Target.Invoke(null,input);
                if(misses!=previous+1 || hits==0)throw new InvalidOperationException("Content mutation not detected.");
            }
            finally{Remove();}
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(DesignCount).Assembly.Location),"designcount-selftest.txt"),
                "PASS: 20000 exact original/patched counts; null/short/long arrays; identical replacement arrays; in-place mutation; identifier/platform/development/editor/entitlement changes; cache hits/misses; own hook removal. No ownership result or Steam API is patched.\n");
        }
    }
}
