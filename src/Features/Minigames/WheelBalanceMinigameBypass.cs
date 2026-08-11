using System.Collections;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.Containers;
using Il2CppCMS.UI;
using Il2CppCMS.UI.Logic;
using Il2CppCMS.UI.Windows;
#else
using CMS;
using CMS.Containers;
using CMS.UI;
using CMS.UI.Logic;
using CMS.UI.Windows;
#endif

namespace Cms21GameplayPlus
{
    [HarmonyPatch]
    public static class WheelBalanceMinigameBypass
    {
        [HarmonyPatch(typeof(WheelBalanceWindow), nameof(WheelBalanceWindow.StartMiniGame))]
        [HarmonyPostfix]
        public static void StartMiniGamePostfix(WheelBalanceWindow __instance)
        {
            if (!MinigameBypassFeature.IsWheelBalanceEnabled || __instance == null)
                return;
            MelonCoroutines.Start(CompleteAfterDelay(__instance));
        }

        private static IEnumerator CompleteAfterDelay(WheelBalanceWindow window)
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager
                .GetActiveScene().name;
            yield return new WaitForFixedUpdate();
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.5f);

            if (!MinigameBypassFeature.IsWheelBalanceEnabled || window == null ||
                !window.isActiveAndEnabled ||
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name !=
                    sceneName)
                yield break;

            WheelBalancerLogic logic = GameObject.FindObjectOfType<WheelBalancerLogic>();
            if (logic == null || logic.groupOnWheelBalancer == null ||
                logic.groupOnWheelBalancer.ItemList == null)
                yield break;

            foreach (Item item in logic.groupOnWheelBalancer.ItemList) {
                if (item == null)
                    continue;
                item.WheelData = new WheelData {
                    ET = item.WheelData.ET,
                    Profile = item.WheelData.Profile,
                    Width = item.WheelData.Width,
                    Size = item.WheelData.Size,
                    IsBalanced = true
                };
            }

            if (window == null || !window.isActiveAndEnabled)
                yield break;
            window.CancelAction();

            yield return new WaitForFixedUpdate();
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.5f);
            if (MinigameBypassFeature.IsWheelBalanceEnabled && logic != null &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ==
                    sceneName)
                logic.balanceCanceled = false;
        }
    }
}
