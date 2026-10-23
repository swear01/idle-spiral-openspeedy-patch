using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            string directory = Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH")
                ?? Assembly.GetExecutingAssembly().Location);
            try
            {
                File.Delete(Path.Combine(directory, "autoload-status.txt"));
                File.Delete(Path.Combine(directory, "autoload-error.txt"));
                string helper = Path.Combine(directory, "IdleSpiral.PatchLoader.exe");
                string runtime = Path.Combine(directory, "IdleSpiral.ClockIsolation.dll");
                var command = new StringBuilder("\"" + helper + "\" autoload " + GetCurrentProcessId() + " \"" + runtime + "\"");
                var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)) };
                if (!CreateProcess(helper, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000,
                    IntPtr.Zero, directory, ref startup, out ProcessInformation process))
                    throw new InvalidOperationException("Could not start autoload helper; Win32 error=" + Marshal.GetLastWin32Error());
                CloseHandle(process.Thread); CloseHandle(process.Process);
                File.WriteAllText(Path.Combine(directory, "autoload-bootstrap.txt"),
                    "scheduled; gamePid=" + GetCurrentProcessId() + "; helperPid=" + process.ProcessId);
            }
            catch (Exception error)
            {
                try { File.WriteAllText(Path.Combine(directory, "autoload-error.txt"), error.ToString()); }
                catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
            public short Show, ReservedSize;
            public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string workingDirectory,
            ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
