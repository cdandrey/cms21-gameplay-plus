using System.Collections;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.UI.Windows;
#else
using CMS;
using CMS.UI.Windows;
#endif

namespace Cms21GameplayPlus
{
    [HarmonyPatch]
    public static class HeadlampAlignmentMinigameBypass
    {
        [HarmonyPatch(typeof(LampAlignmentWindow), nameof(LampAlignmentWindow.Show))]
        [HarmonyPostfix]
        public static void ShowPostfix(LampAlignmentWindow __instance)
        {
            if (!MinigameBypassFeature.IsEnabled || __instance == null ||
                __instance.carLoader == null)
                return;

            ModLogger.Log("[Minigames] Bypassing headlamp alignment.",
                Types.LoggingLevels.Debug);
            __instance.carLoader.HeadlampLeftAlignment =
                new HeadlampAlignment { Horizontal = 0, Vertical = 0 };
            __instance.carLoader.HeadlampRightAlignment =
                new HeadlampAlignment { Horizontal = 0, Vertical = 0 };
            MelonCoroutines.Start(HideAfterDelay(__instance));
        }

        private static IEnumerator HideAfterDelay(LampAlignmentWindow window)
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager
                .GetActiveScene().name;
            yield return new WaitForFixedUpdate();
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.5f);

            if (!MinigameBypassFeature.IsEnabled || window == null ||
                !window.isActiveAndEnabled ||
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name !=
                    sceneName)
                yield break;

            window.HideAction();
        }
    }
}
