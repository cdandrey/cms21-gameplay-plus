using System;
using System.Collections.Generic;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
#else
using CMS;
#endif

namespace Cms21GameplayPlus
{
    internal static partial class SharpEyeShoppingListFeature
    {
        private static class InspectionVisualSystem
        {
            private const string BrakeServoPartId = "serwoHamulca_1";
            private const string BrakeServoCapPartId = "serwoHamulca_1_cap";
            private const string RadiatorCPartId = "chlodnica_2";
            private const string RadiatorCCapPartId = "chlodnica_2_nakretka";
            private const string CoolantReservoir1PartId =
                "coolant_reservoir_1_body";
            private const string CoolantReservoir2PartId =
                "coolant_reservoir_2_body";
            private const string CoolantReservoir3PartId =
                "coolant_reservoir_3_body";
            private const string CoolantCap1PartId = "coolant_cap_1";
            private const string CoolantCap2PartId = "coolant_cap_2";
            private const string CoolantCap3PartId = "coolant_cap_3";
            private const string PowerSteeringReservoirPartId =
                "power_steering_reservoir_1_body";
            private const string PowerSteeringCapPartId =
                "power_steering_cap_1";
            private const string OilDrainPlugPartId = "korek_spustowy_1";

            private static int activeSkillLevel = -1;

            internal static bool IsActive
            {
                get { return inspectionSystemsOverlayActive; }
            }

            internal static CarLoader Loader
            {
                get { return inspectionSystemsOverlayLoader; }
            }

            internal static void EnsureInitialized(Raycast raycast,
                int skillLevel)
            {
                CarLoader loader = inspectionSystemsOverlayLoader ??
                    ResolveInspectionLoader(raycast);
                if (loader == null)
                    return;

                int loaderId;
                try {
                    loaderId = loader.GetInstanceID();
                } catch {
                    return;
                }
                int activeLoaderId = -1;
                if (inspectionSystemsOverlayLoader != null) {
                    try {
                        activeLoaderId = inspectionSystemsOverlayLoader.
                            GetInstanceID();
                    } catch {
                    }
                }

                if (inspectionSystemsOverlayActive &&
                    activeLoaderId == loaderId &&
                    activeSkillLevel == skillLevel)
                    return;

                ShowInspectionSystemsOverlay(loader, skillLevel);
                activeSkillLevel = skillLevel;
                if (skillLevel > 0)
                    ShowBodyBaseHighlight(loader);
                else
                    ClearBodyHighlight();
            }

            internal static void Exit()
            {
                activeSkillLevel = -1;
                ClearBodyHighlight();
                ClearInspectionSystemsOverlay();
            }

            internal static void ApplyHover(InspectionHoverState hover,
                InspectionTargetState targetState, int skillLevel)
            {
                if (skillLevel <= 0) {
                    ClearBodyHighlight();
                    ClearEmptySystemHighlight();
                    return;
                }

                if (hover.System != null) {
                    ClearBodyHighlight();
                    UpdateEmptySystemHighlight(hover.System,
                        targetState.Completed);
                    return;
                }

                ClearEmptySystemHighlight();
                if (hover.Body != null && hover.Loader != null) {
                    ShowBodyHover(hover.Loader, hover.Body,
                        targetState.Completed);
                    return;
                }
                RestoreBodyBaseHighlight();
            }

            internal static void ClearHover()
            {
                ClearEmptySystemHighlight();
                if (activeSkillLevel > 0 && inspectionSystemsOverlayActive)
                    RestoreBodyBaseHighlight();
                else
                    ClearBodyHighlight();
            }

