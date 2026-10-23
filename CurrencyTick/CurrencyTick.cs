using System;
using System.IO;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    // One-shot: applies the game's own currency update tick setting (persisted to the save) on the Unity main thread.
    public static class Loader
    {
        private const string Owner="local.idlespiral.currencytick.once";
        private const int Tick=20;
        private static readonly Harmony Harmony=new Harmony(Owner);
        private static readonly string Output=Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location),"currencytick-status.txt");
        public static void Install()
        {
            File.WriteAllText(Output,"state=scheduled\n");
            Harmony.Patch(AccessTools.Method(typeof(GameSystem),"Update"),postfix:new HarmonyMethod(typeof(Loader),nameof(Apply)));
        }
        public static void Remove(){Harmony.UnpatchAll(Owner);}
        private static void Apply(GameSystem __instance)
        {
            Harmony.UnpatchAll(Owner);
            try
            {
                int before=(int)__instance.UpdateCurrencySecond;
                int applied=__instance.ApplyCurrencyUpdateTick(Tick);
                File.WriteAllText(Output,$"state=applied\nbefore={before}\napplied={applied}\nsave={IdleLibrary.Main.main?.S?.currencyUpdateTick}\n");
            }
            catch(Exception e){File.WriteAllText(Output,"state=failed\n"+e);}
        }
    }
}
