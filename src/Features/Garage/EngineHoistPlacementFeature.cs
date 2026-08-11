using System.Collections;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.Managers;
#else
using CMS;
using CMS.Managers;
#endif

namespace Cms21GameplayPlus
{
    public static class EngineHoistPlacementFeature
    {
        private static readonly Vector3 EngineHoistPosition =
            new Vector3(-13.8f, 0f, -3.08f);
        private static readonly Vector3 EngineHoistRotation =
            new Vector3(0f, 34f, 0f);

        public static void OnSceneInitialized(string sceneName)
        {
            if (sceneName == "garage" && IsEnabled)
                MelonCoroutines.Start(RelocateEngineHoistWhenReady());
        }

        private static bool IsEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.relocateEngineHoistNearStand;
            }
        }

        private static IEnumerator RelocateEngineHoistWhenReady()
        {
            const int attempts = 20;
            for (int attempt = 0; attempt < attempts && IsEnabled; attempt++) {
                ToolsMoveManager manager = Singleton<ToolsMoveManager>.Instance;
                if (manager != null && manager.EngineCrane != null) {
                    manager.engineCraneDefaultPosition = EngineHoistPosition;
                    manager.engineCraneDefaultRotation = EngineHoistRotation;
                    manager.EngineCrane.transform.position = EngineHoistPosition;
                    manager.EngineCrane.transform.eulerAngles = EngineHoistRotation;
                    ModLogger.Log("[EngineHoist] Relocated next to the engine stand.",
                        Types.LoggingLevels.Debug);
                    yield break;
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            ModLogger.Log("[EngineHoist] ToolsMoveManager or EngineCrane was not ready.",
                Types.LoggingLevels.Warning);
        }
    }
}