            internal static void SyncInspectionState()
            {
                if (!inspectionSystemsOverlayActive)
                    return;

                foreach (KeyValuePair<int, List<PartScript>> entry in
                    inspectionSystemsOverlayPreviewPartsBySystem) {
                    List<PartScript> parts = entry.Value;
                    if (parts == null || parts.Count == 0)
                        continue;

                    InteractiveObject system = null;
                    for (int index = 0; index < parts.Count; index++) {
                        PartScript part = parts[index];
                        if (part == null)
                            continue;
                        try {
                            if (inspectionSystemsOverlaySystemByPartId.
                                TryGetValue(part.GetInstanceID(), out system) &&
                                system != null)
                                break;
                        } catch {
                        }
                    }
                    if (system == null)
                        continue;

                    SystemPassState state = GetSystemPassState(system);
                    for (int index = 0; index < parts.Count; index++) {
                        PartScript part = parts[index];
                        if (part == null)
                            continue;
                        string partId = SafePartId(part);
                        if (IsDependentVisualPart(system, partId)) {
                            SetInspectionSystemsOverlayInstalledPartColor(part,
                                false);
                            continue;
                        }
                        if (IsInspectionSystemsOverlayMissingPart(part)) {
                            SetInspectionSystemsOverlayMissingPartColor(part,
                                IsInspectionSystemsOverlayMissingPartExamined(
                                    system, part));
                            continue;
                        }

                        bool examined = false;
                        try {
                            examined = state != null &&
                                state.ExaminedPartInstanceIds.Contains(
                                    part.GetInstanceID());
                        } catch {
                        }
                        SetInspectionSystemsOverlayInstalledPartColor(part,
                            examined);
                    }
                }
            }

            internal static bool IsDependentVisualPart(
                InteractiveObject system, string partId)
            {
                if (system == null || string.IsNullOrEmpty(partId))
                    return false;
                return string.Equals(partId, EngineOilDipstickPartId,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partId, EngineOilFillPlugPartId,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partId, OilDrainPlugPartId,
                        StringComparison.OrdinalIgnoreCase) ||
                    GetFixedDependentAnchorPartId(partId) != null;
            }

