using System.Runtime.InteropServices;

if (args.Length != 2) throw new ArgumentException("Usage: MonoHost GAME_DIRECTORY PATCH_DIRECTORY");
string game = Path.GetFullPath(args[0]);
string patch = Path.GetFullPath(args[1]);
string managed = Path.Combine(game, "IdleSpiral_Data", "Managed");
var library = NativeLibrary.Load(Path.Combine(game, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll"));
T Api<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
Api<SetPath>("mono_set_assemblies_path")(managed + ";" + patch);
Api<Config>("mono_config_parse")(IntPtr.Zero);
var domain = Api<Init>("mono_jit_init_version")("IdleSpiral-isolated-test", "v4.0.30319");
if (domain == IntPtr.Zero) throw new Exception("Failed to initialize isolated Mono.");
Console.WriteLine($"READY pid={Environment.ProcessId}");
Console.Out.Flush();
Thread.Sleep(Timeout.Infinite);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetPath([MarshalAs(UnmanagedType.LPStr)] string value);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Config(IntPtr path);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Init([MarshalAs(UnmanagedType.LPStr)] string domain, [MarshalAs(UnmanagedType.LPStr)] string version);
