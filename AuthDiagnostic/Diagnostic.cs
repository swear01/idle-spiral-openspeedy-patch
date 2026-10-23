using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Millisecond; }
        [DllImport("kernel32.dll")] private static extern void GetSystemTime(out SystemTime time);
        private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static object Field(object obj, string name) => obj?.GetType().GetField(name, Fields)?.GetValue(obj);
        private static object Property(object obj, string name) => obj?.GetType().GetProperty(name, Fields)?.GetValue(obj, null);
        public static void Test()
        {
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location), "0Harmony.dll"));
            Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.CloudProbe").GetMethod("Schedule").Invoke(null, null);
        }
        public static void Install()
        {
            var output = new StringBuilder();
            try
            {
                GetSystemTime(out var time);
                output.AppendLine("realUtc=" + new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, time.Millisecond, DateTimeKind.Utc).ToString("O"));
                output.AppendLine("processUtc=" + DateTime.UtcNow.ToString("O"));
                var main = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Assembly-CSharp");
                var manager = main.GetType("IdleSpiralCloudRestoreBootstrap").GetField("registeredManager", Fields).GetValue(null)
                    ?? main.GetType("AccountFailurePresentation").GetField("observed", Fields).GetValue(null);
                output.AppendLine("managerPresent=" + (manager != null));
                if (manager != null)
                {
                    foreach (var name in new[] { "AuthenticationState", "IsSignedIn", "IsAuthenticationPreparing", "IsAccountLinkInProgress", "IsChangingIdentity", "IsCloudMode" })
                        output.AppendLine(name + "=" + Property(manager, name));
                    var failure = Property(manager, "LastAccountLinkFailure");
                    foreach (var name in new[] { "Provider", "Stage", "Reason", "DiagnosticCode", "RecoveryAction" })
                        output.AppendLine("failure." + name + "=" + Property(failure, name));
                    var error = Property(manager, "LastAuthenticationError") as string;
                    output.AppendLine("authenticationErrorPresent=" + !string.IsNullOrEmpty(error));
                    output.AppendLine("authenticationErrorCategory=" + (string.IsNullOrEmpty(error) ? "none" : error.IndexOf("refresh", StringComparison.OrdinalIgnoreCase) >= 0 ? "refresh" : error.IndexOf("network", StringComparison.OrdinalIgnoreCase) >= 0 ? "network" : "other"));
                    output.AppendLine("googleFlowPresent=" + (Field(manager, "_googleSignInFlow") != null));
                    var preparation = Field(manager, "_authenticationPreparationTask");
                    output.AppendLine("preparationTaskStatus=" + Property(preparation, "Status"));
                    var auth = Property(Property(manager, "Services"), "Authentication");
                    var token = Property(auth, "CurrentUser");
                    output.AppendLine("tokenPresent=" + (token != null));
                    output.AppendLine("tokenExpiration=" + (Property(token, "ExpirationTime") is DateTime expiry ? expiry.ToString("O") : "none"));
                    output.AppendLine("tokenValid=" + Property(token, "IsValid"));
                    output.AppendLine("tokenAccountType=" + Property(token, "AccountType"));
                    var encoded = Property(token, "IdToken") as string;
                    if (!string.IsNullOrEmpty(encoded) && encoded.Split('.').Length == 3)
                    {
                        var payload = encoded.Split('.')[1].Replace('-', '+').Replace('_', '/');
                        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                        var match = Regex.Match(json, "\"exp\"\\s*:\\s*(\\d+)");
                        if (match.Success)
                            output.AppendLine("jwtExpiration=" + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(long.Parse(match.Groups[1].Value)).ToString("O"));
                        var project = Field(manager, "_projectId") as string ?? "idle-factory-hub";
                        var audience = Regex.Match(json, "\"aud\"\\s*:\\s*\"([^\"]+)\"");
                        output.AppendLine("jwtAudienceMatchesProject=" + (audience.Success && audience.Groups[1].Value == project));
                    }
                }
            }
            catch (Exception error) { output.AppendLine("error=" + error.GetType().FullName); }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location), "auth-diagnostic.txt"), output.ToString());
        }
    }
}
