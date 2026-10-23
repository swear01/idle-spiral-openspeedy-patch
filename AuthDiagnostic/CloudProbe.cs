using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    public static class CloudProbe
    {
        private const string Owner = "local.idlespiral.openspeedy.auth-diagnostic";
        private static readonly string Output = Path.Combine(Path.GetDirectoryName(typeof(CloudProbe).Assembly.Location), "cloud-probe.txt");
        private static readonly Harmony Harmony = new Harmony(Owner);
        private static int _pending;
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static object Property(object value, string name) => value?.GetType().GetProperty(name, Flags)?.GetValue(value, null);
        private static object Field(object value, string name) => value?.GetType().GetField(name, Flags)?.GetValue(value);
        public static void Schedule()
        {
            File.WriteAllText(Output, "state=scheduled\n");
            _pending = 1;
            Harmony.Patch(AccessTools.Method(AccessTools.TypeByName("GameSystem"), "Update"), postfix: new HarmonyMethod(typeof(CloudProbe), nameof(OnUpdate)));
        }
        private static void OnUpdate()
        {
            if (Interlocked.Exchange(ref _pending, 0) == 0) return;
            Harmony.Unpatch(AccessTools.Method(AccessTools.TypeByName("GameSystem"), "Update"), HarmonyPatchType.Postfix, Owner);
            Run();
        }
        private static async void Run()
        {
            try
            {
                var main = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Assembly-CSharp");
                var manager = main.GetType("IdleSpiralCloudRestoreBootstrap").GetField("registeredManager", Flags).GetValue(null);
                var auth = Property(Property(manager, "Services"), "Authentication");
                var previous = Property(auth, "CurrentUser");
                var identity = Property(previous, "UserId") as string;
                File.AppendAllText(Output, "state=refreshing-via-original-sdk\n");
                var refresh = (Task)auth.GetType().GetMethod("EnsureValidTokenAsync").Invoke(auth, null);
                await refresh;
                var result = Property(refresh, "Result");
                bool success = Property(result, "IsSucc") is bool valid && valid;
                File.AppendAllText(Output, "originalRefreshSucceeded=" + success + "\n");
                if (!success) return;
                var current = Property(auth, "CurrentUser");
                File.AppendAllText(Output, "sameAccount=" + string.Equals(identity, Property(current, "UserId") as string, StringComparison.Ordinal)
                    + "\ncurrentTokenValid=" + Property(current, "IsValid") + "\n");
                var storage = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "IdleSystem.Storage");
                var type = storage.GetType("IdleSystem.Storage.VfsBundleCloudSaveService");
                var service = Activator.CreateInstance(type, auth, Field(auth, "_config"));
                var read = (Task)type.GetMethod("DownloadAsync").Invoke(service, null);
                await read;
                var readResult = Property(read, "Result");
                File.AppendAllText(Output, "readOnlyActualCloudBundleSucceeded=" + Property(readResult, "IsSucc") + "\n");
                if (Property(readResult, "IsFail") is bool failed && failed)
                {
                    foreach (var field in readResult.GetType().GetFields(Flags).Where(f => f.FieldType.FullName.Contains("Error")))
                    {
                        var error = field.GetValue(readResult);
                        if (error != null) File.AppendAllText(Output, "cloudErrorType=" + error.GetType().FullName + "\ncloudHttpStatus=" + Property(error, "StatusCode") + "\n");
                    }
                }
                var sdk = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "IdleSystem.CloudSave");
                var cloud = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "IdleSpiral.CloudSave");
                var store = Activator.CreateInstance(sdk.GetType("Core.Save.VfsBundleCloudStore"), service);
                var transport = Activator.CreateInstance(cloud.GetType("IdleLibrary.CloudSave.VfsBundleCrossPlatformCloudSaveTransport"), store);
                try
                {
                    var snapshotArgs = new object[] { null, null };
                    bool snapshotReady = (bool)cloud.GetType("IdleLibrary.CloudSave.LocalPersistenceRuntimeBridge").GetMethod("TryGetActiveSnapshot").Invoke(null, snapshotArgs);
                    if (!snapshotReady) throw new InvalidOperationException("No active snapshot");
                    var coordinator = Activator.CreateInstance(cloud.GetType("IdleLibrary.CloudSave.CrossPlatformCloudSaveCoordinator"), transport, "idle-spiral-shared-v1");
                    var inspect = (Task)coordinator.GetType().GetMethod("InspectAsync").Invoke(coordinator, new[] { snapshotArgs[0], (object)CancellationToken.None });
                    await inspect;
                    var inspection = Property(inspect, "Result");
                    File.AppendAllText(Output, "originalCloudInspectionSucceeded=" + (inspection != null)
                        + "\noriginalCloudRelation=" + Property(Property(inspection, "Comparison"), "Relation") + "\n");
                }
                finally { ((IDisposable)transport).Dispose(); }
                File.AppendAllText(Output, "state=completed\n");
            }
            catch (Exception error) { File.AppendAllText(Output, "state=failed\nerrorType=" + error.GetType().FullName + "\ninnerType=" + error.InnerException?.GetType().FullName + "\n"); }
        }
    }
}
