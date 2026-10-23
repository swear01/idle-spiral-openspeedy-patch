using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        private static string DirectoryPath => Path.GetDirectoryName(typeof(Loader).Assembly.Location);
        public static void Install() { Invoke("Install"); }
        public static void Remove() { Invoke("Remove"); }
        public static void Test() { Invoke("Test"); }
        public static void AutoInstall()
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.AdaptiveGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader")
                .GetMethod("AutoInstall").Invoke(null,null);
            Install();
        }
        private static void Invoke(string method)
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
            Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.RenderGuard").GetMethod(method).Invoke(null,null);
        }
    }

    public static class RenderGuard
    {
        private const string Owner="local.idlespiral.openspeedy.render";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly object SuppressionOwner=new object();
        private static readonly string DirectoryPath=Path.GetDirectoryName(typeof(RenderGuard).Assembly.Location);
        private static readonly MethodInfo Update=AccessTools.Method(typeof(GameSystem),"Update");
        private static volatile bool requested;
        private static CreateSpirals applied;
        private static long frames;

        public static void Install()
        {
            Verify(typeof(GameSystem).Assembly,"a67ee1d53fc2247d738ff2166f1a5e217bcb3fdebc3dfebf0a21393b8e5fbfbb");
            Verify(typeof(IdleSpiralSystem).Assembly,"caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b");
            requested=true;
            if(HarmonyLib.Harmony.GetPatchInfo(Update)?.Owners.Contains(Owner)!=true)
                Harmony.Patch(Update,postfix:new HarmonyMethod(typeof(RenderGuard),nameof(OnFrame)));
            Status("scheduled");
        }
        public static void Remove()
        {
            requested=false;
            Status("restore-scheduled");
        }
        private static void OnFrame()
        {
            try
            {
                if(!requested)
                {
                    Release();
                    FinishRemove();
                    return;
                }
                var current=CreateSpirals.Instance;
                if(!ReferenceEquals(current,applied))
                {
                    Release();
                    if(current!=null)
                    {
                        applied=current;
                        applied.SetRenderingSuppressed(SuppressionOwner,true);
                        Status("running");
                    }
                }
                if((++frames&255)==0) Status(ReferenceEquals(applied,null)?"waiting-for-spiral":"running");
            }
            catch(Exception error)
            {
                requested=false;
                File.WriteAllText(Path.Combine(DirectoryPath,"render-error.txt"),error.ToString());
                try { Release(); } finally { FinishRemove(); }
            }
        }
        private static void Release()
        {
            if(applied!=null) applied.SetRenderingSuppressed(SuppressionOwner,false);
            applied=null;
        }
        private static void FinishRemove()
        {
            Harmony.Unpatch(Update,HarmonyPatchType.All,Owner);
            Status("removed");
        }
        private static void Status(string state)
        {
            File.WriteAllText(Path.Combine(DirectoryPath,"render-status.txt"),
                "state="+state+"\nversion=1-native-spiral-suppression\nframes="+frames+
                "\nmainSpiralSuppressed="+(!ReferenceEquals(applied,null) && applied.IsRenderingSuppressed)+
                "\nmainSpiralShouldUpdateVisuals="+(!ReferenceEquals(applied,null) && applied.ShouldUpdateVisuals)+
                "\nthetaAndAchievementUpdatesPreserved=true\n");
        }
        private static void Verify(Assembly assembly,string expected)
        {
            using(var sha=SHA256.Create())
            using(var stream=File.OpenRead(assembly.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=expected)
                    throw new InvalidOperationException("Unsupported game assembly: "+assembly.GetName().Name);
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost") throw new InvalidOperationException("Isolated host only.");
            Install(); Install();
            var info=HarmonyLib.Harmony.GetPatchInfo(Update);
            if(info.Postfixes.Count(p=>p.owner==Owner)!=1 || info.Prefixes.Any(p=>p.owner==Owner) || info.Transpilers.Any(p=>p.owner==Owner))
                throw new InvalidOperationException("Unexpected render hooks.");
            foreach(var method in new[]{AccessTools.Method(typeof(LineDraw),"Update"),AccessTools.Method(typeof(Spiral),"AdvanceVertex"),
                AccessTools.Method(typeof(A_TotalTheta),"NotifyAchievements"),AccessTools.Method(typeof(IdleSpiralSystem),"Advance")})
                if(HarmonyLib.Harmony.GetPatchInfo(method)?.Owners.Contains(Owner)==true) throw new InvalidOperationException("Gameplay method patched.");
            Remove();
            if(HarmonyLib.Harmony.GetPatchInfo(Update)?.Owners.Contains(Owner)!=true) throw new InvalidOperationException("Removal must wait for the main thread.");
            FinishRemove();
            if(HarmonyLib.Harmony.GetPatchInfo(Update)?.Owners.Contains(Owner)==true) throw new InvalidOperationException("Hook removal failed.");
            File.WriteAllText(Path.Combine(DirectoryPath,"render-selftest.txt"),"PASS: supported assembly hashes; idempotent single main-thread postfix; no simulation/theta/achievement hooks; own hook removal. Unity rendering requires live validation.\n");
        }
    }
}
