using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BreakInfinity;
using HarmonyLib;
using IdleLibrary;
using Theory;
using TheoryModel=Theory.Theory;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        private static string DirectoryPath=>Path.GetDirectoryName(typeof(Loader).Assembly.Location);
        public static void Install(){Invoke("Install");}
        public static void Remove(){Invoke("Remove");}
        public static void AutoInstall(){Install();}
        private static void Invoke(string name)
        {
            Assembly.LoadFrom(Path.Combine(DirectoryPath,"0Harmony.dll"));
            try{typeof(AutomationGuard).GetMethod(name).Invoke(null,null);File.Delete(Path.Combine(DirectoryPath,"automation-error.txt"));}
            catch(Exception e){File.WriteAllText(Path.Combine(DirectoryPath,"automation-error.txt"),e.ToString());throw;}
        }
    }
    // The four automation controllers tick once per 1 s of Time.realtimeSinceStartup, which ClockIsolation keeps at 1x.
    // Feed them a clock that runs at the OpenSpeedy factor (still at most one tick per frame, as with accelerated native
    // clocks), and keep the prestige checkpoint saves they trigger to one per 5 real seconds.
    // Battle auto-start restarts go battle -> quitting -> battle inside one simulation step; the two battle panels react to
    // every BattleCtrl.StateChanged by switching Doozy views and buttons, so they get the final state once per frame.
    // Daily Spiral tries reset at real local midnight; every 86400 extra game seconds from the OpenSpeedy factor resets them
    // once more by clearing the stored reset date, so the save only ever holds the real date. Season runs keep the real calendar.
    // The Daily Spiral reset countdown then shows whichever comes first, in game seconds.
    // Theory auto study levels up 100 x TickManager multiplier per real second through LevelUpByLevel, which loops once per
    // level (about 7 us each) and charges every level the starting level's cost, since the level is only written after the
    // loop. The batch is charged that cost once instead; above 1x it takes every level the resources afford.
    public static class AutomationGuard
    {
        private const string Owner="local.idlespiral.openspeedy.automation";
        private const string RuntimeHash="dbfeebdc66091f27f63320971cd3f369b3fd66928fc6a66af336425e44df635f";
        private const ulong CheckpointIntervalTicks=50000000;
        private const double GameDaySeconds=86400,MaxTheoryBatch=1e15;
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly string DirectoryPath=Path.GetDirectoryName(typeof(AutomationGuard).Assembly.Location);
        private static readonly Type[] Controllers={typeof(UpgradeAutomationController),typeof(PrestigeAutomationController),typeof(SoftPrestigeAutomationController),typeof(TornadoPrestigeAutomationController)};
        private static readonly MethodInfo[] StateViews={AccessTools.Method(typeof(BattleUIManager),"OnBattleStateChanged"),AccessTools.Method(typeof(Battle_Mono),"ApplyBattleState")};
        private static readonly AccessTools.FieldRef<FirstTheoryTransaction,TheoryData> TheoryDataRef=AccessTools.FieldRefAccess<FirstTheoryTransaction,TheoryData>("data");
        private static readonly Func<MultipleTransaction,IList<ITransaction>> TheoryCosts=AccessTools.MethodDelegate<Func<MultipleTransaction,IList<ITransaction>>>(AccessTools.PropertyGetter(typeof(MultipleTransaction),"transactions"));
        private static readonly List<KeyValuePair<object,MethodBase>> PendingStateViews=new List<KeyValuePair<object,MethodBase>>();
        private static Func<double> readFactor;
        private static bool installed,inAutomation,flushingStateViews,gameDayActive;
        private static double lastReal=double.NaN,scaled,extraDaySeconds;
        private static ulong lastCheckpoint,lastDayTicks;
        private static long frames,autoBuyRuns,checkpoints,deferredCheckpoints,battleStateEvents,battleStateFlushes,dailySpiralResets,theoryBatches,theoryLevels;
        [DllImport("kernelbase.dll",ExactSpelling=true)]private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
        private static ulong Now(){QueryUnbiasedInterruptTimePrecise(out var value);return value;}

        public static void Install()
        {
            if(installed)return;
            var runtime=Assembly.LoadFrom(Path.Combine(DirectoryPath,"IdleSpiral.SpeedPatch.SteamGuard.dll"));
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(runtime.Location))
                if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=RuntimeHash)
                    throw new InvalidOperationException("Unsupported simulation runtime.");
            readFactor=(Func<double>)Delegate.CreateDelegate(typeof(Func<double>),AccessTools.Method(runtime.GetType("IdleSpiralSpeedPatch.Patch"),"ReadFactor"));
            try
            {
                foreach(var controller in Controllers)
                    Harmony.Patch(AccessTools.Method(controller,"Tick",new[]{typeof(double)}),
                        prefix:new HarmonyMethod(typeof(AutomationGuard),nameof(BeforeTick)),finalizer:new HarmonyMethod(typeof(AutomationGuard),nameof(AfterTick)));
                Harmony.Patch(AccessTools.Method(typeof(UpgradeAutomationController),"RunAutomation"),postfix:new HarmonyMethod(typeof(AutomationGuard),nameof(CountAutoBuy)));
                Harmony.Patch(AccessTools.Method(typeof(PrestigeCheckpoint),"TrySave"),prefix:new HarmonyMethod(typeof(AutomationGuard),nameof(BeforeCheckpoint)));
                foreach(var view in StateViews)Harmony.Patch(view,prefix:new HarmonyMethod(typeof(AutomationGuard),nameof(DeferStateView)));
                Harmony.Patch(AccessTools.Method(typeof(GameSystem),"Update"),postfix:new HarmonyMethod(typeof(AutomationGuard),nameof(AfterFrame)));
                Harmony.Patch(AccessTools.Method(typeof(DailyBonusScreenController),"SecondsUntilLocalMidnight"),postfix:new HarmonyMethod(typeof(AutomationGuard),nameof(GameDayCountdown)));
                Harmony.Patch(AccessTools.Method(typeof(FirstTheoryTransaction),"LevelUpByLevel"),prefix:new HarmonyMethod(typeof(AutomationGuard),nameof(BatchTheoryLevels)));
                installed=true;Status("installed",1);
            }
            catch{Harmony.UnpatchAll(Owner);throw;}
        }
        public static void Remove(){Harmony.UnpatchAll(Owner);installed=false;Status("removed",1);}

        private static void BeforeTick(ref double realtimeSeconds)
        {
            double factor=readFactor();
            if(realtimeSeconds!=lastReal)
            {
                scaled=double.IsNaN(lastReal)?realtimeSeconds:scaled+Math.Max(0,realtimeSeconds-lastReal)*factor;
                lastReal=realtimeSeconds;
                if((++frames&1023)==1)Status("running",factor);
            }
            realtimeSeconds=scaled;
            inAutomation=true;
        }
        private static void CountAutoBuy(){autoBuyRuns++;}
        private static Exception AfterTick(Exception __exception){inAutomation=false;return __exception;}
        private static bool BeforeCheckpoint(ref bool __result)
        {
            if(!inAutomation)return true;
            var now=Now();
            if(lastCheckpoint!=0&&now-lastCheckpoint<CheckpointIntervalTicks){deferredCheckpoints++;__result=true;return false;}
            lastCheckpoint=now;checkpoints++;return true;
        }
        private static bool DeferStateView(object __instance,MethodBase __originalMethod)
        {
            if(flushingStateViews)return true;
            battleStateEvents++;
            foreach(var pending in PendingStateViews)
                if(ReferenceEquals(pending.Key,__instance)&&pending.Value==__originalMethod)return false;
            PendingStateViews.Add(new KeyValuePair<object,MethodBase>(__instance,__originalMethod));
            return false;
        }
        private static void AfterFrame(GameSystem __instance)
        {
            FlushStateViews(__instance);
            AdvanceGameDay(__instance.idleSystem);
        }
        private static void AdvanceGameDay(IdleSpiralSystem system)
        {
            var now=Now();
            double factor=readFactor();
            if(lastDayTicks!=0&&factor>1)extraDaySeconds+=(now-lastDayTicks)/1e7*(factor-1);
            lastDayTicks=now;
            var dto=system?.dto;
            gameDayActive=factor>1&&dto!=null&&system.dailySpiral!=null&&!SeasonRun.IsSeasonRun(dto);
            if(extraDaySeconds<GameDaySeconds)return;
            extraDaySeconds%=GameDaySeconds;
            if(!gameDayActive)return;
            dto.dailyLastResetDate=0;
            if(system.dailySpiral.EnsureCurrentDay())dailySpiralResets++;
        }
        private static void GameDayCountdown(ref long __result)
        {
            if(gameDayActive)__result=(long)Math.Min(__result*readFactor(),Math.Ceiling(GameDaySeconds-extraDaySeconds));
        }
        private static bool BatchTheoryLevels(FirstTheoryTransaction __instance,TheoryModel theory,long level)
        {
            if(!(theory.transaction is MultipleTransaction transaction))return true;
            var costs=TheoryCosts(transaction);
            foreach(var cost in costs)if(!(cost is Transaction))return true;
            var data=TheoryDataRef(__instance);
            if(data.level>FirstTheoryTransaction.Cap||!transaction.CanBuy())return false;
            double limit=readFactor()>1?MaxTheoryBatch:Math.Min(level,MaxTheoryBatch);
            foreach(Transaction cost in costs)
                if(cost.cost.Cost>0)limit=Math.Min(limit,BigDouble.Floor(cost.resource.Number/cost.cost.Cost).ToDouble());
            long count=Math.Min((long)Math.Max(0,limit),long.MaxValue-data.level);
            while(count>0&&!CanPay(costs,count))count--;
            foreach(Transaction cost in costs)cost.resource.Decrement(cost.cost.Cost*count);
            if(count>0)theory.UpdateResourceInfo();
            data.exp=0;
            data.level+=count;
            theoryBatches++;theoryLevels+=count;
            return false;
        }
        private static bool CanPay(IList<ITransaction> costs,long count)
        {
            foreach(Transaction cost in costs)if(cost.resource.Number<cost.cost.Cost*count)return false;
            return true;
        }
        private static void FlushStateViews(GameSystem __instance)
        {
            if(PendingStateViews.Count==0)return;
            var state=new object[]{__instance.idleSystem.battleCtrl.state};
            flushingStateViews=true;
            try
            {
                foreach(var pending in PendingStateViews)
                    if(pending.Key is UnityEngine.Object view&&view!=null){pending.Value.Invoke(view,state);battleStateFlushes++;}
            }
            finally{flushingStateViews=false;PendingStateViews.Clear();}
        }
        private static void Status(string state,double factor)
        {
            try
            {
                File.WriteAllText(Path.Combine(DirectoryPath,"automation-status.txt"),string.Format(CultureInfo.InvariantCulture,
                    "state={0}\nfactor={1:R}\nframes={2}\nscaledSeconds={3:R}\nautoBuyRuns={4}\ncheckpoints={5}\ndeferredCheckpoints={6}\nbattleStateEvents={7}\nbattleStateFlushes={8}\ndailySpiralResets={9}\nextraDaySeconds={10:F0}\ntheoryBatches={11}\ntheoryLevels={12}\n",
                    state,factor,frames,scaled,autoBuyRuns,checkpoints,deferredCheckpoints,battleStateEvents,battleStateFlushes,dailySpiralResets,extraDaySeconds,theoryBatches,theoryLevels));
            }
            catch{}
        }
    }
}
