using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.Containers;
#else
using CMS;
using CMS.Containers;
#endif

namespace Cms21GameplayPlus
{
    internal enum BrakeLatheRepairabilityStatus
    {
        Available,
        Partial,
        Unavailable,
        UnavailableByDefault,
    }

    /// <summary>Extends brake-lathe part support and repair behavior.</summary>
    [HarmonyPatch]
    public static class BrakeLatheExtensionsFeature
    {
        private static readonly HashSet<string> GearPartIds = new HashSet<string> {
            "r6_rolkaLancucha",
            "v8_2_rolkaLancucha",
            "w12_rolka_lancucha_rozrzadu",
            "r6_m130_rolka_lancucha",
            "r6_xk_rolkaLancucha",
            "r6_240z_rolka_walka",
            "el_vw0_intermediate_reduction_gear",
            "el_vw0_final_drive_gear",
            "v8_m119_rolkaWalka_3",
            "b61_rolka_walka_1",
            "b61_rolka_walka_2",
            "b62_rolka_walka",
            "i3_rolkaWalka",
            "i4_rolkaWalka_1",
            "i4_rolkaWalka_2",
            "i6_old_rolkaWalka",
            "r4_sr20_rolka_walka_1",
            "r4_sr20_rolka_walka_2",
            "r5_rolka_walka_1",
            "r5_rolka_walka_2",
            "r6_rolkaWalka",
            "r6_s55_rolka_walka",
            "rolka_walka",
            "rolka_walka_sohc",
            "rot_new_element_nakladka",
            "v10_2_rolkaWalka",
            "v10_4_rolkaWalka",
            "v10_rolka_walka_1",
            "v10_rolka_walka_2",
            "v12_huayra_zebatka",
            "v6_30_rolkaWalka",
            "v6_37n_rolka_walka_1",
            "v6_37n_rolka_walka_2",
            "v8_2_rolkaWalka",
            "v8_62hemi_rolkaWalka",
            "v8_coyote_rolka_walka_1",
            "v8_coyote_rolka_walka_2",
            "v8_f350_rolka_walka",
            "v8_m119_rolka_walka_1",
            "v8_m119_rolka_walka_2",
            "v8_m159_rolka_walka",
            "v8_m177_rolka_walka",
            "v8_rolkaWalka_stara",
            "w12_rolkaWalka"
        };

        private static readonly HashSet<string> ClutchDiscPartIds = new HashSet<string> {
            "docisk_sprzegla",
            "t_docisk_sprzegla",
            "t_v8_kolo_zamachowe",
            "t_w12_kolo_zamachowe",
            "v8_kolo_zamachowe",
            "w12_kolo_zamachowe"
        };

        private static readonly HashSet<string> PulleyPartIds = new HashSet<string> {
            "b61_rolka_pompy_wody",
            "r4_sr20_rolka_pompy_wody",
            "rolka_pompy_wody",
            "rolka_pompy_wody_supercharger",
            "v10_2_rolka_pompy_wody",
            "v10_4_rolka_pompy_wody",
            "v10_rolka_pompy_wody",
            "v8_289_rolka_pompy_wody",
            "v8_360_rolka_pompy_wody",
            "v8_62hemi_rolka_pompy_wody",
            "v8_m119_rolka_wspornika",
            "w12_rolka_pompy_wody",
            "r4_kolo_pasowe_walu",
            "r4_m31_kolo_pasowe_walu",
            "r6_kolo_pasowe_walu",
            "r6_m88_kolo_pasowe_walu",
            "r6_s55_kolo_pasowe_walu",
            "v8_kolo_pasowe_walu",
            "v8_kolo_pasowe_walu_stare",
            "v8_m177_kolo_pasowe_walu"
        };