            internal static void ApplyDependentPartVisibility(
                InteractiveObject system, int skillLevel,
                HashSet<int> solidHighlighted)
            {
                if (system == null || solidHighlighted == null)
                    return;

                string engineBlockPartId = GetEngineBlockPartId(system);
                int engineBlockLevel = GetDependentAnchorLevel(system,
                    engineBlockPartId);
                string oilPanPartId = GetOilPanPartId(system);
                int oilPanLevel = GetDependentAnchorLevel(system,
                    oilPanPartId);

                PartScript[] parts;
                try {
                    parts = system.GetComponentsInChildren<PartScript>(true);
                } catch {
                    return;
                }

                for (int index = 0; index < parts.Length; index++) {
                    PartScript part = parts[index];
                    if (part == null)
                        continue;
                    string partId = SafePartId(part);
                    if (!IsDependentVisualPart(system, partId))
                        continue;

                    int requiredLevel;
                    if (string.Equals(partId, EngineOilDipstickPartId,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(partId, EngineOilFillPlugPartId,
                            StringComparison.OrdinalIgnoreCase)) {
                        requiredLevel = engineBlockLevel;
                    } else if (string.Equals(partId, OilDrainPlugPartId,
                            StringComparison.OrdinalIgnoreCase)) {
                        requiredLevel = oilPanLevel;
                    } else {
                        requiredLevel = GetDependentAnchorLevel(system,
                            GetFixedDependentAnchorPartId(partId));
                    }

                    bool visible = requiredLevel > 0 &&
                        requiredLevel <= skillLevel;
                    if (!visible) {
                        HideUnavailablePart(part);
                        continue;
                    }

                    AddInspectionSystemsOverlayInstalledPart(part, false,
                        solidHighlighted);
                    ShowInspectionSystemsOverlayDependentTransform(
                        part.transform);
                    AddInspectionSystemsOverlayPreviewPart(system, part);
                }
            }

            internal static void HideUnavailablePart(PartScript part)
            {
                if (part == null)
                    return;
                HideInspectionSystemsOverlayUnavailableTransform(part.transform);
                HideDependentCollection(ReadMember(part, "MountObjects"));
                HideDependentCollection(ReadMember(part, "disableOnUnmount"));
                HideDependentCollection(ReadMember(part,
                    "hideWhenUnmontingMounting"));
            }

            private static void HideDependentCollection(object collection)
            {
                VisitCollection(collection, delegate(object value) {
                    Transform transform = GetDependentTransform(value);
                    if (transform != null)
                        HideInspectionSystemsOverlayUnavailableTransform(
                            transform);
                });
            }

            private static Transform GetDependentTransform(object value)
            {
                if (value == null)
                    return null;
                Transform transform = value as Transform;
                if (transform != null)
                    return transform;
                GameObject gameObject = value as GameObject;
                if (gameObject != null)
                    return gameObject.transform;
                Component component = value as Component;
                return component != null ? component.transform : null;
            }

            private static string GetFixedDependentAnchorPartId(string partId)
            {
                if (string.Equals(partId, WasherReservoirCapPartId,
                        StringComparison.OrdinalIgnoreCase))
                    return WasherReservoirPartId;
                if (string.Equals(partId, BrakeServoCapPartId,
                        StringComparison.OrdinalIgnoreCase))
                    return BrakeServoPartId;
                if (string.Equals(partId, RadiatorCCapPartId,
                        StringComparison.OrdinalIgnoreCase))
                    return RadiatorCPartId;
                if (string.Equals(partId, CoolantCap1PartId,
                        StringComparison.OrdinalIgnoreCase))
                    return CoolantReservoir1PartId;
                if (string.Equals(partId, CoolantCap2PartId,
                        StringComparison.OrdinalIgnoreCase))
                    return CoolantReservoir2PartId;
                if (string.Equals(partId, CoolantCap3PartId,
                        StringComparison.OrdinalIgnoreCase))
                    return CoolantReservoir3PartId;
                if (string.Equals(partId, PowerSteeringCapPartId,
                        StringComparison.OrdinalIgnoreCase))
                    return PowerSteeringReservoirPartId;
                return null;
            }

            private static int GetDependentAnchorLevel(
                InteractiveObject system, string anchorPartId)
            {
                if (system == null || string.IsNullOrEmpty(anchorPartId))
                    return 0;
                List<string> specification =
                    GetSystemSpecificationPartIds(system);
                bool found = false;
                for (int index = 0; index < specification.Count; index++) {
                    if (!string.Equals(specification[index], anchorPartId,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    found = true;
                    break;
                }
                return found ?
                    GetPartInspectionSkillLevel(system, anchorPartId) : 0;
            }

            private static string GetEngineBlockPartId(
                InteractiveObject system)
            {
                if (system == null)
                    return null;
                List<string> specification =
                    GetSystemSpecificationPartIds(system);
                if (specification == null || specification.Count == 0)
                    return null;

                for (int index = 0; index < specification.Count; index++) {
                    string id = specification[index];
                    if (string.Equals(id, RotaryEngineBlockPartId,
                            StringComparison.OrdinalIgnoreCase))
                        return id;
                }

                string result = null;
                int level = 0;
                for (int index = 0; index < specification.Count; index++) {
                    string id = specification[index];
                    if (!IsEngineBlockPartId(id))
                        continue;
                    int candidate = GetPartInspectionSkillLevel(system, id);
                    if (candidate <= 0)
                        continue;
                    if (level == 0 || candidate < level) {
                        result = id;
                        level = candidate;
                    }
                }
                return result;
            }

            private static string GetOilPanPartId(InteractiveObject system)
            {
                if (system == null)
                    return null;
                List<string> specification =
                    GetSystemSpecificationPartIds(system);
                if (specification == null || specification.Count == 0)
                    return null;

                string result = null;
                int level = 0;
                for (int index = 0; index < specification.Count; index++) {
                    string id = specification[index];
                    if (!IsOilPanPartId(id))
                        continue;
                    int candidate = GetPartInspectionSkillLevel(system, id);
                    if (candidate <= 0)
                        continue;
                    if (level == 0 || candidate < level) {
                        result = id;
                        level = candidate;
                    }
                }
                return result;
            }

            private static bool IsEngineBlockPartId(string partId)
            {
                if (string.IsNullOrEmpty(partId) ||
                    partId.IndexOf("_blok",
                        StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
                return partId.IndexOf("nakladka",
                    StringComparison.OrdinalIgnoreCase) < 0;
            }

            private static bool IsOilPanPartId(string partId)
            {
                if (string.IsNullOrEmpty(partId))
                    return false;
                return partId.IndexOf("miska",
                        StringComparison.OrdinalIgnoreCase) >= 0 &&
                    partId.IndexOf("olej",
                        StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static CarLoader ResolveInspectionLoader(Raycast raycast)
        {
            CarLoader loader = GetHoveredCarLoader(raycast);
            if (loader != null)
                return loader;
            if (raycast != null) {
                try {
                    loader = raycast.prevCarLoader;
                    if (loader != null)
                        return loader;
                } catch {
                }
            }
            return inspectionResetLoader;
        }
    }
}
