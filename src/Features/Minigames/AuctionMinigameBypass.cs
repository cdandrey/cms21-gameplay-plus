using System.Collections;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.UI.Logic.Auction;
#else
using CMS;
using CMS.UI.Logic.Auction;
#endif

namespace Cms21GameplayPlus
{
    [HarmonyPatch]
    public static class AuctionMinigameBypass
    {
        [HarmonyPatch(typeof(AuctionBidding), nameof(AuctionBidding.Open))]
        [HarmonyPostfix]
        public static void OpenPostfix(AuctionBidding __instance)
        {
            if (!MinigameBypassFeature.IsAuctionEnabled || __instance == null)
                return;

            __instance.bidTime = 0.1f;
            if (__instance.console == null)
                return;
            __instance.console.ClearConsole();
            __instance.console.AddLine(BuildInfo.Name +
                " bypassAuctionMinigame enabled");
        }

        [HarmonyPatch(typeof(AuctionBidding), nameof(AuctionBidding.StartAuction))]
        [HarmonyPostfix]
        public static void StartAuctionPostfix(AuctionBidding __instance)
        {
            if (!MinigameBypassFeature.IsAuctionEnabled || __instance == null)
                return;

            MelonCoroutines.Start(CompleteAuction(__instance));
        }

        private static IEnumerator CompleteAuction(AuctionBidding bidding)
        {
            const int maximumFrames = 6000;
            int frames = 0;
            while (bidding != null && bidding.isActiveAndEnabled &&
                MinigameBypassFeature.IsAuctionEnabled && !bidding.auctionFinished &&
                frames < maximumFrames) {
                bidding.BidAction();
                frames++;
                yield return new WaitForEndOfFrame();
            }
        }
    }
}
