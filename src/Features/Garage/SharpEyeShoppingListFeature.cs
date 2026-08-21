using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.Containers;
using Il2CppCMS.UI;
using Il2CppCMS.UI.Description;
using Il2CppCMS.UI.Logic;
#else
using CMS;
using CMS.Containers;
using CMS.UI;
using CMS.UI.Description;
using CMS.UI.Logic;
#endif

namespace Cms21GameplayPlus
{
    internal static partial class SharpEyeShoppingListFeature
    {
        private const float PerfectConditionThreshold = 0.9999f;
        private const int DiagnosticLineLimit = 4000;
        private const float CustomExamineHoldSeconds = 0.55f;
        private const float CursorVisualCompletionScale = 1.05f;
        private const int MaximumInspectionSkillLevel = 6;
        private const string DiagnosticLogPath =
            @"Mods\CMS21GameplayPlus\SharpEyeUiDiagnostics.log";

        private enum PurchaseKind
        {
            Part,
            Tire,
            Rim
        }

        private sealed class PurchaseKey : IEquatable<PurchaseKey>
        {
            public readonly string Id;
            public readonly PurchaseKind Kind;
            public readonly int Size;
            public readonly int Width;
            public readonly int Profile;
            public readonly int ET;

            public PurchaseKey(string id, PurchaseKind kind, int size = 0,
                int width = 0, int profile = 0, int et = 0)
            {
                Id = string.IsNullOrEmpty(id) ? string.Empty : id.Trim();
                Kind = kind;
                Size = size;
                Width = width;
                Profile = profile;
                ET = et;
            }

            public bool Equals(PurchaseKey other)
            {
                if (ReferenceEquals(other, null))
                    return false;
                return Kind == other.Kind && Size == other.Size &&
                    Width == other.Width && Profile == other.Profile &&
                    ET == other.ET && string.Equals(Id, other.Id,
                        StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as PurchaseKey);
            }

            public override int GetHashCode()
            {
                unchecked {
                    int hash = Id != null ?
                        StringComparer.OrdinalIgnoreCase.GetHashCode(Id) : 0;
                    hash = (hash * 397) ^ (int)Kind;
                    hash = (hash * 397) ^ Size;
                    hash = (hash * 397) ^ Width;
                    hash = (hash * 397) ^ Profile;
                    hash = (hash * 397) ^ ET;
                    return hash;
                }
            }
        }

        private sealed class SystemPassState
        {
            public readonly HashSet<int> ExaminedPartInstanceIds =
                new HashSet<int>();
            public readonly HashSet<int> ExaminedMissingSlots =
                new HashSet<int>();
            public float ManualHoldProgress;
            public int Total;
        }

        private sealed class BodyPartSlot
        {
            public int Index;
            public object Part;
            public string Name;
            public string Id;
            public bool ForceUnmounted;
        }

        private sealed class BodyPassState
        {
            public readonly HashSet<int> ExaminedSlots =
                new HashSet<int>();
            public float HoldProgress;
            public int Total;
        }

        private sealed class BodySelectionSurface
        {
            public Collider Collider;
            public InteractiveObject Target;
        }

        private sealed class WheelSpec
        {
            public string RimId;
            public string TireId;
            public int Size;
            public int Width;
            public int Profile;
            public int ET;
        }

        private static readonly Dictionary<PurchaseKey, int> ObservedShopList =
            new Dictionary<PurchaseKey, int>();
        private static readonly Dictionary<string, bool> PurchasablePartCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, List<string>> SystemSpecificationPartIdsCache =
            new Dictionary<int, List<string>>();
        private static readonly Dictionary<int, int> SystemRequiredInspectionLevelCache =
            new Dictionary<int, int>();
        private static readonly Dictionary<int, List<PartScript>> SystemWheelPartsCache =
            new Dictionary<int, List<PartScript>>();
        private static readonly Dictionary<int, HashSet<int>> LoaderPartInstanceIds =
            new Dictionary<int, HashSet<int>>();
        private static readonly Dictionary<int, SystemPassState> SystemPassStates =
            new Dictionary<int, SystemPassState>();
        private static readonly Dictionary<int, List<BodyPartSlot>> BodyPartsCache =
            new Dictionary<int, List<BodyPartSlot>>();
        private static readonly Dictionary<int, List<InteractiveObject>> BodyHighlightTargetsCache =
            new Dictionary<int, List<InteractiveObject>>();
        private static readonly Dictionary<int, List<BodySelectionSurface>> BodySelectionSurfacesCache =
            new Dictionary<int, List<BodySelectionSurface>>();
        private static readonly Dictionary<int, BodyPassState> BodyPassStates =
            new Dictionary<int, BodyPassState>();
        private static readonly Dictionary<int, List<InteractiveObject>> InspectionSystemsCache =
            new Dictionary<int, List<InteractiveObject>>();
        private static readonly Dictionary<int, string> SystemNameCache =
            new Dictionary<int, string>();
        private static readonly Queue<PurchaseKey> PendingShoppingListAdds =
            new Queue<PurchaseKey>();
        private static readonly Dictionary<PurchaseKey, int> PendingShoppingListCounts =
            new Dictionary<PurchaseKey, int>();
        private static readonly string[] SystemPartMemberNames = new string[] {
            "partList", "parts", "Parts", "partScripts", "PartScripts",
            "partScriptList" };
        private static readonly string[] WheelHandleMemberNames =
            new string[] { "w_frontRightWheel_h", "w_frontLeftWheel_h",
                "w_rearRightWheel_h", "w_rearLeftWheel_h" };
        private static int activeProfileId = int.MinValue;
        private static int shoppingListGeneration;
        private static bool shoppingListWorkerRunning;
        private static bool suppressShopListObserver;
        private static bool processingCustomPartExamine;
        private static int customExaminedPartInstanceId = -1;
        private static bool examineModeSessionActive;
        private static InteractiveObject capturedExamineSystem;
        private static InteractiveObject bodyHighlightTarget;
        private static readonly List<InteractiveObject> activeBodyHighlightTargets =
            new List<InteractiveObject>();
        private static int bodyHighlightLoaderId = -1;
        private static bool bodyHighlightCompleted;
        private static bool bodyHighlightBaseActive;
        private static InteractiveObject emptySystemHighlightTarget;
        private static bool emptySystemHighlightCompleted;
        private static float inspectionSystemResetHoldProgress;
        private static float inspectionVehicleResetHoldProgress;
        private static bool inspectionSystemResetTriggered;
        private static bool inspectionVehicleResetTriggered;
        private static CarLoader inspectionResetLoader;
        private static object inspectionResetHintSource;
        private static bool inspectionResetHintSourceSearchAttempted;
        private static bool inspectionHintSourceDiagnosticsLogged;
        private static Transform inspectionHintHost;
        private static bool inspectionHintHostForcedActive;
        private static bool inspectionHintHostWasActiveSelf;
        private static bool inspectionHintSourceSuppressed;
        private static bool inspectionHintSourceWasActiveSelf;
        private static bool inspectionFooterHoldSuppressed;
        private static bool inspectionFooterHoldWasActiveSelf;
        private const int InspectionPreviewPartLayer = 16;
        private const int InspectionMountedPartLayer = 28;
        private const float InspectionMissingPartXrayAlpha = 1.25f;
        private const float InspectionPointerRayDistance = 250f;
        private const string InspectionSolidColorProperty = "_SolidColor";
        private const string InspectionXrayColorProperty = "_EmissionColor";
        private const float InspectionSystemListWidth = 220f;
        private const float InspectionSystemListHeaderHeight = 18f;
        private const float InspectionSystemListRowHeight = 18f;
        private const float InspectionSystemListRowGap = 1f;
        private const float InspectionSystemListNumberWidth = 20f;
        private const float InspectionSystemListMaximumWidth = 42f;
        private const float InspectionSystemListProgressWidth = 46f;
        private static readonly Color inspectionCompletedMissingSystemColor =
            new Color(0.05f, 0.45f, 1f, 1f);
        private static readonly Color inspectionSystemListCompletedColor =
            new Color(0.236f, 0.604f, 0f, 1f);
        private static readonly Color inspectionSystemListUnavailableColor =
            new Color(0.45f, 0.45f, 0.45f, 1f);
        private static readonly Color inspectionSystemListTextColor =
            new Color(0.9f, 0.9f, 0.9f, 1f);
        private const string RotaryEngineBlockPartId = "rot_new_blok_srodek_1";
        private const string EngineOilDipstickPartId = "bagnet_1";
        private const string EngineOilFillPlugPartId = "korekOleju_1";
        private const string WasherReservoirPartId =
            "windscreen_washer_reservoir_1_body";
        private const string WasherReservoirCapPartId =
            "windscreen_washer_cap_1";
        private static readonly List<PartScript> inspectionSystemsOverlaySolidTargets =
            new List<PartScript>();
        private static readonly Dictionary<int, List<PartScript>>
            inspectionSystemsOverlayPreviewPartsBySystem =
            new Dictionary<int, List<PartScript>>();
        private static readonly Dictionary<int, InteractiveObject>
            inspectionSystemsOverlaySystemByPartId =
            new Dictionary<int, InteractiveObject>();
        private static readonly Dictionary<int, int>
            inspectionSystemsOverlayMissingSlotByPartId =
            new Dictionary<int, int>();
        private static readonly Dictionary<long, PartScript>
            inspectionSystemsOverlayMissingPartBySlot =
            new Dictionary<long, PartScript>();
        private static readonly List<Renderer> inspectionSystemsOverlayHiddenRenderers =
            new List<Renderer>();
        private static readonly Dictionary<int, bool>
            inspectionSystemsOverlayHiddenRendererStates =
            new Dictionary<int, bool>();
        private static readonly List<Collider> inspectionSystemsOverlayHiddenColliders =
            new List<Collider>();
        private static readonly Dictionary<int, bool>
            inspectionSystemsOverlayHiddenColliderStates =
            new Dictionary<int, bool>();
        private struct InspectionOverlayPartState
        {
            internal Color Color;
            internal bool IsUnmounted;
            internal bool MountMode;
            internal bool ReplacedShader;
            internal int Layer;
        }

        private static readonly Dictionary<int, InspectionOverlayPartState>
            inspectionSystemsOverlaySolidOriginalStates =
            new Dictionary<int, InspectionOverlayPartState>();
        private static readonly Dictionary<int, Color>
            inspectionSystemsOverlaySolidOriginalColors =
            new Dictionary<int, Color>();
        private static readonly Dictionary<int, Color>
            inspectionSystemsOverlayXrayOriginalColors =
            new Dictionary<int, Color>();
        private static readonly Dictionary<int, bool>
            inspectionSystemsOverlayMissingVisualDiagnosticStates =
            new Dictionary<int, bool>();
        private static bool inspectionXrayShaderDiagnosticsLogged;
        private static CarLoader inspectionSystemsOverlayLoader;
        private static bool inspectionSystemsOverlayActive;
        private static UiIntegrationBridge.NativeHintHandle
            inspectionExamineHint;
        private static UiIntegrationBridge.NativeHintHandle
            inspectionSystemResetHint;
        private static UiIntegrationBridge.NativeHintHandle
            inspectionVehicleResetHint;
        private static UiIntegrationBridge.NativeHintHandle
            inspectionShowSystemsHint;
        private static bool inspectionExamineHintVisible;
        private static bool inspectionSystemResetHintVisible;
        private static bool inspectionVehicleResetHintVisible;
        private static bool inspectionShowSystemsHintVisible;
        private static string inspectionExamineHintLabel;
        private static string inspectionSystemResetHintLabel;
        private static string inspectionVehicleResetHintLabel;
        private static string inspectionShowSystemsHintLabel;
        private static float inspectionSystemResetHintProgress;
        private static float inspectionVehicleResetHintProgress;
        private static string inspectionNativeHintSuppressionDiagnosticKey;
        private static int cachedInspectionSkillLevel = -1;
        private static string cachedInspectionSkillId;
        private static bool inspectionSkillDiagnosticsLogged;
        private static int inspectionOverlayRaycastSystemId = int.MinValue;
        private static int inspectionOverlayRaycastBodyId = int.MinValue;
        private static int inspectionOverlayRaycastCurrentId = int.MinValue;
        private static readonly List<Collider>
            inspectionOverlayPointerColliders = new List<Collider>();
        private static readonly HashSet<int>
            inspectionOverlayPointerColliderIds = new HashSet<int>();
        private static int inspectionOverlayPointerHitFrame = -1;
        private static Transform inspectionOverlayPointerHitTransform;
        private static bool inspectionOverlayPointerHitUsedBoundsFallback;
        private static RectTransform inspectionSystemListPanel;
        private static bool inspectionSystemListVisible;
        private static bool inspectionSystemListDirty = true;
        private static int inspectionSystemListLoaderId = -1;
        private static int inspectionVehicleProgressLoaderId = -1;
        private static bool inspectionVehicleProgressDirty = true;
        private static bool inspectionVehicleHasProgress;
        private static InteractiveObject indicatorSystem;
        private static string indicatorText;
        private static bool indicatorDirty = true;
        private static int diagnosticLineCount;
        private static bool cursorVisualSourceDiagnosticsLogged;
        private static bool localizeMethodResolved;
        private static MethodInfo localizeMethod;
        private static string bodyDisplayName;
        private static string mouseOverDescriptionText;
        private static Camera bodySelectionCamera;
        private static UnityEngine.UI.Image sharpEyeCursorTimerImage;

        internal static void OnGarageSceneInitialized(int profileId)
        {
            shoppingListGeneration++;
            shoppingListWorkerRunning = false;
            examineModeSessionActive = false;
            DestroySharpEyeCursorTimer();
            ClearPendingShoppingListQueue();
            SystemSpecificationPartIdsCache.Clear();
            SystemRequiredInspectionLevelCache.Clear();
            SystemWheelPartsCache.Clear();
            LoaderPartInstanceIds.Clear();
            SystemPassStates.Clear();
            BodyPartsCache.Clear();
            BodyHighlightTargetsCache.Clear();
            BodySelectionSurfacesCache.Clear();
            BodyPassStates.Clear();
            InspectionSystemsCache.Clear();
            SystemNameCache.Clear();
            bodyDisplayName = null;
            mouseOverDescriptionText = null;
            bodySelectionCamera = null;
            InspectionVisualSystem.Exit();
            ResetInspectionResetInput(true);
            cachedInspectionSkillLevel = -1;
            cachedInspectionSkillId = null;
            inspectionSkillDiagnosticsLogged = false;
            ResetInspectionOverlayRaycastDiagnostics();
            DestroyInspectionSystemList();
            ResetInspectionVehicleProgressCache();
            ClearBodyHighlight();
            cursorVisualSourceDiagnosticsLogged = false;
            inspectionHintSourceDiagnosticsLogged = false;
            inspectionHintHost = null;
            inspectionNativeHintSuppressionDiagnosticKey = null;
            indicatorDirty = true;
            StartDiagnostics();
            examineModeSessionActive = IsExamineGarageModeActive();
            if (activeProfileId == profileId)
                return;

            activeProfileId = profileId;
            ObservedShopList.Clear();
        }

        internal static void OnGarageSceneUnloaded()
        {
            shoppingListGeneration++;
            shoppingListWorkerRunning = false;
            examineModeSessionActive = false;
            DestroySharpEyeCursorTimer();
            ClearPendingShoppingListQueue();
            SystemSpecificationPartIdsCache.Clear();
            SystemRequiredInspectionLevelCache.Clear();
            SystemWheelPartsCache.Clear();
            LoaderPartInstanceIds.Clear();
            SystemPassStates.Clear();
            BodyPartsCache.Clear();
            BodyHighlightTargetsCache.Clear();
            BodySelectionSurfacesCache.Clear();
            BodyPassStates.Clear();
            InspectionSystemsCache.Clear();
            SystemNameCache.Clear();
            bodyDisplayName = null;
            mouseOverDescriptionText = null;
            bodySelectionCamera = null;
            InspectionVisualSystem.Exit();
            ResetInspectionResetInput(true);
            cachedInspectionSkillLevel = -1;
            cachedInspectionSkillId = null;
            inspectionSkillDiagnosticsLogged = false;
            ResetInspectionOverlayRaycastDiagnostics();
            DestroyInspectionSystemList();
            ResetInspectionVehicleProgressCache();
            ClearBodyHighlight();
            cursorVisualSourceDiagnosticsLogged = false;
            inspectionHintSourceDiagnosticsLogged = false;
            inspectionNativeHintSuppressionDiagnosticKey = null;
            indicatorDirty = true;
            LogDiagnostic("garage session ended");
        }

        private static bool UsesSharpEyeInspection(InteractiveObject system)
        {
            int skillLevel = GetInspectionSkillLevel();
            if (system == null || skillLevel <= 0)
                return false;
            if (IsWholeCarBodyObject(system) ||
                IsBodyAggregateObject(GetCarLoader(system), system))
                return true;
            return HasAvailableInspectionPart(system, skillLevel);
        }

        private static int GetInspectionSkillLevel()
        {
            if (cachedInspectionSkillLevel >= 0)
                return cachedInspectionSkillLevel;

            GameManager manager = GlobalState.GameManager;
            if (manager == null)
                manager = Singleton<GameManager>.Instance;
            UpgradeSystem upgradeSystem = manager != null ?
                manager.UpgradeSystem : null;
            if (upgradeSystem == null)
                return 0;

            object upgrades = null;
            try {
                upgrades = upgradeSystem.GetUpgrades(UpgradeType.Points);
            } catch {
            }
            if (upgrades == null)
                return 0;

            object selected = null;
            int selectedScore = 0;
            string candidates = string.Empty;
            VisitCollection(upgrades, delegate(object value) {
                if (value == null)
                    return;
                string id = ToText(ReadMember(value, "ID"));
                if (string.IsNullOrEmpty(id))
                    return;
                if (candidates.Length < 512) {
                    if (candidates.Length > 0)
                        candidates += ",";
                    candidates += id;
                }
                int score = GetInspectionSkillIdScore(id);
                if (score <= selectedScore)
                    return;
                selectedScore = score;
                selected = value;
                cachedInspectionSkillId = id;
            });

            if (selected == null || selectedScore <= 0) {
                cachedInspectionSkillLevel = 0;
                if (!inspectionSkillDiagnosticsLogged) {
                    inspectionSkillDiagnosticsLogged = true;
                    LogDiagnostic("inspection skill unresolved upgrades=" +
                        candidates);
                }
                return cachedInspectionSkillLevel;
            }

            int unlocked = 0;
            int total = 0;
            object unlockedLevels = null;
            try {
                unlockedLevels = upgradeSystem.GetUnlocked(
                    cachedInspectionSkillId, UpgradeType.Points);
            } catch {
            }
            if (unlockedLevels == null)
                unlockedLevels = ReadMember(selected, "Unlocked");
            VisitCollection(unlockedLevels, delegate(object value) {
                total++;
                if (ToBool(value))
                    unlocked++;
            });
            cachedInspectionSkillLevel = Math.Max(0,
                Math.Min(MaximumInspectionSkillLevel, unlocked));
            if (!inspectionSkillDiagnosticsLogged) {
                inspectionSkillDiagnosticsLogged = true;
                LogDiagnostic("inspection skill id=" + cachedInspectionSkillId +
                    " unlocked=" +
                    unlocked.ToString(CultureInfo.InvariantCulture) + "/" +
                    total.ToString(CultureInfo.InvariantCulture) + " level=" +
                    cachedInspectionSkillLevel.ToString(
                        CultureInfo.InvariantCulture));
            }
            return cachedInspectionSkillLevel;
        }

