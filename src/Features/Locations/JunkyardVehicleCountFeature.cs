using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.UI.Logic;
#else
using CMS.UI.Logic;
#endif

namespace Cms21GameplayPlus
{
    /// <summary>Optionally forces the junkyard generator to its maximum car count.</summary>
    public static class JunkyardVehicleCountFeature
    {
        private static bool IsEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.junkyardVehicleMaximumCars;
            }
        }

        public static void OnSceneInitialized(string sceneName)
        {
            if (!IsEnabled || sceneName != "Junkyard")
                return;

            MelonCoroutines.Start(ApplyWhenGeneratorIsReady());
        }

        private static System.Collections.IEnumerator ApplyWhenGeneratorIsReady()
        {
            const int attempts = 20;
            for (int attempt = 0; attempt < attempts && IsEnabled; attempt++) {
                JunkyardGenerator generator =
                    UnityEngine.Object.FindObjectOfType<JunkyardGenerator>();
                if (generator != null) {
                    generator.CarsPercentage = new Vector2(100f, 100f);
                    ModLogger.Log(
                        "[JunkyardVehicles] Maximum car generation enabled.",
                        Types.LoggingLevels.Debug);
                    yield break;
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            ModLogger.Log(
                "[JunkyardVehicles] JunkyardGenerator was not found.",
                Types.LoggingLevels.Warning);
        }
    }
}
