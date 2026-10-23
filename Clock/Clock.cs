using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using IdleLibrary.CloudSave;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install() { Invoke(nameof(Calendar.Install)); }
        public static void Remove() { Invoke(nameof(Calendar.Remove)); }
        public static void Test() { Invoke(nameof(Calendar.Test)); }
        public static void AutoInstall()
        {
            Install();
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location), "IdleSpiral.SpeedPatch.SteamGuard.dll"))
                .GetType("IdleSpiralSpeedPatch.Loader").GetMethod("AutoInstall").Invoke(null, null);
        }
        private static void Invoke(string method)
        {
            try
            {
                Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location), "0Harmony.dll"));
                Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.Calendar").GetMethod(method).Invoke(null, null);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location), "clock-error.txt"), error.ToString());
                throw;
            }
        }
    }

    public static class Calendar
    {
        private const string Owner = "local.idlespiral.openspeedy.calendar";
        private static readonly Harmony Harmony = new Harmony(Owner);
        public static readonly string DirectoryPath = Path.GetDirectoryName(typeof(Calendar).Assembly.Location);
        private static readonly List<MethodBase> Targets = new List<MethodBase>();
        private static readonly MethodInfo NowGetter = typeof(DateTime).GetProperty("Now").GetGetMethod();
        private static readonly MethodInfo UtcGetter = typeof(DateTime).GetProperty("UtcNow").GetGetMethod();
        private static readonly MethodInfo OffsetNowGetter = typeof(DateTimeOffset).GetProperty("Now").GetGetMethod();
        private static readonly MethodInfo OffsetUtcGetter = typeof(DateTimeOffset).GetProperty("UtcNow").GetGetMethod();
        private static readonly MethodInfo AutomaticSave = AccessTools.Method(typeof(IdleLibrary.saveCtrl), "BeginAutomaticSave");
        private sealed class SaveWindow { public bool Started; public ulong LastStart; }
        private static readonly ConditionalWeakTable<object, SaveWindow> SaveWindows = new ConditionalWeakTable<object, SaveWindow>();

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemTime
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Millisecond;
        }
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern void GetSystemTime(out SystemTime time);
        [DllImport("kernelbase.dll", ExactSpelling = true)]
        private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);

        public static DateTime UtcNow()
        {
            GetSystemTime(out SystemTime time);
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, time.Millisecond, DateTimeKind.Utc);
        }
        public static DateTime Now() { return UtcNow().ToLocalTime(); }
        public static DateTimeOffset OffsetNow() { return new DateTimeOffset(Now()); }
        public static DateTimeOffset OffsetUtcNow() { return new DateTimeOffset(UtcNow()); }

        public static void Install()
        {
            if (Targets.Count != 0) return;
            Verify(typeof(GameSystem).Assembly, "a67ee1d53fc2247d738ff2166f1a5e217bcb3fdebc3dfebf0a21393b8e5fbfbb");
            Verify(typeof(DailySpiral).Assembly, "caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b");
            Verify(typeof(IdleLibrary.LegacySaveBackupService).Assembly, "9e665da9e2b44503d98db50557dfefb5cd43c892ab47b8a7b8995535b3784a3e");
            Verify(typeof(LocalPersistenceRuntimeBridge).Assembly, "1b4c9c790e662ed09a6255c56c01a841865c2569882622ed9142db6b40a0970f");
            UtcNow();
            var candidates = new List<MethodBase>();
            foreach (var assembly in new[] { typeof(GameSystem).Assembly, typeof(DailySpiral).Assembly,
                typeof(IdleLibrary.LegacySaveBackupService).Assembly, typeof(LocalPersistenceRuntimeBridge).Assembly })
            foreach (var type in assembly.GetTypes())
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            {
                if (method.GetMethodBody() == null) continue;
                var reader = new DynamicMethod("ReadCalendarCalls", typeof(void), Type.EmptyTypes).GetILGenerator();
                if (!PatchProcessor.ReadMethodBody(method, reader).Any(i => (i.Key == OpCodes.Call || i.Key == OpCodes.Callvirt)
                    && (Equals(i.Value, NowGetter) || Equals(i.Value, UtcGetter) || Equals(i.Value, OffsetNowGetter) || Equals(i.Value, OffsetUtcGetter)))) continue;
                if (method.ContainsGenericParameters) throw new InvalidOperationException("Unsupported generic calendar call: " + method);
                candidates.Add(method);
            }
            if (candidates.Count != 53) throw new InvalidOperationException("Unexpected game calendar call sites: " + candidates.Count);
            foreach (var method in candidates.Concat(new[] { AutomaticSave }))
                if (HarmonyLib.Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) == true)
                    throw new InvalidOperationException("Remove the previous calendar patch before installing this version.");
            try
            {
                foreach (var method in candidates)
                {
                    Targets.Add(method);
                    Harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Calendar), nameof(Rewrite)));
                }
                Harmony.Patch(AutomaticSave, prefix: new HarmonyMethod(typeof(Calendar), nameof(BeforeAutomaticSave)),
                    postfix: new HarmonyMethod(typeof(Calendar), nameof(AfterAutomaticSave)));
                File.WriteAllText(Path.Combine(DirectoryPath, "clock-status.txt"), "state=installed\nsource=GetSystemTime\nutc=" + UtcNow().ToString("O")
                    + "\nautomaticSaveMinimumRealSeconds=15\nmethods=" + Targets.Count + "\n" + string.Join("\n", Targets.Select(m => m.DeclaringType.FullName + "." + m.Name)) + "\n");
                File.Delete(Path.Combine(DirectoryPath, "clock-error.txt"));
            }
            catch { Remove(); throw; }
        }

        public static void Remove()
        {
            foreach (var method in Targets) Harmony.Unpatch(method, HarmonyPatchType.Transpiler, Owner);
            Harmony.Unpatch(AutomaticSave, HarmonyPatchType.All, Owner);
            Targets.Clear();
            File.WriteAllText(Path.Combine(DirectoryPath, "clock-status.txt"), "state=removed\n");
        }

        private static bool IsClockCall(CodeInstruction instruction)
        {
            return instruction.Calls(NowGetter) || instruction.Calls(UtcGetter) || instruction.Calls(OffsetNowGetter) || instruction.Calls(OffsetUtcGetter);
        }
        private static bool SaveDue(SaveWindow window, ulong now) { return !window.Started || now - window.LastStart >= 150000000; }
        private static bool BeforeAutomaticSave(object __instance, ref bool __result)
        {
            QueryUnbiasedInterruptTimePrecise(out ulong now);
            if (SaveDue(SaveWindows.GetValue(__instance, _ => new SaveWindow()), now)) return true;
            __result = false;
            return false;
        }
        private static void AfterAutomaticSave(object __instance, bool __result)
        {
            if (!__result) return;
            QueryUnbiasedInterruptTimePrecise(out ulong now);
            var window = SaveWindows.GetValue(__instance, _ => new SaveWindow());
            window.Started = true;
            window.LastStart = now;
        }
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (IsClockCall(instruction))
                {
                    instruction.opcode = OpCodes.Call;
                    string replacement = instruction.Calls(NowGetter) ? nameof(Now) : instruction.Calls(UtcGetter) ? nameof(UtcNow)
                        : instruction.Calls(OffsetNowGetter) ? nameof(OffsetNow) : nameof(OffsetUtcNow);
                    instruction.operand = AccessTools.Method(typeof(Calendar), replacement);
                }
                yield return instruction;
            }
        }
        private static void Verify(Assembly assembly, string expected)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(assembly.Location))
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != expected)
                    throw new InvalidOperationException("Unsupported game assembly: " + assembly.GetName().Name);
        }

        public static void Test()
        {
            if (Targets.Count != 0) Remove();
            var existing = new DailySpiral(new DTO { dailyLastResetDate = 20990101 }, null);
            var boundClock = (Func<DateTime>)AccessTools.Field(typeof(DailySpiral), "_clock").GetValue(existing);
            boundClock();
            Install();
            try
            {
                var utc = UtcNow();
                if (utc.Kind != DateTimeKind.Utc || Now().Kind != DateTimeKind.Local) throw new Exception("Incorrect calendar kind.");
                if (Math.Abs((boundClock() - Now()).TotalSeconds) > 0.1) throw new Exception("Existing daily clock delegate was not patched.");
                foreach (var method in Targets)
                    if (PatchProcessor.GetCurrentInstructions(method).Any(IsClockCall)) throw new Exception("Unreplaced clock call: " + method);
                QueryUnbiasedInterruptTimePrecise(out ulong start);
                DateTime acceleratedStart = DateTime.UtcNow;
                DateTime realStart = UtcNow();
                ulong end;
                do { QueryUnbiasedInterruptTimePrecise(out end); } while (end - start < 10000000);
                double realElapsed = (UtcNow() - realStart).TotalSeconds;
                if (Math.Abs(realElapsed - (end - start) / 10000000.0) > 0.1) throw new Exception("Calendar is accelerated.");
                var dto = new DTO { dailyLastResetDate = 20990101, daily_counted = 2 };
                var daily = new DailySpiral(dto, null, Now, () => 123, () => false);
                if (daily.EnsureCurrentDay() || dto.daily_counted != 2 || dto.dailyLastResetDate != 20990101)
                    throw new Exception("Future-day protection changed.");
                var ordinary = new DailySpiral(new DTO(), null, () => new DateTime(2026, 10, 20), () => 123, () => false);
                if (ordinary.TryLeft != 3) throw new Exception("Explicit game clock injection changed.");
                var window = new SaveWindow();
                if (!SaveDue(window, 0)) throw new Exception("First automatic save blocked.");
                window.Started = true;
                window.LastStart = 100;
                if (SaveDue(window, 150000099) || !SaveDue(window, 150000100)) throw new Exception("Automatic save real interval is wrong.");
                if (HarmonyLib.Harmony.GetPatchInfo(AccessTools.Method(typeof(IdleLibrary.saveCtrl), "SaveImmediately"))?.Prefixes.Any(p => p.owner == Owner) == true)
                    throw new Exception("Explicit saves were patched.");
                File.WriteAllText(Path.Combine(DirectoryPath, "clock-selftest.txt"), "PASS: all game calendar call sites replaced; existing daily delegate; DateTime itself unchanged; kinds; real clock under current hooks; future date and explicit clock preserved; real autosave interval; explicit save entry untouched.\nmethods="
                    + Targets.Count + "; realSeconds=" + realElapsed + "; processSeconds=" + (DateTime.UtcNow - acceleratedStart).TotalSeconds + "\n");
            }
            finally { Remove(); }
        }
    }
}