        private static int GetInspectionSkillIdScore(string id)
        {
            if (string.IsNullOrEmpty(id))
                return 0;
            string value = id.Replace("_", string.Empty).Replace("-",
                string.Empty).Replace(" ", string.Empty);
            if (value.IndexOf("timetoexaminepart",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                return 120;
            if (value.IndexOf("examin",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                return 110;
            if (value.IndexOf("inspect",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                return 100;
            if (value.IndexOf("diagnos",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                return 90;
            return 0;
        }

        private static int GetRequiredInspectionSkillLevel(
            InteractiveObject system)
        {
            if (system == null)
                return 0;
            if (IsWholeCarBodyObject(system) ||
                IsBodyAggregateObject(GetCarLoader(system), system))
                return 1;

            int systemId;
            try {
                systemId = system.GetInstanceID();
            } catch {
                return 0;
            }
            int cached;
            if (SystemRequiredInspectionLevelCache.TryGetValue(systemId,
                    out cached))
                return cached;

            int level = MaximumInspectionSkillLevel + 1;
            List<string> specification = GetSystemSpecificationPartIds(system);
            for (int index = 0; index < specification.Count; index++) {
                string partId = specification[index];
                if (InspectionVisualSystem.IsDependentVisualPart(system,
                        partId))
                    continue;
                int partLevel = GetPartInspectionSkillLevel(system, partId);
                if (partLevel > 0 && partLevel < level)
                    level = partLevel;
            }
            if (level > MaximumInspectionSkillLevel)
                level = 0;
            SystemRequiredInspectionLevelCache[systemId] = level;
            return level;
        }

        private static int GetPartInspectionSkillLevel(
            InteractiveObject system, string partId)
        {
            int configured;
            if (SharpEyeInspectionRules.TryGetLevel(partId, out configured))
                return configured;
            return GetFallbackPartInspectionSkillLevel(system, partId);
        }

        private static int GetFallbackPartInspectionSkillLevel(
            InteractiveObject system, string partId)
        {
            string rawId = null;
            string objectName = null;
            try {
                rawId = NormalizeSystemName(system != null ?
                    system.GetID() : null);
                objectName = NormalizeSystemName(system != null ?
                    system.name : null);
            } catch {
            }
            string id = partId ?? string.Empty;

            if (rawId != null && rawId.StartsWith("engine_",
                    StringComparison.OrdinalIgnoreCase))
                return LooksLikeInternalPowertrainPart(id) ? 6 : 5;
            if (rawId != null && (rawId.StartsWith("FrontRight",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("FrontLeft",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("RearRight",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("RearLeft",
                        StringComparison.OrdinalIgnoreCase)))
                return LooksLikeDeepSuspensionPart(id) ? 5 : 4;
            if (rawId != null && (rawId.StartsWith("FrontCenter",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("RearCenter",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Driveshaft",
                        StringComparison.OrdinalIgnoreCase)))
                return 3;
            if (objectName != null && (objectName.StartsWith("FCSusp",
                        StringComparison.OrdinalIgnoreCase) ||
                    objectName.StartsWith("RCSusp",
                        StringComparison.OrdinalIgnoreCase)))
                return 3;
            if (objectName != null && (objectName.StartsWith("FLSusp",
                        StringComparison.OrdinalIgnoreCase) ||
                    objectName.StartsWith("FRSusp",
                        StringComparison.OrdinalIgnoreCase) ||
                    objectName.StartsWith("RLSusp",
                        StringComparison.OrdinalIgnoreCase) ||
                    objectName.StartsWith("RRSusp",
                        StringComparison.OrdinalIgnoreCase)))
                return LooksLikeDeepSuspensionPart(id) ? 5 : 4;
            if (rawId != null && (rawId.StartsWith("Battery",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("CoolantReservoir",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("PowerSteeringReservoir",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("WasherReservoir",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("ECU",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("FuseBox",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("ABS",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("BrakePump",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Cooling",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Radiator",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("AirIntake",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("FuelTank",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Exhaust",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Downpipe",
                        StringComparison.OrdinalIgnoreCase)))
                return 2;
            return 5;
        }

        private static bool LooksLikeDeepSuspensionPart(string partId)
        {
            if (string.IsNullOrEmpty(partId))
                return false;
            return partId.IndexOf("tuleja",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                partId.IndexOf("lozysko",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                partId.IndexOf("tloczek",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeInternalPowertrainPart(string partId)
        {
            if (string.IsNullOrEmpty(partId))
                return false;
            string value = partId.ToLowerInvariant();
            return value.Contains("tlok") || value.Contains("piston") ||
                value.Contains("korbow") || value.Contains("crank") ||
                value.Contains("walek") || value.Contains("camshaft") ||
                value.Contains("zawor") || value.Contains("valve") ||
                value.Contains("popych") || value.Contains("rocker") ||
                value.Contains("lancuch") || value.Contains("timing") ||
                value.Contains("panew") || value.Contains("bearing") ||
                value.Contains("rotor") || value.Contains("eccentric");
        }

        private static bool IsPartAvailableForInspection(
            InteractiveObject system, string partId, int skillLevel)
        {
            if (InspectionVisualSystem.IsDependentVisualPart(system, partId))
                return false;
            int required = GetPartInspectionSkillLevel(system, partId);
            return required > 0 && required <= skillLevel;
        }

        private static bool HasAvailableInspectionPart(
            InteractiveObject system, int skillLevel)
        {
            if (system == null || skillLevel <= 0)
                return false;
            List<string> specification = GetSystemSpecificationPartIds(system);
            for (int index = 0; index < specification.Count; index++) {
                if (IsPartAvailableForInspection(system, specification[index],
                        skillLevel))
                    return true;
            }
            return false;
        }

        private static float GetSharpEyeHoldSeconds()
        {
            return CustomExamineHoldSeconds;
        }

        internal static MethodBase FindAddToShopListMethod()
        {
            return AccessTools.Method(typeof(UIManager),
                nameof(UIManager.AddToShopList), new Type[] { typeof(string),
                    typeof(string), typeof(ShopListItemDataEx) });
        }

        private static bool ProcessOneCustomSystemStep(InteractiveObject source)
        {
            PartScript fallbackPart;
            if (TryExamineOnePresentPart(source, out fallbackPart)) {
                LogDiagnostic("fallback present system=" + SafeIoId(source) +
                    " part=" + SafePartId(fallbackPart));
                return true;
            }

            string missingId;
            int missingSlot;
            if (TryExamineOneMissingPart(source, out missingId, out missingSlot)) {
                LogDiagnostic("missing step system=" + SafeIoId(source) +
                    " slot=" + missingSlot.ToString(CultureInfo.InvariantCulture) +
                    " part=" + missingId);
                return true;
            }
            return false;
        }

        internal static bool ShouldAllowPartExamine(PartScript part,
            bool requested)
        {
            if (!requested || part == null || processingCustomPartExamine)
                return true;
            if (!IsInspectionSceneActive() || !examineModeSessionActive ||
                !IsExamineGarageModeActive())
                return true;

            CarLoader loader = null;
            try {
                loader = part.GetComponentInParent<CarLoader>();
            } catch {
            }
            return loader == null;
        }

        internal static void HandlePartExamine(PartScript part, bool requested)
        {
            if (!requested || part == null || !processingCustomPartExamine)
                return;
            try {
                if (part.IsExamined) {
                    customExaminedPartInstanceId = part.GetInstanceID();
                    LogDiagnostic("custom part examined part=" +
                        SafePartId(part));
                }
            } catch {
            }
        }

        internal static void ObserveShopListAdd(string id, string suffix)
        {
            if (!GlobalState.IsGarageSceneActive || suppressShopListObserver ||
                string.IsNullOrEmpty(id))
                return;

            PurchaseKey key = CreateObservedKey(id, suffix);
            if (key == null)
                return;
            Increment(ObservedShopList, key, 1);
        }

        internal static void HandleGameModeChanged(gameMode currentMode)
        {
            NativeInspectionModeReplacement.OnGameModeChanged(currentMode);
        }

        internal static bool CaptureExamineGarageRaycast(Raycast raycast)
        {
            return NativeInspectionModeReplacement.ShouldRunNativeRaycast(
                raycast);
        }

        internal static bool CaptureExamineConditionRaycast(Raycast raycast)
        {
            return NativeInspectionModeReplacement.ShouldRunNativeRaycast(
                raycast);
        }

        internal static void HandleExamineConditionRaycast(Raycast raycast)
        {
            NativeInspectionModeReplacement.Tick(raycast);
        }

        private static void ShowInspectionSystemsOverlay(CarLoader loader,
            int skillLevel)
        {
            if (loader == null)
                return;
            ClearInspectionSystemsOverlay();
            ClearBodyHighlight();
            ClearEmptySystemHighlight();
            SetSharpEyeCursorFill(0f);

            inspectionSystemsOverlayLoader = loader;
            inspectionResetLoader = loader;
            inspectionSystemsOverlayActive = true;
            ResetInspectionOverlayRaycastDiagnostics();
            HashSet<int> solidHighlighted = new HashSet<int>();
            int visibleMissingParts = 0;
            int hiddenSystems = 0;
            List<InteractiveObject> systems = GetInspectionSystems(loader);

            if (skillLevel <= 0) {
                for (int index = 0; index < systems.Count; index++) {
                    InteractiveObject system = systems[index];
                    if (system == null)
                        continue;
                    HideInspectionSystemsOverlayUnavailableSystem(system);
                    hiddenSystems++;
                }
                LogDiagnostic("inspection systems overlay show car=" +
                    SafeLoaderName(loader) +
                    " skill=0 preview=disabled hiddenSystems=" +
                    hiddenSystems.ToString(CultureInfo.InvariantCulture) +
                    " bodyFrame=visible");
                return;
            }

            for (int index = 0; index < systems.Count; index++) {
                InteractiveObject system = systems[index];
                if (system == null)
                    continue;
                int examined;
                int available;
                int full;
                GetSystemAvailableProgress(system, skillLevel, out examined,
                    out available, out full);
                if (available <= 0) {
                    HideInspectionSystemsOverlayUnavailableSystem(system);
                    hiddenSystems++;
                    continue;
                }
                InspectionVisualSystem.ApplyDependentPartVisibility(
                    system, skillLevel, solidHighlighted);
                bool hasAvailableMountedPart;
                int systemMissingParts = AddInspectionSystemsOverlayParts(
                    system, skillLevel, solidHighlighted,
                    out hasAvailableMountedPart);
                LogInspectionOverlaySystemProbe(system, skillLevel, examined,
                    available, full, hasAvailableMountedPart,
                    systemMissingParts);
                visibleMissingParts += systemMissingParts;
            }

            LogDiagnostic("inspection systems overlay show car=" +
                SafeLoaderName(loader) + " skill=" +
                skillLevel.ToString(CultureInfo.InvariantCulture) +
                " missingParts=" +
                visibleMissingParts.ToString(CultureInfo.InvariantCulture) +
                " solidParts=" +
                inspectionSystemsOverlaySolidTargets.Count.ToString(
                    CultureInfo.InvariantCulture) + " hiddenSystems=" +
                hiddenSystems.ToString(CultureInfo.InvariantCulture));
        }

        private static int AddInspectionSystemsOverlayParts(
            InteractiveObject system, int skillLevel,
            HashSet<int> solidHighlighted, out bool hasAvailableMountedPart)
        {
            hasAvailableMountedPart = false;
            if (system == null)
                return 0;

            List<string> specification = GetSystemSpecificationPartIds(system);
            List<int> missingSlots = GetMissingSpecificationSlots(system,
                specification);
            Dictionary<string, Queue<int>> missingById =
                new Dictionary<string, Queue<int>>(
                    StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < missingSlots.Count; index++) {
                int slot = missingSlots[index];
                if (slot < 0 || slot >= specification.Count)
                    continue;
                string id = specification[slot];
                Queue<int> slots;
                if (!missingById.TryGetValue(id, out slots)) {
                    slots = new Queue<int>();
                    missingById.Add(id, slots);
                }
                slots.Enqueue(slot);
            }

            SystemPassState state = GetSystemPassState(system);
            int visibleMissing = 0;
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (part == null)
                    continue;
                string id = SafePartId(part);
                if (InspectionVisualSystem.IsDependentVisualPart(system, id))
                    continue;
                if (string.IsNullOrEmpty(id) ||
                    !IsPartAvailableForInspection(system, id, skillLevel)) {
                    InspectionVisualSystem.HideUnavailablePart(part);
                    continue;
                }
                if (!IsUnmountedPart(part)) {
                    hasAvailableMountedPart = true;
                    bool examined = state != null &&
                        state.ExaminedPartInstanceIds.Contains(
                            part.GetInstanceID());
                    AddInspectionSystemsOverlayInstalledPart(part, examined,
                        solidHighlighted);
                    AddInspectionSystemsOverlayPreviewPart(system, part);
                    continue;
                }

                Queue<int> slots;
                if (!missingById.TryGetValue(id, out slots) ||
                    slots.Count == 0) {
                    InspectionVisualSystem.HideUnavailablePart(part);
                    continue;
                }
                int slot = slots.Dequeue();
                bool completed = state != null &&
                    state.ExaminedMissingSlots.Contains(slot);
                AddInspectionSystemsOverlaySolidPart(part, completed,
                    solidHighlighted);
                AddInspectionSystemsOverlayPreviewPart(system, part);
                AddInspectionSystemsOverlayMissingPart(system, slot, part);
                visibleMissing++;
            }
            return visibleMissing;
        }

        private static void AddInspectionSystemsOverlayPreviewPart(
            InteractiveObject system, PartScript part)
        {
            if (system == null || part == null)
                return;
            int systemId;
            int partId;
            try {
                systemId = system.GetInstanceID();
                partId = part.GetInstanceID();
            } catch {
                return;
            }
            InteractiveObject mappedSystem;
            if (inspectionSystemsOverlaySystemByPartId.TryGetValue(partId,
                    out mappedSystem) && mappedSystem != null)
                return;

            List<PartScript> parts;
            if (!inspectionSystemsOverlayPreviewPartsBySystem.TryGetValue(
                    systemId, out parts)) {
                parts = new List<PartScript>();
                inspectionSystemsOverlayPreviewPartsBySystem.Add(systemId,
                    parts);
            }
            parts.Add(part);
            inspectionSystemsOverlaySystemByPartId[partId] = system;
            AddInspectionOverlayPointerColliders(system, part);
        }

        private static void AddInspectionOverlayPointerColliders(
            InteractiveObject system, PartScript part)
        {
            if (system == null || part == null)
                return;
            Collider[] colliders;
            try {
                colliders = part.GetComponentsInChildren<Collider>(true);
            } catch {
                return;
            }
            for (int index = 0; index < colliders.Length; index++) {
                Collider collider = colliders[index];
                if (collider == null || !collider.enabled)
                    continue;
                int colliderId;
                try {
                    colliderId = collider.GetInstanceID();
                } catch {
                    continue;
                }
                if (!inspectionOverlayPointerColliderIds.Add(colliderId))
                    continue;
                inspectionOverlayPointerColliders.Add(collider);
            }
        }

        private static void AddInspectionSystemsOverlayMissingPart(
            InteractiveObject system, int slot, PartScript part)
        {
            if (system == null || part == null || slot < 0)
                return;
            try {
                int partId = part.GetInstanceID();
                inspectionSystemsOverlayMissingSlotByPartId[partId] = slot;
                inspectionSystemsOverlayMissingPartBySlot[
                    GetInspectionSystemsOverlayMissingPartKey(system, slot)] =
                    part;
            } catch {
            }
        }

        private static long GetInspectionSystemsOverlayMissingPartKey(
            InteractiveObject system, int slot)
        {
            unchecked {
                return ((long)system.GetInstanceID() << 32) | (uint)slot;
            }
        }

        private static bool IsInspectionSystemsOverlayMissingPart(
            PartScript part)
        {
            if (part == null)
                return false;
            try {
                return inspectionSystemsOverlayMissingSlotByPartId.
                    ContainsKey(part.GetInstanceID());
            } catch {
                return false;
            }
        }

        private static bool IsInspectionLogicallyUnmountedPart(
            PartScript part)
        {
            return part == null ||
                IsInspectionSystemsOverlayMissingPart(part) ||
                IsUnmountedPart(part);
        }

        private static bool IsInspectionSystemsOverlayMissingPartExamined(
            InteractiveObject system, PartScript part)
        {
            if (system == null || part == null)
                return false;
            int slot;
            try {
                if (!inspectionSystemsOverlayMissingSlotByPartId.TryGetValue(
                        part.GetInstanceID(), out slot))
                    return false;
            } catch {
                return false;
            }
            SystemPassState state = GetSystemPassState(system);
            return state != null && state.ExaminedMissingSlots.Contains(slot);
        }
        private static void SetInspectionSystemsOverlayMissingPartColor(
            PartScript part, bool examined)
        {
            if (part == null)
                return;
            Color color = examined ?
                inspectionCompletedMissingSystemColor : Color.white;
            bool logTransition =
                ShouldLogInspectionMissingVisualTransition(part, examined);
            if (logTransition)
                LogInspectionMissingVisualState(part, examined, "before");
            try {
                part.Alpha1();
                global::CarHelper.SetXrayAlpha(part.transform,
                    InspectionMissingPartXrayAlpha);
                SetInspectionSystemsOverlayXrayColor(part, color);
            } catch (Exception exception) {
                if (logTransition)
                    LogDiagnostic("inspection visual missing apply failed part=" +
                        SafePartId(part) + " examined=" +
                        examined.ToString() + " exception=" +
                        exception.GetType().Name);
            }
            if (logTransition)
                LogInspectionMissingVisualState(part, examined, "after");
        }

        private static bool ShouldLogInspectionMissingVisualTransition(
            PartScript part, bool examined)
        {
            if (part == null)
                return false;
            int partId;
            try {
                partId = part.GetInstanceID();
            } catch {
                return false;
            }
            bool previous;
            if (!inspectionSystemsOverlayMissingVisualDiagnosticStates.
                    TryGetValue(partId, out previous)) {
                inspectionSystemsOverlayMissingVisualDiagnosticStates[partId] =
                    examined;
                return examined;
            }
            if (previous == examined)
                return false;
            inspectionSystemsOverlayMissingVisualDiagnosticStates[partId] =
                examined;
            return true;
        }

        private static void LogInspectionMissingVisualState(PartScript part,
            bool examined, string stage)
        {
            if (part == null)
                return;
            int partId;
            try {
                partId = part.GetInstanceID();
            } catch {
                return;
            }
            InteractiveObject system = null;
            inspectionSystemsOverlaySystemByPartId.TryGetValue(partId,
                out system);
            int slot = -1;
            inspectionSystemsOverlayMissingSlotByPartId.TryGetValue(partId,
                out slot);
            Color partColor = Color.white;
            try {
                partColor = part.GetColor();
            } catch {
            }
            LogDiagnostic("inspection visual missing state stage=" + stage +
                " system=" + SafeIoId(system) + " part=" + SafePartId(part) +
                " instance=" + partId.ToString(CultureInfo.InvariantCulture) +
                " slot=" + slot.ToString(CultureInfo.InvariantCulture) +
                " examined=" + examined.ToString() + " unmounted=" +
                IsUnmountedPart(part).ToString() + " mountMode=" +
                part.mountMode.ToString() + " replacedShader=" +
                part.replacedShader.ToString() + " partColor=" +
                FormatInspectionVisualColor(partColor) + " alpha=" +
                InspectionMissingPartXrayAlpha.ToString("0.###",
                    CultureInfo.InvariantCulture) + " materials=" +
                GetInspectionMissingVisualMaterialState(part));

            if (!inspectionXrayShaderDiagnosticsLogged) {
                LogInspectionXrayApiSurface();
                LogInspectionXrayShaderProperties(part, stage);
                if (string.Equals(stage, "after",
                        StringComparison.OrdinalIgnoreCase))
                    inspectionXrayShaderDiagnosticsLogged = true;
            }
        }

        private static void LogInspectionXrayApiSurface()
        {
            LogInspectionXrayMethods(typeof(global::CarHelper),
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static, "CarHelper");
            LogInspectionXrayMethods(typeof(PartScript),
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance, "PartScript");
        }

        private static void LogInspectionXrayMethods(Type type,
            BindingFlags flags, string owner)
        {
            if (type == null)
                return;
            try {
                MethodInfo[] methods = type.GetMethods(flags);
                for (int index = 0; index < methods.Length; index++) {
                    MethodInfo method = methods[index];
                    if (method == null)
                        continue;
                    string name = method.Name ?? string.Empty;
                    if (name.IndexOf("xray",
                            StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("color",
                            StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("shader",
                            StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("alpha",
                            StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    string signature = string.Empty;
                    for (int parameterIndex = 0;
                        parameterIndex < parameters.Length; parameterIndex++) {
                        if (parameterIndex > 0)
                            signature += ",";
                        ParameterInfo parameter = parameters[parameterIndex];
                        signature += parameter.ParameterType != null ?
                            parameter.ParameterType.Name : "?";
                    }
                    LogDiagnostic("inspection visual xray api owner=" + owner +
                        " method=" + name + " args=" + signature +
                        " return=" + (method.ReturnType != null ?
                            method.ReturnType.Name : "?"));
                }
            } catch (Exception exception) {
                LogDiagnostic("inspection visual xray api failed owner=" +
                    owner + " exception=" + exception.GetType().Name);
            }
        }

        private static void LogInspectionXrayShaderProperties(PartScript part,
            string stage)
        {
            if (part == null)
                return;
            try {
                Renderer[] renderers =
                    part.GetComponentsInChildren<Renderer>(true);
                int xrayMaterials = 0;
                for (int rendererIndex = 0; rendererIndex < renderers.Length;
                    rendererIndex++) {
                    Renderer renderer = renderers[rendererIndex];
                    if (renderer == null)
                        continue;
                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block);
                    Material[] materials = renderer.materials;
                    for (int materialIndex = 0; materialIndex < materials.Length;
                        materialIndex++) {
                        Material material = materials[materialIndex];
                        Shader shader = material != null ? material.shader : null;
                        string shaderName = shader != null ?
                            shader.name : string.Empty;
                        if (material == null || shader == null ||
                            shaderName.IndexOf("Xray",
                                StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (xrayMaterials++ >= 2)
                            return;
                        LogDiagnostic("inspection visual xray material stage=" +
                            stage + " renderer=" + renderer.name + " shader=" +
                            shaderName + " propertyBlockEmpty=" +
                            block.isEmpty.ToString() + " renderQueue=" +
                            material.renderQueue.ToString(
                                CultureInfo.InvariantCulture));
                        LogInspectionShaderPropertyList(material, shader, stage,
                            renderer.name);
                    }
                }
            } catch (Exception exception) {
                LogDiagnostic("inspection visual xray material dump failed stage=" +
                    stage + " part=" + SafePartId(part) + " exception=" +
                    exception.GetType().Name);
            }
        }

        private static void LogInspectionShaderPropertyList(Material material,
            Shader shader, string stage, string rendererName)
        {
            if (material == null || shader == null)
                return;
            try {
                Type shaderType = shader.GetType();
                MethodInfo getCount = shaderType.GetMethod("GetPropertyCount",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    Type.EmptyTypes, null);
                MethodInfo getName = shaderType.GetMethod("GetPropertyName",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new Type[] { typeof(int) }, null);
                MethodInfo getType = shaderType.GetMethod("GetPropertyType",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new Type[] { typeof(int) }, null);
                if (getCount == null || getName == null) {
                    LogDiagnostic("inspection visual xray properties unavailable " +
                        "stage=" + stage + " shader=" + shader.name);
                    return;
                }
                int count = Convert.ToInt32(getCount.Invoke(shader, null),
                    CultureInfo.InvariantCulture);
                if (count > 64)
                    count = 64;
                for (int index = 0; index < count; index++) {
                    string propertyName = Convert.ToString(getName.Invoke(shader,
                        new object[] { index }), CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(propertyName) ||
                        !material.HasProperty(propertyName))
                        continue;
                    string propertyType = "?";
                    if (getType != null) {
                        object rawType = getType.Invoke(shader,
                            new object[] { index });
                        if (rawType != null)
                            propertyType = rawType.ToString();
                    }
                    string value = GetInspectionShaderPropertyValue(material,
                        propertyName, propertyType);
                    LogDiagnostic("inspection visual xray property stage=" + stage +
                        " renderer=" + rendererName + " index=" +
                        index.ToString(CultureInfo.InvariantCulture) +
                        " name=" + propertyName + " type=" + propertyType +
                        " value=" + value);
                }
            } catch (Exception exception) {
                LogDiagnostic("inspection visual xray properties failed stage=" +
                    stage + " shader=" + shader.name + " exception=" +
                    exception.GetType().Name);
            }
        }

        private static string GetInspectionShaderPropertyValue(
            Material material, string propertyName, string propertyType)
        {
            if (material == null || string.IsNullOrEmpty(propertyName))
                return "-";
            try {
                if (propertyType.IndexOf("Color",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return FormatInspectionVisualColor(
                        material.GetColor(propertyName));
                if (propertyType.IndexOf("Vector",
                        StringComparison.OrdinalIgnoreCase) >= 0) {
                    Vector4 value = material.GetVector(propertyName);
                    return "(" + value.x.ToString("0.###",
                        CultureInfo.InvariantCulture) + "," +
                        value.y.ToString("0.###", CultureInfo.InvariantCulture) +
                        "," + value.z.ToString("0.###",
                            CultureInfo.InvariantCulture) + "," +
                        value.w.ToString("0.###",
                            CultureInfo.InvariantCulture) + ")";
                }
                if (propertyType.IndexOf("Texture",
                        StringComparison.OrdinalIgnoreCase) >= 0) {
                    Texture texture = material.GetTexture(propertyName);
                    return texture != null ? texture.name : "<null>";
                }
                return material.GetFloat(propertyName).ToString("0.###",
                    CultureInfo.InvariantCulture);
            } catch {
                return "?";
            }
        }

        private static string GetInspectionMissingVisualMaterialState(
            PartScript part)
        {
            if (part == null)
                return "<none>";
            string result = string.Empty;
            int materialCount = 0;
            try {
                foreach (Renderer renderer in
                    part.GetComponentsInChildren<Renderer>(true)) {
                    if (renderer == null)
                        continue;
                    foreach (Material material in renderer.materials) {
                        if (material == null)
                            continue;
                        if (materialCount++ >= 8)
                            return result + ";...";
                        if (result.Length > 0)
                            result += ";";
                        string shaderName = material.shader != null ?
                            material.shader.name : "<null>";
                        result += renderer.name + "/" + shaderName +
                            " emission=" + GetInspectionMaterialColor(
                                material, InspectionXrayColorProperty) +
                            " color=" + GetInspectionMaterialColor(material,
                                "_Color") + " base=" +
                            GetInspectionMaterialColor(material, "_BaseColor") +
                            " solid=" + GetInspectionMaterialColor(material,
                                InspectionSolidColorProperty);
                    }
                }
            } catch (Exception exception) {
                return "<failed:" + exception.GetType().Name + ">";
            }
            return result.Length > 0 ? result : "<none>";
        }

        private static string GetInspectionMaterialColor(Material material,
            string property)
        {
            if (material == null || string.IsNullOrEmpty(property))
                return "-";
            try {
                if (!material.HasProperty(property))
                    return "-";
                return FormatInspectionVisualColor(material.GetColor(property));
            } catch {
                return "?";
            }
        }

        private static string FormatInspectionVisualColor(Color color)
        {
            return "(" + color.r.ToString("0.###", CultureInfo.InvariantCulture) +
                "," + color.g.ToString("0.###", CultureInfo.InvariantCulture) +
                "," + color.b.ToString("0.###", CultureInfo.InvariantCulture) +
                "," + color.a.ToString("0.###", CultureInfo.InvariantCulture) +
                ")";
        }

        private static void SetInspectionSystemsOverlayPreviewHover(
            InteractiveObject system, bool active, bool completed = false)
        {
            if (system == null)
                return;
            int systemId;
            try {
                systemId = system.GetInstanceID();
            } catch {
                return;
            }
            List<PartScript> parts;
            if (!inspectionSystemsOverlayPreviewPartsBySystem.TryGetValue(
                    systemId, out parts))
                return;
            SystemPassState state = GetSystemPassState(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (part == null)
                    continue;
                try {
                    if (IsInspectionSystemsOverlayMissingPart(part)) {
                        SetInspectionSystemsOverlayMissingPartColor(part,
                            IsInspectionSystemsOverlayMissingPartExamined(
                                system, part));
                    } else if (InspectionVisualSystem.IsDependentVisualPart(
                            system, SafePartId(part))) {
                        SetInspectionSystemsOverlayInstalledPartColor(part,
                            false);
                    } else {
                        bool partExamined = state != null &&
                            state.ExaminedPartInstanceIds.Contains(
                                part.GetInstanceID());
                        SetInspectionSystemsOverlayInstalledPartColor(part,
                            partExamined);
                    }
                } catch {
                }
            }
        }

        private static void HideInspectionSystemsOverlayUnavailableTransform(
            Transform root)
        {
            if (root == null)
                return;
            foreach (Renderer renderer in
                root.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                int rendererId = renderer.GetInstanceID();
                if (!inspectionSystemsOverlayHiddenRendererStates.
                    ContainsKey(rendererId)) {
                    inspectionSystemsOverlayHiddenRendererStates[rendererId] =
                        renderer.enabled;
                    inspectionSystemsOverlayHiddenRenderers.Add(renderer);
                }
                renderer.enabled = false;
            }
            foreach (Collider collider in
                root.GetComponentsInChildren<Collider>(true)) {
                if (collider == null)
                    continue;
                int colliderId = collider.GetInstanceID();
                if (!inspectionSystemsOverlayHiddenColliderStates.
                    ContainsKey(colliderId)) {
                    inspectionSystemsOverlayHiddenColliderStates[colliderId] =
                        collider.enabled;
                    inspectionSystemsOverlayHiddenColliders.Add(collider);
                }
                collider.enabled = false;
            }
        }

        private static void ShowInspectionSystemsOverlayDependentTransform(
            Transform root)
        {
            if (root == null)
                return;
            foreach (Renderer renderer in
                root.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                int rendererId = renderer.GetInstanceID();
                if (!inspectionSystemsOverlayHiddenRendererStates.
                    ContainsKey(rendererId)) {
                    inspectionSystemsOverlayHiddenRendererStates[rendererId] =
                        renderer.enabled;
                    inspectionSystemsOverlayHiddenRenderers.Add(renderer);
                }
                renderer.enabled = true;
            }
        }

        private static void HideInspectionSystemsOverlayUnavailableSystem(
            InteractiveObject system)
        {
            if (system == null)
                return;
            HideInspectionSystemsOverlayUnavailableTransform(system.transform);
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (part != null)
                    InspectionVisualSystem.HideUnavailablePart(part);
            }
        }

        private static void AddInspectionSystemsOverlayInstalledPart(
            PartScript part, bool examined, HashSet<int> highlighted)
        {
            if (part == null || highlighted == null)
                return;
            int partId;
            try {
                partId = part.GetInstanceID();
            } catch {
                return;
            }
            if (!highlighted.Add(partId))
                return;
            CaptureInspectionSystemsOverlayPartState(part, partId);
            try {
                part.ReplaceShader(false);
                SetInspectionSystemsOverlayMountedLayers(part);
                SetInspectionSystemsOverlayInstalledPartColor(part, examined);
            } catch (Exception exception) {
                LogDiagnostic("inspection visual installed failed part=" +
                    SafePartId(part) + " exception=" +
                    exception.GetType().Name);
            }
        }

        private static void SetInspectionSystemsOverlayInstalledPartColor(
            PartScript part, bool examined)
        {
            if (part == null)
                return;
            try {
                part.UpdateShaderParams(true);
                part.Alpha1();
            } catch {
            }
        }

        private static void CaptureInspectionSystemsOverlayPartState(
            PartScript part, int partId)
        {
            if (part == null ||
                inspectionSystemsOverlaySolidOriginalStates.ContainsKey(
                    partId))
                return;
            InspectionOverlayPartState state =
                new InspectionOverlayPartState();
            state.Color = part.GetColor();
            state.IsUnmounted = part.IsUnmounted;
            state.MountMode = part.mountMode;
            state.ReplacedShader = part.replacedShader;
            state.Layer = part.gameObject != null ?
                part.gameObject.layer : InspectionPreviewPartLayer;
            inspectionSystemsOverlaySolidOriginalStates[partId] = state;
            inspectionSystemsOverlaySolidTargets.Add(part);
        }

        private static void AddInspectionSystemsOverlaySolidPart(
            PartScript part, bool completed, HashSet<int> highlighted)
        {
            if (part == null || highlighted == null)
                return;
            int partId;
            try {
                partId = part.GetInstanceID();
            } catch {
                return;
            }
            if (!highlighted.Add(partId))
                return;
            try {
                CaptureInspectionSystemsOverlayPartState(part, partId);
                SetInspectionSystemsOverlayMountedLayers(part);
                part.SwitchSolidColor(false);
                SetInspectionSystemsOverlayMissingPartColor(part,
                    completed);
            } catch (Exception exception) {
                LogDiagnostic("inspection overlay solid failed part=" +
                    SafePartId(part) + " exception=" +
                    exception.GetType().Name);
            }
        }

        private static void SetInspectionSystemsOverlayMountedLayers(
            PartScript part)
        {
            if (part == null)
                return;
            part.SetLayerRecursively(InspectionMountedPartLayer);
            foreach (Collider collider in
                part.GetComponentsInChildren<Collider>(true)) {
                if (collider == null)
                    continue;
                int colliderId = collider.GetInstanceID();
                if (!inspectionSystemsOverlayHiddenColliderStates.
                    ContainsKey(colliderId)) {
                    inspectionSystemsOverlayHiddenColliderStates[colliderId] =
                        collider.enabled;
                    inspectionSystemsOverlayHiddenColliders.Add(collider);
                }
                collider.enabled = true;
                if (collider.gameObject != null)
                    collider.gameObject.layer = InspectionMountedPartLayer;
            }
        }

        private static void SetInspectionSystemsOverlaySolidColor(
            PartScript part, Color color)
        {
            if (part == null)
                return;
            foreach (Renderer renderer in
                part.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                foreach (Material material in renderer.materials) {
                    if (material == null ||
                        !material.HasProperty(InspectionSolidColorProperty))
                        continue;
                    int materialId = material.GetInstanceID();
                    if (!inspectionSystemsOverlaySolidOriginalColors.
                        ContainsKey(materialId)) {
                        inspectionSystemsOverlaySolidOriginalColors[materialId] =
                            material.GetColor(InspectionSolidColorProperty);
                    }
                    material.SetColor(InspectionSolidColorProperty, color);
                }
            }
        }

        private static void SetInspectionSystemsOverlayXrayColor(
            PartScript part, Color color)
        {
            if (part == null)
                return;
            int partId = part.GetInstanceID();
            foreach (Renderer renderer in
                part.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                PartScript owner = renderer.GetComponentInParent<PartScript>();
                if (owner == null || owner.GetInstanceID() != partId)
                    continue;
                foreach (Material material in renderer.materials) {
                    if (material == null || material.shader == null ||
                        !string.Equals(material.shader.name, "CMS21/Xray",
                            StringComparison.Ordinal))
                        continue;
                    int materialId = material.GetInstanceID();
                    if (material.HasProperty(InspectionXrayColorProperty)) {
                        if (!inspectionSystemsOverlayXrayOriginalColors.
                            ContainsKey(materialId)) {
                            inspectionSystemsOverlayXrayOriginalColors[materialId] =
                                material.GetColor(InspectionXrayColorProperty);
                        }
                        material.SetColor(InspectionXrayColorProperty, color);
                    }
                    if (material.HasProperty(InspectionSolidColorProperty)) {
                        if (!inspectionSystemsOverlaySolidOriginalColors.
                            ContainsKey(materialId)) {
                            inspectionSystemsOverlaySolidOriginalColors[materialId] =
                                material.GetColor(InspectionSolidColorProperty);
                        }
                        material.SetColor(InspectionSolidColorProperty, color);
                    }
                }
            }
        }

        private static void RestoreInspectionSystemsOverlayXrayColors(
            PartScript part)
        {
            if (part == null)
                return;
            foreach (Renderer renderer in
                part.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                foreach (Material material in renderer.materials) {
                    if (material == null ||
                        !material.HasProperty(InspectionXrayColorProperty))
                        continue;
                    Color color;
                    if (inspectionSystemsOverlayXrayOriginalColors.TryGetValue(
                        material.GetInstanceID(), out color)) {
                        material.SetColor(InspectionXrayColorProperty, color);
                    }
                }
            }
        }

        private static void RestoreInspectionSystemsOverlaySolidColors(
            PartScript part)
        {
            if (part == null)
                return;
            foreach (Renderer renderer in
                part.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                foreach (Material material in renderer.materials) {
                    if (material == null ||
                        !material.HasProperty(InspectionSolidColorProperty))
                        continue;
                    Color color;
                    if (inspectionSystemsOverlaySolidOriginalColors.TryGetValue(
                        material.GetInstanceID(), out color)) {
                        material.SetColor(InspectionSolidColorProperty, color);
                    }
                }
            }
        }

        private static void ClearInspectionSystemsOverlay()
        {
            if (!inspectionSystemsOverlayActive &&
                inspectionSystemsOverlaySolidTargets.Count == 0 &&
                inspectionSystemsOverlayHiddenRenderers.Count == 0 &&
                inspectionSystemsOverlayHiddenColliders.Count == 0)
                return;
            ClearEmptySystemHighlight();
            for (int index = 0;
                index < inspectionSystemsOverlaySolidTargets.Count;
                index++) {
                PartScript part =
                    inspectionSystemsOverlaySolidTargets[index];
                if (part == null)
                    continue;
                try {
                    part.SetMouseOver(false);
                    InspectionOverlayPartState state;
                    if (!inspectionSystemsOverlaySolidOriginalStates.
                        TryGetValue(part.GetInstanceID(), out state))
                        continue;
                    part.IsUnmounted = state.IsUnmounted;
                    part.mountMode = state.MountMode;
                    RestoreInspectionSystemsOverlaySolidColors(part);
                    RestoreInspectionSystemsOverlayXrayColors(part);
                    part.SetLayerRecursively(state.Layer);
                    part.ReplaceShader(state.ReplacedShader);
                    if (state.IsUnmounted) {
                        part.SetColor(state.Color);
                        part.Alpha0();
                    } else {
                        part.UpdateShaderParams(false);
                        part.Alpha1();
                    }
                } catch {
                }
            }
            for (int index = 0;
                index < inspectionSystemsOverlayHiddenRenderers.Count;
                index++) {
                Renderer renderer =
                    inspectionSystemsOverlayHiddenRenderers[index];
                if (renderer == null)
                    continue;
                bool enabled;
                if (inspectionSystemsOverlayHiddenRendererStates.
                    TryGetValue(renderer.GetInstanceID(), out enabled))
                    renderer.enabled = enabled;
            }
            for (int index = 0;
                index < inspectionSystemsOverlayHiddenColliders.Count;
                index++) {
                Collider collider =
                    inspectionSystemsOverlayHiddenColliders[index];
                if (collider == null)
                    continue;
                bool enabled;
                if (inspectionSystemsOverlayHiddenColliderStates.
                    TryGetValue(collider.GetInstanceID(), out enabled))
                    collider.enabled = enabled;
            }
            ResetInspectionOverlayRaycastDiagnostics();
            inspectionSystemsOverlaySolidTargets.Clear();
            inspectionSystemsOverlayPreviewPartsBySystem.Clear();
            inspectionSystemsOverlaySystemByPartId.Clear();
            inspectionOverlayPointerColliders.Clear();
            inspectionOverlayPointerColliderIds.Clear();
            inspectionSystemsOverlayMissingSlotByPartId.Clear();
            inspectionSystemsOverlayMissingPartBySlot.Clear();
            inspectionSystemsOverlaySolidOriginalStates.Clear();
            inspectionSystemsOverlaySolidOriginalColors.Clear();
            inspectionSystemsOverlayXrayOriginalColors.Clear();
            inspectionSystemsOverlayMissingVisualDiagnosticStates.Clear();
            inspectionSystemsOverlayHiddenRenderers.Clear();
            inspectionSystemsOverlayHiddenRendererStates.Clear();
            inspectionSystemsOverlayHiddenColliders.Clear();
            inspectionSystemsOverlayHiddenColliderStates.Clear();
            inspectionSystemsOverlayLoader = null;
            inspectionSystemsOverlayActive = false;
            ClearBodyHighlight();
            LogDiagnostic("inspection systems overlay hide");
        }

        private static InteractiveObject
            ResolveInspectionSystemsOverlayPartSystem(Transform hitTransform)
        {
            if (hitTransform == null)
                return null;
            try {
                PartScript part = hitTransform.GetComponentInParent<PartScript>();
                if (part == null)
                    return null;
                InteractiveObject system;
                return inspectionSystemsOverlaySystemByPartId.TryGetValue(
                    part.GetInstanceID(), out system) ? system : null;
            } catch {
                return null;
            }
        }

        private static Transform GetRaycastHitTransform(Raycast raycast)
        {
            if (inspectionSystemsOverlayActive)
                return inspectionOverlayPointerHitFrame == Time.frameCount ?
                    inspectionOverlayPointerHitTransform : null;
            try {
                return raycast != null ? raycast.hit.transform : null;
            } catch {
                return null;
            }
        }

        private static Transform GetInspectionOverlayPointerHitTransform(
            Vector3 mousePosition)
        {
            if (!inspectionSystemsOverlayActive)
                return null;
            if (inspectionOverlayPointerHitFrame == Time.frameCount)
                return inspectionOverlayPointerHitTransform;
            inspectionOverlayPointerHitFrame = Time.frameCount;
            inspectionOverlayPointerHitTransform = null;
            inspectionOverlayPointerHitUsedBoundsFallback = false;

            Camera camera = bodySelectionCamera;
            if (camera == null) {
                camera = Camera.main;
                bodySelectionCamera = camera;
            }
            if (camera == null)
                return null;

            try {
                Ray ray = camera.ScreenPointToRay(mousePosition);
                float nearest = float.MaxValue;
                for (int index = 0; index <
                    inspectionOverlayPointerColliders.Count; index++) {
                    Collider collider = inspectionOverlayPointerColliders[index];
                    if (!IsInspectionOverlayPointerColliderCandidate(collider))
                        continue;
                    RaycastHit hit;
                    if (!collider.Raycast(ray, out hit,
                            InspectionPointerRayDistance) ||
                        hit.distance >= nearest)
                        continue;
                    nearest = hit.distance;
                    inspectionOverlayPointerHitTransform = collider.transform;
                }

                if (inspectionOverlayPointerHitTransform == null) {
                    nearest = float.MaxValue;
                    for (int index = 0; index <
                        inspectionOverlayPointerColliders.Count; index++) {
                        Collider collider =
                            inspectionOverlayPointerColliders[index];
                        if (!IsInspectionOverlayPointerColliderCandidate(
                                collider))
                            continue;
                        float distance;
                        Bounds bounds = collider.bounds;
                        if (!bounds.IntersectRay(ray, out distance) ||
                            distance < 0f ||
                            distance > InspectionPointerRayDistance ||
                            distance >= nearest)
                            continue;
                        nearest = distance;
                        inspectionOverlayPointerHitTransform =
                            collider.transform;
                    }
                    inspectionOverlayPointerHitUsedBoundsFallback =
                        inspectionOverlayPointerHitTransform != null;
                }
            } catch {
                inspectionOverlayPointerHitTransform = null;
                inspectionOverlayPointerHitUsedBoundsFallback = false;
            }
            return inspectionOverlayPointerHitTransform;
        }

        private static bool IsInspectionOverlayPointerColliderCandidate(
            Collider collider)
        {
            return collider != null && collider.enabled &&
                collider.gameObject != null &&
                collider.gameObject.activeInHierarchy;
        }

        private static InteractiveObject GetHitInteractiveObject(
            Transform hitTransform)
        {
            if (hitTransform == null)
                return null;
            try {
                return hitTransform.GetComponentInParent<InteractiveObject>();
            } catch {
                return null;
            }
        }

        internal static void HandleExamineGarageRaycast(Raycast raycast)
        {
            NativeInspectionModeReplacement.Tick(raycast);
        }

        private static List<InteractiveObject> GetInspectionSystems(
            CarLoader loader)
        {
            if (loader == null)
                return new List<InteractiveObject>();
            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return new List<InteractiveObject>();
            }

            List<InteractiveObject> cached;
            if (InspectionSystemsCache.TryGetValue(loaderId, out cached))
                return cached;

            cached = new List<InteractiveObject>();
            try {
                UnhollowerBaseLib.Il2CppReferenceArray<UnityEngine.Object> all =
                    Resources.FindObjectsOfTypeAll(
                        UnhollowerRuntimeLib.Il2CppType.Of<InteractiveObject>());
                if (all != null) {
                    for (int index = 0; index < all.Length; index++) {
                        UnityEngine.Object raw = all[index];
                        InteractiveObject candidate = raw != null ?
                            raw.TryCast<InteractiveObject>() : null;
                        if (candidate == null)
                            continue;
                        CarLoader candidateLoader = GetCarLoader(candidate);
                        if (candidateLoader == null)
                            continue;
                        try {
                            if (candidateLoader.GetInstanceID() != loaderId)
                                continue;
                        } catch {
                            continue;
                        }
                        if (GetRequiredInspectionSkillLevel(candidate) > 1)
                            cached.Add(candidate);
                    }
                }
            } catch {
            }
            cached.Sort(CompareInspectionSystems);
            InspectionSystemsCache[loaderId] = cached;
            return cached;
        }

        private static int CompareInspectionSystems(InteractiveObject left,
            InteractiveObject right)
        {
            int leftLevel = GetRequiredInspectionSkillLevel(left);
            int rightLevel = GetRequiredInspectionSkillLevel(right);
            int comparison = leftLevel.CompareTo(rightLevel);
            if (comparison != 0)
                return comparison;
            comparison = GetInspectionSystemOrder(left).CompareTo(
                GetInspectionSystemOrder(right));
            if (comparison != 0)
                return comparison;
            comparison = string.Compare(NormalizeSystemName(left != null ?
                left.name : null), NormalizeSystemName(right != null ?
                right.name : null), StringComparison.OrdinalIgnoreCase);
            return comparison != 0 ? comparison :
                string.Compare(SafeIoId(left), SafeIoId(right),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static int GetInspectionSystemOrder(InteractiveObject system)
        {
            if (system == null)
                return 999;
            string id = NormalizeSystemName(SafeIoId(system));
            string name = NormalizeSystemName(system.name);
            if (id.StartsWith("WasherReservoir", StringComparison.OrdinalIgnoreCase)) return 10;
            if (id.StartsWith("CoolantReservoir", StringComparison.OrdinalIgnoreCase)) return 20;
            if (id.StartsWith("PowerSteeringReservoir", StringComparison.OrdinalIgnoreCase)) return 30;
            if (id.StartsWith("Battery", StringComparison.OrdinalIgnoreCase)) return 40;
            if (id.StartsWith("FuseBox", StringComparison.OrdinalIgnoreCase)) return 50;
            if (id.StartsWith("ECU", StringComparison.OrdinalIgnoreCase)) return 60;
            if (id.StartsWith("AirIntake", StringComparison.OrdinalIgnoreCase)) return 110;
            if (id.StartsWith("Cooling", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("Radiator", StringComparison.OrdinalIgnoreCase)) return 120;
            if (id.StartsWith("BrakePump", StringComparison.OrdinalIgnoreCase)) return 130;
            if (id.StartsWith("ABS", StringComparison.OrdinalIgnoreCase)) return 140;
            if (id.StartsWith("FuelTank", StringComparison.OrdinalIgnoreCase)) return 150;
            if (id.StartsWith("Downpipe", StringComparison.OrdinalIgnoreCase)) return 210;
            if (id.StartsWith("Exhaust", StringComparison.OrdinalIgnoreCase)) return 220;
            if (id.StartsWith("Driveshaft", StringComparison.OrdinalIgnoreCase)) return 230;
            if (id.StartsWith("FrontCenter", StringComparison.OrdinalIgnoreCase)) return 240;
            if (id.StartsWith("RearCenter", StringComparison.OrdinalIgnoreCase)) return 250;
            if (name.StartsWith("FLSusp", StringComparison.OrdinalIgnoreCase)) return 320;
            if (name.StartsWith("FRSusp", StringComparison.OrdinalIgnoreCase)) return 330;
            if (name.StartsWith("RLSusp", StringComparison.OrdinalIgnoreCase)) return 350;
            if (name.StartsWith("RRSusp", StringComparison.OrdinalIgnoreCase)) return 360;
            if (id.StartsWith("engine_", StringComparison.OrdinalIgnoreCase)) return 410;
            return 900;
        }

        private static string SafeLoaderName(CarLoader loader)
        {
            if (loader == null)
                return "<null>";
            try {
                if (loader.gameObject != null &&
                    !string.IsNullOrEmpty(loader.gameObject.name))
                    return loader.gameObject.name;
                return loader.name ?? "<unnamed>";
            } catch {
                return "<error>";
            }
        }

        private static void ResetInspectionOverlayRaycastDiagnostics()
        {
            inspectionOverlayRaycastSystemId = int.MinValue;
            inspectionOverlayRaycastBodyId = int.MinValue;
            inspectionOverlayRaycastCurrentId = int.MinValue;
            inspectionOverlayPointerHitFrame = -1;
            inspectionOverlayPointerHitTransform = null;
            inspectionOverlayPointerHitUsedBoundsFallback = false;
        }

        private static int SafeInstanceId(UnityEngine.Object value)
        {
            if (value == null)
                return 0;
            try {
                return value.GetInstanceID();
            } catch {
                return -1;
            }
        }

        private static string GetDiagnosticTransformPath(Transform value,
            int maximumDepth)
        {
            if (value == null)
                return "<null>";
            string path = string.Empty;
            Transform current = value;
            int depth = 0;
            while (current != null && depth < maximumDepth) {
                path = path.Length == 0 ? current.name :
                    current.name + "/" + path;
                current = current.parent;
                depth++;
            }
            return path;
        }

        private static void LogInspectionOverlayRaycastProbe(Raycast raycast,
            InteractiveObject raycastObject, InteractiveObject resolvedSystem,
            InteractiveObject resolvedBody, InteractiveObject current)
        {
            if (!inspectionSystemsOverlayActive)
                return;
            try {
                Transform hit = GetRaycastHitTransform(raycast);
                int systemId = SafeInstanceId(resolvedSystem);
                int bodyId = SafeInstanceId(resolvedBody);
                int currentId = SafeInstanceId(current);
                if (inspectionOverlayRaycastSystemId == systemId &&
                    inspectionOverlayRaycastBodyId == bodyId &&
                    inspectionOverlayRaycastCurrentId == currentId)
                    return;
                inspectionOverlayRaycastSystemId = systemId;
                inspectionOverlayRaycastBodyId = bodyId;
                inspectionOverlayRaycastCurrentId = currentId;

                PartScript part = hit != null ?
                    hit.GetComponentInParent<PartScript>() : null;
                InteractiveObject mappedSystem =
                    ResolveInspectionSystemsOverlayPartSystem(hit);
                int partId = SafeInstanceId(part);
                Collider collider = hit != null ? hit.GetComponent<Collider>() :
                    null;
                if (collider == null && hit != null)
                    collider = hit.GetComponentInParent<Collider>();
                GameScript game = GameScript.Get();
                InteractiveObject gameHover = game != null ?
                    game.IOMouseOverIO : null;
                int missingSlot = -1;
                bool mappedMissing = part != null &&
                    inspectionSystemsOverlayMissingSlotByPartId.TryGetValue(
                        partId, out missingSlot);
                int available = -1;
                int examined = -1;
                int full = -1;
                if (resolvedSystem != null)
                    GetSystemAvailableProgress(resolvedSystem,
                        GetInspectionSkillLevel(), out examined, out available,
                        out full);
                LogDiagnostic("inspection overlay raycast hit=" +
                    GetDiagnosticTransformPath(hit, 8) + " hitMode=" +
                    (hit != null ?
                        (inspectionOverlayPointerHitUsedBoundsFallback ?
                            "bounds" : "collider") : "null") +
                    " hitLayer=" +
                    (hit != null ? hit.gameObject.layer.ToString(
                        CultureInfo.InvariantCulture) : "-") + " collider=" +
                    (collider != null ? collider.name : "<null>") +
                    " colliderEnabled=" +
                    (collider != null ? collider.enabled.ToString() : "-") +
                    " part=" + (part != null ? SafePartId(part) :
                        "<none>") + " partUnmounted=" +
                    (part != null ? part.IsUnmounted.ToString() : "-") +
                    " partLayer=" +
                    (part != null && part.gameObject != null ?
                        part.gameObject.layer.ToString(
                            CultureInfo.InvariantCulture) : "-") +
                    " missingSlot=" +
                    (mappedMissing ? missingSlot.ToString(
                        CultureInfo.InvariantCulture) : "-") +
                    " mapped=" + SafeIoId(mappedSystem) + " hitIo=" +
                    SafeIoId(GetHitInteractiveObject(hit)) + " raycastIo=" +
                    SafeIoId(raycastObject) + " gameHover=" +
                    SafeIoId(gameHover) + " resolvedSystem=" +
                    SafeIoId(resolvedSystem) + " resolvedBody=" +
                    SafeIoId(resolvedBody) + " current=" + SafeIoId(current) +
                    " progress=" + examined.ToString(
                        CultureInfo.InvariantCulture) + "/" +
                    available.ToString(CultureInfo.InvariantCulture) +
                    " full=" + full.ToString(CultureInfo.InvariantCulture));
            } catch (Exception exception) {
                LogDiagnostic("inspection overlay raycast failed exception=" +
                    exception.GetType().Name);
            }
        }

        private static void LogInspectionOverlaySystemProbe(
            InteractiveObject system, int skillLevel, int examined,
            int available, int full, bool hasAvailableMountedPart,
            int visibleMissingParts)
        {
            if (system == null)
                return;
            try {
                List<PartScript> mappedParts;
                int systemId = system.GetInstanceID();
                inspectionSystemsOverlayPreviewPartsBySystem.TryGetValue(
                    systemId, out mappedParts);
                int mapped = mappedParts != null ? mappedParts.Count : 0;
                int mounted = 0;
                int missing = 0;
                int enabledColliders = 0;
                int layer16 = 0;
                int layer28 = 0;
                if (mappedParts != null) {
                    for (int index = 0; index < mappedParts.Count; index++) {
                        PartScript part = mappedParts[index];
                        if (part == null)
                            continue;
                        if (IsInspectionSystemsOverlayMissingPart(part))
                            missing++;
                        else
                            mounted++;
                        if (part.gameObject != null) {
                            if (part.gameObject.layer == InspectionPreviewPartLayer)
                                layer16++;
                            if (part.gameObject.layer == InspectionMountedPartLayer)
                                layer28++;
                        }
                        Collider[] colliders =
                            part.GetComponentsInChildren<Collider>(true);
                        for (int colliderIndex = 0;
                            colliderIndex < colliders.Length; colliderIndex++) {
                            Collider collider = colliders[colliderIndex];
                            if (collider != null && collider.enabled)
                                enabledColliders++;
                        }
                    }
                }
                LogDiagnostic("inspection overlay system system=" +
                    SafeIoId(system) + " skill=" + skillLevel.ToString(
                        CultureInfo.InvariantCulture) + " progress=" +
                    examined.ToString(CultureInfo.InvariantCulture) + "/" +
                    available.ToString(CultureInfo.InvariantCulture) +
                    " full=" + full.ToString(CultureInfo.InvariantCulture) +
                    " mapped=" + mapped.ToString(
                        CultureInfo.InvariantCulture) + " mounted=" +
                    mounted.ToString(CultureInfo.InvariantCulture) +
                    " missing=" + missing.ToString(
                        CultureInfo.InvariantCulture) + " visibleMissing=" +
                    visibleMissingParts.ToString(
                        CultureInfo.InvariantCulture) + " mountedAvailable=" +
                    hasAvailableMountedPart.ToString() +
                    " enabledColliders=" + enabledColliders.ToString(
                        CultureInfo.InvariantCulture) + " layer16=" +
                    layer16.ToString(CultureInfo.InvariantCulture) +
                    " layer28=" + layer28.ToString(
                        CultureInfo.InvariantCulture));
            } catch (Exception exception) {
                LogDiagnostic("inspection overlay system probe failed system=" +
                    SafeIoId(system) + " exception=" +
                    exception.GetType().Name);
            }
        }

        private static void UpdateEmptySystemHighlight(
            InteractiveObject system, bool completed)
        {
            if (system == null || IsWholeCarBodyObject(system)) {
                ClearEmptySystemHighlight();
                return;
            }

            bool targetChanged = emptySystemHighlightTarget == null ||
                emptySystemHighlightTarget.GetInstanceID() !=
                    system.GetInstanceID();
            bool highlightChanged = targetChanged ||
                emptySystemHighlightCompleted != completed;
            if (targetChanged)
                ClearEmptySystemHighlight();
            emptySystemHighlightTarget = system;
            emptySystemHighlightCompleted = completed;
            if (highlightChanged && inspectionSystemsOverlayActive) {
                try {
                    system.SetMouseOver(true, completed ? Color.green :
                        Color.yellow);
                } catch {
                }
                SetInspectionSystemsOverlayPreviewHover(system, true,
                    completed);
            }

            if (targetChanged && inspectionSystemsOverlayActive) {
                int systemId = system.GetInstanceID();
                List<PartScript> visibleParts;
                int visiblePartCount =
                    inspectionSystemsOverlayPreviewPartsBySystem.TryGetValue(
                        systemId, out visibleParts) &&
                    visibleParts != null ? visibleParts.Count : 0;
                LogDiagnostic("inspection overlay hover system=" +
                    SafeIoId(system) + " parts=" +
                    visiblePartCount.ToString(CultureInfo.InvariantCulture) +
                    " complete=" + completed.ToString());
            }
        }

        private static void ClearMouseOverDescription(
            InteractiveObject system = null, bool force = false)
        {
            if (!force && string.IsNullOrEmpty(mouseOverDescriptionText))
                return;
            try {
                if (system != null) {
                    GameScript game = GameScript.Get();
                    InteractiveObject current = game != null ?
                        game.IOMouseOverIO : null;
                    if (current == null || current.GetInstanceID() !=
                        system.GetInstanceID())
                        return;
                }
                UIManager ui = UIManager.Get();
                UnityEngine.UI.Text text = ui != null ?
                    ReadMember(ui, "TextDescription") as UnityEngine.UI.Text :
                    null;
                if (text != null && text.text.Length != 0)
                    text.text = string.Empty;
                mouseOverDescriptionText = null;
            } catch {
            }
        }

        private static void SetMouseOverDescription(string displayName)
        {
            if (string.IsNullOrEmpty(displayName) ||
                string.Equals(mouseOverDescriptionText, displayName,
                    StringComparison.Ordinal))
                return;
            try {
                UIManager ui = UIManager.Get();
                UnityEngine.UI.Text text = ui != null ?
                    ReadMember(ui, "TextDescription") as UnityEngine.UI.Text :
                    null;
                if (text != null && !string.Equals(text.text, displayName,
                        StringComparison.Ordinal))
                    text.text = displayName;
                mouseOverDescriptionText = displayName;
            } catch {
            }
        }

        private static void ClearEmptySystemHighlight()
        {
            if (emptySystemHighlightTarget == null)
                return;
            InteractiveObject target = emptySystemHighlightTarget;
            emptySystemHighlightTarget = null;
            emptySystemHighlightCompleted = false;
            try {
                target.SetMouseOver(false);
            } catch {
            }
            if (inspectionSystemsOverlayActive)
                SetInspectionSystemsOverlayPreviewHover(target, false);
        }

        private static void ShowBodyBaseHighlight(CarLoader loader)
        {
            if (loader == null || !inspectionSystemsOverlayActive)
                return;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return;
            }
            if (bodyHighlightBaseActive && bodyHighlightLoaderId == loaderId &&
                activeBodyHighlightTargets.Count > 0)
                return;

            if (bodyHighlightLoaderId != loaderId)
                ClearBodyHighlight();
            List<InteractiveObject> targets = GetBodyHighlightTargets(loader);
            activeBodyHighlightTargets.Clear();
            for (int index = 0; index < targets.Count; index++) {
                InteractiveObject target = targets[index];
                if (target == null)
                    continue;
                try {
                    target.SetMouseOver(true, Color.white);
                    activeBodyHighlightTargets.Add(target);
                } catch {
                }
            }
            bodyHighlightLoaderId = loaderId;
            bodyHighlightBaseActive = activeBodyHighlightTargets.Count > 0;
            bodyHighlightCompleted = false;
            bodyHighlightTarget = null;
        }

        private static void RestoreBodyBaseHighlight()
        {
            if (!inspectionSystemsOverlayActive ||
                inspectionSystemsOverlayLoader == null) {
                ClearBodyHighlight();
                return;
            }
            if (bodyHighlightBaseActive && bodyHighlightTarget == null)
                return;
            ShowBodyBaseHighlight(inspectionSystemsOverlayLoader);
        }

        private static void ShowBodyHover(CarLoader loader,
            InteractiveObject bodyObject, bool bodyComplete)
        {
            if (loader == null || bodyObject == null)
                return;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return;
            }
            if (bodyHighlightLoaderId != loaderId ||
                activeBodyHighlightTargets.Count == 0)
                ShowBodyBaseHighlight(loader);

            bool highlightChanged = bodyHighlightTarget == null ||
                bodyHighlightTarget.GetInstanceID() != bodyObject.GetInstanceID() ||
                bodyHighlightCompleted != bodyComplete ||
                bodyHighlightBaseActive;
            bodyHighlightTarget = bodyObject;
            if (!highlightChanged)
                return;

            for (int index = 0; index < activeBodyHighlightTargets.Count;
                index++) {
                InteractiveObject target = activeBodyHighlightTargets[index];
                if (target == null)
                    continue;
                try {
                    target.SetMouseOver(true, bodyComplete ? Color.green :
                        Color.yellow);
                } catch {
                }
            }
            bodyHighlightLoaderId = loaderId;
            bodyHighlightCompleted = bodyComplete;
            bodyHighlightBaseActive = false;
            LogDiagnostic("body hover active io=" + SafeIoId(bodyObject));
        }

        private static void ClearBodyHighlight()
        {
            if (bodyHighlightTarget == null && bodyHighlightLoaderId < 0 &&
                activeBodyHighlightTargets.Count == 0 &&
                !bodyHighlightBaseActive)
                return;
            for (int index = 0; index < activeBodyHighlightTargets.Count;
                index++) {
                InteractiveObject target = activeBodyHighlightTargets[index];
                if (target == null)
                    continue;
                try {
                    target.SetMouseOver(false);
                } catch {
                }
            }
            activeBodyHighlightTargets.Clear();
            bodyHighlightLoaderId = -1;
            bodyHighlightCompleted = false;
            bodyHighlightBaseActive = false;
            bodyHighlightTarget = null;
        }

        private static List<BodyPartSlot> GetBodyParts(CarLoader loader)
        {
            List<BodyPartSlot> empty = new List<BodyPartSlot>();
            if (loader == null)
                return empty;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return empty;
            }
            List<BodyPartSlot> cached;
            if (BodyPartsCache.TryGetValue(loaderId, out cached))
                return cached;

            cached = new List<BodyPartSlot>();
            string carId = ToText(ReadMember(loader, "carToLoad"));
            object bodyParts = InvokeNoArgs(loader, "GetCarParts") ??
                ReadMember(loader, "carParts");
            VisitCollection(bodyParts, delegate(object value) {
                if (value == null)
                    return;
                string partName = ToText(ReadMember(value, "name"));
                if (string.IsNullOrEmpty(partName) ||
                    string.Equals(partName, "body",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "details",
                        StringComparison.OrdinalIgnoreCase) ||
                    partName.StartsWith("license_plate_",
                        StringComparison.OrdinalIgnoreCase))
                    return;

                string normalId = !string.IsNullOrEmpty(carId) ?
                    carId + "-" + partName : partName;
                string tunedId = ToText(ReadMember(value, "TunedID"));
                string directId = ToText(InvokeNoArgs(value, "GetID"));
                if (string.IsNullOrEmpty(directId))
                    directId = ToText(ReadMember(value, "ID"));
                if (string.IsNullOrEmpty(directId))
                    directId = ToText(ReadMember(value, "PartID"));
                string id = null;
                if (!string.IsNullOrEmpty(tunedId) &&
                    IsPurchasablePart(tunedId))
                    id = tunedId;
                else if (!string.IsNullOrEmpty(normalId) &&
                    IsPurchasablePart(normalId))
                    id = normalId;
                else if (!string.IsNullOrEmpty(directId) &&
                    IsPurchasablePart(directId))
                    id = directId;
                else if (IsPurchasablePart(partName))
                    id = partName;
                if (string.IsNullOrEmpty(id))
                    return;
                BodyPartSlot slot = new BodyPartSlot();
                slot.Index = cached.Count;
                slot.Part = value;
                slot.Name = partName;
                slot.Id = id;
                cached.Add(slot);
            });
            AppendInteriorBodyParts(loader, cached);
            BodyPartsCache[loaderId] = cached;
            return cached;
        }

        private static void AppendInteriorBodyParts(CarLoader loader,
            List<BodyPartSlot> parts)
        {
            if (loader == null || parts == null)
                return;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return;
            }

            HashSet<int> seenInteractiveObjects = new HashSet<int>();
            try {
                UnhollowerBaseLib.Il2CppReferenceArray<UnityEngine.Object> all =
                    Resources.FindObjectsOfTypeAll(
                        UnhollowerRuntimeLib.Il2CppType.Of<InteractiveObject>());
                if (all == null)
                    return;

                for (int index = 0; index < all.Length; index++) {
                    UnityEngine.Object raw = all[index];
                    InteractiveObject candidate = raw != null ?
                        raw.TryCast<InteractiveObject>() : null;
                    if (candidate == null)
                        continue;
                    CarLoader candidateLoader = GetCarLoader(candidate);
                    if (candidateLoader == null ||
                        candidateLoader.GetInstanceID() != loaderId)
                        continue;

                    string ioId = NormalizeSystemName(SafeIoId(candidate));
                    if (!IsInteriorBodyPartId(ioId) ||
                        !IsPurchasablePart(ioId))
                        continue;

                    int ioInstanceId = candidate.GetInstanceID();
                    if (!seenInteractiveObjects.Add(ioInstanceId))
                        continue;

                    PartScript mountedPart = FindInteriorPartScript(candidate,
                        ioId);
                    string partId = mountedPart != null ?
                        SafePartId(mountedPart) : ioId;
                    if (string.IsNullOrEmpty(partId) ||
                        !IsPurchasablePart(partId))
                        partId = ioId;

                    BodyPartSlot slot = new BodyPartSlot();
                    slot.Index = parts.Count;
                    slot.Part = mountedPart;
                    slot.Name = ioId;
                    slot.Id = partId;
                    slot.ForceUnmounted = mountedPart == null;
                    parts.Add(slot);
                    LogDiagnostic("body interior slot id=" + partId +
                        " mounted=" + (mountedPart != null).ToString());
                }
            } catch {
            }
        }

        private static bool IsInteriorBodyPartId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            return id.StartsWith("seat_", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("bench_", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("steering_wheel",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static PartScript FindInteriorPartScript(
            InteractiveObject candidate, string expectedId)
        {
            if (candidate == null)
                return null;
            try {
                PartScript[] parts =
                    candidate.GetComponentsInChildren<PartScript>(true);
                if (parts == null)
                    return null;
                PartScript fallback = null;
                for (int index = 0; index < parts.Length; index++) {
                    PartScript part = parts[index];
                    if (part == null)
                        continue;
                    string id = SafePartId(part);
                    if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id))
                        continue;
                    if (string.Equals(id, expectedId,
                            StringComparison.OrdinalIgnoreCase))
                        return part;
                    if (fallback == null)
                        fallback = part;
                }
                return fallback;
            } catch {
                return null;
            }
        }

        private static List<InteractiveObject> GetBodyHighlightTargets(
            CarLoader loader)
        {
            List<InteractiveObject> empty = new List<InteractiveObject>();
            if (loader == null)
                return empty;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return empty;
            }
            List<InteractiveObject> cached;
            if (BodyHighlightTargetsCache.TryGetValue(loaderId, out cached))
                return cached;

            cached = new List<InteractiveObject>();
            HashSet<string> names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            List<BodyPartSlot> bodyParts = GetBodyParts(loader);
            for (int index = 0; index < bodyParts.Count; index++) {
                BodyPartSlot slot = bodyParts[index];
                if (slot != null && !string.IsNullOrEmpty(slot.Name))
                    names.Add(slot.Name);
            }

            try {
                UnhollowerBaseLib.Il2CppReferenceArray<UnityEngine.Object> all =
                    Resources.FindObjectsOfTypeAll(
                        UnhollowerRuntimeLib.Il2CppType.Of<InteractiveObject>());
                if (all != null) {
                    for (int index = 0; index < all.Length; index++) {
                        UnityEngine.Object raw = all[index];
                        InteractiveObject candidate = raw != null ?
                            raw.TryCast<InteractiveObject>() : null;
                        if (candidate == null)
                            continue;
                        CarLoader candidateLoader = GetCarLoader(candidate);
                        if (candidateLoader == null ||
                            candidateLoader.GetInstanceID() != loaderId)
                            continue;
                        string id = NormalizeSystemName(SafeIoId(candidate));
                        if (!IsWholeCarBodyObject(candidate) &&
                            !names.Contains(id))
                            continue;
                        cached.Add(candidate);
                    }
                }
            } catch {
            }
            BodyHighlightTargetsCache[loaderId] = cached;
            return cached;
        }

        private static List<BodySelectionSurface> GetBodySelectionSurfaces(
            CarLoader loader)
        {
            List<BodySelectionSurface> empty =
                new List<BodySelectionSurface>();
            if (loader == null)
                return empty;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return empty;
            }
            List<BodySelectionSurface> cached;
            if (BodySelectionSurfacesCache.TryGetValue(loaderId, out cached))
                return cached;

            cached = new List<BodySelectionSurface>();
            HashSet<int> colliderIds = new HashSet<int>();
            List<InteractiveObject> targets = GetBodyHighlightTargets(loader);
            for (int index = 0; index < targets.Count; index++) {
                InteractiveObject candidate = targets[index];
                if (candidate == null)
                    continue;
                try {
                    var colliders =
                        candidate.GetComponentsInChildren<Collider>(true);
                    if (colliders == null)
                        continue;
                    for (int colliderIndex = 0;
                        colliderIndex < colliders.Length; colliderIndex++) {
                        Collider collider = colliders[colliderIndex];
                        if (collider == null || !collider.enabled)
                            continue;
                        int colliderId = collider.GetInstanceID();
                        if (!colliderIds.Add(colliderId))
                            continue;
                        BodySelectionSurface surface =
                            new BodySelectionSurface();
                        surface.Collider = collider;
                        surface.Target = candidate;
                        cached.Add(surface);
                    }
                } catch {
                }
            }
            BodySelectionSurfacesCache[loaderId] = cached;
            return cached;
        }

        private static bool TryGetBodySelectionHit(Raycast raycast,
            CarLoader loader, Vector3 mousePosition,
            out InteractiveObject bodyTarget)
        {
            bodyTarget = null;
            if (raycast == null || loader == null)
                return false;

            Camera camera = bodySelectionCamera;
            if (camera == null) {
                camera = Camera.main;
                bodySelectionCamera = camera;
            }
            if (camera == null)
                return false;

            Vector3 origin = camera.transform.position;
            Vector3 direction = camera.transform.forward;
            float mechanicalDistance = float.MaxValue;
            Ray ray;
            if (inspectionSystemsOverlayActive) {
                ray = camera.ScreenPointToRay(mousePosition);
                origin = ray.origin;
                direction = ray.direction;
            } else {
                try {
                    if (raycast.hit.transform != null) {
                        Vector3 delta = raycast.hit.point - origin;
                        if (delta.sqrMagnitude > 0.000001f)
                            direction = delta.normalized;
                        mechanicalDistance = raycast.hit.distance;
                    }
                } catch {
                }
                ray = new Ray(origin, direction);
            }
            if (direction.sqrMagnitude <= 0.000001f)
                return false;

            float maxDistance = inspectionSystemsOverlayActive ?
                InspectionPointerRayDistance :
                (mechanicalDistance < float.MaxValue ? mechanicalDistance :
                    25f);
            float nearest = float.MaxValue;
            InteractiveObject nearestTarget = null;
            List<BodySelectionSurface> surfaces =
                GetBodySelectionSurfaces(loader);
            for (int index = 0; index < surfaces.Count; index++) {
                BodySelectionSurface surface = surfaces[index];
                if (surface == null || surface.Collider == null ||
                    surface.Target == null || !surface.Collider.enabled)
                    continue;
                try {
                    RaycastHit bodyHit;
                    if (!surface.Collider.Raycast(ray, out bodyHit,
                            maxDistance))
                        continue;
                    if (bodyHit.distance >= nearest)
                        continue;
                    nearest = bodyHit.distance;
                    nearestTarget = surface.Target;
                } catch {
                }
            }
            if (nearestTarget == null)
                return false;
            if (!inspectionSystemsOverlayActive &&
                mechanicalDistance < float.MaxValue &&
                nearest > mechanicalDistance + 0.001f)
                return false;
            bodyTarget = nearestTarget;
            return true;
        }

        private static bool IsBodyAggregateObject(CarLoader loader,
            InteractiveObject candidate)
        {
            if (candidate == null)
                return false;
            if (IsWholeCarBodyObject(candidate))
                return true;
            if (loader == null)
                return false;

            int candidateId;
            try {
                candidateId = candidate.GetInstanceID();
            } catch {
                return false;
            }
            List<InteractiveObject> targets = GetBodyHighlightTargets(loader);
            for (int index = 0; index < targets.Count; index++) {
                InteractiveObject target = targets[index];
                if (target == null)
                    continue;
                try {
                    if (target.GetInstanceID() == candidateId)
                        return true;
                } catch {
                }
            }
            return false;
        }

        private static BodyPassState GetBodyPassState(CarLoader loader)
        {
            if (loader == null)
                return null;
            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return null;
            }

            BodyPassState state;
            if (BodyPassStates.TryGetValue(loaderId, out state))
                return state;

            List<BodyPartSlot> parts = GetBodyParts(loader);
            state = new BodyPassState();
            state.Total = parts.Count;
            for (int index = 0; index < parts.Count; index++) {
                BodyPartSlot slot = parts[index];
                if (slot == null || IsBodyPartUnmounted(slot))
                    continue;
                if (IsBodyPartExamined(slot))
                    state.ExaminedSlots.Add(slot.Index);
            }
            BodyPassStates[loaderId] = state;
            LogDiagnostic("body pass init car=" + SafeLoaderName(loader) +
                " progress=" +
                state.ExaminedSlots.Count.ToString(CultureInfo.InvariantCulture) +
                "/" + state.Total.ToString(CultureInfo.InvariantCulture));
            return state;
        }

        private static bool UpdateBodyHold(CarLoader loader)
        {
            BodyPassState state = GetBodyPassState(loader);
            if (GetInspectionSkillLevel() <= 0) {
                if (state != null)
                    ResetBodyHold(state);
                return false;
            }
            if (state == null || state.Total <= 0) {
                if (state != null)
                    ResetBodyHold(state);
                return false;
            }
            if (state.ExaminedSlots.Count >= state.Total) {
                ResetBodyHold(state);
                return false;
            }
            state.HoldProgress = Mathf.Clamp01(state.HoldProgress +
                Time.deltaTime / GetSharpEyeHoldSeconds());
            SetSharpEyeCursorFill(state.HoldProgress);
            if (state.HoldProgress < 1f)
                return false;

            state.HoldProgress = 0f;
            SetSharpEyeCursorFill(0f);
            if (!ProcessOneBodyStep(loader, state))
                return false;
            MarkInspectionProgressChanged();
            PlaySwitchModeSound();
            return true;
        }

        private static void ResetVehicleInspection(CarLoader loader)
        {
            if (loader == null)
                return;
            ResetBodyInspection(loader);

            List<InteractiveObject> systems = GetInspectionSystems(loader);
            int resetSystems = 0;
            for (int index = 0; index < systems.Count; index++) {
                InteractiveObject system = systems[index];
                if (system == null ||
                    GetRequiredInspectionSkillLevel(system) <= 0)
                    continue;
                ResetSystemInspection(system);
                resetSystems++;
            }
            MarkInspectionProgressChanged();
            LogDiagnostic("inspection reset vehicle car=" +
                SafeLoaderName(loader) + " systems=" +
                resetSystems.ToString(CultureInfo.InvariantCulture));
            PlaySwitchModeSound();
        }

        private static void ResetBodyInspection(CarLoader loader)
        {
            if (loader == null)
                return;
            List<BodyPartSlot> parts = GetBodyParts(loader);
            for (int index = 0; index < parts.Count; index++) {
                BodyPartSlot slot = parts[index];
                if (slot == null || IsBodyPartUnmounted(slot) ||
                    slot.Part == null)
                    continue;
                PartScript part = slot.Part as PartScript;
                if (part != null)
                    ResetPartInspectionState(part);
                else
                    SetBodyPartExamined(slot, false);
            }
            try {
                BodyPassStates.Remove(loader.GetInstanceID());
            } catch {
            }
            SetSharpEyeCursorFill(0f);
        }

        private static void ResetSystemInspection(InteractiveObject system)
        {
            if (system == null)
                return;
            int systemId;
            try {
                systemId = system.GetInstanceID();
            } catch {
                return;
            }

            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part) ||
                    InspectionVisualSystem.IsDependentVisualPart(system,
                        SafePartId(part)))
                    continue;
                ResetPartInspectionState(part);
            }
            SystemPassStates.Remove(systemId);
            SetSharpEyeCursorFill(0f);
        }

        private static void ResetPartInspectionState(PartScript part)
        {
            if (part == null)
                return;
            WriteMember(part, "IsExamined", false);
        }

        private static void ResetInspectionResetState()
        {
            inspectionSystemResetHoldProgress = 0f;
            inspectionVehicleResetHoldProgress = 0f;
            inspectionSystemResetTriggered = false;
            inspectionVehicleResetTriggered = false;
            inspectionResetLoader = null;
        }

        private static void ResetInspectionResetInput(bool destroyHints)
        {
            ResetInspectionResetState();
            if (destroyHints)
                DestroyInspectionResetHints();
            else
                HideInspectionResetHints();
        }

        private static void CaptureInspectionResetHintSource()
        {
            if (inspectionResetHintSource != null)
                return;

            try {
                UIManager ui = UIManager.Get();
                object description = ui != null ?
                    ReadMember(ui, "currentAlternativeDescription") : null;
                object control = ResolveControlDescription(description);
                Component candidateSource = control as Component;
                if (IsUsableInspectionHintSource(candidateSource)) {
                    inspectionResetHintSource = control;
                    CaptureInspectionHintAnchor(candidateSource,
                        "current-alternative");
                    return;
                }
            } catch {
            }

            EnsureInspectionResetHintSource();
        }

        private static object ResolveControlDescription(object description)
        {
            if (description == null)
                return null;

            object current = ReadMember(description,
                "currentControlDescription");
            if (current is Component)
                return current;

            Component component = description as Component;
            if (component == null)
                return null;

            try {
                ControlDescription direct =
                    component.GetComponent<ControlDescription>();
                if (direct != null)
                    return direct;
                ControlDescription[] children =
                    component.GetComponentsInChildren<ControlDescription>(
                        true);
                if (children != null && children.Length > 0)
                    return children[0];
            } catch {
            }
            return null;
        }

        private static void EnsureInspectionResetHintSource()
        {
            if (inspectionResetHintSource != null ||
                inspectionResetHintSourceSearchAttempted)
                return;
            inspectionResetHintSourceSearchAttempted = true;
            ControlDescription source =
                FindExamineFooterControlDescription();
            inspectionResetHintSource = source;
            if (source != null)
                CaptureInspectionHintAnchor(source, "examine-footer");
            else
                LogDiagnostic("inspection hint source unavailable");
        }

        private static ControlDescription
            FindExamineFooterControlDescription()
        {
            try {
                UnhollowerBaseLib.Il2CppReferenceArray<UnityEngine.Object> all =
                    Resources.FindObjectsOfTypeAll(
                        UnhollowerRuntimeLib.Il2CppType.Of<ControlDescription>());
                if (all == null)
                    return null;

                ControlDescription fallback = null;
                for (int index = 0; index < all.Length; index++) {
                    UnityEngine.Object raw = all[index];
                    ControlDescription candidate = raw != null ?
                        raw.TryCast<ControlDescription>() : null;
                    if (!IsUsableInspectionHintSource(candidate))
                        continue;
                    if (candidate.gameObject.activeInHierarchy)
                        return candidate;
                    if (fallback == null)
                        fallback = candidate;
                }
                return fallback;
            } catch {
                return null;
            }
        }

        private static bool IsUsableInspectionHintSource(Component source)
        {
            if (source == null || source.gameObject == null)
                return false;
            RectTransform rect = source.GetComponent<RectTransform>();
            Transform parent = rect != null ? rect.parent : null;
            return parent != null && parent.parent != null &&
                parent.parent.gameObject.activeInHierarchy &&
                parent.name.StartsWith("_HoldExamine",
                    StringComparison.Ordinal);
        }

        private static void CaptureInspectionHintAnchor(
            Component source, string reason)
        {
            if (source == null)
                return;
            try {
                RectTransform rect = source.GetComponent<RectTransform>();
                if (rect == null)
                    return;
                Transform sourceParent = rect.parent;
                inspectionHintHost = sourceParent;
                LogInspectionHintSourceDiagnostics(source, reason);
            } catch {
            }
        }

        private static void LogInspectionHintSourceDiagnostics(
            Component source, string reason)
        {
            if (inspectionHintSourceDiagnosticsLogged || source == null)
                return;
            inspectionHintSourceDiagnosticsLogged = true;
            try {
                RectTransform rect = source.GetComponent<RectTransform>();
                LogDiagnostic("inspection hint source reason=" + reason +
                    " name=" + source.name + " path=" +
                    GetTransformPath(source.transform) + " active=" +
                    source.gameObject.activeInHierarchy.ToString() +
                    " anchor=" + DescribeRectTransform(rect) +
                    " parent=" + DescribeTransform(rect != null ?
                        rect.parent : null) + " host=" +
                    DescribeTransform(inspectionHintHost));
            } catch (Exception exception) {
                LogDiagnostic("inspection hint source diagnostics failed " +
                    "exception=" + exception.GetType().Name);
            }
        }

        private static void UpdateInspectionSystemListUi(CarLoader loader)
        {
            if (!inspectionSystemsOverlayActive) {
                if (inspectionSystemListVisible)
                    DestroyInspectionSystemList();
                return;
            }
            if (!inspectionSystemListVisible)
                return;
            if (loader == null)
                loader = inspectionResetLoader;
            if (loader == null)
                return;
            RefreshInspectionSystemList(loader);
        }

        private static void RefreshInspectionSystemList(CarLoader loader)
        {
            if (!inspectionSystemListVisible || loader == null)
                return;
            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return;
            }
            if (!inspectionSystemListDirty &&
                inspectionSystemListLoaderId == loaderId)
                return;

            DestroyInspectionSystemListPanel();
            if (!BuildInspectionSystemListPanel(loader))
                return;
            inspectionSystemListLoaderId = loaderId;
            inspectionSystemListDirty = false;
        }

        private static bool BuildInspectionSystemListPanel(CarLoader loader)
        {
            try {
                GameObject statsObject = GameObject.Find(
                    "!!Logic/Canvas/StatsContainer");
                if (statsObject == null)
                    statsObject = GameObject.Find("StatsContainer");
                RectTransform statsRect = statsObject != null ?
                    statsObject.GetComponent<RectTransform>() : null;
                Transform backgroundTransform = statsRect != null ?
                    statsRect.Find("BG") : null;
                Transform textSourceTransform = statsRect != null ?
                    statsRect.Find("Level/Image") : null;
                UnityEngine.UI.Image backgroundSource =
                    backgroundTransform != null ?
                    backgroundTransform.GetComponent<UnityEngine.UI.Image>() :
                    null;
                UnityEngine.UI.Text textSource = textSourceTransform != null ?
                    textSourceTransform.GetComponent<UnityEngine.UI.Text>() :
                    null;
                if (statsRect == null || backgroundSource == null ||
                    textSource == null)
                    return false;

                int skillLevel = GetInspectionSkillLevel();
                if (skillLevel <= 0)
                    return false;
                List<InspectionSystemListEntry> entries =
                    GetInspectionSystemListEntries(loader);
                if (entries.Count == 0)
                    return false;

                GameObject panelObject = UnityEngine.Object.Instantiate(
                    backgroundSource.gameObject) as GameObject;
                if (panelObject == null)
                    return false;
                panelObject.name =
                    "CMS21GameplayPlus_SharpEye_SystemList";
                RectTransform panelRect =
                    panelObject.GetComponent<RectTransform>();
                if (panelRect == null) {
                    UnityEngine.Object.Destroy(panelObject);
                    return false;
                }
                panelRect.SetParent(statsRect, false);
                panelRect.anchorMin = new Vector2(1f, 1f);
                panelRect.anchorMax = new Vector2(1f, 1f);
                panelRect.pivot = new Vector2(1f, 1f);
                panelRect.anchoredPosition = new Vector2(0f,
                    -statsRect.rect.height - 4f);
                panelRect.localScale = Vector3.one;
                panelRect.SetAsLastSibling();
                panelRect.sizeDelta = new Vector2(
                    Mathf.Max(InspectionSystemListWidth,
                        statsRect.rect.width),
                    InspectionSystemListHeaderHeight +
                    InspectionSystemListRowGap +
                    entries.Count * InspectionSystemListRowHeight +
                    (entries.Count - 1) * InspectionSystemListRowGap);
                UnityEngine.UI.Image panelImage =
                    panelObject.GetComponent<UnityEngine.UI.Image>();
                if (panelImage != null) {
                    panelImage.enabled = false;
                    panelImage.raycastTarget = false;
                }
                inspectionSystemListPanel = panelRect;

                AddInspectionSystemListHeader(panelRect, backgroundSource,
                    textSource);
                for (int index = 0; index < entries.Count; index++)
                    AddInspectionSystemListRow(panelRect, backgroundSource,
                        textSource, entries[index], index);
                panelObject.SetActive(true);
                return true;
            } catch {
                DestroyInspectionSystemListPanel();
                return false;
            }
        }

        private struct InspectionSystemListEntry
        {
            internal string Name;
            internal int MaximumPercent;
            internal int ProgressPercent;
            internal float ProgressFill;
        }

        private static List<InspectionSystemListEntry>
            GetInspectionSystemListEntries(CarLoader loader)
        {
            List<InspectionSystemListEntry> entries =
                new List<InspectionSystemListEntry>();
            int skillLevel = GetInspectionSkillLevel();
            BodyPassState bodyState = GetBodyPassState(loader);
            if (bodyState != null && bodyState.Total > 0) {
                int available = skillLevel >= 1 ? bodyState.Total : 0;
                int examined = available > 0 ? Math.Min(available,
                    bodyState.ExaminedSlots.Count) : 0;
                entries.Add(CreateInspectionSystemListEntry(
                    GetBodyDisplayName(), examined, available,
                    bodyState.Total));
            }

            List<InteractiveObject> systems = GetInspectionSystems(loader);
            for (int index = 0; index < systems.Count; index++) {
                InteractiveObject system = systems[index];
                if (system == null)
                    continue;
                int examined;
                int available;
                int full;
                GetSystemAvailableProgress(system, skillLevel, out examined,
                    out available, out full);
                if (available <= 0)
                    continue;
                entries.Add(CreateInspectionSystemListEntry(
                    GetLocalizedSystemName(system), examined, available,
                    full));
            }
            return entries;
        }

        private static InspectionSystemListEntry CreateInspectionSystemListEntry(
            string name, int examined, int available, int full)
        {
            float maximum = full > 0 ?
                Mathf.Clamp01((float)available / full) : 0f;
            float progress = full > 0 ?
                Mathf.Clamp01((float)examined / full) : 0f;
            return new InspectionSystemListEntry {
                Name = name,
                MaximumPercent = Mathf.RoundToInt(maximum * 100f),
                ProgressPercent = Mathf.RoundToInt(progress * 100f),
                ProgressFill = available > 0 ?
                    Mathf.Clamp01((float)examined / available) : 0f
            };
        }

        private static void AddInspectionSystemListHeader(
            RectTransform panelRect, UnityEngine.UI.Image backgroundSource,
            UnityEngine.UI.Text textSource)
        {
            RectTransform rowRect = CreateInspectionSystemListRowBackground(
                panelRect, backgroundSource, "Header", 0f,
                InspectionSystemListHeaderHeight);
            if (rowRect == null)
                return;
            AddInspectionSystemListBadge(rowRect, backgroundSource,
                textSource, "HeaderNumber", "#", 0f,
                InspectionSystemListNumberWidth, Color.clear, Color.white);
            float rightColumns = InspectionSystemListMaximumWidth +
                InspectionSystemListProgressWidth + 4f;
            AddInspectionSystemListText(rowRect, textSource,
                GetInspectionHintLabel("LOC_SharpEyeSystemHeaderName"),
                InspectionSystemListNumberWidth + 4f, rightColumns,
                TextAnchor.MiddleLeft, 7, Color.white);
            AddInspectionSystemListText(rowRect, textSource,
                GetInspectionHintLabel("LOC_SharpEyeSystemHeaderMaximum"),
                -(InspectionSystemListProgressWidth +
                    InspectionSystemListMaximumWidth + 2f),
                InspectionSystemListMaximumWidth, TextAnchor.MiddleCenter,
                7, Color.white, true);
            AddInspectionSystemListText(rowRect, textSource,
                GetInspectionHintLabel("LOC_SharpEyeSystemHeaderProgress"),
                -InspectionSystemListProgressWidth,
                InspectionSystemListProgressWidth, TextAnchor.MiddleCenter,
                7, Color.white, true);
        }

        private static void AddInspectionSystemListRow(
            RectTransform panelRect, UnityEngine.UI.Image backgroundSource,
            UnityEngine.UI.Text textSource, InspectionSystemListEntry entry,
            int index)
        {
            float y = -(InspectionSystemListHeaderHeight +
                InspectionSystemListRowGap + index *
                (InspectionSystemListRowHeight + InspectionSystemListRowGap));
            RectTransform rowRect = CreateInspectionSystemListRowBackground(
                panelRect, backgroundSource, "System_" +
                (index + 1).ToString(CultureInfo.InvariantCulture), y,
                InspectionSystemListRowHeight);
            if (rowRect == null)
                return;

            AddInspectionSystemListBadge(rowRect, backgroundSource,
                textSource, "Number",
                (index + 1).ToString(CultureInfo.InvariantCulture),
                0f, InspectionSystemListNumberWidth,
                inspectionSystemListUnavailableColor, Color.black);
            AddInspectionSystemListName(rowRect, textSource, entry.Name);
            AddInspectionSystemListBadge(rowRect, backgroundSource,
                textSource, "Maximum",
                entry.MaximumPercent.ToString(CultureInfo.InvariantCulture) +
                    "%",
                -(InspectionSystemListProgressWidth +
                    InspectionSystemListMaximumWidth + 2f),
                InspectionSystemListMaximumWidth,
                inspectionSystemListUnavailableColor, Color.black, true);
            AddInspectionSystemListProgress(rowRect, backgroundSource,
                textSource, entry.ProgressPercent, entry.ProgressFill);
        }

        private static RectTransform CreateInspectionSystemListRowBackground(
            RectTransform panelRect, UnityEngine.UI.Image backgroundSource,
            string name, float y, float height)
        {
            GameObject rowObject = UnityEngine.Object.Instantiate(
                backgroundSource.gameObject) as GameObject;
            if (rowObject == null)
                return null;
            rowObject.name = name;
            RectTransform rowRect = rowObject.GetComponent<RectTransform>();
            UnityEngine.UI.Image rowImage =
                rowObject.GetComponent<UnityEngine.UI.Image>();
            if (rowRect == null || rowImage == null) {
                UnityEngine.Object.Destroy(rowObject);
                return null;
            }
            rowRect.SetParent(panelRect, false);
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchoredPosition = new Vector2(0f, y);
            rowRect.sizeDelta = new Vector2(0f, height);
            rowImage.raycastTarget = false;
            rowObject.SetActive(true);
            return rowRect;
        }

        private static void AddInspectionSystemListBadge(
            RectTransform rowRect, UnityEngine.UI.Image backgroundSource,
            UnityEngine.UI.Text textSource, string name, string value,
            float rightOrLeft, float width, Color background,
            Color foreground, bool rightAnchored = false)
        {
            GameObject badgeObject = UnityEngine.Object.Instantiate(
                backgroundSource.gameObject) as GameObject;
            if (badgeObject == null)
                return;
            badgeObject.name = name;
            RectTransform badgeRect =
                badgeObject.GetComponent<RectTransform>();
            UnityEngine.UI.Image badgeImage =
                badgeObject.GetComponent<UnityEngine.UI.Image>();
            if (badgeRect == null || badgeImage == null) {
                UnityEngine.Object.Destroy(badgeObject);
                return;
            }
            badgeRect.SetParent(rowRect, false);
            if (rightAnchored) {
                badgeRect.anchorMin = new Vector2(1f, 0f);
                badgeRect.anchorMax = new Vector2(1f, 1f);
                badgeRect.pivot = new Vector2(0f, 0.5f);
            } else {
                badgeRect.anchorMin = new Vector2(0f, 0f);
                badgeRect.anchorMax = new Vector2(0f, 1f);
                badgeRect.pivot = new Vector2(0f, 0.5f);
            }
            badgeRect.anchoredPosition = new Vector2(rightOrLeft, 0f);
            badgeRect.sizeDelta = new Vector2(width, 0f);
            badgeImage.color = background;
            badgeImage.raycastTarget = false;
            AddInspectionSystemListText(badgeRect, textSource, value, 0f, 0f,
                TextAnchor.MiddleCenter, 8, foreground, false, true);
            badgeObject.SetActive(true);
        }

        private static void AddInspectionSystemListProgress(
            RectTransform rowRect, UnityEngine.UI.Image backgroundSource,
            UnityEngine.UI.Text textSource, int progressPercent, float fill)
        {
            GameObject barObject = UnityEngine.Object.Instantiate(
                backgroundSource.gameObject) as GameObject;
            if (barObject == null)
                return;
            barObject.name = "Progress";
            RectTransform barRect = barObject.GetComponent<RectTransform>();
            UnityEngine.UI.Image barImage =
                barObject.GetComponent<UnityEngine.UI.Image>();
            if (barRect == null || barImage == null) {
                UnityEngine.Object.Destroy(barObject);
                return;
            }
            barRect.SetParent(rowRect, false);
            barRect.anchorMin = new Vector2(1f, 0f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot = new Vector2(1f, 0.5f);
            barRect.anchoredPosition = Vector2.zero;
            barRect.sizeDelta = new Vector2(InspectionSystemListProgressWidth,
                0f);
            barImage.color = inspectionSystemListUnavailableColor;
            barImage.raycastTarget = false;

            if (fill > 0f) {
                GameObject fillObject = UnityEngine.Object.Instantiate(
                    backgroundSource.gameObject) as GameObject;
                if (fillObject != null) {
                    fillObject.name = "Fill";
                    RectTransform fillRect =
                        fillObject.GetComponent<RectTransform>();
                    UnityEngine.UI.Image fillImage =
                        fillObject.GetComponent<UnityEngine.UI.Image>();
                    if (fillRect != null && fillImage != null) {
                        fillRect.SetParent(barRect, false);
                        fillRect.anchorMin = Vector2.zero;
                        fillRect.anchorMax = new Vector2(
                            Mathf.Clamp01(fill), 1f);
                        fillRect.offsetMin = Vector2.zero;
                        fillRect.offsetMax = Vector2.zero;
                        fillImage.color = inspectionSystemListCompletedColor;
                        fillImage.raycastTarget = false;
                        fillObject.SetActive(true);
                    } else {
                        UnityEngine.Object.Destroy(fillObject);
                    }
                }
            }
            AddInspectionSystemListText(barRect, textSource,
                progressPercent.ToString(CultureInfo.InvariantCulture) + "%",
                0f, 0f, TextAnchor.MiddleCenter, 8, Color.black, false, true);
            barObject.SetActive(true);
        }

        private static void AddInspectionSystemListName(RectTransform rowRect,
            UnityEngine.UI.Text textSource, string name)
        {
            float rightColumns = InspectionSystemListMaximumWidth +
                InspectionSystemListProgressWidth + 6f;
            string displayName = string.IsNullOrEmpty(name) ?
                "?" : name.Replace("\r", " ").Replace("\n", " ");
            AddInspectionSystemListText(rowRect, textSource,
                displayName.ToUpperInvariant(),
                InspectionSystemListNumberWidth + 4f, rightColumns,
                TextAnchor.MiddleLeft, 8, inspectionSystemListTextColor);
        }

        private static void AddInspectionSystemListText(
            RectTransform parent, UnityEngine.UI.Text textSource,
            string value, float leftOrRight, float oppositeInset,
            TextAnchor alignment, int fontSize, Color color,
            bool rightAnchored = false, bool stretch = false)
        {
            GameObject textObject = UnityEngine.Object.Instantiate(
                textSource.gameObject) as GameObject;
            if (textObject == null)
                return;
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            UnityEngine.UI.Text text =
                textObject.GetComponent<UnityEngine.UI.Text>();
            if (textRect == null || text == null) {
                UnityEngine.Object.Destroy(textObject);
                return;
            }
            textRect.SetParent(parent, false);
            if (stretch) {
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
            } else if (rightAnchored) {
                textRect.anchorMin = new Vector2(1f, 0f);
                textRect.anchorMax = new Vector2(1f, 1f);
                textRect.pivot = new Vector2(0f, 0.5f);
                textRect.anchoredPosition = new Vector2(leftOrRight, 0f);
                textRect.sizeDelta = new Vector2(oppositeInset, 0f);
            } else {
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(leftOrRight, 0f);
                textRect.offsetMax = new Vector2(-oppositeInset, 0f);
            }
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 6;
            text.resizeTextMaxSize = fontSize;
            text.color = color;
            text.raycastTarget = false;
            if (text.canvasRenderer != null)
                text.canvasRenderer.SetColor(color);
            textObject.SetActive(true);
        }

        private static void DestroyInspectionSystemListPanel()
        {
            if (inspectionSystemListPanel != null &&
                inspectionSystemListPanel.gameObject != null) {
                UnityEngine.Object.Destroy(
                    inspectionSystemListPanel.gameObject);
            }
            inspectionSystemListPanel = null;
            inspectionSystemListLoaderId = -1;
        }

        private static void DestroyInspectionSystemList()
        {
            DestroyInspectionSystemListPanel();
            inspectionSystemListVisible = false;
            inspectionSystemListDirty = true;
        }

        private static void MarkInspectionProgressChanged()
        {
            indicatorDirty = true;
            inspectionVehicleProgressDirty = true;
            inspectionSystemListDirty = true;
        }

        private static void ResetInspectionVehicleProgressCache()
        {
            inspectionVehicleProgressLoaderId = -1;
            inspectionVehicleProgressDirty = true;
            inspectionVehicleHasProgress = false;
            inspectionSystemListDirty = true;
        }

        private static bool HasVehicleInspectionProgress(CarLoader loader)
        {
            if (loader == null)
                return false;
            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return false;
            }
            if (!inspectionVehicleProgressDirty &&
                inspectionVehicleProgressLoaderId == loaderId)
                return inspectionVehicleHasProgress;

            bool hasProgress = false;
            BodyPassState bodyState = GetBodyPassState(loader);
            if (bodyState != null && bodyState.ExaminedSlots.Count > 0)
                hasProgress = true;
            if (!hasProgress) {
                List<InteractiveObject> systems =
                    GetInspectionSystems(loader);
                for (int index = 0; index < systems.Count; index++) {
                    InteractiveObject system = systems[index];
                    if (system == null)
                        continue;
                    int examined;
                    int total;
                    GetSystemProgress(system, out examined, out total);
                    if (total > 0 && examined > 0) {
                        hasProgress = true;
                        break;
                    }
                }
            }

            inspectionVehicleProgressLoaderId = loaderId;
            inspectionVehicleHasProgress = hasProgress;
            inspectionVehicleProgressDirty = false;
            return hasProgress;
        }

        private static string DescribeRectTransform(RectTransform rect)
        {
            if (rect == null)
                return "<null>";
            return rect.name + " pos=" +
                rect.anchoredPosition.x.ToString("0.##",
                    CultureInfo.InvariantCulture) + "," +
                rect.anchoredPosition.y.ToString("0.##",
                    CultureInfo.InvariantCulture) + " size=" +
                rect.rect.width.ToString("0.##",
                    CultureInfo.InvariantCulture) + "x" +
                rect.rect.height.ToString("0.##",
                    CultureInfo.InvariantCulture);
        }

        private static string DescribeTransform(Transform transform)
        {
            if (transform == null)
                return "<null>";
            return transform.GetType().Name + " path=" +
                GetTransformPath(transform) + " active=" +
                transform.gameObject.activeInHierarchy.ToString();
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";
            string path = transform.name;
            Transform current = transform.parent;
            int depth = 0;
            while (current != null && depth++ < 8) {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        private static void UpdateInspectionResetHints(CarLoader loader,
            InteractiveObject target, bool bodyTarget,
            InspectionTargetState targetState, int skillLevel,
            bool examineHeld)
        {
            if (loader == null) {
                HideInspectionActionHints();
                return;
            }
            if (examineHeld) {
                HideInspectionActionHints();
                SuppressInspectionFooterForHold();
                return;
            }

            bool examineHintWasVisible = inspectionExamineHintVisible;
            bool systemResetHintWasVisible =
                inspectionSystemResetHintVisible;
            bool vehicleResetHintWasVisible =
                inspectionVehicleResetHintVisible;

            bool hasTargetProgress = target != null &&
                targetState.HasProgress;
            bool canResetTarget = hasTargetProgress &&
                (bodyTarget ? skillLevel > 0 : targetState.Available > 0);
            bool hasVehicleProgress = HasVehicleInspectionProgress(loader);
            bool showExamineHint = target != null && targetState.CanExamine;

            CaptureInspectionResetHintSource();
            if (inspectionResetHintSource == null) {
                HideInspectionActionHints();
                return;
            }

            if (showExamineHint) {
                string examineLabel = GetInspectionExamineHintLabel();
                if (!EnsureInspectionExamineHint(examineLabel)) {
                    inspectionExamineHintVisible = false;
                } else {
                    if (!inspectionExamineHintVisible ||
                        !string.Equals(inspectionExamineHintLabel,
                            examineLabel, StringComparison.Ordinal)) {
                        UiIntegrationBridge.UpdateNativeFooterHint(
                            inspectionExamineHint, examineLabel, true);
                        ApplyInspectionHintVisualStyle(
                            inspectionExamineHint);
                        inspectionExamineHintLabel = examineLabel;
                        inspectionExamineHintVisible = true;
                        LogInspectionExamineHintShown(loader, target,
                            bodyTarget);
                    }
                }
            } else {
                HideInspectionExamineHint();
            }

            if ((canResetTarget || hasVehicleProgress) &&
                EnsureInspectionResetHints()) {
                if (canResetTarget) {
                    string systemLabel = GetInspectionHintLabel(
                        "LOC_SharpEyeResetSystem");
                    if (!inspectionSystemResetHintVisible ||
                        !string.Equals(inspectionSystemResetHintLabel,
                            systemLabel, StringComparison.Ordinal)) {
                        UiIntegrationBridge.UpdateNativeFooterHint(
                            inspectionSystemResetHint, systemLabel, true);
                        ApplyInspectionHintVisualStyle(
                            inspectionSystemResetHint);
                        inspectionSystemResetHintLabel = systemLabel;
                        inspectionSystemResetHintVisible = true;
                    }
                    SetInspectionResetHintHoldProgress(
                        inspectionSystemResetHint,
                        ref inspectionSystemResetHintProgress,
                        inspectionSystemResetHoldProgress /
                            GetSharpEyeHoldSeconds());
                } else {
                    HideInspectionSystemResetHint();
                }

                if (hasVehicleProgress) {
                    string vehicleLabel = GetInspectionHintLabel(
                        "LOC_SharpEyeResetVehicle");
                    if (!inspectionVehicleResetHintVisible ||
                        !string.Equals(inspectionVehicleResetHintLabel,
                            vehicleLabel, StringComparison.Ordinal)) {
                        UiIntegrationBridge.UpdateNativeFooterHint(
                            inspectionVehicleResetHint, vehicleLabel, true);
                        ApplyInspectionHintVisualStyle(
                            inspectionVehicleResetHint);
                        inspectionVehicleResetHintLabel = vehicleLabel;
                        inspectionVehicleResetHintVisible = true;
                    }
                    SetInspectionResetHintHoldProgress(
                        inspectionVehicleResetHint,
                        ref inspectionVehicleResetHintProgress,
                        inspectionVehicleResetHoldProgress /
                            GetSharpEyeHoldSeconds());
                } else {
                    HideInspectionVehicleResetHint();
                }
            } else {
                HideInspectionResetActionHints();
            }

            ShowInspectionSystemsHint();
            if (examineHintWasVisible != inspectionExamineHintVisible ||
                systemResetHintWasVisible !=
                    inspectionSystemResetHintVisible ||
                vehicleResetHintWasVisible !=
                    inspectionVehicleResetHintVisible)
                UpdateInspectionHintHostVisibility();
        }

        private static void ShowInspectionSystemsHint()
        {
            if (inspectionResetHintSource == null)
                return;
            string label = GetInspectionHintLabel(
                inspectionSystemListVisible ?
                    "LOC_SharpEyeHideSystems" :
                    "LOC_SharpEyeShowSystems");
            if (inspectionShowSystemsHint == null) {
                inspectionShowSystemsHint =
                    UiIntegrationBridge.CreateNativeFooterHint(
                        inspectionResetHintSource,
                        "CMS21GameplayPlus_SharpEye_ShowSystems",
                        new string[] { "Tab" }, label, false, 0f);
                PrepareInspectionHintHost(inspectionShowSystemsHint);
                LogInspectionHintCreated("show-systems",
                    inspectionShowSystemsHint);
            }
            if (inspectionShowSystemsHint == null)
                return;
            bool wasVisible = inspectionShowSystemsHintVisible;
            if (!wasVisible || !string.Equals(inspectionShowSystemsHintLabel,
                    label, StringComparison.Ordinal)) {
                UiIntegrationBridge.UpdateNativeFooterHint(
                    inspectionShowSystemsHint, label, true);
                ApplyInspectionHintVisualStyle(inspectionShowSystemsHint);
                inspectionShowSystemsHintLabel = label;
                inspectionShowSystemsHintVisible = true;
                if (!wasVisible)
                    UpdateInspectionHintHostVisibility();
            }
        }
        private static void LogInspectionExamineHintShown(
            CarLoader loader, InteractiveObject target, bool bodyTarget)
        {
            if (bodyTarget) {
                BodyPassState bodyState = GetBodyPassState(loader);
                LogDiagnostic("inspection examine hint show body progress=" +
                    (bodyState != null ?
                        bodyState.ExaminedSlots.Count.ToString(
                            CultureInfo.InvariantCulture) + "/" +
                        bodyState.Total.ToString(CultureInfo.InvariantCulture) :
                        "<none>"));
                return;
            }

            SystemPassState state = GetSystemPassState(target);
            int examined;
            int total;
            GetSystemProgress(target, out examined, out total);
            LogDiagnostic("inspection examine hint show system=" +
                SafeIoId(target) + " progress=" +
                examined.ToString(CultureInfo.InvariantCulture) + "/" +
                total.ToString(CultureInfo.InvariantCulture) +
                " customPending=" +
                (state != null && HasPendingCustomStep(target, state)).ToString());
        }

        private static string GetInspectionExamineHintLabel()
        {
            return GetInspectionHintLabel("LOC_SharpEyeExamine");
        }

        private static string GetInspectionHintLabel(string key)
        {
            string text = ModLocalization.Get(key);
            return !string.IsNullOrEmpty(text) ?
                text.ToUpperInvariant() : string.Empty;
        }

        private static bool EnsureInspectionExamineHint(string label)
        {
            if (inspectionResetHintSource == null)
                return false;
            if (inspectionExamineHint == null) {
                inspectionExamineHint =
                    UiIntegrationBridge.CreateNativeFooterHint(
                        inspectionResetHintSource,
                        "CMS21GameplayPlus_SharpEye_Examine",
                        null, label, true, GetSharpEyeHoldSeconds());
                PrepareInspectionHintHost(inspectionExamineHint);
                LogInspectionHintCreated("examine", inspectionExamineHint);
            }
            return inspectionExamineHint != null;
        }
        private static bool EnsureInspectionResetHints()
        {
            if (inspectionResetHintSource == null)
                return false;
            if (inspectionSystemResetHint == null) {
                inspectionSystemResetHint =
                    UiIntegrationBridge.CreateNativeFooterHint(
                        inspectionResetHintSource,
                        "CMS21GameplayPlus_SharpEye_ResetSystem",
                        new string[] { "LeftAlt" },
                        GetInspectionHintLabel("LOC_SharpEyeResetSystem"),
                        true, GetSharpEyeHoldSeconds());
                PrepareInspectionHintHost(inspectionSystemResetHint);
                LogInspectionHintCreated("reset-system",
                    inspectionSystemResetHint);
            }
            if (inspectionVehicleResetHint == null) {
                inspectionVehicleResetHint =
                    UiIntegrationBridge.CreateNativeFooterHint(
                        inspectionResetHintSource,
                        "CMS21GameplayPlus_SharpEye_ResetVehicle",
                        new string[] { "Space" },
                        GetInspectionHintLabel("LOC_SharpEyeResetVehicle"),
                        true, GetSharpEyeHoldSeconds());
                PrepareInspectionHintHost(inspectionVehicleResetHint);
                LogInspectionHintCreated("reset-vehicle",
                    inspectionVehicleResetHint);
            }
            return inspectionSystemResetHint != null &&
                inspectionVehicleResetHint != null;
        }

        private static void PrepareInspectionHintHost(
            UiIntegrationBridge.NativeHintHandle handle)
        {
            if (handle == null || handle.Rect == null ||
                inspectionHintHost == null)
                return;
            if (handle.Rect.parent != inspectionHintHost &&
                !UiIntegrationBridge.ReparentNativeFooterHint(handle,
                    inspectionHintHost))
                return;
            ApplyInspectionHintVisualStyle(handle);
        }

        private static void ApplyInspectionHintVisualStyle(
            UiIntegrationBridge.NativeHintHandle handle)
        {
            if (handle == null || handle.Rect == null)
                return;
            Component source = inspectionResetHintSource as Component;
            if (source == null)
                return;
            try {
                UnityEngine.UI.Text[] sourceTexts =
                    source.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                UnityEngine.UI.Text[] targetTexts =
                    handle.Rect.GetComponentsInChildren<
                        UnityEngine.UI.Text>(true);
                int count = Mathf.Min(sourceTexts != null ?
                    sourceTexts.Length : 0, targetTexts != null ?
                    targetTexts.Length : 0);
                for (int index = 0; index < count; index++) {
                    UnityEngine.UI.Text sourceText = sourceTexts[index];
                    UnityEngine.UI.Text targetText = targetTexts[index];
                    if (sourceText == null || targetText == null)
                        continue;
                    targetText.font = sourceText.font;
                    targetText.fontSize = sourceText.fontSize;
                    targetText.fontStyle = sourceText.fontStyle;
                    targetText.color = Color.white;
                    targetText.material = sourceText.material;
                    targetText.alignment = sourceText.alignment;
                    targetText.horizontalOverflow =
                        sourceText.horizontalOverflow;
                    targetText.verticalOverflow = sourceText.verticalOverflow;
                    targetText.resizeTextForBestFit =
                        sourceText.resizeTextForBestFit;
                    targetText.resizeTextMinSize =
                        sourceText.resizeTextMinSize;
                    targetText.resizeTextMaxSize =
                        sourceText.resizeTextMaxSize;
                    if (targetText.canvasRenderer != null)
                        targetText.canvasRenderer.SetColor(Color.white);
                }
                for (int index = 0; index < targetTexts.Length; index++) {
                    UnityEngine.UI.Text targetText = targetTexts[index];
                    if (targetText == null)
                        continue;
                    targetText.color = Color.white;
                    if (targetText.canvasRenderer != null)
                        targetText.canvasRenderer.SetColor(Color.white);
                }
                object description = ReadMember(handle.Hint, "Description");
                WriteMember(description, "forceNormalColor", true);
                UiIntegrationBridge.RefreshNativeFooterHintVisualBounds(
                    handle);
            } catch {
            }
        }

        private static void LogInspectionHintCreated(string kind,
            UiIntegrationBridge.NativeHintHandle handle)
        {
            if (handle == null || handle.Rect == null)
                return;
            try {
                LogDiagnostic("inspection hint created kind=" + kind +
                    " active=" +
                    handle.Rect.gameObject.activeInHierarchy.ToString() +
                    " rect=" + DescribeRectTransform(handle.Rect) +
                    " parent=" + DescribeTransform(handle.Rect.parent));
            } catch {
            }
        }

        private static void UpdateInspectionHintHostVisibility()
        {
            Transform host = inspectionHintHost;
            Component source = inspectionResetHintSource as Component;
            if (host == null || source == null || source.gameObject == null)
                return;

            bool customVisible = inspectionExamineHintVisible ||
                inspectionSystemResetHintVisible ||
                inspectionVehicleResetHintVisible ||
                inspectionShowSystemsHintVisible;
            bool nativeActive = IsNativeExamineUiActive();
            if (!customVisible) {
                RestoreInspectionHintHost(nativeActive);
                return;
            }

            if (!host.gameObject.activeSelf) {
                if (!inspectionHintHostForcedActive) {
                    inspectionHintHostWasActiveSelf =
                        host.gameObject.activeSelf;
                    inspectionHintHostForcedActive = true;
                }
                host.gameObject.SetActive(true);
            }

            if (nativeActive) {
                RestoreInspectionHintSource();
                return;
            }

            if (source.gameObject.activeSelf) {
                if (!inspectionHintSourceSuppressed) {
                    inspectionHintSourceWasActiveSelf =
                        source.gameObject.activeSelf;
                    inspectionHintSourceSuppressed = true;
                }
                source.gameObject.SetActive(false);
                RectTransform hostRect = host as RectTransform;
                if (hostRect != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(
                        hostRect);
            }
        }

        private static void RestoreInspectionHintHost(bool nativeActive)
        {
            RestoreInspectionHintSource();
            if (!inspectionHintHostForcedActive || inspectionHintHost == null ||
                nativeActive)
                return;
            inspectionHintHost.gameObject.SetActive(
                inspectionHintHostWasActiveSelf);
            inspectionHintHostForcedActive = false;
            inspectionHintHostWasActiveSelf = false;
        }

        private static void RestoreInspectionHintSource()
        {
            if (!inspectionHintSourceSuppressed)
                return;
            Component source = inspectionResetHintSource as Component;
            if (source != null && source.gameObject != null) {
                source.gameObject.SetActive(inspectionHintSourceWasActiveSelf);
                RectTransform hostRect = inspectionHintHost as RectTransform;
                if (hostRect != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(
                        hostRect);
            }
            inspectionHintSourceSuppressed = false;
            inspectionHintSourceWasActiveSelf = false;
        }

        private static void SetInspectionResetHintHoldProgress(
            UiIntegrationBridge.NativeHintHandle handle, ref float cached,
            float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(cached, value))
                return;
            UiIntegrationBridge.SetNativeFooterHintHoldProgress(handle, value);
            cached = value;
        }

        private static void HideInspectionExamineHint()
        {
            if (!inspectionExamineHintVisible)
                return;
            UiIntegrationBridge.UpdateNativeFooterHint(
                inspectionExamineHint, inspectionExamineHintLabel ??
                    string.Empty, false);
            inspectionExamineHintVisible = false;
        }

        private static void HideInspectionSystemResetHint()
        {
            if (!inspectionSystemResetHintVisible)
                return;
            SetInspectionResetHintHoldProgress(inspectionSystemResetHint,
                ref inspectionSystemResetHintProgress, 0f);
            UiIntegrationBridge.UpdateNativeFooterHint(
                inspectionSystemResetHint,
                inspectionSystemResetHintLabel ??
                    GetInspectionHintLabel("LOC_SharpEyeResetSystem"), false);
            inspectionSystemResetHintVisible = false;
        }

        private static void HideInspectionVehicleResetHint()
        {
            if (!inspectionVehicleResetHintVisible)
                return;
            SetInspectionResetHintHoldProgress(inspectionVehicleResetHint,
                ref inspectionVehicleResetHintProgress, 0f);
            UiIntegrationBridge.UpdateNativeFooterHint(
                inspectionVehicleResetHint,
                inspectionVehicleResetHintLabel ??
                    GetInspectionHintLabel("LOC_SharpEyeResetVehicle"), false);
            inspectionVehicleResetHintVisible = false;
        }

        private static void HideInspectionResetActionHints()
        {
            HideInspectionSystemResetHint();
            HideInspectionVehicleResetHint();
        }

        private static void HideInspectionShowSystemsHint()
        {
            if (!inspectionShowSystemsHintVisible)
                return;
            UiIntegrationBridge.UpdateNativeFooterHint(
                inspectionShowSystemsHint,
                inspectionShowSystemsHintLabel ?? GetInspectionHintLabel(
                    inspectionSystemsOverlayActive ?
                        "LOC_SharpEyeHideSystems" :
                        "LOC_SharpEyeShowSystems"), false);
            inspectionShowSystemsHintVisible = false;
            UpdateInspectionHintHostVisibility();
        }

        private static void HideInspectionActionHints()
        {
            bool changed = inspectionExamineHintVisible ||
                inspectionSystemResetHintVisible ||
                inspectionVehicleResetHintVisible;
            HideInspectionExamineHint();
            HideInspectionResetActionHints();
            if (changed)
                UpdateInspectionHintHostVisibility();
        }

        private static void HideInspectionResetHints()
        {
            bool showSystemsHintWasVisible = inspectionShowSystemsHintVisible;
            bool actionHintsVisible = inspectionExamineHintVisible ||
                inspectionSystemResetHintVisible ||
                inspectionVehicleResetHintVisible;
            HideInspectionExamineHint();
            HideInspectionResetActionHints();
            HideInspectionShowSystemsHint();
            if (!showSystemsHintWasVisible && actionHintsVisible)
                UpdateInspectionHintHostVisibility();
        }

        private static void DestroyInspectionResetHints()
        {
            RestoreInspectionHintHost(IsNativeExamineUiActive());
            UiIntegrationBridge.DestroyNativeFooterHint(
                inspectionExamineHint);
            UiIntegrationBridge.DestroyNativeFooterHint(
                inspectionSystemResetHint);
            UiIntegrationBridge.DestroyNativeFooterHint(
                inspectionVehicleResetHint);
            UiIntegrationBridge.DestroyNativeFooterHint(
                inspectionShowSystemsHint);
            inspectionExamineHint = null;
            inspectionSystemResetHint = null;
            inspectionVehicleResetHint = null;
            inspectionShowSystemsHint = null;
            inspectionResetHintSource = null;
            inspectionResetHintSourceSearchAttempted = false;
            inspectionHintSourceDiagnosticsLogged = false;
            inspectionHintHost = null;
            inspectionHintHostForcedActive = false;
            inspectionHintHostWasActiveSelf = false;
            inspectionHintSourceSuppressed = false;
            inspectionHintSourceWasActiveSelf = false;
            inspectionFooterHoldSuppressed = false;
            inspectionFooterHoldWasActiveSelf = false;
            inspectionExamineHintVisible = false;
            inspectionSystemResetHintVisible = false;
            inspectionVehicleResetHintVisible = false;
            inspectionShowSystemsHintVisible = false;
            inspectionSystemResetHintProgress = 0f;
            inspectionVehicleResetHintProgress = 0f;
            inspectionExamineHintLabel = null;
            inspectionSystemResetHintLabel = null;
            inspectionVehicleResetHintLabel = null;
            inspectionShowSystemsHintLabel = null;
        }

        private static void ResetBodyHold(BodyPassState state)
        {
            if (state == null || state.HoldProgress <= 0f)
                return;
            state.HoldProgress = 0f;
            SetSharpEyeCursorFill(0f);
        }

        private static bool ProcessOneBodyStep(CarLoader loader,
            BodyPassState state)
        {
            List<BodyPartSlot> parts = GetBodyParts(loader);
            for (int index = 0; index < parts.Count; index++) {
                BodyPartSlot slot = parts[index];
                if (slot == null || state.ExaminedSlots.Contains(slot.Index) ||
                    IsBodyPartUnmounted(slot))
                    continue;
                if (ExaminePresentBodyPart(loader, state, slot))
                    return true;
            }

            for (int index = 0; index < parts.Count; index++) {
                BodyPartSlot slot = parts[index];
                if (slot == null || state.ExaminedSlots.Contains(slot.Index) ||
                    !IsBodyPartUnmounted(slot))
                    continue;
                state.ExaminedSlots.Add(slot.Index);
                QueueBodyPartShoppingList(loader, slot);
                ShowMissingPartPopup(slot.Id);
                LogDiagnostic("body missing step part=" + slot.Id);
                return true;
            }
            return false;
        }

        private static bool ExaminePresentBodyPart(CarLoader loader,
            BodyPassState state, BodyPartSlot slot)
        {
            if (slot == null || slot.Part == null)
                return false;
            bool wasExamined = IsBodyPartExamined(slot);
            if (wasExamined)
                SetBodyPartExamined(slot, false);

            try {
                InvokeOneArg(slot.Part, "Examine", true);
                if (!IsBodyPartExamined(slot))
                    SetBodyPartExamined(slot, true);
                state.ExaminedSlots.Add(slot.Index);
                QueueBodyPartShoppingList(loader, slot);
                ShowPresentBodyPartPopup(slot);
                LogDiagnostic("body present step part=" + slot.Id +
                    " repeat=" + (wasExamined ? "true" : "false"));
                return true;
            } catch (Exception exception) {
                if (wasExamined)
                    SetBodyPartExamined(slot, true);
                LogDiagnostic("body present step failed part=" + slot.Id +
                    " exception=" + exception.GetType().Name);
                return false;
            }
        }

        private static bool IsBodyPartUnmounted(BodyPartSlot slot)
        {
            return slot != null && (slot.ForceUnmounted || slot.Part == null ||
                ToBool(ReadMember(slot.Part, "Unmounted")));
        }

        private static bool IsBodyPartExamined(BodyPartSlot slot)
        {
            if (slot == null || slot.Part == null)
                return false;
            object value = InvokeNoArgs(slot.Part, "GetExamined") ??
                ReadMember(slot.Part, "Examined");
            return ToBool(value);
        }

        private static void SetBodyPartExamined(BodyPartSlot slot, bool value)
        {
            if (slot == null || slot.Part == null)
                return;
            InvokeOneArg(slot.Part, "SetExamined", value);
            if (IsBodyPartExamined(slot) != value)
                WriteMember(slot.Part, "Examined", value);
        }

        private static void QueueBodyPartShoppingList(CarLoader loader,
            BodyPartSlot slot)
        {
            if (!GlobalState.IsGarageSceneActive || loader == null ||
                slot == null || string.IsNullOrEmpty(slot.Id) ||
                !IsPurchasablePart(slot.Id))
                return;
            if (!IsBodyPartUnmounted(slot) &&
                GetBodyPartCondition(slot) >= PerfectConditionThreshold)
                return;
            QueueSinglePurchaseIfNeeded(loader,
                new PurchaseKey(slot.Id, PurchaseKind.Part));
        }

        private static float GetBodyPartCondition(BodyPartSlot slot)
        {
            if (slot == null || slot.Part == null)
                return 0f;
            try {
                return Convert.ToSingle(ReadMember(slot.Part, "Condition"),
                    CultureInfo.InvariantCulture);
            } catch {
                return 0f;
            }
        }

        private static void ShowPresentBodyPartPopup(BodyPartSlot slot)
        {
            if (slot == null || string.IsNullOrEmpty(slot.Id))
                return;
            try {
                float condition = Convert.ToSingle(
                    ReadMember(slot.Part, "Condition"),
                    CultureInfo.InvariantCulture);
                int percent = Math.Max(0, Math.Min(100,
                    (int)(condition * 100f)));
                string color = ColorUtility.ToHtmlStringRGB(
                    GetConditionPopupColor(condition));
                string message = GetPartDisplayName(slot.Id) +
                    " (<color=#" + color + ">" +
                    percent.ToString(CultureInfo.InvariantCulture) +
                    "%</color>)";
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.ShowPopup("GUI_Desc_Examine", message,
                        PopupType.Normal);
            } catch (Exception exception) {
                LogDiagnostic("body present popup failed part=" + slot.Id +
                    " exception=" + exception.GetType().Name);
            }
        }

        private static bool IsInspectionSceneActive()
        {
            return GlobalState.IsGarageSceneActive ||
                GlobalState.IsJunkyardSceneActive;
        }

        private static bool IsInspectionMode(gameMode mode)
        {
            if (GlobalState.IsGarageSceneActive)
                return mode == gameMode.ExamineGarage;
            return GlobalState.IsJunkyardSceneActive &&
                (mode == gameMode.ExamineGarage ||
                    mode == gameMode.ExamineCondition);
        }

        private static bool IsExamineGarageModeActive()
        {
            try {
                GameMode mode = GameMode.Get();
                return mode != null && IsInspectionMode(
                    mode.GetCurrentMode());
            } catch {
                return false;
            }
        }

        internal static bool ShouldAllowNativeMouseOver()
        {
            return NativeInspectionModeReplacement.ShouldAllowNativeMouseOver();
        }

        private static bool HideNativeExamineHint()
        {
            try {
                UIManager ui = UIManager.Get();
                object description = ui != null ?
                    ReadMember(ui, "currentAlternativeDescription") : null;
                string name = ToText(ReadMember(description, "name"));
                if (string.IsNullOrEmpty(name) ||
                    !name.StartsWith("_HoldExamine",
                        StringComparison.Ordinal))
                    return false;
                ControlDescription control =
                    ResolveControlDescription(description) as
                        ControlDescription;
                if (control == null || control.gameObject == null ||
                    !control.gameObject.activeInHierarchy)
                    return false;
                control.Hide();
                return true;
            } catch {
                return false;
            }
        }

        private static void SuppressInspectionFooterForHold()
        {
            if (inspectionFooterHoldSuppressed)
                return;
            HideNativeExamineHint();
            if (inspectionHintHost == null ||
                inspectionHintHost.gameObject == null ||
                !inspectionHintHost.name.StartsWith("_HoldExamine",
                    StringComparison.Ordinal))
                return;
            inspectionFooterHoldWasActiveSelf =
                inspectionHintHost.gameObject.activeSelf;
            inspectionFooterHoldSuppressed = true;
            if (inspectionHintHost.gameObject.activeSelf)
                inspectionHintHost.gameObject.SetActive(false);
        }

        private static void RestoreInspectionFooterAfterHold()
        {
            if (!inspectionFooterHoldSuppressed)
                return;
            if (inspectionHintHost != null &&
                inspectionHintHost.gameObject != null)
                inspectionHintHost.gameObject.SetActive(
                    inspectionFooterHoldWasActiveSelf);
            inspectionFooterHoldSuppressed = false;
            inspectionFooterHoldWasActiveSelf = false;
        }

        private static void HideInspectionFooterForModeExit()
        {
            HideInspectionResetHints();
            inspectionFooterHoldSuppressed = false;
            inspectionFooterHoldWasActiveSelf = false;
            RestoreInspectionHintSource();
            HideNativeExamineHint();
            if (inspectionHintHost != null &&
                inspectionHintHost.gameObject != null &&
                inspectionHintHost.name.StartsWith("_HoldExamine",
                    StringComparison.Ordinal))
                inspectionHintHost.gameObject.SetActive(false);
            inspectionHintHostForcedActive = false;
            inspectionHintHostWasActiveSelf = false;
        }

        internal static bool ShouldAllowNativeExamineHintShow(
            ControlDescription control)
        {
            if (!IsInspectionSceneActive() ||
                !examineModeSessionActive || !IsExamineGarageModeActive() ||
                control == null || control.gameObject == null ||
                control.transform == null || control.transform.parent == null ||
                !string.Equals(control.gameObject.name, "ControlDescription",
                    StringComparison.Ordinal) ||
                !control.transform.parent.name.StartsWith("_HoldExamine",
                    StringComparison.Ordinal))
                return true;

            string reason = inspectionSystemsOverlayActive ?
                "sharp-eye" : "sharp-eye-off";
            InteractiveObject system = capturedExamineSystem;
            string key = reason + "|" + SafeIoId(system);
            if (!string.Equals(
                    inspectionNativeHintSuppressionDiagnosticKey, key,
                    StringComparison.Ordinal)) {
                inspectionNativeHintSuppressionDiagnosticKey = key;
                LogDiagnostic("native examine hint show suppressed reason=" +
                    reason + " system=" + SafeIoId(system));
            }
            return false;
        }

        private static bool IsNativeExamineUiSelected()
        {
            try {
                UIManager ui = UIManager.Get();
                object description = ui != null ?
                    ReadMember(ui, "currentAlternativeDescription") : null;
                if (description == null)
                    return false;
                string name = ToText(ReadMember(description, "name"));
                return !string.IsNullOrEmpty(name) &&
                    name.StartsWith("_HoldExamine",
                        StringComparison.Ordinal);
            } catch {
                return false;
            }
        }

        private static bool IsNativeExamineUiActive()
        {
            if (!IsNativeExamineUiSelected())
                return false;
            try {
                UIManager ui = UIManager.Get();
                object description = ui != null ?
                    ReadMember(ui, "currentAlternativeDescription") : null;
                Component control =
                    ResolveControlDescription(description) as Component;
                return control != null && control.gameObject != null &&
                    control.gameObject.activeInHierarchy;
            } catch {
                return false;
            }
        }

        private static SystemPassState GetSystemPassState(
            InteractiveObject system)
        {
            if (system == null)
                return null;

            int systemId = system.GetInstanceID();
            SystemPassState state;
            if (SystemPassStates.TryGetValue(systemId, out state))
                return state;

            state = new SystemPassState();
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part))
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id) ||
                    InspectionVisualSystem.IsDependentVisualPart(system, id))
                    continue;
                try {
                    if (part.IsExamined)
                        state.ExaminedPartInstanceIds.Add(part.GetInstanceID());
                } catch {
                }
            }
            List<string> specification = GetSystemSpecificationPartIds(system);
            int inspectableSpecificationCount = 0;
            for (int index = 0; index < specification.Count; index++) {
                string id = specification[index];
                if (InspectionVisualSystem.IsDependentVisualPart(system, id) ||
                    GetPartInspectionSkillLevel(system, id) <= 0)
                    continue;
                inspectableSpecificationCount++;
            }
            state.Total = Math.Max(inspectableSpecificationCount,
                state.ExaminedPartInstanceIds.Count + CountUnexaminedPresentParts(
                    system, parts, state.ExaminedPartInstanceIds));
            SystemPassStates.Add(systemId, state);
            return state;
        }

        private static int CountUnexaminedPresentParts(
            InteractiveObject system, List<PartScript> parts,
            HashSet<int> examinedPartInstanceIds)
        {
            if (parts == null)
                return 0;
            int count = 0;
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part))
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id) ||
                    InspectionVisualSystem.IsDependentVisualPart(system, id))
                    continue;
                if (examinedPartInstanceIds == null ||
                    !examinedPartInstanceIds.Contains(part.GetInstanceID()))
                    count++;
            }
            return count;
        }

        private static bool UpdateManualCustomHold(InteractiveObject system,
            SystemPassState state)
        {
            if (system == null || state == null)
                return false;

            bool needsManualDriver = HasPendingCustomStep(system, state);
            if (!needsManualDriver) {
                ResetManualCustomHold(state);
                return false;
            }

            state.ManualHoldProgress = Mathf.Clamp01(
                state.ManualHoldProgress + Time.deltaTime /
                GetSharpEyeHoldSeconds());
            SetSharpEyeCursorFill(state.ManualHoldProgress);
            if (state.ManualHoldProgress < 1f)
                return false;

            state.ManualHoldProgress = 0f;
            SetSharpEyeCursorFill(0f);
            if (!ProcessOneCustomSystemStep(system))
                return false;

            PlaySwitchModeSound();
            LogDiagnostic("manual custom examine success system=" +
                SafeIoId(system));
            return true;
        }

        private static void ResetManualCustomHold(SystemPassState state)
        {
            if (state == null || state.ManualHoldProgress <= 0f)
                return;
            state.ManualHoldProgress = 0f;
            SetSharpEyeCursorFill(0f);
        }

        private static void SetSharpEyeCursorFill(float value)
        {
            if (value <= 0f && sharpEyeCursorTimerImage == null)
                return;
            UnityEngine.UI.Image image = EnsureSharpEyeCursorTimer();
            if (image == null)
                return;

            float fill = Mathf.Clamp01(value * CursorVisualCompletionScale);
            try {
                if (!Mathf.Approximately(image.fillAmount, fill))
                    image.fillAmount = fill;
                if (image.gameObject != null &&
                    image.gameObject.activeSelf != (fill > 0f))
                    image.gameObject.SetActive(fill > 0f);
            } catch {
            }
        }

        private static UnityEngine.UI.Image EnsureSharpEyeCursorTimer()
        {
            if (sharpEyeCursorTimerImage != null)
                return sharpEyeCursorTimerImage;

            try {
                Cursor3D cursor = Cursor3D.Get();
                if (cursor == null) {
                    LogCursorVisualSourceDiagnostics(null, null,
                        "cursor-null");
                    return null;
                }
                UnityEngine.UI.Image source = cursor.cursorTimerImage;
                if (source == null) {
                    LogCursorVisualSourceDiagnostics(cursor, null,
                        "cursorTimerImage-null");
                    return null;
                }
                if (source.rectTransform == null) {
                    LogCursorVisualSourceDiagnostics(cursor, source,
                        "rectTransform-null");
                    return null;
                }

                GameObject visual = UnityEngine.Object.Instantiate(
                    source.gameObject) as GameObject;
                visual.name =
                    "CMS21GameplayPlus_SharpEyeCursorTimer";
                RectTransform rect = visual.GetComponent<RectTransform>();
                RectTransform sourceRect = source.rectTransform;
                Canvas canvas = source.GetComponentInParent<Canvas>();
                rect.SetParent(sourceRect.parent, false);
                rect.anchorMin = sourceRect.anchorMin;
                rect.anchorMax = sourceRect.anchorMax;
                rect.pivot = sourceRect.pivot;
                rect.anchoredPosition = sourceRect.anchoredPosition;
                rect.sizeDelta = sourceRect.sizeDelta;
                rect.localScale = sourceRect.localScale;
                rect.localRotation = sourceRect.localRotation;
                visual.transform.SetSiblingIndex(
                    sourceRect.GetSiblingIndex() + 1);

                UnityEngine.UI.Image image =
                    visual.GetComponent<UnityEngine.UI.Image>();
                image.sprite = source.sprite;
                image.color = source.color;
                image.material = source.material;
                image.type = source.type;
                image.fillMethod = source.fillMethod;
                image.fillOrigin = source.fillOrigin;
                image.fillClockwise = source.fillClockwise;
                image.fillCenter = source.fillCenter;
                image.preserveAspect = source.preserveAspect;
                image.raycastTarget = false;
                image.enabled = true;
                image.canvasRenderer.SetAlpha(1f);
                image.fillAmount = 0f;
                visual.SetActive(false);
                sharpEyeCursorTimerImage = image;
                LogDiagnostic("cursor visual created sourceActive=" +
                    source.gameObject.activeInHierarchy.ToString() +
                    " parentActive=" +
                    (sourceRect.parent != null ?
                        sourceRect.parent.gameObject.activeInHierarchy.ToString() :
                        "null") + " canvasActive=" +
                    (canvas != null ?
                        canvas.gameObject.activeInHierarchy.ToString() :
                        "null"));
                return image;
            } catch (Exception exception) {
                LogDiagnostic("cursor visual create failed exception=" +
                    exception.GetType().Name);
                return null;
            }
        }

        private static void LogCursorVisualSourceDiagnostics(
            Cursor3D cursor, UnityEngine.UI.Image source, string reason)
        {
            if (cursorVisualSourceDiagnosticsLogged)
                return;
            cursorVisualSourceDiagnosticsLogged = true;

            LogDiagnostic("cursor visual source unavailable reason=" +
                reason + " cursorType=" +
                (cursor != null ? cursor.GetType().FullName : "<null>") +
                " source=" + (source != null ? source.name : "<null>"));
            if (cursor == null)
                return;

            try {
                Type type = cursor.GetType();
                FieldInfo[] fields = type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                for (int index = 0; index < fields.Length; index++) {
                    FieldInfo field = fields[index];
                    if (!IsCursorVisualCandidate(field.Name,
                            field.FieldType))
                        continue;
                    object value = null;
                    try {
                        value = field.GetValue(cursor);
                    } catch {
                    }
                    LogDiagnostic("cursor visual candidate field=" +
                        field.Name + " type=" + field.FieldType.FullName +
                        " value=" + DescribeCursorVisualValue(value));
                }

                PropertyInfo[] properties = type.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                for (int index = 0; index < properties.Length; index++) {
                    PropertyInfo property = properties[index];
                    if (property.GetIndexParameters().Length != 0 ||
                        !IsCursorVisualCandidate(property.Name,
                            property.PropertyType))
                        continue;
                    object value = null;
                    try {
                        value = property.GetValue(cursor, null);
                    } catch {
                    }
                    LogDiagnostic("cursor visual candidate property=" +
                        property.Name + " type=" +
                        property.PropertyType.FullName + " value=" +
                        DescribeCursorVisualValue(value));
                }
            } catch (Exception exception) {
                LogDiagnostic("cursor visual candidate dump failed exception=" +
                    exception.GetType().Name);
            }
        }

        private static bool IsCursorVisualCandidate(string name, Type type)
        {
            string lower = (name ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("cursor") || lower.Contains("timer") ||
                lower.Contains("hold") || lower.Contains("examine"))
                return true;
            try {
                return type != null &&
                    typeof(UnityEngine.UI.Image).IsAssignableFrom(type);
            } catch {
                return false;
            }
        }

        private static string DescribeCursorVisualValue(object value)
        {
            if (value == null)
                return "<null>";
            try {
                Component component = value as Component;
                if (component != null)
                    return component.GetType().FullName + ":" +
                        (component.gameObject != null ?
                            component.gameObject.name : "<no-gameobject>");
                return value.GetType().FullName;
            } catch {
                return "<error>";
            }
        }

        private static void DestroySharpEyeCursorTimer()
        {
            if (sharpEyeCursorTimerImage == null)
                return;
            try {
                if (sharpEyeCursorTimerImage.gameObject != null)
                    UnityEngine.Object.Destroy(
                        sharpEyeCursorTimerImage.gameObject);
            } catch {
            }
            sharpEyeCursorTimerImage = null;
        }

        private static void PlaySwitchModeSound()
        {
            try {
                SoundManager soundManager = SoundManager.Get();
                if (soundManager != null)
                    InvokeOneArg(soundManager, "PlaySFX", "SwitchMode");
            } catch {
            }
        }

        private static bool HasPendingCustomStep(InteractiveObject system,
            SystemPassState state)
        {
            if (system == null || state == null)
                return false;
            int skillLevel = GetInspectionSkillLevel();
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part))
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id) ||
                    !IsPartAvailableForInspection(system, id, skillLevel))
                    continue;
                if (!state.ExaminedPartInstanceIds.Contains(part.GetInstanceID()))
                    return true;
            }

            List<string> specification = GetSystemSpecificationPartIds(system);
            List<int> missing = GetMissingSpecificationSlots(system,
                specification);
            for (int index = 0; index < missing.Count; index++) {
                int slot = missing[index];
                if (slot < 0 || slot >= specification.Count ||
                    !IsPartAvailableForInspection(system, specification[slot],
                        skillLevel))
                    continue;
                if (!state.ExaminedMissingSlots.Contains(slot))
                    return true;
            }
            return false;
        }

        private static bool TryExamineOnePresentPart(
            InteractiveObject system, out PartScript examinedPart)
        {
            examinedPart = null;
            SystemPassState state = GetSystemPassState(system);
            if (state == null)
                return false;

            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part))
                    continue;

                string id = SafePartId(part);
                int instanceId = part.GetInstanceID();
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id) ||
                    !IsPartAvailableForInspection(system, id,
                        GetInspectionSkillLevel()) ||
                    state.ExaminedPartInstanceIds.Contains(instanceId))
                    continue;

                bool wasExamined = false;
                try {
                    wasExamined = part.IsExamined;
                } catch {
                }
                if (wasExamined && !WriteMember(part, "IsExamined", false)) {
                    LogDiagnostic("repeat reset failed part=" + id);
                    continue;
                }

                processingCustomPartExamine = true;
                customExaminedPartInstanceId = -1;
                try {
                    LogDiagnostic("custom present ENTER system=" +
                        SafeIoId(system) + " part=" + id +
                        " repeat=" + (wasExamined ? "true" : "false"));
                    part.Examine(true);
                    LogDiagnostic("custom present RETURN system=" +
                        SafeIoId(system) + " part=" + id);
                    if (customExaminedPartInstanceId == instanceId) {
                        LogDiagnostic("custom present CONFIRMED system=" +
                            SafeIoId(system) + " part=" + id);
                        state.ExaminedPartInstanceIds.Add(instanceId);
                        CarLoader loader = GetCarLoader(system);
                        if (loader != null) {
                            LogDiagnostic("custom present SHOP ENTER system=" +
                                SafeIoId(system) + " part=" + id);
                            QueueSinglePartShoppingList(loader, system, part);
                            LogDiagnostic("custom present SHOP RETURN system=" +
                                SafeIoId(system) + " part=" + id);
                        }
                        LogDiagnostic("custom present POPUP ENTER system=" +
                            SafeIoId(system) + " part=" + id);
                        ShowPresentPartPopup(part);
                        LogDiagnostic("custom present POPUP RETURN system=" +
                            SafeIoId(system) + " part=" + id);
                        LogDiagnostic("custom present COMPLETE system=" +
                            SafeIoId(system) + " part=" + id);
                        examinedPart = part;
                        MarkInspectionProgressChanged();
                        return true;
                    }
                    LogDiagnostic("custom present NOT CONFIRMED system=" +
                        SafeIoId(system) + " part=" + id);
                } catch (Exception exception) {
                    LogDiagnostic("fallback present failed part=" + id +
                        " exception=" + exception.GetType().Name);
                } finally {
                    processingCustomPartExamine = false;
                    customExaminedPartInstanceId = -1;
                    if (wasExamined) {
                        try {
                            WriteMember(part, "IsExamined", true);
                        } catch {
                        }
                    }
                }
            }
            return false;
        }

        private static bool TryExamineOneMissingPart(
            InteractiveObject system, out string partId, out int slotIndex)
        {
            partId = null;
            slotIndex = -1;
            if (system == null)
                return false;

            List<string> specification = GetSystemSpecificationPartIds(system);
            if (specification.Count == 0)
                return false;

            List<int> missingSlots = GetMissingSpecificationSlots(system,
                specification);
            if (missingSlots.Count == 0)
                return false;

            SystemPassState state = GetSystemPassState(system);
            if (state == null)
                return false;

            for (int index = 0; index < missingSlots.Count; index++) {
                int candidate = missingSlots[index];
                if (candidate < 0 || candidate >= specification.Count ||
                    !IsPartAvailableForInspection(system,
                        specification[candidate], GetInspectionSkillLevel()) ||
                    state.ExaminedMissingSlots.Contains(candidate))
                    continue;

                state.ExaminedMissingSlots.Add(candidate);
                slotIndex = candidate;
                partId = specification[candidate];
                PartScript previewPart;
                bool hasPreview = inspectionSystemsOverlayMissingPartBySlot.
                    TryGetValue(GetInspectionSystemsOverlayMissingPartKey(
                        system, candidate), out previewPart) &&
                    previewPart != null;
                LogDiagnostic("inspection visual missing examined system=" +
                    SafeIoId(system) + " part=" + partId + " slot=" +
                    candidate.ToString(CultureInfo.InvariantCulture) +
                    " preview=" + (hasPreview ? SafePartId(previewPart) :
                        "<none>"));
                CarLoader loader = GetCarLoader(system);
                if (loader != null)
                    QueueSinglePurchaseIfNeeded(loader,
                        CreatePurchaseKey(loader, partId, system), system);
                ShowMissingPartPopup(partId);
                MarkInspectionProgressChanged();
                return true;
            }
            return false;
        }

        private static List<int> GetMissingSpecificationSlots(
            InteractiveObject system, List<string> specification)
        {
            List<int> result = new List<int>();
            if (system == null || specification == null ||
                specification.Count == 0)
                return result;

            Dictionary<string, int> mountedCounts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part))
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id))
                    continue;
                int count;
                mountedCounts.TryGetValue(id, out count);
                mountedCounts[id] = count + 1;
            }

            for (int index = 0; index < specification.Count; index++) {
                string id = specification[index];
                int count;
                if (mountedCounts.TryGetValue(id, out count) && count > 0) {
                    mountedCounts[id] = count - 1;
                    continue;
                }
                result.Add(index);
            }
            return result;
        }

        private static void QueueSinglePartShoppingList(CarLoader loader,
            InteractiveObject system, PartScript part)
        {
            if (!GlobalState.IsGarageSceneActive || loader == null ||
                part == null)
                return;
            string id = SafePartId(part);
            if (string.IsNullOrEmpty(id))
                return;
            QueueSinglePurchaseIfNeeded(loader,
                CreatePurchaseKey(loader, id, system), system);
        }

        private static PurchaseKey CreatePurchaseKey(CarLoader loader,
            string id, InteractiveObject system = null)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            if (!IsWheelId(id))
                return new PurchaseKey(id, PurchaseKind.Part);

            WheelSpec wheel;
            if (system != null && TryGetSystemWheelSpec(loader, system, out wheel)) {
                if (string.Equals(id, wheel.RimId,
                        StringComparison.OrdinalIgnoreCase))
                    return new PurchaseKey(id, PurchaseKind.Rim, wheel.Size, 0, 0,
                        wheel.ET);
                if (string.Equals(id, wheel.TireId,
                        StringComparison.OrdinalIgnoreCase))
                    return new PurchaseKey(id, PurchaseKind.Tire, wheel.Size,
                        wheel.Width, wheel.Profile);
            }

            PurchaseKey result = null;
            List<WheelSpec> wheels = GetWheelSpecs(loader);
            for (int index = 0; index < wheels.Count && result == null; index++) {
                WheelSpec current = wheels[index];
                if (string.Equals(id, current.RimId,
                        StringComparison.OrdinalIgnoreCase)) {
                    result = new PurchaseKey(id, PurchaseKind.Rim, current.Size,
                        0, 0, current.ET);
                } else if (string.Equals(id, current.TireId,
                        StringComparison.OrdinalIgnoreCase)) {
                    result = new PurchaseKey(id, PurchaseKind.Tire, current.Size,
                        current.Width, current.Profile);
                }
            }
            return result ?? new PurchaseKey(id, PurchaseKind.Part);
        }

