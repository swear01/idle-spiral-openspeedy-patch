using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using IdleSystem.Auth;
using Core.Save;
using Newtonsoft.Json.Linq;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        private static string DirectoryPath => Path.GetDirectoryName(typeof(Loader).Assembly.Location);
        public static void Install() { Invoke(nameof(AuthGuard.Install)); }
        public static void Remove() { Invoke(nameof(AuthGuard.Remove)); }
        public static void Test() { Invoke(nameof(AuthGuard.Test)); }
        public static void AutoInstall()
        {
            var calendar = Assembly.LoadFrom(Path.Combine(DirectoryPath, "IdleSpiral.CalendarGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader");
            calendar.GetMethod("Install").Invoke(null, null);
            Install();
            calendar.GetMethod("AutoInstall").Invoke(null, null);
        }
        private static void Invoke(string method)
        {
            try
            {
                Assembly.LoadFrom(Path.Combine(DirectoryPath, "0Harmony.dll"));
                Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.AuthGuard").GetMethod(method).Invoke(null, null);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(DirectoryPath, "auth-error.txt"), error.GetType().FullName + "\n" + error.InnerException?.GetType().FullName);
                throw;
            }
        }
    }

    public static class AuthGuard
    {
        private const string Owner = "local.idlespiral.openspeedy.auth";
        private static readonly Harmony Harmony = new Harmony(Owner);
        private static readonly string DirectoryPath = Path.GetDirectoryName(typeof(AuthGuard).Assembly.Location);
        private static readonly MethodInfo UtcGetter = typeof(DateTime).GetProperty("UtcNow").GetGetMethod();
        private static readonly MethodInfo ExpiryGetter = typeof(AuthToken).GetProperty("ExpirationTime").GetGetMethod();
        private static readonly MethodInfo[] Targets = {
            AccessTools.Method(typeof(AuthToken), "IsValidWithMargin"),
            AccessTools.Method(typeof(FirebaseAuthService), "SetupTokenRefreshTimer"),
            AccessTools.Method(typeof(FirebaseAuthService), "ConvertToAuthToken"),
            AccessTools.Method(typeof(CloudSaveManager), "ApplyToken")
        };
        private sealed class ExpiryCache { public string Token; public DateTime Expiry; }
        private static readonly ConditionalWeakTable<AuthToken, ExpiryCache> Expiries = new ConditionalWeakTable<AuthToken, ExpiryCache>();
        private static bool _installed;
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Millisecond; }
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern void GetSystemTime(out SystemTime time);
        public static DateTime UtcNow()
        {
            GetSystemTime(out var time);
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, time.Millisecond, DateTimeKind.Utc);
        }
        public static DateTime EffectiveExpiry(AuthToken token)
        {
            var entry = Expiries.GetValue(token, _ => new ExpiryCache());
            lock (entry)
            {
                if (entry.Token != token.IdToken)
                {
                    entry.Token = token.IdToken;
                    entry.Expiry = ReadExpiry(entry.Token);
                }
                var local = token.ExpirationTime.ToUniversalTime();
                return local < entry.Expiry ? local : entry.Expiry;
            }
        }
        private static DateTime ReadExpiry(string token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token) || token.Length > 65536) return DateTime.MinValue;
                var parts = token.Split('.');
                if (parts.Length != 3) return DateTime.MinValue;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                var exp = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))["exp"];
                if (exp == null || exp.Type != JTokenType.Integer) return DateTime.MinValue;
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((long)exp);
            }
            catch { return DateTime.MinValue; }
        }
        public static void Install()
        {
            if (_installed) return;
            Verify(typeof(AuthToken).Assembly, "c1e2a0f1c8352428ab8f4d9bb2bb02d380d48781c0499c02ef76baf0f6d05923");
            Verify(typeof(CloudSaveManager).Assembly, "1daee61b66847527ccb60415183c1c167fb4f416047f1591ffb1b94fadd8ca74");
            int clocks = 0, expiries = 0;
            foreach (var target in Targets)
            {
                if (target == null || HarmonyLib.Harmony.GetPatchInfo(target)?.Owners.Contains(Owner) == true)
                    throw new InvalidOperationException("Missing or already patched authentication method.");
                foreach (var instruction in PatchProcessor.GetOriginalInstructions(target))
                {
                    if (instruction.Calls(UtcGetter)) clocks++;
                    if (instruction.Calls(ExpiryGetter)) expiries++;
                }
            }
            if (clocks != 5 || expiries != 2) throw new InvalidOperationException("Unexpected authentication clock structure.");
            try
            {
                foreach (var target in Targets) Harmony.Patch(target, transpiler: new HarmonyMethod(typeof(AuthGuard), nameof(Rewrite)));
                _installed = true;
                File.WriteAllText(Path.Combine(DirectoryPath, "auth-status.txt"), "state=installed\nsource=GetSystemTime\nmethods=4\nclockCalls=5\nexpiryReads=2\nexpiryPolicy=earlier-of-local-and-jwt-exp; refresh-only; server-validation-unchanged\n");
                File.Delete(Path.Combine(DirectoryPath, "auth-error.txt"));
            }
            catch { Remove(); throw; }
        }
        public static void Remove()
        {
            foreach (var target in Targets) if (target != null) Harmony.Unpatch(target, HarmonyPatchType.Transpiler, Owner);
            _installed = false;
            File.WriteAllText(Path.Combine(DirectoryPath, "auth-status.txt"), "state=removed\n");
        }
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(UtcGetter) || instruction.Calls(ExpiryGetter))
                {
                    instruction.operand = AccessTools.Method(typeof(AuthGuard), instruction.Calls(UtcGetter) ? nameof(UtcNow) : nameof(EffectiveExpiry));
                    instruction.opcode = OpCodes.Call;
                }
                yield return instruction;
            }
        }
        private static void Verify(Assembly assembly, string expected)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(assembly.Location))
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != expected)
                    throw new InvalidOperationException("Unsupported authentication assembly: " + assembly.GetName().Name);
        }
        private static string TestToken(DateTime expiry)
        {
            long seconds = (long)(expiry - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + seconds + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test";
        }
        public static void Test()
        {
            if (Process.GetCurrentProcess().ProcessName != "IdleSpiral.MonoHost") throw new InvalidOperationException("Run authentication tests only in the isolated Mono host.");
            Install();
            try
            {
                var now = UtcNow();
                var token = new AuthToken { IdToken = TestToken(now.AddMinutes(-10)), ExpirationTime = now.AddDays(1) };
                if (token.IsValid) throw new Exception("Expired JWT accepted with future cached expiry.");
                token.IdToken = TestToken(now.AddHours(1));
                if (!token.IsValid) throw new Exception("Valid JWT rejected or mutated token cache stale.");
                token.ExpirationTime = now.AddMinutes(-1);
                if (token.IsValid) throw new Exception("Earlier local expiry ignored.");
                token.ExpirationTime = now.AddDays(1);
                token.IdToken = TestToken(now.AddMinutes(4));
                if (token.IsValid || !token.IsValidWithMargin(TimeSpan.Zero)) throw new Exception("Original refresh margin changed.");
                foreach (var invalid in new[] { null, "", "not-a-token", "e30.e30.test", "e30.eyJleHAiOiJub3QtbnVtYmVyIn0.test", "e30.eyJleHAiOjkyMjMzNzIwMzY4NTQ3NzU4MDd9.test" })
                {
                    token.IdToken = invalid;
                    if (token.IsValid) throw new Exception("Malformed expiry accepted.");
                }
                foreach (var target in Targets)
                    if (PatchProcessor.GetCurrentInstructions(target).Any(i => i.Calls(UtcGetter) || i.Calls(ExpiryGetter)))
                        throw new Exception("Unreplaced authentication clock.");
                File.WriteAllText(Path.Combine(DirectoryPath, "auth-selftest.txt"), "PASS: expired JWT with future cache; valid token; token mutation; earlier local expiry; original margin; malformed/missing/overflow expiry; four method rewrites; no account/network actions.\n");
            }
            finally { Remove(); }
        }
    }
}
