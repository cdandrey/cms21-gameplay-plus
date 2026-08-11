using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.SceneLoaders;
using Il2CppCMS.Tracks.CarPhysics;
using Il2CppCMS.UI;
using Il2CppCMS.UI.Logic;
using Il2CppCMS.UI.Windows;
#else
using CMS.SceneLoaders;
using CMS.Tracks.CarPhysics;
using CMS.UI;
using CMS.UI.Logic;
using CMS.UI.Windows;
#endif

namespace Cms21GameplayPlus
{
    public static class AuctionCarPoolFeature
    {
        private static readonly Vector2 ExpandedAmountRange = new Vector2(300f, 450f);

        public static void OnSceneInitialized(string sceneName)
        {
            if (!IsEnabled || sceneName != "Auctions")
                return;

            MelonCoroutines.Start(ApplyWhenManagerIsReady());
        }

        private static bool IsEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.expandedAuctionCarPool;
            }
        }

        private static System.Collections.IEnumerator ApplyWhenManagerIsReady()
        {
            const int attempts = 20;
            for (int attempt = 0; attempt < attempts && IsEnabled; attempt++) {
                AuctionManager manager = UnityEngine.Object.FindObjectOfType<AuctionManager>();
                if (manager != null) {
                    manager.normalCarsAmountRange = ExpandedAmountRange;
                    manager.salvageCarsAmountRange = ExpandedAmountRange;
                    ModLogger.Log("[AuctionCarPool] Expanded auction car ranges to 300-450.",
                        Types.LoggingLevels.Debug);
                    yield break;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }

            ModLogger.Log("[AuctionCarPool] AuctionManager was not found.",
                Types.LoggingLevels.Warning);
        }
    }
}