        private static List<WheelSpec> GetWheelSpecs(CarLoader loader)
        {
            List<WheelSpec> result = new List<WheelSpec>();
            object wheels = loader != null ? InvokeNoArgs(loader, "GetWheels") :
                null;
            VisitCollection(wheels, delegate(object value) {
                if (value == null)
                    return;
                WheelSpec wheel = new WheelSpec();
                wheel.Size = ToRoundedInt(ReadMember(value, "Size"));
                wheel.Width = ToRoundedInt(ReadMember(value, "Width"));
                wheel.Profile = ToRoundedInt(ReadMember(value, "Profile"));
                wheel.ET = ToRoundedInt(ReadMember(value, "ET"));
                wheel.RimId = ToText(ReadMember(value, "Rim"));
                wheel.TireId = ToText(ReadMember(value, "Tire"));
                if (!string.IsNullOrEmpty(wheel.RimId) ||
                    !string.IsNullOrEmpty(wheel.TireId))
                    result.Add(wheel);
            });
            return result;
        }

        private static bool TryGetSystemWheelSpec(CarLoader loader,
            InteractiveObject system, out WheelSpec wheel)
        {
            wheel = null;
            bool rear;
            if (loader == null || !IsCornerSuspensionSystem(system, out rear))
                return false;
            List<WheelSpec> wheels = GetWheelSpecs(loader);
            if (wheels.Count == 0)
                return false;
            wheel = rear && wheels.Count > 1 ? wheels[wheels.Count - 1] :
                wheels[0];
            return wheel != null;
        }