        private const float NativeRepairDurationSeconds = 20f;
        private const float NativeMachineAnimationDurationSeconds = 5f;
        private const float ImmediateTweenDurationSeconds = 0.0001f;
        private const int MachineAnimationStepCount = 4;
        private const float NativeCutterTargetZ = 0.2935f;
        private const float NativeReferenceDiameter = 0.31f;
        private const float NativeAdjusterRotationZ = 720f;
        private const float SoundFadeLeadSeconds = 1f;
        private const float ProcessingRingHideDelaySeconds = 0.05f;
        private const float GearTargetDiameter = 0.18f;
        private const float PulleyTargetDiameter = 0.20f;
        private const float ClutchDiscTargetDiameter = 0.30f;
        private const float MaxModelScaleMultiplier = 8f;

        private static int strapInstanceId;
        private static Vector3 strapNativeScale;

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetItemsForBrakeLathe))]
        [HarmonyPostfix]
        public static void GetItemsPostfix(
            Il2CppSystem.Collections.Generic.List<BaseItem> __result,
            Inventory __instance)
        {
            if (!GlobalState.IsGarageSceneActive || Main.SettingsEntry == null ||
                __result == null || __instance == null)
                return;

            Settings settings = Main.SettingsEntry.Value;
            if (!settings.allowBrakeLatheFixDrumBrake &&
                !settings.allowBrakeLatheFixGears &&
                !settings.allowBrakeLatheFixFlywheel &&
                !settings.allowBrakeLatheFixPulleys)
                return;

            HashSet<Item> existing = new HashSet<Item>();
            foreach (BaseItem baseItem in __result) {
                Item item = baseItem != null ? baseItem.TryCast<Item>() : null;
                if (item != null)
                    existing.Add(item);
            }

            GameInventory gameInventory = Singleton<GameInventory>.Instance;
            foreach (Item item in __instance.items) {
                if (!IsEnabledExtraPart(item, settings) ||
                    !RepairabilityManager.HasRepairGroup(
                        gameInventory, item.ID) ||
                    item.Condition <= GlobalData.JunkCondition ||
                    item.Condition >= 1f || !existing.Add(item))
                    continue;

                __result.Add(item);
            }
        }

        [HarmonyPatch(typeof(LeanTween), nameof(LeanTween.value),
            new Type[] { typeof(GameObject), typeof(float), typeof(float),
                typeof(float) })]
        [HarmonyPrefix]
        public static void RepairDurationPrefix(GameObject __0, float __1,
            float __2, ref float __3)
        {
            if (!GlobalState.IsGarageSceneActive ||
                !Mathf.Approximately(__2, 1f) ||
                !Mathf.Approximately(__3, NativeRepairDurationSeconds))
                return;

            ToolsManager tools = ToolsManager.Get();
            BrakeLatheLogic brakeLathe = tools != null
                ? tools.BrakeLatheLogic : null;
            if (brakeLathe == null || brakeLathe.Item == null ||
                __0 != brakeLathe.gameObject ||
                !Mathf.Approximately(__1, brakeLathe.Item.Condition))
                return;

            float machineAnimationDuration =
                GetMachineAnimationDurationSeconds();
            float repairDuration = machineAnimationDuration *
                MachineAnimationStepCount;
            if (repairDuration <= 0f) {
                __3 = ImmediateTweenDurationSeconds;
                CompleteRepairImmediately(brakeLathe);
                return;
            }

            __3 = repairDuration;
            if (brakeLathe.shaft != null && brakeLathe.brakeDisc != null) {
                MelonCoroutines.Start(FadeLatheSoundBeforeStop(
                    brakeLathe, brakeLathe.Item.ID,
                    brakeLathe.brakeDisc.GetInstanceID(), repairDuration));
            }
        }

        private static IEnumerator FadeLatheSoundBeforeStop(
            BrakeLatheLogic brakeLathe, string itemId, int modelInstanceId,
            float repairDuration)
        {
            yield return new WaitForSeconds(
                repairDuration - SoundFadeLeadSeconds);

            if (!GlobalState.IsGarageSceneActive || brakeLathe == null ||
                brakeLathe.Item == null || brakeLathe.Item.ID != itemId ||
                brakeLathe.brakeDisc == null ||
                brakeLathe.brakeDisc.GetInstanceID() != modelInstanceId ||
                brakeLathe.shaft == null)
                yield break;

            SoundManager soundManager = SoundManager.Get();
            if (soundManager != null)
                soundManager.StopLoopSFX(brakeLathe.shaft.gameObject, true);

            yield return new WaitForSeconds(
                SoundFadeLeadSeconds + ProcessingRingHideDelaySeconds);

            if (!GlobalState.IsGarageSceneActive || brakeLathe == null ||
                brakeLathe.Item == null || brakeLathe.Item.ID != itemId ||
                brakeLathe.brakeDisc == null ||
                brakeLathe.brakeDisc.GetInstanceID() != modelInstanceId ||
                brakeLathe.strap == null)
                yield break;

            brakeLathe.strap.gameObject.SetActive(false);
        }

        private static void CompleteRepairImmediately(
            BrakeLatheLogic brakeLathe)
        {
            if (brakeLathe == null || brakeLathe.Item == null)
                return;

            brakeLathe.Item.Condition = 1f;
            StopLatheSoundImmediately(brakeLathe);
            if (brakeLathe.strap != null)
                brakeLathe.strap.gameObject.SetActive(false);

            if (brakeLathe.brakeDisc != null) {
                MelonCoroutines.Start(CompleteImmediateRepairCleanup(
                    brakeLathe, brakeLathe.Item.ID,
                    brakeLathe.brakeDisc.GetInstanceID()));
            }
        }

        private static IEnumerator CompleteImmediateRepairCleanup(
            BrakeLatheLogic brakeLathe, string itemId, int modelInstanceId)
        {
            yield return null;
            if (!FinalizeImmediateRepair(brakeLathe, itemId, modelInstanceId))
                yield break;

            yield return new WaitForSeconds(ProcessingRingHideDelaySeconds);
            FinalizeImmediateRepair(brakeLathe, itemId, modelInstanceId);
        }

        private static bool FinalizeImmediateRepair(
            BrakeLatheLogic brakeLathe, string itemId, int modelInstanceId)
        {
            if (!GlobalState.IsGarageSceneActive || brakeLathe == null ||
                brakeLathe.Item == null || brakeLathe.Item.ID != itemId ||
                brakeLathe.brakeDisc == null ||
                brakeLathe.brakeDisc.GetInstanceID() != modelInstanceId)
                return false;

            brakeLathe.Item.Condition = 1f;
            StopLatheSoundImmediately(brakeLathe);
            if (brakeLathe.strap != null)
                brakeLathe.strap.gameObject.SetActive(false);
            return true;
        }

        private static void StopLatheSoundImmediately(
            BrakeLatheLogic brakeLathe)
        {
            if (brakeLathe == null || brakeLathe.shaft == null)
                return;
            SoundManager soundManager = SoundManager.Get();
            if (soundManager != null)
                soundManager.StopLoopSFX(brakeLathe.shaft.gameObject, false);
        }

        [HarmonyPatch(typeof(LeanTween), nameof(LeanTween.moveLocalZ),
            new Type[] { typeof(GameObject), typeof(float), typeof(float) })]
        [HarmonyPrefix]
        public static void CutterAnimationDurationPrefix(GameObject __0, ref float __1,
            ref float __2)
        {
            if (!GlobalState.IsGarageSceneActive ||
                !Mathf.Approximately(__1, NativeCutterTargetZ) ||
                !Mathf.Approximately(__2, NativeMachineAnimationDurationSeconds))
                return;

            ToolsManager tools = ToolsManager.Get();
            BrakeLatheLogic brakeLathe = tools != null
                ? tools.BrakeLatheLogic : null;
            if (brakeLathe == null || brakeLathe.Item == null ||
                brakeLathe.cutter == null || __0 != brakeLathe.cutter.gameObject)
                return;

            float targetDiameter;
            if (TryGetExtraPartTargetDiameter(brakeLathe.Item.ID,
                out targetDiameter)) {
                __1 = NativeCutterTargetZ -
                    (NativeReferenceDiameter - targetDiameter) * 0.5f;
            }

            float duration = GetMachineAnimationDurationSeconds();
            __2 = duration > 0f
                ? duration : ImmediateTweenDurationSeconds;
        }

        [HarmonyPatch(typeof(LeanTween), nameof(LeanTween.rotateZ),
            new Type[] { typeof(GameObject), typeof(float), typeof(float) })]
        [HarmonyPrefix]
        public static void AdjusterAnimationDurationPrefix(GameObject __0, float __1,
            ref float __2)
        {
            if (!GlobalState.IsGarageSceneActive ||
                !Mathf.Approximately(__1, NativeAdjusterRotationZ) ||
                !Mathf.Approximately(__2, NativeMachineAnimationDurationSeconds))
                return;

            ToolsManager tools = ToolsManager.Get();
            BrakeLatheLogic brakeLathe = tools != null
                ? tools.BrakeLatheLogic : null;
            if (brakeLathe == null || brakeLathe.Item == null ||
                brakeLathe.adjuster == null || __0 != brakeLathe.adjuster.gameObject)
                return;

            float duration = GetMachineAnimationDurationSeconds();
            __2 = duration > 0f
                ? duration : ImmediateTweenDurationSeconds;
        }

        [HarmonyPatch(typeof(BrakeLatheLogic), nameof(BrakeLatheLogic.SetItem),
            new Type[] { typeof(Item), typeof(bool) })]
        [HarmonyPostfix]
        public static void SetItemPostfix(BrakeLatheLogic __instance, Item __0)
        {
            if (!GlobalState.IsGarageSceneActive || __instance == null ||
                __0 == null)
                return;

            CaptureNativeStrapScale(__instance);

            float targetDiameter;
            if (!TryGetExtraPartTargetDiameter(__0.ID, out targetDiameter)) {
                RestoreNativeStrapScale(__instance);
                return;
            }

            if (__instance.brakeDisc == null)
                return;

            ConfigureExtraPartModel(__instance.brakeDisc, targetDiameter);
            ConfigureProcessingRing(__instance, targetDiameter);
        }

        private static float GetMachineAnimationDurationSeconds()
        {
            BrakeLatheProcessingDuration value = Main.SettingsEntry != null
                ? Main.SettingsEntry.Value.brakeLatheProcessingDuration
                : BrakeLatheProcessingDuration.Medium;
            switch (value) {
                case BrakeLatheProcessingDuration.Off:
                    return 0f;
                case BrakeLatheProcessingDuration.Fast:
                    return 1f;
                case BrakeLatheProcessingDuration.Medium:
                    return 2f;
                case BrakeLatheProcessingDuration.Slow:
                    return 3f;
                case BrakeLatheProcessingDuration.Default:
                    return 5f;
                default:
                    return 2f;
            }
        }

        private static bool TryGetExtraPartTargetDiameter(string itemId,
            out float targetDiameter)
        {
            if (GearPartIds.Contains(itemId)) {
                targetDiameter = GearTargetDiameter;
                return true;
            }
            if (ClutchDiscPartIds.Contains(itemId)) {
                targetDiameter = ClutchDiscTargetDiameter;
                return true;
            }
            if (PulleyPartIds.Contains(itemId)) {
                targetDiameter = PulleyTargetDiameter;
                return true;
            }

            targetDiameter = 0f;
            return false;
        }

        private static void CaptureNativeStrapScale(BrakeLatheLogic brakeLathe)
        {
            if (brakeLathe.strap == null)
                return;

            int instanceId = brakeLathe.strap.gameObject.GetInstanceID();
            if (strapInstanceId == instanceId)
                return;

            strapInstanceId = instanceId;
            strapNativeScale = brakeLathe.strap.localScale;
        }

        private static void RestoreNativeStrapScale(BrakeLatheLogic brakeLathe)
        {
            if (brakeLathe.strap == null ||
                brakeLathe.strap.gameObject.GetInstanceID() != strapInstanceId)
                return;

            brakeLathe.strap.localScale = strapNativeScale;
        }

        private static void ConfigureProcessingRing(BrakeLatheLogic brakeLathe,
            float targetDiameter)
        {
            if (brakeLathe.strap == null ||
                brakeLathe.strap.gameObject.GetInstanceID() != strapInstanceId)
                return;

            brakeLathe.strap.localScale = strapNativeScale;

            Bounds localBounds;
            if (!TryGetModelLocalBounds(brakeLathe.strap.gameObject,
                out localBounds))
                return;

            Vector3 size = localBounds.size;
            int thinAxis = GetThinAxis(size);
            float radialDiameter = 0f;
            if (thinAxis != 0)
                radialDiameter = Mathf.Max(radialDiameter,
                    size.x * Mathf.Abs(strapNativeScale.x));
            if (thinAxis != 1)
                radialDiameter = Mathf.Max(radialDiameter,
                    size.y * Mathf.Abs(strapNativeScale.y));
            if (thinAxis != 2)
                radialDiameter = Mathf.Max(radialDiameter,
                    size.z * Mathf.Abs(strapNativeScale.z));
            if (radialDiameter <= 0.0001f)
                return;

            float multiplier = targetDiameter / radialDiameter;
            Vector3 scale = strapNativeScale;
            if (thinAxis != 0)
                scale.x *= multiplier;
            if (thinAxis != 1)
                scale.y *= multiplier;
            if (thinAxis != 2)
                scale.z *= multiplier;
            brakeLathe.strap.localScale = scale;
        }

        private static int GetThinAxis(Vector3 size)
        {
            int thinAxis = 0;
            float smallest = size.x;
            if (size.y < smallest) {
                smallest = size.y;
                thinAxis = 1;
            }
            if (size.z < smallest)
                thinAxis = 2;
            return thinAxis;
        }

        private static void ConfigureExtraPartModel(GameObject model,
            float targetDiameter)
        {
            Bounds localBounds;
            if (!TryGetModelLocalBounds(model, out localBounds))
                return;

            Vector3 size = localBounds.size;
            int thinAxis = GetThinAxis(size);

            Quaternion alignment = Quaternion.identity;
            float radialDiameter;
            if (thinAxis == 1) {
                alignment = Quaternion.Euler(0f, 0f, 90f);
                radialDiameter = Mathf.Max(size.x, size.z);
            } else if (thinAxis == 2) {
                alignment = Quaternion.Euler(0f, 90f, 0f);
                radialDiameter = Mathf.Max(size.x, size.y);
            } else {
                radialDiameter = Mathf.Max(size.y, size.z);
            }

            Vector3 currentScale = model.transform.localScale;
            float rootScale = Mathf.Max(Mathf.Abs(currentScale.x),
                Mathf.Max(Mathf.Abs(currentScale.y), Mathf.Abs(currentScale.z)));
            float displayedDiameter = radialDiameter * rootScale;
            if (displayedDiameter > 0.0001f) {
                float multiplier = Mathf.Clamp(
                    targetDiameter / displayedDiameter, 1f,
                    MaxModelScaleMultiplier);
                model.transform.localScale = currentScale * multiplier;
            }

            model.transform.localRotation = alignment;

            Vector3 alignedCenter = alignment * localBounds.center;
            Vector3 localPosition = model.transform.localPosition;
            float appliedScale = Mathf.Max(
                Mathf.Abs(model.transform.localScale.x),
                Mathf.Max(Mathf.Abs(model.transform.localScale.y),
                    Mathf.Abs(model.transform.localScale.z)));
            localPosition.y = -alignedCenter.y * appliedScale;
            localPosition.z = -alignedCenter.z * appliedScale;
            model.transform.localPosition = localPosition;
        }

        private static bool TryGetModelLocalBounds(GameObject model,
            out Bounds combinedBounds)
        {
            combinedBounds = new Bounds();
            bool hasBounds = false;

            foreach (Renderer renderer in
                model.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;

                Bounds rendererBounds;
                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null) {
                    rendererBounds = meshFilter.sharedMesh.bounds;
                } else {
                    SkinnedMeshRenderer skinnedRenderer =
                        renderer as SkinnedMeshRenderer;
                    if (skinnedRenderer != null &&
                        skinnedRenderer.sharedMesh != null) {
                        rendererBounds = skinnedRenderer.sharedMesh.bounds;
                    } else {
                        Bounds worldBounds = renderer.bounds;
                        Vector3 worldMin = worldBounds.min;
                        Vector3 worldMax = worldBounds.max;
                        for (int worldCorner = 0; worldCorner < 8; worldCorner++) {
                            Vector3 worldPoint = new Vector3(
                                (worldCorner & 1) == 0 ? worldMin.x : worldMax.x,
                                (worldCorner & 2) == 0 ? worldMin.y : worldMax.y,
                                (worldCorner & 4) == 0 ? worldMin.z : worldMax.z);
                            Vector3 modelPoint =
                                model.transform.InverseTransformPoint(worldPoint);
                            if (!hasBounds) {
                                combinedBounds = new Bounds(modelPoint, Vector3.zero);
                                hasBounds = true;
                            } else {
                                combinedBounds.Encapsulate(modelPoint);
                            }
                        }
                        continue;
                    }
                }

                Vector3 min = rendererBounds.min;
                Vector3 max = rendererBounds.max;
                for (int corner = 0; corner < 8; corner++) {
                    Vector3 rendererPoint = new Vector3(
                        (corner & 1) == 0 ? min.x : max.x,
                        (corner & 2) == 0 ? min.y : max.y,
                        (corner & 4) == 0 ? min.z : max.z);
                    Vector3 modelPoint = model.transform.InverseTransformPoint(
                        renderer.transform.TransformPoint(rendererPoint));
                    if (!hasBounds) {
                        combinedBounds = new Bounds(modelPoint, Vector3.zero);
                        hasBounds = true;
                    } else {
                        combinedBounds.Encapsulate(modelPoint);
                    }
                }
            }

            return hasBounds;
        }

        internal static IEnumerable<string> GetSupportedRepairabilityPartIds()
        {
            yield return "pokrywaBeben_1";
            foreach (string partId in GearPartIds)
                yield return partId;
            foreach (string partId in ClutchDiscPartIds)
                yield return partId;
            foreach (string partId in PulleyPartIds)
                yield return partId;
        }

        internal static void GetRepairabilityStatuses(
            GameInventory inventory, IDictionary<string, int> repairGroups,
            bool useDefaultRepairability,
            out BrakeLatheRepairabilityStatus drumStatus,
            out BrakeLatheRepairabilityStatus gearsStatus,
            out BrakeLatheRepairabilityStatus clutchDiscsStatus,
            out BrakeLatheRepairabilityStatus pulleysStatus)
        {
            drumStatus = GetRepairabilityStatus(inventory,
                new string[] { "pokrywaBeben_1" }, useDefaultRepairability,
                repairGroups);
            gearsStatus = GetRepairabilityStatus(inventory, GearPartIds,
                useDefaultRepairability, repairGroups);
            clutchDiscsStatus = GetRepairabilityStatus(inventory,
                ClutchDiscPartIds, useDefaultRepairability, repairGroups);
            pulleysStatus = GetRepairabilityStatus(inventory, PulleyPartIds,
                useDefaultRepairability, repairGroups);
        }

        internal static bool SynchronizeRepairabilitySettings(
            GameInventory inventory, out BrakeLatheRepairabilityStatus drumStatus,
            out BrakeLatheRepairabilityStatus gearsStatus,
            out BrakeLatheRepairabilityStatus clutchDiscsStatus,
            out BrakeLatheRepairabilityStatus pulleysStatus)
        {
            bool useDefaultRepairability = Main.SettingsEntry != null &&
                !Main.SettingsEntry.Value.modifyRepairGroups;
            GetRepairabilityStatuses(inventory, null,
                useDefaultRepairability, out drumStatus, out gearsStatus,
                out clutchDiscsStatus, out pulleysStatus);

            if (Main.SettingsEntry == null)
                return false;

            Settings settings = Main.SettingsEntry.Value;
            bool changed = false;
            if (IsUnavailable(drumStatus) &&
                settings.allowBrakeLatheFixDrumBrake) {
                settings.allowBrakeLatheFixDrumBrake = false;
                changed = true;
            }
            if (IsUnavailable(gearsStatus) && settings.allowBrakeLatheFixGears) {
                settings.allowBrakeLatheFixGears = false;
                changed = true;
            }
            if (IsUnavailable(clutchDiscsStatus) &&
                settings.allowBrakeLatheFixFlywheel) {
                settings.allowBrakeLatheFixFlywheel = false;
                changed = true;
            }
            if (IsUnavailable(pulleysStatus) &&
                settings.allowBrakeLatheFixPulleys) {
                settings.allowBrakeLatheFixPulleys = false;
                changed = true;
            }
            return changed;
        }

        internal static bool IsAvailable(BrakeLatheRepairabilityStatus status)
        {
            return !IsUnavailable(status);
        }

        private static bool IsUnavailable(
            BrakeLatheRepairabilityStatus status)
        {
            return status == BrakeLatheRepairabilityStatus.Unavailable ||
                status == BrakeLatheRepairabilityStatus.UnavailableByDefault;
        }

        private static BrakeLatheRepairabilityStatus GetRepairabilityStatus(
            GameInventory inventory, IEnumerable<string> partIds,
            bool useDefaultRepairability,
            IDictionary<string, int> repairGroups)
        {
            int existing = 0;
            int repairable = 0;
            foreach (string partId in partIds) {
                if (inventory == null ||
                    !inventory.ExistsInPartProperty(partId))
                    continue;
                existing++;
                int repairGroup;
                bool hasRepairGroup = repairGroups != null &&
                    repairGroups.TryGetValue(partId, out repairGroup)
                        ? repairGroup != 0
                        : RepairabilityManager.HasRepairGroup(inventory,
                            partId);
                if (hasRepairGroup)
                    repairable++;
            }

            if (existing == 0 || repairable == existing)
                return BrakeLatheRepairabilityStatus.Available;
            if (repairable > 0)
                return BrakeLatheRepairabilityStatus.Partial;
            return useDefaultRepairability
                ? BrakeLatheRepairabilityStatus.UnavailableByDefault
                : BrakeLatheRepairabilityStatus.Unavailable;
        }

        private static bool IsEnabledExtraPart(Item item, Settings settings)
        {
            if (item == null)
                return false;
            if (settings.allowBrakeLatheFixDrumBrake && item.ID == "pokrywaBeben_1")
                return true;
            if (settings.allowBrakeLatheFixGears && GearPartIds.Contains(item.ID))
                return true;
            if (settings.allowBrakeLatheFixFlywheel &&
                ClutchDiscPartIds.Contains(item.ID))
                return true;
            return settings.allowBrakeLatheFixPulleys && PulleyPartIds.Contains(item.ID);
        }
    }
}
