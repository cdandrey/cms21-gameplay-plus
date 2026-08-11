using System.Collections;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.SceneLoaders;
#else
using CMS;
using CMS.SceneLoaders;
#endif

namespace Cms21GameplayPlus
{
    /// <summary>Stores and restores the state of the vanilla garage, paint-shop, and office doors.</summary>
    [HarmonyPatch]
    public static class GarageDoorStateFeature
    {
        private const int MaximumSceneLoaderWaits = 120;
        private static bool IsEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.rememberGarageDoorState;
            }
        }

        public static void OnGarageSceneInitialized()
        {
            if (IsEnabled)
                MelonCoroutines.Start(RestoreRememberedStateWhenReady());
        }

        [HarmonyPatch(typeof(GarageTeleport), nameof(GarageTeleport.Use))]
        [HarmonyPostfix]
        public static void GarageTeleportUsePostfix(GarageTeleport __instance)
        {
            if (!IsEnabled || !GlobalState.IsGarageSceneActive || __instance == null)
                return;

            Types.ProfileState profile = CurrentProfile;
            bool changed = false;
            switch (__instance.name) {
                case "Door_4_wing_left":
                    changed = SetState(ref profile.garageDoorLeftWasOpen,
                        __instance.isOpen);
                    break;
                case "Door_4_wing_right":
                    changed = SetState(ref profile.garageDoorRightWasOpen,
                        __instance.isOpen);
                    break;
                case "Paintshop_Booth_Door_Left":
                    changed = SetState(ref profile.paintShopDoorLeftWasOpen,
                        __instance.isOpen);
                    break;
                case "Paintshop_Booth_Door_Right":
                    changed = SetState(ref profile.paintShopDoorRightWasOpen,
                        __instance.isOpen);
                    break;
                case "Paintshop_Booth_Door_Small":
                    changed = SetState(ref profile.paintShopDoorSmallWasOpen,
                        __instance.isOpen);
                    break;
                case "Office_Entrance_Door_R":
                    changed = SetState(ref profile.officeDoorRightWasOpen,
                        __instance.isOpen);
                    break;
                case "Office_Entrance_Door_L":
                    changed = SetState(ref profile.officeDoorLeftWasOpen,
                        __instance.isOpen);
                    break;
            }

            if (changed)
                Main.MarkProfileMemoryDirty();
        }

        private static IEnumerator RestoreRememberedStateWhenReady()
        {
            yield return new WaitForSeconds(1f);

            int waits = 0;
            while (SceneLoader.blockProgress && waits < MaximumSceneLoaderWaits) {
                if (!IsGarageLoaded())
                    yield break;
                waits++;
                yield return new WaitForSeconds(0.5f);
            }

            if (SceneLoader.blockProgress || !IsGarageLoaded()) {
                ModLogger.Log("[GarageDoors] Scene initialization did not complete " +
                    "within the wait limit.", Types.LoggingLevels.Warning);
                yield break;
            }

            if (IsEnabled)
                RestoreRememberedState();
        }

        private static void RestoreRememberedState()
        {
            if (GlobalState.GameManager == null)
                return;

            Types.ProfileState profile = CurrentProfile;
            RestoreDoor("Door_4_wing_left", profile.garageDoorLeftWasOpen);
            RestoreDoor("Door_4_wing_right", profile.garageDoorRightWasOpen);
            RestoreDoor("Paintshop_Booth_Door_Left", profile.paintShopDoorLeftWasOpen);
            RestoreDoor("Paintshop_Booth_Door_Right", profile.paintShopDoorRightWasOpen);
            RestoreDoor("Paintshop_Booth_Door_Small", profile.paintShopDoorSmallWasOpen);
            RestoreDoor("Office_Entrance_Door_R", profile.officeDoorRightWasOpen);
            RestoreDoor("Office_Entrance_Door_L", profile.officeDoorLeftWasOpen);
        }

        private static void RestoreDoor(string objectName, bool shouldBeOpen)
        {
            GameObject doorObject = GameObject.Find(objectName);
            if (doorObject == null)
                return;

            GarageTeleport door = doorObject.GetComponentInChildren<GarageTeleport>();
            if (door == null || door.isOpen == shouldBeOpen)
                return;

            GameScript gameScript = GameScript.Get();
            if (gameScript == null)
                return;

            gameScript.StartCoroutine(door.Switch(true));
            ModLogger.Log("[GarageDoors] Restored " + objectName + " to " +
                (shouldBeOpen ? "open" : "closed") + ".", Types.LoggingLevels.Debug);
        }

        private static bool SetState(ref bool target, bool value)
        {
            if (target == value)
                return false;
            target = value;
            return true;
        }

        private static bool IsGarageLoaded()
        {
            return GlobalState.IsGarageSceneActive &&
                UnityEngine.SceneManagement.SceneManager.GetSceneByName("garage").isLoaded;
        }

        private static Types.ProfileState CurrentProfile {
            get {
                return Main.ProfileMemory.profileStates[
                    GlobalState.LoadedProfileId];
            }
        }
    }
}