        private static bool IsCornerSuspensionSystem(InteractiveObject system,
            out bool rear)
        {
            rear = false;
            if (system == null)
                return false;
            string name = NormalizeSystemName(system.name);
            if (name.StartsWith("FLSusp", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("FRSusp", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.StartsWith("RLSusp", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("RRSusp", StringComparison.OrdinalIgnoreCase)) {
                rear = true;
                return true;
            }
            return false;
        }

        private static void AppendSystemWheelSpecification(CarLoader loader,
            InteractiveObject system, List<string> result)
        {
            if (result == null)
                return;
            WheelSpec wheel;
            if (!TryGetSystemWheelSpec(loader, system, out wheel))
                return;
            if (!string.IsNullOrEmpty(wheel.RimId) &&
                !ContainsId(result, wheel.RimId))
                result.Add(wheel.RimId);
            if (!string.IsNullOrEmpty(wheel.TireId) &&
                !ContainsId(result, wheel.TireId))
                result.Add(wheel.TireId);
        }

        private static bool ContainsId(List<string> values, string id)
        {
            if (values == null || string.IsNullOrEmpty(id))
                return false;
            for (int index = 0; index < values.Count; index++) {
                if (string.Equals(values[index], id,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void AppendSystemWheelParts(CarLoader loader,
            InteractiveObject system, List<PartScript> result, HashSet<int> seen)
        {
            if (loader == null || system == null || result == null || seen == null)
                return;

            int systemId = system.GetInstanceID();
            List<PartScript> wheelParts;
            if (!SystemWheelPartsCache.TryGetValue(systemId, out wheelParts)) {
                wheelParts = new List<PartScript>();
                HashSet<int> wheelSeen = new HashSet<int>();
                WheelSpec wheel;
                if (TryGetSystemWheelSpec(loader, system, out wheel)) {
                    GameObject wheelHandle = GetSystemWheelHandle(loader, system);
                    if (wheelHandle != null) {
                        AppendOwnedWheelPart(loader, wheelHandle, wheel.RimId,
                            wheelParts, wheelSeen);
                        AppendOwnedWheelPart(loader, wheelHandle, wheel.TireId,
                            wheelParts, wheelSeen);
                    }
                }
                SystemWheelPartsCache[systemId] = wheelParts;
            }

            for (int index = 0; index < wheelParts.Count; index++) {
                PartScript part = wheelParts[index];
                if (part != null && seen.Add(part.GetInstanceID()))
                    result.Add(part);
            }
        }

        private static void AppendOwnedWheelPart(CarLoader loader,
            GameObject wheelHandle, string id, List<PartScript> result,
            HashSet<int> seen)
        {
            if (loader == null || wheelHandle == null || string.IsNullOrEmpty(id))
                return;

            PartScript[] children = null;
            try {
                children = wheelHandle.transform.GetComponentsInChildren<PartScript>(true);
            } catch {
            }
            if (children != null) {
                for (int index = 0; index < children.Length; index++) {
                    PartScript child = children[index];
                    if (child == null || IsUnmountedPart(child) ||
                        !string.Equals(SafePartId(child), id,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (seen.Add(child.GetInstanceID()))
                        result.Add(child);
                    return;
                }
            }

            PartScript owned = null;
            float bestDistance = float.MaxValue;
            object partCache = InvokeNoArgs(loader, "GetPartScriptCache") ??
                ReadMember(loader, "partScriptCache");
            VisitCollection(partCache, delegate(object value) {
                PartScript part = value as PartScript;
                if (part == null || IsUnmountedPart(part) ||
                    !string.Equals(SafePartId(part), id,
                        StringComparison.OrdinalIgnoreCase))
                    return;
                GameObject nearestHandle = GetNearestWheelHandle(loader, part);
                if (nearestHandle == null || nearestHandle.GetInstanceID() !=
                    wheelHandle.GetInstanceID())
                    return;
                try {
                    float distance = (part.transform.position -
                        wheelHandle.transform.position).sqrMagnitude;
                    if (distance < bestDistance) {
                        bestDistance = distance;
                        owned = part;
                    }
                } catch {
                }
            });

            if (owned == null)
                return;
            if (seen.Add(owned.GetInstanceID()))
                result.Add(owned);
        }

        private static GameObject GetSystemWheelHandle(CarLoader loader,
            InteractiveObject system)
        {
            if (loader == null || system == null)
                return null;
            string name = NormalizeSystemName(system.name);
            if (name.StartsWith("FRSusp", StringComparison.OrdinalIgnoreCase))
                return GetWheelHandle(loader, "w_frontRightWheel_h");
            if (name.StartsWith("FLSusp", StringComparison.OrdinalIgnoreCase))
                return GetWheelHandle(loader, "w_frontLeftWheel_h");
            if (name.StartsWith("RRSusp", StringComparison.OrdinalIgnoreCase))
                return GetWheelHandle(loader, "w_rearRightWheel_h");
            if (name.StartsWith("RLSusp", StringComparison.OrdinalIgnoreCase))
                return GetWheelHandle(loader, "w_rearLeftWheel_h");
            return null;
        }

        private static GameObject GetWheelHandle(CarLoader loader, string name)
        {
            object value = ReadMember(loader, name);
            GameObject gameObject = value as GameObject;
            if (gameObject != null)
                return gameObject;
            Transform transform = value as Transform;
            if (transform != null)
                return transform.gameObject;
            Component component = value as Component;
            return component != null ? component.gameObject : null;
        }

        private static GameObject GetNearestWheelHandle(CarLoader loader,
            PartScript part)
        {
            if (loader == null || part == null)
                return null;
            GameObject nearest = null;
            float bestDistance = float.MaxValue;
            for (int index = 0; index < WheelHandleMemberNames.Length;
                index++) {
                GameObject handle = GetWheelHandle(loader,
                    WheelHandleMemberNames[index]);
                if (handle == null)
                    continue;
                try {
                    float distance = (part.transform.position -
                        handle.transform.position).sqrMagnitude;
                    if (distance < bestDistance) {
                        bestDistance = distance;
                        nearest = handle;
                    }
                } catch {
                }
            }
            return nearest;
        }

        private static void QueueSinglePurchaseIfNeeded(CarLoader loader,
            PurchaseKey key, InteractiveObject system = null)
        {
            if (!GlobalState.IsGarageSceneActive || loader == null ||
                key == null || string.IsNullOrEmpty(key.Id) ||
                !IsPurchasablePart(key.Id))
                return;

            int vehicleCount = Math.Max(1,
                GetVehicleRequiredCount(loader, key));
            Dictionary<PurchaseKey, int> uncovered =
                new Dictionary<PurchaseKey, int>();
            uncovered.Add(key, vehicleCount);
            SubtractPerfectInventoryParts(uncovered);
            SubtractPerfectWarehouseParts(uncovered);
            SubtractPerfectSystemParts(uncovered, loader, system);
            int listLimit;
            if (!uncovered.TryGetValue(key, out listLimit) || listLimit <= 0)
                return;

            int currentCount = 0;
            int actualCount;
            if (TryGetCurrentShopListCount(key, out actualCount))
                currentCount = Math.Max(currentCount, actualCount);
            int observedCount;
            if (ObservedShopList.TryGetValue(key, out observedCount))
                currentCount = Math.Max(currentCount, observedCount);
            int pendingCount;
            PendingShoppingListCounts.TryGetValue(key, out pendingCount);
            if (currentCount + pendingCount >= listLimit) {
                LogDiagnostic("shopping skip cap part=" + key.Id +
                    " current=" + currentCount.ToString(
                        CultureInfo.InvariantCulture) +
                    " pending=" + pendingCount.ToString(
                        CultureInfo.InvariantCulture) +
                    " limit=" + listLimit.ToString(
                        CultureInfo.InvariantCulture) +
                    " vehicle=" + vehicleCount.ToString(
                        CultureInfo.InvariantCulture));
                return;
            }

            if (key.Kind == PurchaseKind.Tire ||
                key.Kind == PurchaseKind.Rim) {
                LogDiagnostic("shopping wheel add part=" + key.Id +
                    " current=" + currentCount.ToString(
                        CultureInfo.InvariantCulture) +
                    " pending=" + pendingCount.ToString(
                        CultureInfo.InvariantCulture) +
                    " limit=" + listLimit.ToString(
                        CultureInfo.InvariantCulture) +
                    " vehicle=" + vehicleCount.ToString(
                        CultureInfo.InvariantCulture));
            }

            List<PurchaseKey> additions = new List<PurchaseKey>(1);
            additions.Add(key);
            QueueShoppingListAdditions(additions);
        }

        private static bool TryGetCurrentShopListCount(PurchaseKey key,
            out int count)
        {
            count = 0;
            if (key == null)
                return false;
            try {
                UIManager ui = UIManager.Get();
                object shopListWindow = ReadMember(ui, "ShopListWindow");
                object items = ReadMember(shopListWindow, "items");
                if (items == null)
                    return false;
                int resultCount = 0;
                VisitCollection(items, delegate(object value) {
                    if (value == null)
                        return;
                    string id = ToText(ReadMember(value, "ID"));
                    if (string.IsNullOrEmpty(id))
                        return;
                    PurchaseKey existing = CreateShopListKey(id,
                        ReadMember(value, "AdditionalData"));
                    if (!key.Equals(existing))
                        return;
                    resultCount += Math.Max(1,
                        ToInt(ReadMember(value, "Amount"), 1));
                });
                count = resultCount;
                return true;
            } catch {
                count = 0;
                return false;
            }
        }

        private static int GetVehicleRequiredCount(CarLoader loader,
            PurchaseKey key)
        {
            if (loader == null || key == null)
                return 0;

            int count = 0;
            if (key.Kind == PurchaseKind.Tire ||
                key.Kind == PurchaseKind.Rim) {
                List<WheelSpec> wheels = GetWheelSpecs(loader);
                if (wheels.Count == 0)
                    return 0;

                WheelSpec front = wheels[0];
                WheelSpec rear = wheels.Count > 1 ?
                    wheels[wheels.Count - 1] : front;
                if (WheelSpecMatchesKey(front, key))
                    count += 2;
                if (WheelSpecMatchesKey(rear, key))
                    count += 2;
                return Math.Min(4, count);
            }

            object configuredParts = InvokeNoArgs(loader, "GetParts") ??
                ReadMember(loader, "Parts");
            VisitCollection(configuredParts, delegate(object value) {
                string id = ToText(ReadMember(value, "p_name"));
                if (string.Equals(id, key.Id,
                        StringComparison.OrdinalIgnoreCase))
                    count++;
            });
            if (count > 0)
                return count;

            object partCache = InvokeNoArgs(loader, "GetPartScriptCache") ??
                ReadMember(loader, "partScriptCache");
            VisitCollection(partCache, delegate(object value) {
                PartScript part = value as PartScript;
                if (part != null && string.Equals(SafePartId(part), key.Id,
                        StringComparison.OrdinalIgnoreCase))
                    count++;
            });
            return count;
        }

        private static bool WheelSpecMatchesKey(WheelSpec wheel,
            PurchaseKey key)
        {
            if (wheel == null || key == null || wheel.Size != key.Size)
                return false;
            if (key.Kind == PurchaseKind.Tire)
                return string.Equals(wheel.TireId, key.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                    wheel.Width == key.Width && wheel.Profile == key.Profile;
            if (key.Kind == PurchaseKind.Rim)
                return string.Equals(wheel.RimId, key.Id,
                    StringComparison.OrdinalIgnoreCase) && wheel.ET == key.ET;
            return false;
        }

        private static void ShowPresentPartPopup(PartScript part)
        {
            if (part == null)
                return;
            string id = SafePartId(part);
            if (string.IsNullOrEmpty(id))
                return;
            try {
                float condition = part.Condition;
                int percent = Math.Max(0, Math.Min(100,
                    (int)(condition * 100f)));
                string color = ColorUtility.ToHtmlStringRGB(
                    GetConditionPopupColor(condition));
                string message = GetPartDisplayName(id) + " (<color=#" +
                    color + ">" + percent.ToString(
                        CultureInfo.InvariantCulture) + "%</color>)";
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.ShowPopup("GUI_Desc_Examine", message,
                        PopupType.Normal);
            } catch (Exception exception) {
                LogDiagnostic("present popup failed part=" + id +
                    " exception=" + exception.GetType().Name);
            }
        }

        private static Color GetConditionPopupColor(float condition)
        {
            if (condition < 0.15f)
                return new Color(1f, 0f, 0f, 1f);
            if (condition < 0.50f)
                return new Color(1f, 0.451f, 0f, 1f);
            if (condition < PerfectConditionThreshold)
                return new Color(1f, 0.851f, 0f, 1f);
            return Color.green;
        }

        private static void ShowMissingPartPopup(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            try {
                UIManager ui = UIManager.Get();
                if (ui == null)
                    return;
                string color = ColorUtility.ToHtmlStringRGB(
                    Colors.UnknownCondition);
                string message = GetPartDisplayName(id) + " (<color=#" +
                    color + ">---</color>)";
                ui.ShowPopup("GUI_Desc_Examine", message, PopupType.Normal);
            } catch (Exception exception) {
                LogDiagnostic("missing popup failed part=" + id +
                    " exception=" + exception.GetType().Name);
            }
        }

        private static string GetPartDisplayName(string id)
        {
            if (string.IsNullOrEmpty(id))
                return string.Empty;
            try {
                GameInventory inventory = Singleton<GameInventory>.Instance;
                if (inventory != null) {
                    string localized = null;
                    try {
                        localized = inventory.GetLocalizedName(id);
                    } catch {
                    }
                    if (IsLocalizedPartName(id, localized))
                        return localized;
                    try {
                        localized = inventory.GetItemLocalizeName(id);
                    } catch {
                    }
                    if (IsLocalizedPartName(id, localized))
                        return localized;
                    if (inventory.ExistsInPartProperty(id)) {
                        PartProperty property = inventory.GetItemProperty(id);
                        localized = ToText(ReadMember(property,
                            "LocalizedName"));
                        if (IsLocalizedPartName(id, localized))
                            return localized;
                    }
                }
            } catch {
            }
            return id;
        }

        private static bool IsLocalizedPartName(string id, string value)
        {
            return !string.IsNullOrEmpty(value) &&
                !string.Equals(NormalizeSystemName(value),
                    NormalizeSystemName(id),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void StartDiagnostics()
        {
            diagnosticLineCount = 0;
            try {
                string directory = Path.GetDirectoryName(DiagnosticLogPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(DiagnosticLogPath,
                    "CMS21 Gameplay+ Sharp Eye system diagnostics" +
                    Environment.NewLine);
                LogDiagnostic("garage session started");
            } catch {
            }
        }

        private static void LogDiagnostic(string message)
        {
            if (diagnosticLineCount >= DiagnosticLineLimit ||
                string.IsNullOrEmpty(message) ||
                !ShouldWriteDiagnostic(message))
                return;
            diagnosticLineCount++;
            try {
                File.AppendAllText(DiagnosticLogPath,
                    Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                    " " + message + Environment.NewLine);
            } catch {
            }
        }

        private static bool ShouldWriteDiagnostic(string message)
        {
            return message.StartsWith("garage session",
                    StringComparison.Ordinal) ||
                message.StartsWith("inspection skill",
                    StringComparison.Ordinal) ||
                message.StartsWith("inspection mode",
                    StringComparison.Ordinal) ||
                message.StartsWith("inspection systems overlay",
                    StringComparison.Ordinal) ||
                message.StartsWith("inspection overlay",
                    StringComparison.Ordinal) ||
                message.StartsWith("inspection visual",
                    StringComparison.Ordinal) ||
                message.StartsWith("inspection indicator",
                    StringComparison.Ordinal) ||
                message.StartsWith("inspection reset",
                    StringComparison.Ordinal) ||
                message.IndexOf("failed",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("exception=",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeIoId(InteractiveObject io)
        {
            if (io == null)
                return "<null>";
            try {
                return io.GetID() ?? io.name ?? "<null>";
            } catch {
                return "<error>";
            }
        }

        private static string SafePartId(PartScript part)
        {
            if (part == null)
                return null;
            try {
                return part.GetID();
            } catch {
                return null;
            }
        }

        private static bool IsWholeCarBodyObject(InteractiveObject interactiveObject)
        {
            if (interactiveObject == null)
                return false;

            string rawId = null;
            try {
                rawId = NormalizeSystemName(interactiveObject.GetID());
            } catch {
            }
            return string.Equals(rawId, "body",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawId, "details",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawId, "CarBody",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void QueueShoppingListAdditions(List<PurchaseKey> additions)
        {
            if (!GlobalState.IsGarageSceneActive || additions == null ||
                additions.Count == 0)
                return;

            for (int index = 0; index < additions.Count; index++) {
                PurchaseKey key = additions[index];
                PendingShoppingListAdds.Enqueue(key);
                Increment(PendingShoppingListCounts, key, 1);
            }
            if (shoppingListWorkerRunning)
                return;

            shoppingListWorkerRunning = true;
            int generation = shoppingListGeneration;
            MelonCoroutines.Start(DrainShoppingListQueue(generation));
        }

        private static IEnumerator DrainShoppingListQueue(int generation)
        {
            try {
                while (generation == shoppingListGeneration &&
                    GlobalState.IsGarageSceneActive &&
                    PendingShoppingListAdds.Count > 0) {
                    PurchaseKey key = PendingShoppingListAdds.Dequeue();
                    DecrementPendingShoppingListCount(key);
                    bool added = AddToShoppingList(key);
                    if (!added) {
                        ClearPendingShoppingListQueue();
                        yield break;
                    }
                    yield return null;
                }
            } finally {
                if (generation == shoppingListGeneration)
                    shoppingListWorkerRunning = false;
            }
        }

        private static void DecrementPendingShoppingListCount(PurchaseKey key)
        {
            int count;
            if (!PendingShoppingListCounts.TryGetValue(key, out count))
                return;
            if (count <= 1)
                PendingShoppingListCounts.Remove(key);
            else
                PendingShoppingListCounts[key] = count - 1;
        }

        private static void ClearPendingShoppingListQueue()
        {
            PendingShoppingListAdds.Clear();
            PendingShoppingListCounts.Clear();
        }

        private static CarLoader GetCarLoader(InteractiveObject interactiveObject)
        {
            if (interactiveObject == null)
                return null;
            try {
                GameScript game = GameScript.Get();
                return game != null ?
                    game.FindIOCarLoader(interactiveObject.gameObject) : null;
            } catch {
                return null;
            }
        }

        private static CarLoader GetHoveredCarLoader(Raycast raycast)
        {
            try {
                GameScript game = GameScript.Get();
                CarLoader loader = game != null ?
                    game.GetIOMouseOverCarLoader2() : null;
                if (loader != null)
                    return loader;
            } catch {
            }

            if (raycast == null)
                return null;
            try {
                Transform hitTransform = raycast.hit.transform;
                if (hitTransform != null) {
                    CarLoader hitLoader =
                        hitTransform.GetComponentInParent<CarLoader>();
                    if (hitLoader != null)
                        return hitLoader;
                }

                CarLoader loader = raycast.prevCarLoader;
                if (loader == null || hitTransform == null)
                    return null;
                Transform loaderTransform = loader.transform;
                return hitTransform == loaderTransform ||
                    hitTransform.IsChildOf(loaderTransform) ? loader : null;
            } catch {
                return null;
            }
        }

        private static void GetSystemProgress(InteractiveObject interactiveObject,
            out int examined, out int total)
        {
            SystemPassState state = GetSystemPassState(interactiveObject);
            if (state == null) {
                examined = 0;
                total = 0;
                return;
            }

            total = state.Total;
            examined = Math.Min(total, state.ExaminedPartInstanceIds.Count +
                state.ExaminedMissingSlots.Count);
        }

        private static void GetSystemAvailableProgress(
            InteractiveObject system, int skillLevel, out int examined,
            out int available, out int full)
        {
            examined = 0;
            available = 0;
            full = 0;
            if (system == null)
                return;

            List<string> specification = GetSystemSpecificationPartIds(system);
            for (int index = 0; index < specification.Count; index++) {
                string id = specification[index];
                if (InspectionVisualSystem.IsDependentVisualPart(system, id))
                    continue;
                int required = GetPartInspectionSkillLevel(system, id);
                if (required <= 0)
                    continue;
                full++;
                if (required <= skillLevel)
                    available++;
            }
            SystemPassState state = GetSystemPassState(system);
            if (state == null)
                return;
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part))
                    continue;
                if (available <= 0)
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id) ||
                    !IsPartAvailableForInspection(system, id, skillLevel))
                    continue;
                if (state.ExaminedPartInstanceIds.Contains(part.GetInstanceID()))
                    examined++;
            }
            if (available <= 0)
                return;
            foreach (int slot in state.ExaminedMissingSlots) {
                if (slot < 0 || slot >= specification.Count)
                    continue;
                if (IsPartAvailableForInspection(system, specification[slot],
                        skillLevel))
                    examined++;
            }
            examined = Math.Min(examined, available);
        }

        private static bool IsUnmountedPart(PartScript part)
        {
            if (part == null)
                return false;

            object value = ReadMember(part, "IsUnmounted");
            if (value != null)
                return ToBool(value);
            value = ReadMember(part, "Unmounted");
            return value != null && ToBool(value);
        }

        private static List<string> GetSystemSpecificationPartIds(
            InteractiveObject interactiveObject)
        {
            List<string> cached;
            if (interactiveObject == null)
                return new List<string>();

            int systemId = interactiveObject.GetInstanceID();
            if (SystemSpecificationPartIdsCache.TryGetValue(systemId, out cached))
                return cached;

            List<string> result = new List<string>();
            CarLoader loader = GetCarLoader(interactiveObject);
            if (loader != null) {
                object configuredParts = InvokeNoArgs(loader, "GetParts") ??
                    ReadMember(loader, "Parts");
                VisitCollection(configuredParts, delegate(object value) {
                    if (value == null)
                        return;

                    string id = ToText(ReadMember(value, "p_name"));
                    if (string.IsNullOrEmpty(id) ||
                        !IsPurchasablePart(id))
                        return;

                    GameObject handle = ReadMember(value, "p_handle") as GameObject;
                    if (handle == null)
                        return;

                    InteractiveObject owner = null;
                    try {
                        owner = handle.transform.GetComponentInParent<InteractiveObject>();
                    } catch {
                    }
                    if (owner != null && owner.GetInstanceID() == systemId)
                        result.Add(id);
                });
                AppendSystemWheelSpecification(loader, interactiveObject, result);
            }

            Dictionary<string, int> configuredCounts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < result.Count; index++) {
                int count;
                configuredCounts.TryGetValue(result[index], out count);
                configuredCounts[result[index]] = count + 1;
            }

            Dictionary<string, int> mountedCounts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<PartScript> mountedParts = GetSystemParts(interactiveObject);
            for (int index = 0; index < mountedParts.Count; index++) {
                PartScript part = mountedParts[index];
                if (part == null)
                    continue;
                string id = null;
                try {
                    id = part.GetID();
                } catch {
                }
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id))
                    continue;

                int mountedCount;
                mountedCounts.TryGetValue(id, out mountedCount);
                mountedCount++;
                mountedCounts[id] = mountedCount;

                int configuredCount;
                configuredCounts.TryGetValue(id, out configuredCount);
                if (mountedCount > configuredCount) {
                    result.Add(id);
                    configuredCounts[id] = configuredCount + 1;
                }
            }

            SystemSpecificationPartIdsCache[systemId] = result;
            return result;
        }

        private static List<PartScript> GetSystemParts(
            InteractiveObject interactiveObject)
        {
            List<PartScript> result = new List<PartScript>();
            if (interactiveObject == null)
                return result;

            CarLoader loader = GetCarLoader(interactiveObject);
            HashSet<int> seen = new HashSet<int>();
            for (int index = 0; index < SystemPartMemberNames.Length; index++)
                AppendSystemPartCollection(ReadMember(interactiveObject,
                    SystemPartMemberNames[index]), result, seen);
            if (result.Count > 0) {
                AppendSystemWheelParts(loader, interactiveObject, result, seen);
                return result;
            }

            PartScript[] parts = null;
            try {
                parts = interactiveObject.GetComponentsInChildren<PartScript>(true);
            } catch {
            }
            if (parts == null || parts.Length == 0) {
                AppendSystemWheelParts(loader, interactiveObject, result, seen);
                return result;
            }

            int systemId = interactiveObject.GetInstanceID();
            HashSet<int> carPartIds = GetCarPartInstanceIds(loader);
            for (int index = 0; index < parts.Length; index++) {
                PartScript part = parts[index];
                if (!IsSystemSpecificationPart(part, carPartIds))
                    continue;
                InteractiveObject owner = null;
                try {
                    owner = part.GetComponentInParent<InteractiveObject>();
                } catch {
                }
                if (owner != null && owner.GetInstanceID() != systemId)
                    continue;
                int partId = part.GetInstanceID();
                if (seen.Add(partId))
                    result.Add(part);
            }

            if (result.Count > 0) {
                AppendSystemWheelParts(loader, interactiveObject, result, seen);
                return result;
            }

            for (int index = 0; index < parts.Length; index++) {
                PartScript part = parts[index];
                if (!IsSystemSpecificationPart(part, carPartIds))
                    continue;
                int partId = part.GetInstanceID();
                if (seen.Add(partId))
                    result.Add(part);
            }
            AppendSystemWheelParts(loader, interactiveObject, result, seen);
            return result;
        }

        private static bool IsSystemSpecificationPart(PartScript part,
            HashSet<int> carPartIds)
        {
            if (part == null)
                return false;
            try {
                if (carPartIds != null &&
                    !carPartIds.Contains(part.GetInstanceID()))
                    return false;
                string id = part.GetID();
                return !string.IsNullOrEmpty(id) && IsPurchasablePart(id);
            } catch {
                return false;
            }
        }

        private static HashSet<int> GetCarPartInstanceIds(CarLoader loader)
        {
            if (loader == null)
                return null;

            int loaderId = loader.GetInstanceID();
            HashSet<int> cached;
            if (LoaderPartInstanceIds.TryGetValue(loaderId, out cached))
                return cached;

            cached = new HashSet<int>();
            object partCache = InvokeNoArgs(loader, "GetPartScriptCache") ??
                ReadMember(loader, "partScriptCache");
            VisitCollection(partCache, delegate(object value) {
                PartScript part = value as PartScript;
                if (part != null)
                    cached.Add(part.GetInstanceID());
            });
            LoaderPartInstanceIds[loaderId] = cached;
            return cached;
        }

        private static void AppendSystemPartCollection(object collection,
            List<PartScript> result, HashSet<int> seen)
        {
            if (collection == null || result == null || seen == null)
                return;
            VisitCollection(collection, delegate(object value) {
                PartScript part = value as PartScript;
                if (part == null)
                    return;
                int partId = part.GetInstanceID();
                if (seen.Add(partId))
                    result.Add(part);
            });
        }

        private static string NormalizeSystemName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            string result = value.Trim();
            if (result.StartsWith("#", StringComparison.Ordinal))
                result = result.Substring(1);
            int clone = result.IndexOf("(Clone)",
                StringComparison.OrdinalIgnoreCase);
            if (clone >= 0)
                result = result.Substring(0, clone);
            return result.Trim();
        }


        private static string GetSpecificInspectionSystemName(
            InteractiveObject system)
        {
            if (system == null)
                return null;

            string rawId = null;
            string objectName = null;
            try {
                rawId = NormalizeSystemName(system.GetID());
            } catch {
            }
            objectName = NormalizeSystemName(system.name);

            if ((!string.IsNullOrEmpty(rawId) && rawId.StartsWith(
                        "FrontLeft", StringComparison.OrdinalIgnoreCase)) ||
                objectName.StartsWith("FLSusp",
                    StringComparison.OrdinalIgnoreCase))
                return ModLocalization.Get("LOC_SharpEyeSuspensionFrontLeft");
            if ((!string.IsNullOrEmpty(rawId) && rawId.StartsWith(
                        "FrontRight", StringComparison.OrdinalIgnoreCase)) ||
                objectName.StartsWith("FRSusp",
                    StringComparison.OrdinalIgnoreCase))
                return ModLocalization.Get("LOC_SharpEyeSuspensionFrontRight");
            if ((!string.IsNullOrEmpty(rawId) && rawId.StartsWith(
                        "RearLeft", StringComparison.OrdinalIgnoreCase)) ||
                objectName.StartsWith("RLSusp",
                    StringComparison.OrdinalIgnoreCase))
                return ModLocalization.Get("LOC_SharpEyeSuspensionRearLeft");
            if ((!string.IsNullOrEmpty(rawId) && rawId.StartsWith(
                        "RearRight", StringComparison.OrdinalIgnoreCase)) ||
                objectName.StartsWith("RRSusp",
                    StringComparison.OrdinalIgnoreCase))
                return ModLocalization.Get("LOC_SharpEyeSuspensionRearRight");
            if ((!string.IsNullOrEmpty(rawId) && rawId.StartsWith(
                        "FrontCenter", StringComparison.OrdinalIgnoreCase)) ||
                objectName.StartsWith("FCSusp",
                    StringComparison.OrdinalIgnoreCase))
                return ModLocalization.Get("LOC_SharpEyeSuspensionFrontCenter");
            if ((!string.IsNullOrEmpty(rawId) && rawId.StartsWith(
                        "RearCenter", StringComparison.OrdinalIgnoreCase)) ||
                objectName.StartsWith("RCSusp",
                    StringComparison.OrdinalIgnoreCase))
                return ModLocalization.Get("LOC_SharpEyeSuspensionRearCenter");
            return null;
        }

        private static string GetLocalizedSystemName(InteractiveObject system)
        {
            if (system == null)
                return string.Empty;

            string specificName = GetSpecificInspectionSystemName(system);
            if (IsUserFacingSystemName(specificName))
                return specificName;

            string cached;
            if (SystemNameCache.TryGetValue(system.GetInstanceID(), out cached) &&
                IsUserFacingSystemName(cached))
                return cached;

            string categoryName = GetLocalizedSystemCategoryName(system);
            if (IsUserFacingSystemName(categoryName)) {
                SystemNameCache[system.GetInstanceID()] = categoryName;
                return categoryName;
            }

            string directName = ToText(ReadMember(system, "LocalizedName"));
            if (!IsUserFacingSystemName(directName))
                directName = ToText(ReadMember(system, "localizedName"));
            if (!IsUserFacingSystemName(directName))
                directName = ToText(InvokeNoArgs(system, "GetLocalizedName"));
            if (IsUserFacingSystemName(directName) &&
                !LooksLikeTechnicalSystemName(system, directName)) {
                SystemNameCache[system.GetInstanceID()] = directName;
                return directName;
            }

            GameInventory inventory = Singleton<GameInventory>.Instance;
            string rawId = null;
            try {
                rawId = system.GetID();
            } catch {
            }
            if (inventory == null)
                return NormalizeSystemName(rawId);

            string ioName = null;
            string ioId = null;
            try {
                ioName = inventory.GetIOLocalizedName(system);
            } catch {
            }
            try {
                ioId = inventory.GetInteractiveObjectID(system);
            } catch {
            }
            if (IsUserFacingSystemName(ioName) &&
                !LooksLikeTechnicalSystemName(system, ioName)) {
                SystemNameCache[system.GetInstanceID()] = ioName;
                return ioName;
            }

            string[] candidates = new string[] { ioName, ioId, rawId,
                NormalizeSystemName(ioName), NormalizeSystemName(ioId),
                NormalizeSystemName(rawId) };
            object localization = ReadMember(inventory, "localization") ??
                ReadMember(inventory, "Localization");
            for (int index = 0; index < candidates.Length; index++) {
                string candidate = candidates[index];
                if (string.IsNullOrEmpty(candidate))
                    continue;

                string localized = TryLocalizeSystemName(candidate);
                if (!IsUserFacingSystemName(localized))
                    localized = ToText(InvokeOneArg(localization,
                        "GetLocalizedValue", candidate));
                if (!IsUserFacingSystemName(localized)) {
                    try {
                        localized = inventory.GetLocalizedName(candidate);
                    } catch {
                    }
                }
                if (!IsUserFacingSystemName(localized)) {
                    try {
                        localized = inventory.GetItemLocalizeName(candidate);
                    } catch {
                    }
                }
                if (!IsUserFacingSystemName(localized) ||
                    LooksLikeTechnicalSystemName(system, localized))
                    continue;

                SystemNameCache[system.GetInstanceID()] = localized;
                return localized;
            }

            List<string> specification = GetSystemSpecificationPartIds(system);
            string specificationName = GetSpecificationSystemName(
                rawId, specification);
            if (IsUserFacingSystemName(specificationName)) {
                SystemNameCache[system.GetInstanceID()] = specificationName;
                return specificationName;
            }

            return NormalizeSystemName(!string.IsNullOrEmpty(ioId) ?
                ioId : rawId);
        }

        private static string GetSpecificationSystemName(string rawId,
            List<string> specification)
        {
            if (specification == null || specification.Count == 0)
                return null;

            string commonName = null;
            for (int index = 0; index < specification.Count; index++) {
                string id = specification[index];
                string partName = GetPartDisplayName(id);
                if (!IsLocalizedPartName(id, partName)) {
                    commonName = null;
                    break;
                }
                if (commonName == null) {
                    commonName = partName;
                    continue;
                }
                if (!string.Equals(commonName, partName,
                        StringComparison.OrdinalIgnoreCase)) {
                    commonName = null;
                    break;
                }
            }
            if (IsUserFacingSystemName(commonName))
                return commonName;

            string normalizedId = NormalizeSystemName(rawId);
            if (!string.IsNullOrEmpty(normalizedId) &&
                normalizedId.StartsWith("Downpipe",
                    StringComparison.OrdinalIgnoreCase)) {
                string firstId = specification[0];
                string firstName = GetPartDisplayName(firstId);
                if (IsLocalizedPartName(firstId, firstName))
                    return firstName;
            }

            if (specification.Count == 1) {
                string onlyId = specification[0];
                string onlyName = GetPartDisplayName(onlyId);
                if (IsLocalizedPartName(onlyId, onlyName))
                    return onlyName;
            }
            return null;
        }

        private static string GetLocalizedSystemCategoryName(
            InteractiveObject system)
        {
            string key = GetSystemLocalizationKey(system);
            if (string.IsNullOrEmpty(key))
                return null;

            string localized = TryLocalizeSystemName(key);
            if (!IsUserFacingSystemName(localized)) {
                try {
                    GameInventory inventory = Singleton<GameInventory>.Instance;
                    object localization = ReadMember(inventory, "localization") ??
                        ReadMember(inventory, "Localization");
                    localized = ToText(InvokeOneArg(localization,
                        "GetLocalizedValue", key));
                } catch {
                }
            }
            if (!IsUserFacingSystemName(localized) ||
                string.Equals(localized, key, StringComparison.OrdinalIgnoreCase))
                return null;
            return localized;
        }

        private static string GetSystemLocalizationKey(InteractiveObject system)
        {
            if (system == null)
                return null;

            string objectName = null;
            string rawId = null;
            try {
                objectName = system.name;
                rawId = NormalizeSystemName(system.GetID());
            } catch {
            }

            if (!string.IsNullOrEmpty(objectName) &&
                objectName.EndsWith("Susp", StringComparison.OrdinalIgnoreCase))
                return "#suspension";
            if (string.IsNullOrEmpty(rawId))
                return null;
            if (rawId.StartsWith("engine_",
                    StringComparison.OrdinalIgnoreCase))
                return "#engine";
            if (rawId.StartsWith("FrontRight",
                    StringComparison.OrdinalIgnoreCase) ||
                rawId.StartsWith("FrontCenter",
                    StringComparison.OrdinalIgnoreCase) ||
                rawId.StartsWith("RearRight",
                    StringComparison.OrdinalIgnoreCase) ||
                rawId.StartsWith("RearCenter",
                    StringComparison.OrdinalIgnoreCase))
                return "#suspension";

            int separator = rawId.IndexOf('_');
            string stem = separator > 0 ? rawId.Substring(0, separator) : rawId;
            int firstDigit = -1;
            for (int index = 0; index < stem.Length; index++) {
                if (!char.IsDigit(stem[index]))
                    continue;
                firstDigit = index;
                break;
            }
            if (firstDigit > 0)
                stem = stem.Substring(0, firstDigit);
            int end = stem.Length;
            while (end > 0 && char.IsDigit(stem[end - 1]))
                end--;
            stem = stem.Substring(0, end);
            if (string.IsNullOrEmpty(stem))
                return null;
            return "#" + stem.ToLowerInvariant();
        }

        private static string TryLocalizeSystemName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            if (!localizeMethodResolved) {
                localizeMethodResolved = true;
                try {
                    Type[] types = typeof(GameScript).Assembly.GetTypes();
                    for (int typeIndex = 0; typeIndex < types.Length &&
                        localizeMethod == null; typeIndex++) {
                        Type type = types[typeIndex];
                        if (type == null || type.Name.IndexOf("Localiz",
                                StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        MethodInfo[] methods = type.GetMethods(
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Static);
                        for (int methodIndex = 0; methodIndex < methods.Length;
                            methodIndex++) {
                            MethodInfo method = methods[methodIndex];
                            if (!string.Equals(method.Name, "Localize",
                                    StringComparison.Ordinal) ||
                                method.ReturnType != typeof(string))
                                continue;
                            ParameterInfo[] parameters = method.GetParameters();
                            if (parameters.Length == 1 &&
                                parameters[0].ParameterType == typeof(string)) {
                                localizeMethod = method;
                                break;
                            }
                        }
                    }
                } catch {
                    localizeMethod = null;
                }
            }

            if (localizeMethod == null)
                return null;
            try {
                return localizeMethod.Invoke(null, new object[] { value }) as string;
            } catch {
                return null;
            }
        }

        private static string GetBodyDisplayName()
        {
            if (!string.IsNullOrEmpty(bodyDisplayName))
                return bodyDisplayName;

            try {
                string localized = TryLocalizeSystemName("#body");
                if (!IsUserFacingSystemName(localized)) {
                    GameInventory inventory = Singleton<GameInventory>.Instance;
                    object localization = ReadMember(inventory, "localization") ??
                        ReadMember(inventory, "Localization");
                    localized = ToText(InvokeOneArg(localization,
                        "GetLocalizedValue", "#body"));
                }
                if (IsUserFacingSystemName(localized)) {
                    bodyDisplayName = localized;
                    return bodyDisplayName;
                }
            } catch {
            }
            bodyDisplayName = ModLocalization.Get("LOC_SharpEyeBody");
            return bodyDisplayName;
        }

        private static bool IsUserFacingSystemName(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                !value.StartsWith("#", StringComparison.Ordinal) &&
                value.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool LooksLikeTechnicalSystemName(InteractiveObject system,
            string value)
        {
            if (system == null || string.IsNullOrEmpty(value))
                return false;
            string normalizedValue = NormalizeSystemName(value);
            string rawId = null;
            try {
                rawId = NormalizeSystemName(system.GetID());
            } catch {
            }
            return !string.IsNullOrEmpty(rawId) &&
                string.Equals(normalizedValue, rawId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void SetInspectionIndicator(InteractiveObject system,
            int examined, int available)
        {
            if (system == null)
                return;
            string text = examined.ToString(CultureInfo.InvariantCulture) +
                " / " + available.ToString(CultureInfo.InvariantCulture);
            bool changed = indicatorDirty || indicatorSystem == null ||
                indicatorSystem.GetInstanceID() != system.GetInstanceID() ||
                !string.Equals(indicatorText, text, StringComparison.Ordinal);
            if (!changed)
                return;
            try {
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.SetBonusTextDescription(text);
                indicatorSystem = system;
                indicatorText = text;
                indicatorDirty = false;
                LogDiagnostic("inspection indicator system=" +
                    SafeIoId(system) + " progress=" + text + " skill=" +
                    GetInspectionSkillLevel().ToString(
                        CultureInfo.InvariantCulture) + " overlay=" +
                    inspectionSystemsOverlayActive.ToString());
            } catch {
            }
        }

        private static void ClearSystemIndicator(bool force = false)
        {
            if (!force && indicatorSystem == null &&
                string.IsNullOrEmpty(indicatorText))
                return;
            try {
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.ClearBonusTextDescription();
            } catch {
            }
            indicatorSystem = null;
            indicatorText = null;
            indicatorDirty = true;
        }

        private static void SubtractPerfectInventoryParts(
            Dictionary<PurchaseKey, int> remaining)
        {
            Inventory inventory = Singleton<Inventory>.Instance;
            if (inventory == null)
                return;

            if (inventory.items != null) {
                for (int index = 0; index < inventory.items.Count; index++)
                    ConsumePerfectOwnedItem(inventory.items[index], remaining);
            }

            if (inventory.groups == null)
                return;
            for (int groupIndex = 0; groupIndex < inventory.groups.Count;
                groupIndex++) {
                ConsumePerfectGroup(inventory.groups[groupIndex], remaining);
            }
        }

        private static void SubtractPerfectWarehouseParts(
            Dictionary<PurchaseKey, int> remaining)
        {
            GameManager manager = Singleton<GameManager>.Instance;
            Warehouse warehouse = manager != null ? manager.Warehouse : null;
            if (warehouse == null)
                return;

            int unlocked = Math.Max(0, Warehouse.amountOfUnlockedWarehouses);
            if (warehouse.warehouseList != null) {
                int itemWarehouses = Math.Min(unlocked,
                    warehouse.warehouseList.Count);
                for (int warehouseIndex = 0; warehouseIndex < itemWarehouses;
                    warehouseIndex++) {
                    Il2CppSystem.Collections.Generic.List<Item> items =
                        warehouse.warehouseList[warehouseIndex];
                    if (items == null)
                        continue;
                    for (int index = 0; index < items.Count; index++)
                        ConsumePerfectOwnedItem(items[index], remaining);
                }
            }

            if (warehouse.warehouseGroupList == null)
                return;
            int groupWarehouses = Math.Min(unlocked,
                warehouse.warehouseGroupList.Count);
            for (int warehouseIndex = 0; warehouseIndex < groupWarehouses;
                warehouseIndex++) {
                Il2CppSystem.Collections.Generic.List<GroupItem> groups =
                    warehouse.warehouseGroupList[warehouseIndex];
                if (groups == null)
                    continue;
                for (int groupIndex = 0; groupIndex < groups.Count;
                    groupIndex++) {
                    ConsumePerfectGroup(groups[groupIndex], remaining);
                }
            }
        }

        private static void SubtractPerfectSystemParts(
            Dictionary<PurchaseKey, int> remaining, CarLoader loader,
            InteractiveObject system)
        {
            if (remaining == null || loader == null || system == null)
                return;

            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (IsInspectionLogicallyUnmountedPart(part) ||
                    part.Condition < PerfectConditionThreshold)
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id))
                    continue;
                PurchaseKey key = CreatePurchaseKey(loader, id, system);
                int count;
                if (key == null || !remaining.TryGetValue(key, out count) ||
                    count <= 0)
                    continue;
                remaining[key] = count - 1;
            }
        }

        private static void ConsumePerfectGroup(GroupItem group,
            Dictionary<PurchaseKey, int> remaining)
        {
            if (group == null)
                return;
            object groupItems = ReadMember(group, "ItemList");
            VisitCollection(groupItems, delegate(object value) {
                Item item = value as Item;
                if (item != null)
                    ConsumePerfectOwnedItem(item, remaining);
            });
        }

        private static void ConsumePerfectOwnedItem(Item item,
            Dictionary<PurchaseKey, int> remaining)
        {
            if (item == null || item.Condition < PerfectConditionThreshold)
                return;

            string exactId = item.ID;
            string normalId = null;
            try {
                normalId = item.GetNormalID();
            } catch {
                normalId = item.NormalID;
            }
            if (string.IsNullOrEmpty(normalId))
                normalId = item.NormalID;
            if (string.IsNullOrEmpty(exactId) && string.IsNullOrEmpty(normalId))
                return;

            PurchaseKey exact = FindMatchingRemainingKey(item, exactId,
                remaining);
            if (exact != null) {
                remaining[exact]--;
                return;
            }

            if (!string.IsNullOrEmpty(normalId) &&
                !string.Equals(normalId, exactId, StringComparison.OrdinalIgnoreCase)) {
                PurchaseKey normal = FindMatchingRemainingKey(item, normalId,
                    remaining);
                if (normal != null)
                    remaining[normal]--;
            }
        }

        private static PurchaseKey FindMatchingRemainingKey(Item item,
            string candidateId, Dictionary<PurchaseKey, int> remaining)
        {
            if (string.IsNullOrEmpty(candidateId))
                return null;

            foreach (KeyValuePair<PurchaseKey, int> pair in remaining) {
                PurchaseKey key = pair.Key;
                if (pair.Value <= 0 ||
                    !string.Equals(key.Id, candidateId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (OwnedItemMatchesKey(item, key))
                    return key;
            }
            return null;
        }

        private static bool OwnedItemMatchesKey(Item item, PurchaseKey key)
        {
            if (key.Kind == PurchaseKind.Part)
                return true;

            object wheelData = ReadMember(item, "WheelData") ?? item;
            int size = ToRoundedInt(ReadMember(wheelData, "Size"));
            if (size != key.Size)
                return false;

            if (key.Kind == PurchaseKind.Rim) {
                int et = ToRoundedInt(ReadMember(wheelData, "ET"));
                return et == key.ET;
            }

            int width = ToRoundedInt(ReadMember(wheelData, "Width"));
            int profile = ToRoundedInt(ReadMember(wheelData, "Profile"));
            return width == key.Width && profile == key.Profile;
        }

        private static PurchaseKey CreateShopListKey(string id,
            object additionalData)
        {
            if (additionalData != null) {
                bool tire = ToBool(ReadMember(additionalData, "Tire"));
                bool rim = ToBool(ReadMember(additionalData, "Rim"));
                int size = ToRoundedInt(ReadMember(additionalData, "Size"));
                if (tire) {
                    return new PurchaseKey(id, PurchaseKind.Tire, size,
                        ToRoundedInt(ReadMember(additionalData, "Width")),
                        ToRoundedInt(ReadMember(additionalData, "Profile")));
                }
                if (rim) {
                    return new PurchaseKey(id, PurchaseKind.Rim, size, 0, 0,
                        ToRoundedInt(ReadMember(additionalData, "ET")));
                }
            }
            return new PurchaseKey(id, PurchaseKind.Part);
        }

        private static ShopListItemDataEx CopyShopListAdditionalData(
            ShopListItemDataEx source)
        {
            ShopListItemDataEx copy = new ShopListItemDataEx();
            copy.Reset();
            if (source == null)
                return copy;
            copy.LicensePlateName = source.LicensePlateName;
            copy.LicensePlate = source.LicensePlate;
            copy.Tire = source.Tire;
            copy.Rim = source.Rim;
            copy.Width = source.Width;
            copy.Size = source.Size;
            copy.Profile = source.Profile;
            copy.ET = source.ET;
            return copy;
        }

        private static bool AddWheelToShoppingList(UIManager ui,
            PurchaseKey key, ShopListItemDataEx additionalData)
        {
            if (ui == null || key == null || additionalData == null)
                return false;

            try {
                var window = ui.ShopListWindow;
                var items = window != null ? window.items : null;
                if (items == null)
                    return false;

                ShopListItemData exact = null;
                foreach (ShopListItemData item in items) {
                    if (item == null || !string.Equals(item.ID, key.Id,
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    PurchaseKey existing = CreateShopListKey(item.ID,
                        item.AdditionalData);
                    if (key.Equals(existing)) {
                        exact = item;
                        break;
                    }
                }

                if (exact != null) {
                    exact.Amount = Math.Max(0, exact.Amount) + 1;
                } else {
                    ShopListItemData item = new ShopListItemData();
                    item.ID = key.Id;
                    item.Amount = 1;
                    item.AdditionalData =
                        CopyShopListAdditionalData(additionalData);
                    items.Add(item);
                }

                Increment(ObservedShopList, key, 1);
                ShowWheelShoppingListFeedback(ui, key);
                LogDiagnostic("shopping wheel direct add part=" + key.Id);
                return true;
            } catch (Exception exception) {
                ModLogger.Log("[SharpEye] Failed to add wheel '" + key.Id +
                    "' to the shopping list." + Environment.NewLine + exception,
                    Types.LoggingLevels.Error);
                LogDiagnostic("shopping wheel direct add failed part=" +
                    key.Id + " exception=" + exception.GetType().Name);
                return false;
            }
        }

        private static void ShowWheelShoppingListFeedback(UIManager ui,
            PurchaseKey key)
        {
            if (ui == null || key == null)
                return;
            try {
                ui.ShowPopup(ModLocalization.Get("LOC_SharpEyeAddedToShoppingList"),
                    GetPartDisplayName(key.Id) + BuildShopListSuffix(key),
                    PopupType.Normal);
            } catch {
            }
            try {
                SoundManager soundManager = SoundManager.Get();
                if (soundManager != null)
                    InvokeOneArg(soundManager, "PlaySFX", "AddItemToList");
            } catch {
            }
        }

        private static bool AddToShoppingList(PurchaseKey key)
        {
            UIManager ui = UIManager.Get();
            if (ui == null)
                return false;

            ShopListItemDataEx additionalData = null;
            try {
                GameScript game = GameScript.Get();
                if (game != null)
                    additionalData = game.GetAdditionalShopListItemData();
            } catch {
            }
            if (additionalData == null)
                additionalData = new ShopListItemDataEx();
            additionalData.Reset();
            additionalData.Tire = key.Kind == PurchaseKind.Tire;
            additionalData.Rim = key.Kind == PurchaseKind.Rim;
            additionalData.Size = key.Size;
            additionalData.Width = key.Width;
            additionalData.Profile = key.Profile;
            additionalData.ET = key.ET;

            if (key.Kind == PurchaseKind.Tire ||
                key.Kind == PurchaseKind.Rim)
                return AddWheelToShoppingList(ui, key, additionalData);

            string suffix = BuildShopListSuffix(key);
            try {
                suppressShopListObserver = true;
                ui.AddToShopList(key.Id, suffix, additionalData);
            } catch (Exception exception) {
                ModLogger.Log("[SharpEye] Failed to add '" + key.Id +
                    "' to the shopping list." + Environment.NewLine + exception,
                    Types.LoggingLevels.Error);
                return false;
            } finally {
                suppressShopListObserver = false;
            }

            Increment(ObservedShopList, key, 1);
            return true;
        }

        private static string BuildShopListSuffix(PurchaseKey key)
        {
            if (key.Kind == PurchaseKind.Tire) {
                return " (" + key.Width.ToString(CultureInfo.InvariantCulture) +
                    "/" + key.Profile.ToString(CultureInfo.InvariantCulture) +
                    "R" + key.Size.ToString(CultureInfo.InvariantCulture) + ")";
            }
            if (key.Kind == PurchaseKind.Rim) {
                return " (" + key.Size.ToString(CultureInfo.InvariantCulture) +
                    "\") ET: " + key.ET.ToString(CultureInfo.InvariantCulture);
            }
            return string.Empty;
        }

        private static PurchaseKey CreateObservedKey(string id, string suffix)
        {
            if (id.StartsWith("tire_", StringComparison.OrdinalIgnoreCase)) {
                int width;
                int profile;
                int size;
                if (TryParseTireSuffix(suffix, out width, out profile, out size))
                    return new PurchaseKey(id, PurchaseKind.Tire, size, width,
                        profile);
            }
            if (id.StartsWith("rim_", StringComparison.OrdinalIgnoreCase)) {
                int size;
                int et;
                if (TryParseRimSuffix(suffix, out size, out et))
                    return new PurchaseKey(id, PurchaseKind.Rim, size, 0, 0, et);
            }
            return new PurchaseKey(id, PurchaseKind.Part);
        }

        private static bool TryParseTireSuffix(string suffix, out int width,
            out int profile, out int size)
        {
            width = 0;
            profile = 0;
            size = 0;
            if (string.IsNullOrEmpty(suffix))
                return false;

            int open = suffix.IndexOf('(');
            int slash = suffix.IndexOf('/', open + 1);
            int r = suffix.IndexOf('R', slash + 1);
            int close = suffix.IndexOf(')', r + 1);
            if (open < 0 || slash <= open || r <= slash)
                return false;
            if (close < 0)
                close = suffix.Length;

            return int.TryParse(suffix.Substring(open + 1,
                    slash - open - 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out width) &&
                int.TryParse(suffix.Substring(slash + 1,
                    r - slash - 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out profile) &&
                int.TryParse(suffix.Substring(r + 1,
                    close - r - 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out size);
        }

        private static bool TryParseRimSuffix(string suffix, out int size,
            out int et)
        {
            size = 0;
            et = 0;
            if (string.IsNullOrEmpty(suffix))
                return false;

            int open = suffix.IndexOf('(');
            int quote = suffix.IndexOf('"', open + 1);
            int colon = suffix.LastIndexOf(':');
            if (open < 0 || quote <= open || colon < 0)
                return false;

            return int.TryParse(suffix.Substring(open + 1,
                    quote - open - 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out size) &&
                int.TryParse(suffix.Substring(colon + 1).Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out et);
        }

        private static bool IsWheelId(string id)
        {
            return id.StartsWith("rim_", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("tire_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPurchasablePart(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            bool cached;
            if (PurchasablePartCache.TryGetValue(id, out cached))
                return cached;

            bool purchasable = false;
            try {
                GameInventory inventory = Singleton<GameInventory>.Instance;
                if (inventory != null && inventory.ExistsInPartProperty(id)) {
                    PartProperty property = inventory.GetItemProperty(id);
                    object price = ReadMember(property, "Price");
                    purchasable = property != null && price != null &&
                        Convert.ToSingle(price, CultureInfo.InvariantCulture) > 0f;
                }
            } catch {
            }
            PurchasablePartCache[id] = purchasable;
            return purchasable;
        }

        private static void Increment(Dictionary<PurchaseKey, int> values,
            PurchaseKey key, int amount)
        {
            int count;
            values[key] = values.TryGetValue(key, out count) ? count + amount :
                amount;
        }

        private delegate void ObjectVisitor(object value);

        private static void VisitCollection(object collection,
            ObjectVisitor visitor)
        {
            if (collection == null || visitor == null)
                return;

            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null) {
                foreach (object value in enumerable)
                    visitor(value);
                return;
            }

            int count = ToInt(ReadMember(collection, "Count"), -1);
            if (count < 0)
                count = ToInt(ReadMember(collection, "Length"), -1);
            if (count < 0)
                return;

            PropertyInfo indexer = collection.GetType().GetProperty("Item",
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance, null, null, new Type[] { typeof(int) },
                null);
            if (indexer == null)
                return;

            for (int index = 0; index < count; index++) {
                try {
                    visitor(indexer.GetValue(collection, new object[] { index }));
                } catch {
                }
            }
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null)
                return null;
            Type type = instance.GetType();
            try {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance, null);
            } catch {
            }
            try {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                if (field != null)
                    return field.GetValue(instance);
            } catch {
            }
            return null;
        }

        private static bool WriteMember(object instance, string name,
            object value)
        {
            if (instance == null)
                return false;
            Type type = instance.GetType();
            try {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                if (property != null && property.CanWrite &&
                    property.GetIndexParameters().Length == 0) {
                    property.SetValue(instance, value, null);
                    return true;
                }
            } catch {
            }
            try {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                if (field != null) {
                    field.SetValue(instance, value);
                    return true;
                }
            } catch {
            }
            return false;
        }

        private static object InvokeNoArgs(object instance, string name)
        {
            if (instance == null)
                return null;
            try {
                MethodInfo method = instance.GetType().GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method != null ? method.Invoke(instance, null) : null;
            } catch {
                return null;
            }
        }

        private static object InvokeOneArg(object instance, string name,
            object argument)
        {
            if (instance == null)
                return null;
            try {
                MethodInfo[] methods = instance.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                for (int index = 0; index < methods.Length; index++) {
                    MethodInfo method = methods[index];
                    if (!string.Equals(method.Name, name,
                            StringComparison.Ordinal))
                        continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1)
                        continue;
                    return method.Invoke(instance, new object[] { argument });
                }
            } catch {
            }
            return null;
        }

        private static int ToRoundedInt(object value)
        {
            try {
                return value == null ? 0 :
                    (int)Math.Round(Convert.ToDouble(value,
                        CultureInfo.InvariantCulture));
            } catch {
                return 0;
            }
        }

        private static int ToInt(object value, int fallback)
        {
            try {
                return value == null ? fallback : Convert.ToInt32(value,
                    CultureInfo.InvariantCulture);
            } catch {
                return fallback;
            }
        }

        private static bool ToBool(object value)
        {
            try {
                return value != null && Convert.ToBoolean(value,
                    CultureInfo.InvariantCulture);
            } catch {
                return false;
            }
        }

        private static string ToText(object value)
        {
            if (value == null)
                return null;
            try {
                return value.ToString();
            } catch {
                return null;
            }
        }

    }

    [HarmonyPatch]
    internal static class SharpEyeNativeExamineHintShowPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ControlDescription), "Show",
                Type.EmptyTypes);
        }

        private static bool Prefix(ControlDescription __instance)
        {
            return SharpEyeShoppingListFeature
                .ShouldAllowNativeExamineHintShow(__instance);
        }
    }

    [HarmonyPatch]
    internal static class SharpEyePartExaminePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PartScript),
                nameof(PartScript.Examine), new Type[] { typeof(bool) });
        }

        private static bool Prefix(PartScript __instance, bool __0)
        {
            return SharpEyeShoppingListFeature.ShouldAllowPartExamine(
                __instance, __0);
        }

        private static void Postfix(PartScript __instance, bool __0)
        {
            SharpEyeShoppingListFeature.HandlePartExamine(__instance, __0);
        }
    }

    [HarmonyPatch]
    internal static class SharpEyeExamineGarageRaycastPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Raycast), "ExamineGarage");
        }

        private static bool Prefix(Raycast __instance)
        {
            return SharpEyeShoppingListFeature.CaptureExamineGarageRaycast(
                __instance);
        }

        private static void Postfix(Raycast __instance)
        {
            SharpEyeShoppingListFeature.HandleExamineGarageRaycast(__instance);
        }
    }

    [HarmonyPatch]
    internal static class SharpEyeExamineConditionRaycastPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Raycast), "ExamineCondition");
        }

