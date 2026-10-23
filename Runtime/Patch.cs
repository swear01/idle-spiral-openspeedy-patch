using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    public static class Patch
    {
        private const string Owner = "local.idlespiral.openspeedy.simulation";
        private const string ExpectedDomainHash = "caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b";
        private static readonly Harmony Harmony = new Harmony(Owner);
        private static readonly string DirectoryPath = Path.GetDirectoryName(typeof(Patch).Assembly.Location);
        private static long _frames;
        private static double _admittedSeconds;
        private static double _largestBacklog;
        private const int MaximumSteps = 128;
        private const ulong FrameBudgetTicks = 80000; // Eight real milliseconds, in 100 ns units.
        private static long _yieldedFrames;
        private static double _overflowSeconds;
        private static double _lastFrameMilliseconds;
        private static double _maxFrameMilliseconds;
        private static readonly Action<GameSystem, float> SetBaseDelta = (Action<GameSystem, float>)Delegate.CreateDelegate(
            typeof(Action<GameSystem, float>), AccessTools.PropertySetter(typeof(GameSystem), "AdmittedSimulationDelta"));
        private static readonly Action<GameSystem, float> SetGameDelta = (Action<GameSystem, float>)Delegate.CreateDelegate(
            typeof(Action<GameSystem, float>), AccessTools.PropertySetter(typeof(GameSystem), "AdmittedGameSimulationDelta"));

        [DllImport("kernelbase.dll", ExactSpelling = true)]
        private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);

        private static ulong RealTime()
        {
            QueryUnbiasedInterruptTimePrecise(out ulong time);
            return time;
        }

        [DllImport("speedpatch64.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double SP_GetSpeed();
        [DllImport("speedpatch64.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SP_IsEnabled();

        public static void Install()
        {
            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(typeof(FixedStepSimulationClock).Assembly.Location))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (hash != ExpectedDomainHash)
                throw new InvalidOperationException("Unsupported IdleSpiralDomain.dll; no patch applied.");
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(typeof(GameSystem).Assembly.Location))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (hash != "a67ee1d53fc2247d738ff2166f1a5e217bcb3fdebc3dfebf0a21393b8e5fbfbb")
                throw new InvalidOperationException("Unsupported Assembly-CSharp.dll; no patch applied.");
            RealTime();
            var update = AccessTools.Method(typeof(GameSystem), "Update");
            var existing = HarmonyLib.Harmony.GetPatchInfo(update);
            if (existing?.Owners.Contains(Owner) == true)
            {
                if (existing.Transpilers.Any(p => p.owner == Owner && p.PatchMethod.DeclaringType.Assembly == typeof(Patch).Assembly))
                    return;
                throw new InvalidOperationException("Remove the previous patch version before installing this version.");
            }
            Harmony.Patch(update, transpiler: new HarmonyMethod(typeof(Patch), nameof(RewriteClockCalls)));
            WriteStatus("installed", 0, 0, 0);
        }

        public static void Remove()
        {
            Harmony.Unpatch(AccessTools.Method(typeof(GameSystem), "Update"), HarmonyPatchType.All, Owner);
            WriteStatus("removed", 0, 0, 0);
        }

        private static IEnumerable<CodeInstruction> RewriteClockCalls(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int drainIndex = code.FindIndex(i => i.Calls(AccessTools.Method(typeof(FixedStepSimulationClock), "DrainRenderedFrame")));
            int advanceIndex = code.FindIndex(i => i.Calls(AccessTools.Method(typeof(IdleSpiralSystem), "Advance")));
            int end = advanceIndex < 0 ? -1 : code.FindIndex(advanceIndex, i => i.opcode == OpCodes.Blt || i.opcode == OpCodes.Blt_S);
            if (drainIndex < 3 || advanceIndex <= drainIndex || end <= advanceIndex ||
                code[drainIndex - 3].opcode != OpCodes.Ldarg_0 ||
                !(code[drainIndex - 2].operand is FieldInfo clockField) || clockField.Name != "_simulationClock" ||
                code[drainIndex - 1].opcode != OpCodes.Ldloca_S)
                throw new InvalidOperationException("Unexpected GameSystem.Update simulation loop.");
            int start = drainIndex - 3;
            var removed = new HashSet<Label>(code.Skip(start).Take(end - start + 1).SelectMany(i => i.labels));
            foreach (var instruction in code.Take(start).Concat(code.Skip(end + 1)))
            {
                if ((instruction.operand is Label target && removed.Contains(target)) ||
                    (instruction.operand is Label[] targets && targets.Any(removed.Contains)))
                    throw new InvalidOperationException("Simulation loop has an external branch entry.");
            }
            if (code.Skip(start).Take(end - start + 1).Any(i => i.blocks.Count != 0))
                throw new InvalidOperationException("Simulation loop has unexpected exception blocks.");
            var replacement = new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, clockField),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch), nameof(RunSimulationFrame))),
                new CodeInstruction(OpCodes.Stloc_S, code[drainIndex - 1].operand)
            };
            code.RemoveRange(start, end - start + 1);
            code.InsertRange(start, replacement);
            int addCalls = 0;
            foreach (var instruction in code)
            {
                var method = instruction.operand as MethodInfo;
                if (method?.DeclaringType != typeof(FixedStepSimulationClock)) continue;
                if (method.Name == nameof(FixedStepSimulationClock.AddElapsed))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(Patch), nameof(AddElapsed));
                    addCalls++;
                }
            }
            if (addCalls != 1 || code.Any(i => i.Calls(AccessTools.Method(typeof(FixedStepSimulationClock), "DrainRenderedFrame"))))
                throw new InvalidOperationException("Unexpected GameSystem.Update clock call sites.");
            return code;
        }

        public static double AddElapsed(FixedStepSimulationClock clock, double elapsedSeconds, double simulationTimeScale)
        {
            return AdmitElapsed(clock, elapsedSeconds, simulationTimeScale, ReadFactor());
        }

        private static double AdmitElapsed(FixedStepSimulationClock clock, double elapsed, double scale, double factor)
        {
            if (factor <= 1 || elapsed > 2.0 * factor || !(elapsed > 0) || double.IsInfinity(elapsed) ||
                !(scale > 0) || double.IsInfinity(scale) || double.IsInfinity(elapsed * scale))
                return clock.AddElapsed(elapsed, scale);
            double wanted = elapsed * scale;
            double admitted = Math.Min(wanted, Math.Max(0, Math.Min(40.0, 2.0 * factor) - clock.BacklogSeconds));
            double overflow = wanted - admitted;
            _overflowSeconds += overflow;
            while (admitted > 2.0)
            {
                clock.AddElapsed(2.0, 1.0);
                admitted -= 2.0;
            }
            clock.AddElapsed(admitted, 1.0);
            return overflow;
        }

        public static double RunSimulationFrame(GameSystem game, FixedStepSimulationClock clock)
        {
            double factor = ReadFactor();
            int steps = RunFrame(clock, factor, () => {
                float requested = TickManager.Scale(0.02f);
                float delta = game.idleSystem.OfflineBoost.AdmitGameDelta(requested, 0.02);
                game.idleSystem.Advance(delta, 0.02f);
                SetGameDelta(game, game.AdmittedGameSimulationDelta + delta);
            }, RealTime, out double deferredOfflineSeconds, out bool yielded, out double milliseconds);
            SetBaseDelta(game, steps * 0.02f);
            _frames++;
            _admittedSeconds += steps * 0.02;
            _largestBacklog = Math.Max(_largestBacklog, clock.BacklogSeconds);
            if (yielded) _yieldedFrames++;
            _lastFrameMilliseconds = milliseconds;
            _maxFrameMilliseconds = Math.Max(_maxFrameMilliseconds, milliseconds);
            if ((_frames & 127) == 0)
                WriteStatus("running", factor, StepBudget(factor), clock.BacklogSeconds);
            return deferredOfflineSeconds;
        }

        private static int RunFrame(FixedStepSimulationClock clock, double factor, Action step, Func<ulong> now,
            out double deferred, out bool yielded, out double milliseconds)
        {
            ulong started = now();
            yielded = false;
            deferred = 0.0;
            int count = 0;
            if (factor <= 1.0)
            {
                int originalSteps = clock.DrainRenderedFrame(out deferred);
                for (; count < originalSteps; count++) step();
            }
            else
            {
                int budget = StepBudget(factor);
                while (count < budget)
                {
                    if (count > 0 && now() - started >= FrameBudgetTicks)
                    {
                        yielded = clock.BacklogSeconds + 1e-9 >= 0.02;
                        break;
                    }
                    if (clock.Drain(1) == 0) break;
                    step();
                    count++;
                }
                yielded |= count == budget && clock.BacklogSeconds + 1e-9 >= 0.02;
            }
            milliseconds = (now() - started) / 10000.0;
            return count;
        }

        private static int StepBudget(double factor)
        {
            return (int)Math.Min(MaximumSteps, Math.Max(4.0, Math.Ceiling(4.0 * factor)));
        }

        private static double ReadFactor()
        {
            try
            {
                if (!SP_IsEnabled()) return 1.0;
                double factor = SP_GetSpeed();
                return factor > 1.0 && !double.IsInfinity(factor) ? Math.Min(1000.0, factor) : 1.0;
            }
            catch (DllNotFoundException) { return 1.0; }
            catch (EntryPointNotFoundException) { return 1.0; }
        }

        private static void WriteStatus(string state, double factor, int budget, double backlog)
        {
            try
            {
                File.WriteAllText(Path.Combine(DirectoryPath, "patch-status.txt"), string.Format(
                    CultureInfo.InvariantCulture,
                    "state={0}\nversion=2-freeze-guard\nframes={1}\nfactor={2:R}\nstepBudget={3}\nbacklogSeconds={4:R}\nmaxBacklogSeconds={5:R}\nadmittedSeconds={6:R}\nframeBudgetMilliseconds=8\nlastSimulationMilliseconds={7:R}\nmaxSimulationMilliseconds={8:R}\nyieldedFrames={9}\noverflowOfflineSeconds={10:R}\n",
                    state, _frames, factor, budget, backlog, _largestBacklog, _admittedSeconds,
                    _lastFrameMilliseconds, _maxFrameMilliseconds, _yieldedFrames, _overflowSeconds));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static void Test()
        {
            Require(StepBudget(20) == 80, "20x step budget");
            var clock = new FixedStepSimulationClock();
            Require(AdmitElapsed(clock, 3.0, 1.0, 20.0) == 0, "short real gap is admitted");
            Require(Math.Abs(clock.BacklogSeconds - 3.0) < 1e-8, "three seconds retained");
            int count = 0;
            double offline;
            while (clock.BacklogSeconds >= 0.02)
            {
                count += RunFrame(clock, 20, () => {}, () => 0, out offline, out _, out _);
                Require(offline == 0, "no time diverted");
            }
            Require(count == 150, "all 150 steps drained");
            Require(AdmitElapsed(clock, 41.0, 1.0, 20.0) == 41.0, "long real gap stays offline");
            var fractional = new FixedStepSimulationClock();
            AdmitElapsed(fractional, 0.031, 1.0, 20);
            Require(RunFrame(fractional, 20, () => {}, () => 0, out offline, out _, out _) == 1, "whole step admitted");
            Require(Math.Abs(fractional.BacklogSeconds - 0.011) < 1e-8, "fraction retained");
            var disabled = new FixedStepSimulationClock();
            disabled.AddElapsed(0.2, 1);
            Require(RunFrame(disabled, 1, () => {}, () => 0, out offline, out _, out _) == 4 && Math.Abs(offline - 0.12) < 1e-8,
                "disabled speed preserves original behavior");
            var paused = new FixedStepSimulationClock();
            AdmitElapsed(paused, 3, 0, 20);
            Require(paused.BacklogSeconds == 0, "pause stays paused");
            Require(StepBudget(1000) == 128, "high multiplier has a hard step cap");
            var overloaded = new FixedStepSimulationClock();
            Require(Math.Abs(AdmitElapsed(overloaded, 60, 1, 1000) - 20) < 1e-8, "overflow uses original offline path");
            Require(Math.Abs(overloaded.BacklogSeconds - 40) < 1e-8, "backlog capped at forty game seconds");
            Require(RunFrame(overloaded, 1000, () => {}, () => 0, out offline, out bool capped, out _) == 128 && capped,
                "extreme factor yields after 128 steps");
            var slow = new FixedStepSimulationClock();
            AdmitElapsed(slow, 3, 1, 20);
            ulong ticks = 0;
            Require(RunFrame(slow, 20, () => ticks += 30000, () => ticks, out offline, out bool timed, out double ms) == 3,
                "three slow steps yield within one step of eight ms");
            Require(timed && ms == 9 && Math.Abs(slow.BacklogSeconds - 2.94) < 1e-8, "unexecuted steps remain intact");
            int resumed = 3;
            while (slow.BacklogSeconds + 1e-9 >= 0.02)
                resumed += RunFrame(slow, 20, () => ticks += 30000, () => ticks, out offline, out _, out _);
            Require(resumed == 150, "yielded work resumes without loss or duplication");
            var stress = new FixedStepSimulationClock();
            double overflowTotal = 0;
            int stressSteps = 0;
            for (int frame = 0; frame < 10000; frame++)
            {
                overflowTotal += AdmitElapsed(stress, 0.4, 1, 1000);
                stressSteps += RunFrame(stress, 1000, () => ticks += 10000, () => ticks, out offline, out _, out _);
                Require(stress.BacklogSeconds <= 40.0 + 1e-8, "sustained overload stays bounded");
            }
            Require(stressSteps == 80000, "stress frames yield after eight slow steps");
            Require(Math.Abs(4000 - stressSteps * 0.02 - overflowTotal - stress.BacklogSeconds) < 1e-6,
                "overload conserves simulated, deferred and queued time");
            ulong begin = RealTime();
            System.Threading.Thread.Sleep(20);
            Require(RealTime() > begin, "independent real timer advances");
            Install();
            var method = AccessTools.Method(typeof(GameSystem), "Update");
            Require(HarmonyLib.Harmony.GetPatchInfo(method).Owners.Contains(Owner), "runtime hook installed");
            Remove();
            Require(HarmonyLib.Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) != true, "runtime hook removed");
            File.WriteAllText(Path.Combine(DirectoryPath, "selftest.txt"), "PASS: clock accounting, gap policy, disabled/pause behavior, hard cap, timed yield/resume, bounded backlog, 10000-frame overload conservation, independent timer, Harmony install/remove\n");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Self-test failed: " + message);
        }
    }
}
