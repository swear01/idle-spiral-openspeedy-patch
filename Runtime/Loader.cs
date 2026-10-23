using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install() { Invoke("Install"); }
        public static void Remove() { Invoke("Remove"); }
        public static void Test() { Invoke("Test"); }

        public static void AutoInstall()
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            try
            {
                File.Delete(Path.Combine(directory, "autoload-status.txt"));
                File.Delete(Path.Combine(directory, "autoload-error.txt"));
                Install();
                string[] settings = File.ReadAllLines(Path.Combine(directory, "openspeedy-loader.txt"));
                if (settings.Length != 2 || !Path.IsPathRooted(settings[0])) throw new InvalidOperationException("Invalid OpenSpeedy settings.");
                string hash;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(settings[0]))
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(hash, settings[1], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("OpenSpeedy DLL changed; check compatibility and reinstall autoload.");
                if (GetModuleHandle("speedpatch64.dll") == IntPtr.Zero && LoadLibrary(settings[0]) == IntPtr.Zero)
                    throw new InvalidOperationException("OpenSpeedy load failed; Win32 error=" + Marshal.GetLastWin32Error());
                File.WriteAllText(Path.Combine(directory, "autoload-status.txt"), "installed; bounded simulation patch and OpenSpeedy loaded; pid="
                    + GetCurrentProcessId() + "; " + SpeedState());
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(directory, "autoload-error.txt"), error.ToString());
                throw;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string SpeedState()
        {
            return "factor=" + SP_GetSpeed().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "; enabled=" + SP_IsEnabled();
        }
        [DllImport("speedpatch64.dll", CallingConvention = CallingConvention.Cdecl)] private static extern double SP_GetSpeed();
        [DllImport("speedpatch64.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SP_IsEnabled();

        private static void Invoke(string method)
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            try
            {
                Assembly.LoadFrom(Path.Combine(directory, "0Harmony.dll"));
                Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.Patch")
                    .GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(directory, "patch-error.txt"), error.ToString());
                throw;
            }
        }
    }
}