        private static bool Prefix(Raycast __instance)
        {
            return SharpEyeShoppingListFeature.CaptureExamineConditionRaycast(
                __instance);
        }

        private static void Postfix(Raycast __instance)
        {
            SharpEyeShoppingListFeature.HandleExamineConditionRaycast(
                __instance);
        }
    }

    [HarmonyPatch]
    internal static class SharpEyeGameModePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GameMode), "SetCurrentMode",
                new Type[] { typeof(gameMode) });
        }

        private static void Prefix(gameMode __0)
        {
            SharpEyeShoppingListFeature.HandleGameModeChanged(__0);
        }

    }

    [HarmonyPatch]
    internal static class SharpEyeNativeMouseOverPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GameScript),
                nameof(GameScript.SetIOMouseOver),
                new Type[] { typeof(GameObject), typeof(string),
                    typeof(InteractiveObject) });
        }

        private static bool Prefix()
        {
            return SharpEyeShoppingListFeature.ShouldAllowNativeMouseOver();
        }
    }

    [HarmonyPatch]
    internal static class SharpEyeShopListObserverPatch
    {
        private static MethodBase TargetMethod()
        {
            return SharpEyeShoppingListFeature.FindAddToShopListMethod();
        }

        private static void Postfix(string __0, string __1)
        {
            SharpEyeShoppingListFeature.ObserveShopListAdd(__0, __1);
        }
    }
}
