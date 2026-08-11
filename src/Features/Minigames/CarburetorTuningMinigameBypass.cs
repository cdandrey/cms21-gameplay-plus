using HarmonyLib;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppCMS.UI.Logic.Tune;
#else
using CMS;
using UnhollowerBaseLib;
using CMS.UI.Logic.Tune;
#endif

namespace Cms21GameplayPlus
{
    [HarmonyPatch]
    public static class CarburetorTuningMinigameBypass
    {
        [HarmonyPatch(typeof(TuningBar), nameof(TuningBar.DecrementValue))]
        [HarmonyPostfix]
        public static void DecrementValuePostfix(TuningBar __instance)
        {
            ApplySuccessfulTune(__instance);
        }

        [HarmonyPatch(typeof(TuningBar), nameof(TuningBar.IncrementValue))]
        [HarmonyPostfix]
        public static void IncrementValuePostfix(TuningBar __instance)
        {
            ApplySuccessfulTune(__instance);
        }

        private static void ApplySuccessfulTune(TuningBar tuningBar)
        {
            if (!MinigameBypassFeature.IsEnabled || !tuningBar.carbTuning)
                return;

            ModLogger.Log("[Minigames] Bypassing carburetor tuning.",
                Types.LoggingLevels.Debug);
            Il2CppStructArray<short> values = new Il2CppStructArray<short>(3);
            for (int i = 0; i < values.Length; i++)
                values[i] = 2;

            CarbTuning tuning = GameObject.FindObjectOfType<CarbTuning>();
            if (tuning == null)
                return;
            tuning.SetBars(values);
            tuning.currentTune = tuning.CalcTuningValue();
            tuning.UpdateTuningText();
        }
    }
}
