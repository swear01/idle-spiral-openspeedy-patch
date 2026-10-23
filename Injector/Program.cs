using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SharpMonoInjector;

if (args.Length != 3 || (args[0] != "apply" && args[0] != "remove" && args[0] != "test" && args[0] != "autoload"))
    throw new ArgumentException("Usage: IdleSpiral.PatchLoader apply|remove|test|autoload PID PATCH_DLL");
string assemblyPath = Path.GetFullPath(args[2]);
try {
var target = Process.GetProcessById(int.Parse(args[1]));
if (target.ProcessName != "IdleSpiral" && target.ProcessName != "IdleSpiral.MonoHost")
    throw new InvalidOperationException("Target must be IdleSpiral or the isolated Mono test host.");
if (args[0] == "autoload")
{
    string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "IdleSystem", "IdleSpiral", "Player.log");
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        target.Refresh();
        if (target.HasExited) throw new InvalidOperationException("Game exited before autoload.");
        bool ready = target.ProcessName == "IdleSpiral.MonoHost";
        if (!ready && File.Exists(log))
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            ready = reader.ReadToEnd().Contains("[mainthread] player loop thread=1", StringComparison.Ordinal);
        }
        if (ready && deadline.Elapsed.TotalSeconds >= 2) break;
        if (deadline.Elapsed.TotalSeconds > 90) throw new TimeoutException("Game initialization marker did not appear within 90 real seconds.");
        Thread.Sleep(250);
    }
}
using var mono = new RemoteMono(target);
var assembly = mono.Call("mono_domain_assembly_open", mono.Domain, mono.String(assemblyPath));
if (assembly == IntPtr.Zero) throw new Exception("Mono could not load the patch assembly.");
var image = mono.Call("mono_assembly_get_image", assembly);
var type = mono.Call("mono_class_from_name", image, mono.String("IdleSpiralSpeedPatch"), mono.String("Loader"));
if (type == IntPtr.Zero) throw new Exception("Patch loader class not found.");
var methodName = args[0] == "apply" ? "Install" : args[0] == "remove" ? "Remove" : args[0] == "autoload" ? "AutoInstall" : "Test";
var method = mono.Call("mono_class_get_method_from_name", type, mono.String(methodName), IntPtr.Zero);
if (method == IntPtr.Zero) throw new Exception("Patch entry method not found.");
var error = mono.Allocate(8);
mono.Call("mono_runtime_invoke", method, IntPtr.Zero, IntPtr.Zero, error);
if (mono.ReadPointer(error) != IntPtr.Zero)
    throw new Exception("Managed patch call failed; inspect patch-error.txt beside the DLL.");
Console.WriteLine($"{methodName} completed in PID {target.Id}; assembly=0x{assembly.ToInt64():X}");
}
catch (Exception error)
{
    if (args[0] == "autoload")
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(assemblyPath)!, "autoload-error.txt"), error.ToString());
        Environment.ExitCode = 1;
        return;
    }
    throw;
}

sealed class RemoteMono : IDisposable
{
    private readonly IntPtr handle;
    private readonly Memory memory;
    private readonly Dictionary<string, IntPtr> exports;
    private bool unfinishedThread;
    public IntPtr Domain { get; }

    public RemoteMono(Process process)
    {
        var module = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "mono-2.0-bdwgc.dll");
        handle = Native.OpenProcess(ProcessAccessRights.PROCESS_CREATE_THREAD | ProcessAccessRights.PROCESS_VM_OPERATION |
            ProcessAccessRights.PROCESS_VM_READ | ProcessAccessRights.PROCESS_VM_WRITE | ProcessAccessRights.PROCESS_QUERY_INFORMATION,
            false, process.Id);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        memory = new Memory(handle);
        var required = new[] { "mono_get_root_domain", "mono_thread_attach", "mono_thread_current", "mono_thread_detach",
            "mono_domain_assembly_open", "mono_assembly_get_image", "mono_class_from_name", "mono_class_get_method_from_name", "mono_runtime_invoke" };
        exports = ProcessUtils.GetExportedFunctions(handle, module.BaseAddress)
            .Where(e => required.Contains(e.Name)).ToDictionary(e => e.Name, e => e.Address);
        foreach (string name in required)
            if (!exports.ContainsKey(name)) throw new Exception("Missing Mono export: " + name);
        Domain = Call("mono_get_root_domain");
        if (Domain == IntPtr.Zero) throw new Exception("Mono root domain is not ready.");
    }

    public IntPtr Allocate(int bytes) { return memory.AllocateAndWrite(new byte[bytes]); }
    public IntPtr String(string value) { return memory.AllocateAndWrite(Encoding.UTF8.GetBytes(value + "\0")); }
    public IntPtr ReadPointer(IntPtr address) { return (IntPtr)memory.ReadLong(address); }

    public IntPtr Call(string name, params IntPtr[] args)
    {
        if (args.Length > 4) throw new ArgumentException("At most four native arguments are supported.");
        var result = Allocate(8);
        var code = new X64Code();
        code.Bytes(0x48, 0x83, 0xEC, 0x28);
        bool attach = Domain != IntPtr.Zero;
        if (attach)
        {
            code.Argument(0, Domain);
            code.Call(exports["mono_thread_attach"]);
        }
        for (int i = 0; i < args.Length; i++) code.Argument(i, args[i]);
        code.Call(exports[name]);
        code.Bytes(0x48, 0xA3);
        code.Pointer(result);
        if (attach)
        {
            code.Call(exports["mono_thread_current"]);
            code.Bytes(0x48, 0x89, 0xC1);
            code.Call(exports["mono_thread_detach"]);
        }
        code.Bytes(0x48, 0x83, 0xC4, 0x28, 0xC3);
        var entry = memory.AllocateAndWrite(code.ToArray());
        var thread = Native.CreateRemoteThread(handle, IntPtr.Zero, 0, entry, IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var wait = Native.WaitForSingleObject(thread, 15000);
            if (wait != WaitResult.WAIT_OBJECT_0)
            {
                unfinishedThread = true;
                throw new Exception("Remote Mono call did not finish; allocations retained to avoid freeing executing code: " + name);
            }
            return ReadPointer(result);
        }
        finally { Native.CloseHandle(thread); }
    }

    public void Dispose()
    {
        if (!unfinishedThread) memory.Dispose();
        Native.CloseHandle(handle);
    }
}

sealed class X64Code
{
    private readonly List<byte> bytes = new();
    public void Bytes(params byte[] value) { bytes.AddRange(value); }
    public void Pointer(IntPtr value) { bytes.AddRange(BitConverter.GetBytes(value.ToInt64())); }
    public void Argument(int index, IntPtr value)
    {
        switch (index)
        {
            case 0: Bytes(0x48, 0xB9); break;
            case 1: Bytes(0x48, 0xBA); break;
            case 2: Bytes(0x49, 0xB8); break;
            case 3: Bytes(0x49, 0xB9); break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
        Pointer(value);
    }
    public void Call(IntPtr address) { Bytes(0x48, 0xB8); Pointer(address); Bytes(0xFF, 0xD0); }
    public byte[] ToArray() { return bytes.ToArray(); }
}
