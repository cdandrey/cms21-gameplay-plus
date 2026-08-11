using HarmonyLib;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.UI.Logic;
using Il2CppCMS.UI.Logic.Scrap;
using Il2CppCMS.UI.Windows;
#else
using CMS;
using CMS.UI.Logic;
using CMS.UI.Logic.Scrap;
using CMS.UI.Windows;
#endif

namespace Cms21GameplayPlus
{
    [HarmonyPatch]
    public static class PartRepairMinigameBypass
    {
        [HarmonyPatch(typeof(RepairPartWindow), nameof(RepairPartWindow.StartMiniGame))]
        [HarmonyPostfix]
        public static void StartMiniGamePostfix(RepairPartWindow __instance)
        {
            if (!MinigameBypassFeature.IsEnabled)
                return;

            ModLogger.Log("[Minigames] Bypassing part repair.",
                Types.LoggingLevels.Debug);
            __instance.ProcessGameResult(BarType.Success);
            __instance.CancelMiniGameAction();
        }
    }
}
