using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using BreakInfinity;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install()
        {
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location), "0Harmony.dll"));
            Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.BattleProbe").GetMethod("Install").Invoke(null, null);
        }
        public static void Remove() { Assembly.GetExecutingAssembly().GetType("IdleSpiralSpeedPatch.BattleProbe").GetMethod("Remove").Invoke(null, null); }
    }
    public static class BattleProbe
    {
        private const string Owner = "local.idlespiral.battle.observer";
        private static readonly Harmony Patch = new Harmony(Owner);
        private static readonly string Output = Path.Combine(Path.GetDirectoryName(typeof(BattleProbe).Assembly.Location), "battle-observation.txt");
        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static ulong started, next;
        private static long frames, battleCalls, attacks, enemyAttacks, creates;
        private static double baseSeconds, gameSeconds, battleSeconds, allySeconds, enemySeconds, generatedAttacks;
        private static object scheduler;
        private static bool active;
        [DllImport("kernelbase.dll", ExactSpelling = true)] private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        [DllImport("speedpatch64.dll", CallingConvention = CallingConvention.Cdecl)] private static extern double SP_GetSpeed();
        private static ulong Now() { QueryUnbiasedInterruptTimePrecise(out var time); return time; }
        private static void Hook(Type type, string name, string observer, bool postfix = false)
        {
            var method = AccessTools.Method(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            Targets.Add(method);
            var hook = new HarmonyMethod(typeof(BattleProbe), observer);
            Patch.Patch(method, prefix: postfix ? null : hook, postfix: postfix ? hook : null);
        }
        public static void Install()
        {
            if (active || Targets.Count != 0) throw new InvalidOperationException("Observer already installed.");
            File.WriteAllText(Output, "state=scheduled; durationRealSeconds=30; speedAndGameplayUnchanged=true\n");
            try
            {
                Hook(typeof(BattleCtrl), "UpdateBattle", nameof(Battle));
                Hook(typeof(Ally), "AllyUpdate", nameof(AllyTime));
                Hook(typeof(AllyAttack), "Attack", nameof(Attack));
                Hook(typeof(Enemy), "Update", nameof(EnemyTime));
                Hook(typeof(Ally), "OnAttack", nameof(EnemyAttack));
                Hook(typeof(BattleCtrl), "CreateEnemy", nameof(Create));
                Hook(typeof(LogicalAttackScheduler), "Accumulate", nameof(Accumulate));
                Hook(typeof(GameSystem), "Update", nameof(Frame), true);
            }
            catch { Remove(); throw; }
        }
        public static void Remove()
        {
            active = false;
            foreach (var target in Targets) Patch.Unpatch(target, HarmonyPatchType.All, Owner);
            Targets.Clear();
        }
        private static void Battle(float __0) { if (active) { battleCalls++; battleSeconds += __0; } }
        private static void AllyTime(float __0) { if (active) allySeconds += __0; }
        private static void EnemyTime(float __0) { if (active) enemySeconds += __0; }
        private static void Attack() { if (active) attacks++; }
        private static void EnemyAttack() { if (active) enemyAttacks++; }
        private static void Create() { if (active) creates++; }
        private static void Accumulate(object __instance, BigDouble __0, double __1)
        {
            if (active && ReferenceEquals(__instance, scheduler)) generatedAttacks += __0.ToDouble() * __1;
        }
        private static string N(object value) => Convert.ToString(value, CultureInfo.InvariantCulture);
        private static void Frame(GameSystem __instance)
        {
            try
            {
                var battle = __instance.idleSystem?.battleCtrl;
                if (battle == null) return;
                var now = Now();
                if (started == 0)
                {
                    scheduler = AccessTools.Field(typeof(Ally), "_attackScheduler").GetValue(battle.ally);
                    started = now; next = now; active = true;
                }
                else
                {
                    frames++;
                    baseSeconds += __instance.AdmittedSimulationDelta;
                    gameSeconds += __instance.AdmittedGameSimulationDelta;
                }
                if (now < next) return;
                var dto = __instance.idleSystem.dto;
                var fields = new Dictionary<string, object> {
                    {"realSeconds", (now-started)/10000000.0}, {"factor", SP_GetSpeed()}, {"frames",frames},
                    {"baseSeconds",baseSeconds}, {"gameSeconds",gameSeconds}, {"battleSeconds",battleSeconds},
                    {"battleCalls",battleCalls}, {"allySeconds",allySeconds}, {"enemySeconds",enemySeconds},
                    {"generatedAttacks",generatedAttacks}, {"directAttacks",attacks}, {"enemyAttacks",enemyAttacks}, {"enemyCreations",creates},
                    {"state",battle.state}, {"enemy",battle.currentId}, {"allyProgress",battle.ally.AttackProgress},
                    {"pendingAttacks",battle.ally.PendingLogicalAttacks}, {"secondsPerAttack",battle.ally.Stats.GetSecondPerAttack()},
                    {"rawAttackProgress",battle.ally.Stats.RawAttackProgressPerFramePrecise},
                    {"nukeMode",battle.battleProgressManager.CurrentNukeMode(battle.currentId)},
                    {"performanceModifier",battle.battleProgressManager.Modifier(battle.currentReward)},
                    {"bulkPending",AccessTools.Property(typeof(BattleCtrl),"LastBulkSolvePending").GetValue(battle,null)},
                    {"difficulty",battle.currentDifficulty}, {"allyHp",battle.ally.CurrentHp}, {"allyAtk",battle.ally.ATK},
                    {"enemyHp",battle.currentEnemy?.CurrentHp}, {"enemyMaxHp",battle.currentEnemy?.MaxHP},
                    {"enemyDef",battle.currentEnemy?.DEF}, {"defeated",dto.enemy_defeated[(int)battle.currentId]},
                    {"offlineMemory",dto.offlineBoostAmount}, {"boost",dto.isOfflineBoost}
                };
                var line = new List<string>();
                foreach (var pair in fields) line.Add(pair.Key+"="+N(pair.Value));
                File.AppendAllText(Output, string.Join("\t",line)+"\n");
                next = now+10000000;
                if (now-started >= 300000000)
                {
                    Remove();
                    File.AppendAllText(Output,"state=completed; observerHooksRemoved=true\n");
                }
            }
            catch (Exception error)
            {
                Remove();
                File.AppendAllText(Output,"state=failed; error="+error.GetType().FullName+"; message="+error.Message+"\n");
            }
        }
    }
}
