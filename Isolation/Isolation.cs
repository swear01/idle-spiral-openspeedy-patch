using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
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
            bool controllerLoaded=GetModuleHandle("speedpatch64.dll")!=IntPtr.Zero;
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.SpeedPatch.SteamGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader").GetMethod("AutoInstall").Invoke(null,null);
            // speedpatch64 enables its host process on load; leave the switch to the OpenSpeedy GUI.
            if(!controllerLoaded)SP_Disable((uint)Process.GetCurrentProcess().Id);
            Install();
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.DesignCountGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader").GetMethod("AutoInstall").Invoke(null,null);
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.MacroStepGuard.dll")).GetType("IdleSpiralSpeedPatch.Loader").GetMethod("AutoInstall").Invoke(null,null);
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string name);
        [DllImport("speedpatch64.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void SP_Disable(uint processId);
        private static void Invoke(string name)
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
            try{typeof(Isolation).GetMethod(name).Invoke(null,null);File.Delete(Path.Combine(DirectoryPath,"isolation-error.txt"));}
            catch(Exception e){File.WriteAllText(Path.Combine(DirectoryPath,"isolation-error.txt"),e.ToString());throw;}
        }
    }
    public static class Isolation
    {
        private const string Owner="local.idlespiral.openspeedy.isolation";
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly string DirectoryPath=Path.GetDirectoryName(typeof(Isolation).Assembly.Location);
        private static IntPtr slot,originalFlag,falseFlag;
        private static MethodInfo factorMethod,elapsedMethod,simulationMethod,updateMethod;
        private static Func<FixedStepSimulationClock,double,double,double> originalAdmission;
        private static bool installed;
        private static long admissions;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate double NativeSpeed();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate bool NativeEnabled();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int NativeEnabledById(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void NativeSetEnabled(uint id);
        private static NativeSpeed getSpeed;
        private static NativeEnabled isEnabled;
        private static NativeEnabledById isEnabledById;
        private static NativeSetEnabled enable,disable;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,SetLastError=true)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernelbase.dll",ExactSpelling=true)]private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}
        private static T Export<T>(IntPtr module,string name)where T:class
        {
            var pointer=GetProcAddress(module,name);if(pointer==IntPtr.Zero)throw new InvalidOperationException("Missing native export: "+name);
            return Marshal.GetDelegateForFunctionPointer(pointer,typeof(T)) as T;
        }
        private static void Verify(string file,string expected)
        {
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=expected)throw new InvalidOperationException("Unsupported isolation binary: "+Path.GetFileName(file));
        }
        public static void Install()
        {
            if(installed)return;
            var runtime=Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.SpeedPatch.SteamGuard.dll"));
            Verify(runtime.Location,"dbfeebdc66091f27f63320971cd3f369b3fd66928fc6a66af336425e44df635f");
            var module=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m=>m.ModuleName.Equals("speedpatch64.dll",StringComparison.OrdinalIgnoreCase));
            Verify(module.FileName,"c72ba4d9e35c5c00efc113e541da91a02dbc0c85c6fb2effff5cee63898bc70c");
            var native=module.BaseAddress;
            var exported=GetProcAddress(native,"SP_IsEnabled");
            var expected=new byte[]{0x48,0x83,0xec,0x18,0x48,0x83,0x3d,0xdc,0x92,0,0,0,0x74,0x0f,0x48,0x8b,0x05,0xd3,0x92,0,0,0x0f,0xb6,0};
            var code=new byte[expected.Length];Marshal.Copy(exported,code,0,code.Length);
            if(!code.SequenceEqual(expected) || exported.ToInt64()-native.ToInt64()!=0x1170)throw new InvalidOperationException("Unexpected native enable-reader structure.");
            slot=new IntPtr(exported.ToInt64()+12+Marshal.ReadInt32(exported,7));
            if(slot.ToInt64()!=exported.ToInt64()+21+Marshal.ReadInt32(exported,17) || (slot.ToInt64()&7)!=0 ||
                slot.ToInt64()<native.ToInt64() || slot.ToInt64()+8>native.ToInt64()+module.ModuleMemorySize)throw new InvalidOperationException("Invalid native flag slot.");
            originalFlag=Marshal.ReadIntPtr(slot);
            if(originalFlag==IntPtr.Zero)throw new InvalidOperationException("OpenSpeedy not initialized.");
            getSpeed=Export<NativeSpeed>(native,"SP_GetSpeed");isEnabled=Export<NativeEnabled>(native,"SP_IsEnabled");
            isEnabledById=Export<NativeEnabledById>(native,"SP_IsEnabledById");
            enable=Export<NativeSetEnabled>(native,"SP_Enable");disable=Export<NativeSetEnabled>(native,"SP_Disable");
            if((Marshal.ReadByte(originalFlag)!=0)!=(isEnabledById((uint)Process.GetCurrentProcess().Id)!=0))throw new InvalidOperationException("Enable mapping differs from controller.");
            if(falseFlag==IntPtr.Zero){falseFlag=Marshal.AllocHGlobal(1);Marshal.WriteByte(falseFlag,0);}
            var type=runtime.GetType("IdleSpiralSpeedPatch.Patch");factorMethod=AccessTools.Method(type,"ReadFactor");elapsedMethod=AccessTools.Method(type,"AddElapsed");
            simulationMethod=AccessTools.Method(type,"RunSimulationFrame");updateMethod=AccessTools.Method(typeof(GameSystem),"Update");
            Now();
            try
            {
                Harmony.Patch(factorMethod,prefix:new HarmonyMethod(typeof(Isolation),nameof(Factor)));
                Harmony.Patch(elapsedMethod,transpiler:new HarmonyMethod(typeof(Isolation),nameof(ReplaceFactor)));
                Harmony.Patch(simulationMethod,transpiler:new HarmonyMethod(typeof(Isolation),nameof(ReplaceFactor)));
                originalAdmission=(Func<FixedStepSimulationClock,double,double,double>)Delegate.CreateDelegate(typeof(Func<FixedStepSimulationClock,double,double,double>),elapsedMethod);
                Harmony.Patch(updateMethod,transpiler:new HarmonyMethod(typeof(Isolation),nameof(ReplaceAdmission)){priority=Priority.Last});
                ExchangeFlag(falseFlag);installed=true;Status("installed");
            }
            catch{Remove();throw;}
        }
        private static unsafe void ExchangeFlag(IntPtr value){Interlocked.Exchange(ref *(long*)slot.ToPointer(),value.ToInt64());}
        private static double ReadFactor()
        {
            if(Marshal.ReadByte(originalFlag)==0)return 1;
            double value=getSpeed();return value>1 && !double.IsInfinity(value)?Math.Min(1000,value):1;
        }
        private static bool Factor(ref double __result){__result=ReadFactor();return false;}
        private static IEnumerable<CodeInstruction> ReplaceFactor(IEnumerable<CodeInstruction> instructions)
        {
            int calls=0;
            foreach(var instruction in instructions)
            {
                if(instruction.Calls(factorMethod)){instruction.operand=AccessTools.Method(typeof(Isolation),nameof(ReadFactor));calls++;}
                yield return instruction;
            }
            if(calls!=1)throw new InvalidOperationException("Unexpected factor call site count.");
        }
        private static IEnumerable<CodeInstruction> ReplaceAdmission(IEnumerable<CodeInstruction> instructions)
        {
            int calls=0;
            foreach(var instruction in instructions)
            {
                if(instruction.Calls(elapsedMethod)){instruction.operand=AccessTools.Method(typeof(Isolation),nameof(Admission));calls++;}
                yield return instruction;
            }
            if(calls!=1)throw new InvalidOperationException("Unexpected elapsed admission call site count.");
        }
        private static double Admission(FixedStepSimulationClock clock,double elapsed,double scale)
        {
            if(Marshal.ReadIntPtr(slot)==falseFlag)elapsed*=ReadFactor();
            if((++admissions&4095)==1)Status("running");
            return originalAdmission(clock,elapsed,scale);
        }
        public static void Remove()
        {
            if(slot!=IntPtr.Zero && Marshal.ReadIntPtr(slot)==falseFlag)ExchangeFlag(originalFlag);
            foreach(var method in new[]{updateMethod,simulationMethod,elapsedMethod,factorMethod})if(method!=null)Harmony.Unpatch(method,HarmonyPatchType.All,Owner);
            installed=false;Status("removed");
        }
        private static void Status(string state)
        {
            File.WriteAllText(Path.Combine(DirectoryPath,"isolation-status.txt"),string.Format(CultureInfo.InvariantCulture,
                "state={0}\npid={1}\nnativeClockBypass={2}\ncontrollerEnabled={3}\nsharedFactor={4:R}\nmanagedFactor={5:R}\nrealElapsedScaledOnce=true\nadmissions={6}\n",
                state,Process.GetCurrentProcess().Id,slot!=IntPtr.Zero&&Marshal.ReadIntPtr(slot)==falseFlag,
                originalFlag!=IntPtr.Zero&&Marshal.ReadByte(originalFlag)!=0,getSpeed==null?0:getSpeed(),getSpeed==null?0:ReadFactor(),admissions));
        }
        public static void Test()
        {
            if(Process.GetCurrentProcess().ProcessName!="IdleSpiral.MonoHost")throw new InvalidOperationException("Isolated host only.");
            Install();Install();
            Func<FixedStepSimulationClock,double,double,double> add=Admission;
            var read=(Func<double>)Delegate.CreateDelegate(typeof(Func<double>),factorMethod);
            double configured=getSpeed();
            if(isEnabled() || isEnabledById((uint)Process.GetCurrentProcess().Id)==0 || configured<2 || read()!=configured)throw new InvalidOperationException("Native isolation or controller semantics failed.");
            var clock=new FixedStepSimulationClock();add(clock,0.01,2);
            if(Math.Abs(clock.BacklogSeconds-0.02*configured)>1e-8)throw new InvalidOperationException("Elapsed not scaled exactly once.");
            ulong start=Now();long qpc=Stopwatch.GetTimestamp();
            while(Now()-start<2000000)Thread.SpinWait(200);
            double qpcSeconds=(Stopwatch.GetTimestamp()-qpc)/(double)Stopwatch.Frequency;
            double realSeconds=(Now()-start)/1e7;
            if(qpcSeconds/realSeconds<0.95 || qpcSeconds/realSeconds>1.05)throw new InvalidOperationException("Native QPC still accelerated.");
            disable((uint)Process.GetCurrentProcess().Id);
            try
            {
                clock=new FixedStepSimulationClock();add(clock,0.01,1);
                if(read()!=1 || Math.Abs(clock.BacklogSeconds-0.01)>1e-8)throw new InvalidOperationException("Controller disable ignored.");
            }
            finally{enable((uint)Process.GetCurrentProcess().Id);}
            Remove();
            if(Marshal.ReadIntPtr(slot)!=originalFlag || !isEnabled())throw new InvalidOperationException("Native flag restoration failed.");
            File.WriteAllText(Path.Combine(DirectoryPath,"isolation-selftest.txt"),string.Format(CultureInfo.InvariantCulture,
                "PASS: exact native/runtime hashes; native enable-reader byte pattern and same aligned data slot; controller mapping preserved; QPC/real rate={0:R}; shared factor={1:R}; elapsed scaled once; original controller disable/re-enable; atomic data-slot restoration. No executable bytes patched.\n",qpcSeconds/realSeconds,configured));
        }
    }
}
