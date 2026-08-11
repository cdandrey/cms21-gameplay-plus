using System.Collections.Generic;
using HarmonyLib;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.Containers;
#else
using CMS;
using CMS.Containers;
#endif

namespace Cms21GameplayPlus
{
    /// <summary>Adds repairable brake drums to the brake-lathe inventory.</summary>
    [HarmonyPatch]
    public static class BrakeLatheExtensionsFeature
    {
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetItemsForBrakeLathe))]
        [HarmonyPostfix]
        public static void GetItemsPostfix(
            Il2CppSystem.Collections.Generic.List<BaseItem> __result,
            Inventory __instance)
        {
            if (!GlobalState.IsGarageSceneActive || Main.SettingsEntry == null ||
                !Main.SettingsEntry.Value.allowBrakeLatheFixDrumBrake ||
                __result == null || __instance == null)
                return;

            HashSet<Item> existing = new HashSet<Item>();
            foreach (BaseItem baseItem in __result) {
                Item item = baseItem != null ? baseItem.TryCast<Item>() : null;
                if (item != null)
                    existing.Add(item);
            }

            int added = 0;
            foreach (Item item in __instance.items) {
                if (item.ID != "pokrywaBeben_1" ||
                    item.Condition <= GlobalData.JunkCondition ||
                    item.Condition >= 1f || !existing.Add(item))
                    continue;

                __result.Add(item);
                added++;
            }

            if (added > 0) {
                ModLogger.Log("[BrakeLathe] Added " + added +
                    " repairable brake drum(s).", Types.LoggingLevels.Debug);
            }
        }
    }
}
