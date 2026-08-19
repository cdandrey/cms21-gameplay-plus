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
    internal static class SharpEyeShoppingListFeature
    {
        private const float PerfectConditionThreshold = 0.9999f;
        private const int DiagnosticLineLimit = 4000;
        private const float CustomExamineHoldSeconds = 0.55f;
        private const float CursorVisualCompletionScale = 1.05f;
        private const int MaximumInspectionSkillLevel = 6;
        private const string DiagnosticLogPath =
            @"Mods\CMS21GameplayPlus\SharpEyeUiDiagnostics.log";

        private enum InspectionPath
        {
            Native,
            SharpEye
        }

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
            public readonly List<PartScript> NativeCarrierCandidates =
                new List<PartScript>();
            public PartScript CarrierPart;
            public bool CustomContinuationActive;
            public bool PassStarted;
            public bool CarrierVisualStateLogged;
            public bool CarrierVisualMouseDownLogged;
            public float ManualHoldProgress;
            public float CarrierVisualProgress;
            public float NativeVisualProgress;
            public bool HasMountedParts;
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
        private static readonly Dictionary<int, int> SystemSpecificationCountCache =
            new Dictionary<int, int>();
        private static readonly Dictionary<int, List<string>> SystemSpecificationPartIdsCache =
            new Dictionary<int, List<string>>();
        private static readonly Dictionary<int, PartScript> SystemSinglePartFallback =
            new Dictionary<int, PartScript>();
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
        private static readonly Dictionary<int, string> CarDisplayNameCache =
            new Dictionary<int, string>();
        private static readonly Dictionary<int, string> SystemNameCache =
            new Dictionary<int, string>();
        private static readonly HashSet<int> DumpedInspectionLoaderIds =
            new HashSet<int>();
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
        private static bool customExamineCall;
        private static bool examineModeSessionActive;
        private static bool repeatClickArmed;
        private static bool examineMouseWasDown;
        private static InteractiveObject capturedExamineSystem;
        private static InteractiveObject capturedExamineBody;
        private static InteractiveObject bodyHighlightTarget;
        private static readonly List<InteractiveObject> activeBodyHighlightTargets =
            new List<InteractiveObject>();
        private static int bodyHighlightLoaderId = -1;
        private static InteractiveObject observedMouseOverTarget;
        private static int observedMouseOverFrame = -1;
        private static InteractiveObject observedBodyMouseOverTarget;
        private static int observedBodyMouseOverFrame = -1;
        private static bool suppressMouseOverObservation;
        private static InteractiveObject emptySystemHighlightTarget;
        private static int inspectionResetTargetId = -1;
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
        private const string InspectionSolidColorProperty = "_SolidColor";
        private const float InspectionSystemListWidth = 220f;
        private const float InspectionSystemListRowHeight = 18f;
        private const float InspectionSystemListRowGap = 1f;
        private const float InspectionSystemListNumberWidth = 22f;
        private const float InspectionSystemListStatusWidth = 78f;
        private static readonly Color inspectionCompletedMissingSystemColor =
            new Color(0.35f, 0.75f, 1f, 1f);
        private static readonly Color inspectionSystemListCompletedColor =
            new Color(0.236f, 0.604f, 0f, 1f);
        private static readonly Color inspectionSystemListUnavailableColor =
            new Color(0.45f, 0.45f, 0.45f, 1f);
        private static readonly List<InteractiveObject> inspectionSystemsOverlayTargets =
            new List<InteractiveObject>();
        private static readonly List<PartScript> inspectionSystemsOverlaySolidTargets =
            new List<PartScript>();
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
            internal bool IsExamined;
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
        private static string inspectionNativeHintSuppressionDiagnosticKey;
        private static int cachedInspectionSkillLevel = -1;
        private static string cachedInspectionSkillId;
        private static bool inspectionSkillDiagnosticsLogged;
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
        private static Camera bodySelectionCamera;
        private static UnityEngine.UI.Image sharpEyeCursorTimerImage;

        internal static void OnGarageSceneInitialized(int profileId)
        {
            shoppingListGeneration++;
            shoppingListWorkerRunning = false;
            examineModeSessionActive = false;
            repeatClickArmed = false;
            examineMouseWasDown = false;
            RestoreAllCarriers();
            DestroySharpEyeCursorTimer();
            ClearPendingShoppingListQueue();
            SystemSpecificationCountCache.Clear();
            SystemSpecificationPartIdsCache.Clear();
            SystemSinglePartFallback.Clear();
            SystemWheelPartsCache.Clear();
            LoaderPartInstanceIds.Clear();
            SystemPassStates.Clear();
            BodyPartsCache.Clear();
            BodyHighlightTargetsCache.Clear();
            BodySelectionSurfacesCache.Clear();
            BodyPassStates.Clear();
            InspectionSystemsCache.Clear();
            CarDisplayNameCache.Clear();
            SystemNameCache.Clear();
            bodyDisplayName = null;
            bodySelectionCamera = null;
            capturedExamineBody = null;
            observedMouseOverTarget = null;
            observedMouseOverFrame = -1;
            observedBodyMouseOverTarget = null;
            observedBodyMouseOverFrame = -1;
            suppressMouseOverObservation = false;
            ClearInspectionSystemsOverlay();
            ResetInspectionResetInput(true);
            cachedInspectionSkillLevel = -1;
            cachedInspectionSkillId = null;
            inspectionSkillDiagnosticsLogged = false;
            DestroyInspectionSystemList();
            ResetInspectionVehicleProgressCache();
            ClearBodyHighlight();
            DumpedInspectionLoaderIds.Clear();
            cursorVisualSourceDiagnosticsLogged = false;
            inspectionHintSourceDiagnosticsLogged = false;
            inspectionHintHost = null;
            inspectionNativeHintSuppressionDiagnosticKey = null;
            indicatorDirty = true;
            StartDiagnostics();
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
            repeatClickArmed = false;
            examineMouseWasDown = false;
            RestoreAllCarriers();
            DestroySharpEyeCursorTimer();
            ClearPendingShoppingListQueue();
            SystemSpecificationCountCache.Clear();
            SystemSpecificationPartIdsCache.Clear();
            SystemSinglePartFallback.Clear();
            SystemWheelPartsCache.Clear();
            LoaderPartInstanceIds.Clear();
            SystemPassStates.Clear();
            BodyPartsCache.Clear();
            BodyHighlightTargetsCache.Clear();
            BodySelectionSurfacesCache.Clear();
            BodyPassStates.Clear();
            InspectionSystemsCache.Clear();
            CarDisplayNameCache.Clear();
            SystemNameCache.Clear();
            bodyDisplayName = null;
            bodySelectionCamera = null;
            capturedExamineBody = null;
            observedMouseOverTarget = null;
            observedMouseOverFrame = -1;
            observedBodyMouseOverTarget = null;
            observedBodyMouseOverFrame = -1;
            suppressMouseOverObservation = false;
            ClearInspectionSystemsOverlay();
            ResetInspectionResetInput(true);
            cachedInspectionSkillLevel = -1;
            cachedInspectionSkillId = null;
            inspectionSkillDiagnosticsLogged = false;
            DestroyInspectionSystemList();
            ResetInspectionVehicleProgressCache();
            ClearBodyHighlight();
            DumpedInspectionLoaderIds.Clear();
            cursorVisualSourceDiagnosticsLogged = false;
            inspectionHintSourceDiagnosticsLogged = false;
            inspectionNativeHintSuppressionDiagnosticKey = null;
            indicatorDirty = true;
            LogDiagnostic("garage session ended");
        }

        private static InspectionPath ResolveInspectionPath(
            InteractiveObject system)
        {
            int requiredLevel = GetRequiredInspectionSkillLevel(system);
            if (requiredLevel <= 0)
                return InspectionPath.Native;
            return GetInspectionSkillLevel() >= requiredLevel ?
                InspectionPath.SharpEye : InspectionPath.Native;
        }

        private static bool UsesSharpEyeInspection(InteractiveObject system)
        {
            return ResolveInspectionPath(system) == InspectionPath.SharpEye;
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

            string rawId = null;
            try {
                rawId = NormalizeSystemName(system.GetID());
            } catch {
            }
            string objectName = NormalizeSystemName(system.name);

            if (!string.IsNullOrEmpty(rawId)) {
                if (rawId.StartsWith("engine_",
                        StringComparison.OrdinalIgnoreCase))
                    return 6;
                if (rawId.StartsWith("FrontRight",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("RearRight",
                        StringComparison.OrdinalIgnoreCase))
                    return 5;
                if (rawId.StartsWith("FrontCenter",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("RearCenter",
                        StringComparison.OrdinalIgnoreCase))
                    return 4;
                if (rawId.StartsWith("Driveshaft",
                        StringComparison.OrdinalIgnoreCase))
                    return 4;
                if (rawId.StartsWith("Exhaust",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Downpipe",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("AirIntake",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Cooling",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("Radiator",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("FuelTank",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("ABS",
                        StringComparison.OrdinalIgnoreCase) ||
                    rawId.StartsWith("BrakePump",
                        StringComparison.OrdinalIgnoreCase))
                    return 3;
                if (rawId.StartsWith("Battery",
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
                        StringComparison.OrdinalIgnoreCase))
                    return 2;
            }

            if (objectName.StartsWith("FCSusp",
                    StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith("RCSusp",
                    StringComparison.OrdinalIgnoreCase))
                return 4;

            if (objectName.StartsWith("FLSusp",
                    StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith("FRSusp",
                    StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith("RLSusp",
                    StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith("RRSusp",
                    StringComparison.OrdinalIgnoreCase))
                return 5;

            if (IsWholeCarBodyObject(system))
                return 1;
            CarLoader loader = GetCarLoader(system);
            return loader != null && IsBodyAggregateObject(loader, system) ?
                1 : 0;
        }

        private static float GetSharpEyeHoldSeconds()
        {
            return CustomExamineHoldSeconds;
        }

        internal static MethodBase FindExamineRandomPartMethod()
        {
            return AccessTools.Method(typeof(InteractiveObject),
                nameof(InteractiveObject.ExamineRandomPart),
                new Type[] { typeof(bool) });
        }

        internal static MethodBase FindAddToShopListMethod()
        {
            return AccessTools.Method(typeof(UIManager),
                nameof(UIManager.AddToShopList), new Type[] { typeof(string),
                    typeof(string), typeof(ShopListItemDataEx) });
        }

        internal static bool HandleExamineRandomPartPrefix(
            InteractiveObject source, bool requested, ref bool result)
        {
            customExamineCall = false;
            if (!requested || source == null ||
                !GlobalState.IsGarageSceneActive ||
                !examineModeSessionActive || !IsExamineGarageModeActive() ||
                IsWholeCarBodyObject(source) || !UsesSharpEyeInspection(source))
                return true;

            SystemPassState state = GetSystemPassState(source);
            if (state == null || !state.CustomContinuationActive)
                return true;

            ResetCarrierVisualHold(state);
            RestoreCarrier(state);
            if (ProcessOneCustomSystemStep(source)) {
                result = true;
                customExamineCall = true;
                state.PassStarted = true;
                indicatorDirty = true;
                EnsureCustomContinuation(source, state);
                return false;
            }

            state.CustomContinuationActive = false;
            RestoreCarrier(state);
            return true;
        }

        internal static void HandleExamineRandomPart(InteractiveObject source,
            bool requested, ref bool result)
        {
            if (!requested || source == null ||
                !GlobalState.IsGarageSceneActive ||
                !examineModeSessionActive || !IsExamineGarageModeActive() ||
                IsWholeCarBodyObject(source) || !UsesSharpEyeInspection(source))
                return;

            SystemPassState state = GetSystemPassState(source);
            if (result) {
                if (state != null)
                    state.PassStarted = true;
                indicatorDirty = true;
                if (customExamineCall) {
                    LogDiagnostic("custom examine success system=" +
                        SafeIoId(source));
                } else {
                    LogDiagnostic("native examine success system=" +
                        SafeIoId(source));
                    TryStartCustomContinuation(source, state);
                }
                customExamineCall = false;
                return;
            }

            if (ProcessOneCustomSystemStep(source)) {
                result = true;
                if (state != null) {
                    state.PassStarted = true;
                    state.CustomContinuationActive = true;
                    EnsureCustomContinuation(source, state);
                }
                indicatorDirty = true;
                LogDiagnostic("custom fallback success system=" +
                    SafeIoId(source));
                return;
            }

            if (state != null) {
                state.CustomContinuationActive = false;
                RestoreCarrier(state);
            }
            LogDiagnostic("system complete system=" + SafeIoId(source));
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

        internal static void HandlePartExamine(PartScript part, bool requested,
            bool wasExamined)
        {
            if (!requested || part == null ||
                !GlobalState.IsGarageSceneActive ||
                !examineModeSessionActive || !IsExamineGarageModeActive())
                return;

            bool isExamined = false;
            try {
                isExamined = part.IsExamined;
            } catch {
            }
            if (!isExamined)
                return;
            if (processingCustomPartExamine) {
                customExaminedPartInstanceId = part.GetInstanceID();
                LogDiagnostic("custom part examined part=" + SafePartId(part));
                return;
            }

            InteractiveObject system = null;
            try {
                system = part.transform.GetComponentInParent<InteractiveObject>();
            } catch {
            }
            if (system == null || IsWholeCarBodyObject(system) ||
                !UsesSharpEyeInspection(system))
                return;

            SystemPassState state = GetSystemPassState(system);
            if (state != null) {
                state.ExaminedPartInstanceIds.Add(part.GetInstanceID());
                state.PassStarted = true;
                state.NativeVisualProgress = 0f;
                if (!processingCustomPartExamine)
                    AddNativeCarrierCandidate(state, part);
            }

            MarkInspectionProgressChanged();
            CarLoader loader = GetCarLoader(system);
            if (loader != null)
                QueueSinglePartShoppingList(loader, system, part);

            if (wasExamined)
                LogDiagnostic("native re-examine observed system=" +
                    SafeIoId(system) + " part=" + SafePartId(part));
        }

        internal static void LogPopup(string title, string message,
            PopupType popupType)
        {
            if (!examineModeSessionActive || !IsExamineGarageModeActive())
                return;
            LogDiagnostic("popup type=" + popupType + " title=\"" +
                SafeLogText(title) + "\" message=\"" +
                SafeLogText(message) + "\"");
        }

        internal static void LogSound(string method, string id)
        {
            if (!examineModeSessionActive || !IsExamineGarageModeActive())
                return;
            LogDiagnostic("sound " + method + " id=\"" +
                SafeLogText(id) + "\"");
        }

        internal static void ObserveShopListAdd(string id, string suffix)
        {
            if (suppressShopListObserver || string.IsNullOrEmpty(id))
                return;

            PurchaseKey key = CreateObservedKey(id, suffix);
            if (key == null)
                return;
            Increment(ObservedShopList, key, 1);
        }

        internal static void HandleGameModeChanged(gameMode currentMode)
        {
            if (!GlobalState.IsGarageSceneActive)
                return;

            bool wasExamineGarage = examineModeSessionActive;
            examineModeSessionActive = currentMode == gameMode.ExamineGarage;
            indicatorDirty = true;
            if (!wasExamineGarage && examineModeSessionActive) {
                RestoreInspectionFooterAfterHold();
                RestoreAllCarriers();
                ClearInspectionSystemsOverlay();
                BodyPartsCache.Clear();
                BodyHighlightTargetsCache.Clear();
                BodySelectionSurfacesCache.Clear();
                InspectionSystemsCache.Clear();
                SystemWheelPartsCache.Clear();
                bodySelectionCamera = null;
                capturedExamineBody = null;
                observedMouseOverTarget = null;
                observedMouseOverFrame = -1;
                observedBodyMouseOverTarget = null;
                observedBodyMouseOverFrame = -1;
                suppressMouseOverObservation = false;
                if (inspectionResetHintSource == null)
                    inspectionResetHintSourceSearchAttempted = false;
                ResetInspectionResetInput(false);
                cachedInspectionSkillLevel = -1;
                cachedInspectionSkillId = null;
                inspectionSkillDiagnosticsLogged = false;
                inspectionSystemListDirty = true;
                ResetInspectionVehicleProgressCache();
                GetInspectionSkillLevel();
                ClearBodyHighlight();
                repeatClickArmed = false;
                examineMouseWasDown = Input.GetMouseButton(0);
            }
            if (wasExamineGarage && !examineModeSessionActive) {
                RestoreAllCarriers();
                ClearInspectionSystemsOverlay();
                capturedExamineBody = null;
                observedMouseOverTarget = null;
                observedMouseOverFrame = -1;
                observedBodyMouseOverTarget = null;
                observedBodyMouseOverFrame = -1;
                suppressMouseOverObservation = false;
                HideInspectionFooterForModeExit();
                DestroyInspectionSystemList();
                ResetInspectionResetInput(false);
                ClearBodyHighlight();
                repeatClickArmed = false;
                examineMouseWasDown = false;
                DestroySharpEyeCursorTimer();
                ClearSystemIndicator(true);
            }
        }

        internal static bool CaptureExamineGarageRaycast(Raycast raycast)
        {
            capturedExamineSystem = null;
            capturedExamineBody = null;
            if (!GlobalState.IsGarageSceneActive ||
                !examineModeSessionActive || !IsExamineGarageModeActive() ||
                raycast == null)
                return true;

            if (!Input.GetMouseButton(0))
                RestoreInspectionFooterAfterHold();

            InteractiveObject raycastObject = raycast.iO;
            InteractiveObject systemObject = ResolveSystemHoverTarget(
                raycast, raycastObject);
            InteractiveObject bodyObject = systemObject == null ?
                ResolveBodyHoverTarget(raycast, raycastObject) : null;
            bool bodyTarget = bodyObject != null;
            InteractiveObject current = systemObject ?? bodyObject;
            if (UpdateInspectionSystemsOverlayInput(raycast, current))
                return !Input.GetMouseButton(0);
            if (UpdateInspectionResetInput(raycast, current, bodyTarget))
                return false;
            if (current != null && !UsesSharpEyeInspection(current)) {
                PrepareNativeInspection();
                return true;
            }
            if (current != null) {
                if (bodyTarget)
                    capturedExamineBody = current;
                else
                    capturedExamineSystem = current;
            }

            bool mouseDown = Input.GetMouseButton(0);
            if (mouseDown && current != null)
                SuppressInspectionFooterForHold();
            if (!mouseDown)
                repeatClickArmed = true;
            bool mousePressed = mouseDown && !examineMouseWasDown;
            examineMouseWasDown = mouseDown;

            if (mousePressed && !bodyTarget)
                LogExamineHoverProbe(raycast, raycastObject);

            if (bodyTarget) {
                // Let the stock hover raycast refresh while the button is up.
                // Suppress it only during an actual body hold.
                return !mouseDown;
            }

            if (current == null || !repeatClickArmed || !mousePressed)
                return true;

            int examined;
            int total;
            GetSystemProgress(current, out examined, out total);
            SystemPassState state = GetSystemPassState(current);
            int nativeTotal = GetRawNativeSystemPartCount(current);
            int nativeExamined = GetRawNativeSystemExaminedCount(current);
            bool nativePassExhausted = nativeTotal <= 0 ||
                nativeExamined >= nativeTotal;
            if (state != null && !state.PassStarted && examined > 0 &&
                examined < total && nativePassExhausted &&
                HasPendingCustomStep(current, state)) {
                state.PassStarted = true;
                state.CustomContinuationActive = true;
                EnsureCustomContinuation(current, state);
                repeatClickArmed = false;
                indicatorDirty = true;
                LogDiagnostic("resume preexisting pass system=" +
                    SafeIoId(current) + " progress=" +
                    examined.ToString(CultureInfo.InvariantCulture) + "/" +
                    total.ToString(CultureInfo.InvariantCulture) +
                    " native=" +
                    nativeExamined.ToString(CultureInfo.InvariantCulture) +
                    "/" + nativeTotal.ToString(CultureInfo.InvariantCulture));
                return true;
            }

            return true;
        }

        private static bool UpdateInspectionSystemsOverlayInput(
            Raycast raycast, InteractiveObject target)
        {
            bool keyDown = Input.GetKey(KeyCode.Tab);
            if (!keyDown) {
                if (inspectionSystemsOverlayActive)
                    ClearInspectionSystemsOverlay();
                return false;
            }
            if (Input.GetMouseButton(0) && !inspectionSystemsOverlayActive)
                return false;

            CarLoader loader = target != null ? GetCarLoader(target) : null;
            if (loader == null)
                loader = GetHoveredCarLoader(raycast);
            if (loader == null)
                loader = inspectionSystemsOverlayLoader;
            if (loader == null)
                loader = inspectionResetLoader;
            if (loader == null)
                return false;

            bool changed = !inspectionSystemsOverlayActive ||
                inspectionSystemsOverlayLoader == null;
            if (!changed) {
                try {
                    changed = inspectionSystemsOverlayLoader.GetInstanceID() !=
                        loader.GetInstanceID();
                } catch {
                    changed = true;
                }
            }
            if (changed)
                ShowInspectionSystemsOverlay(loader);
            return inspectionSystemsOverlayActive;
        }

        private static void ShowInspectionSystemsOverlay(CarLoader loader)
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
            HideNativeExamineHint();
            HashSet<int> highlighted = new HashSet<int>();
            HashSet<int> solidHighlighted = new HashSet<int>();
            int fullyUnmountedSystems = 0;
            int skillLevel = GetInspectionSkillLevel();

            BodyPassState bodyState = skillLevel > 0 ?
                GetBodyPassState(loader) : null;
            if (bodyState != null && bodyState.Total > 0) {
                bool bodyComplete =
                    bodyState.ExaminedSlots.Count >= bodyState.Total;
                List<InteractiveObject> bodyTargets =
                    GetBodyHighlightTargets(loader);
                for (int index = 0; index < bodyTargets.Count; index++)
                    AddInspectionSystemsOverlayTarget(bodyTargets[index],
                        bodyComplete, highlighted, true);
            }

            List<InteractiveObject> systems = GetInspectionSystems(loader);
            for (int index = 0; index < systems.Count; index++) {
                InteractiveObject system = systems[index];
                if (system == null)
                    continue;
                int requiredLevel = GetRequiredInspectionSkillLevel(system);
                if (requiredLevel < 2)
                    continue;
                if (requiredLevel > skillLevel) {
                    HideInspectionSystemsOverlayUnavailableSystem(system);
                    continue;
                }
                if (!UsesSharpEyeInspection(system))
                    continue;
                int examined;
                int total;
                GetSystemProgress(system, out examined, out total);
                if (total <= 0)
                    continue;
                bool completed = examined >= total;
                if (!IsSystemFullyUnmounted(system)) {
                    AddInspectionSystemsOverlayTarget(system, completed,
                        highlighted, true);
                    continue;
                }
                fullyUnmountedSystems++;
                AddInspectionSystemsOverlaySolidSystem(system, completed,
                    solidHighlighted);
            }

            SetMouseOverDescription(ModLocalization.Get(
                "LOC_SharpEyeShowSystems"));
            LogDiagnostic("inspection systems overlay show car=" +
                SafeLoaderName(loader) + " outlines=" +
                inspectionSystemsOverlayTargets.Count.ToString(
                    CultureInfo.InvariantCulture) + " fullyUnmounted=" +
                fullyUnmountedSystems.ToString(CultureInfo.InvariantCulture) +
                " solidParts=" +
                inspectionSystemsOverlaySolidTargets.Count.ToString(
                    CultureInfo.InvariantCulture));
        }

        private static void HideInspectionSystemsOverlayUnavailableSystem(
            InteractiveObject system)
        {
            if (system == null)
                return;
            foreach (Renderer renderer in
                system.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                int rendererId = renderer.GetInstanceID();
                if (inspectionSystemsOverlayHiddenRendererStates.
                    ContainsKey(rendererId))
                    continue;
                inspectionSystemsOverlayHiddenRendererStates[rendererId] =
                    renderer.enabled;
                inspectionSystemsOverlayHiddenRenderers.Add(renderer);
                renderer.enabled = false;
            }
            foreach (Collider collider in
                system.GetComponentsInChildren<Collider>(true)) {
                if (collider == null)
                    continue;
                int colliderId = collider.GetInstanceID();
                if (inspectionSystemsOverlayHiddenColliderStates.
                    ContainsKey(colliderId))
                    continue;
                inspectionSystemsOverlayHiddenColliderStates[colliderId] =
                    collider.enabled;
                inspectionSystemsOverlayHiddenColliders.Add(collider);
                collider.enabled = false;
            }
        }

        private static void AddInspectionSystemsOverlayTarget(
            InteractiveObject target, bool completed, HashSet<int> highlighted,
            bool outline)
        {
            if (target == null || highlighted == null || !outline)
                return;
            int targetId;
            try {
                targetId = target.GetInstanceID();
            } catch {
                return;
            }
            if (!highlighted.Add(targetId))
                return;
            bool previousSuppression = suppressMouseOverObservation;
            try {
                suppressMouseOverObservation = true;
                target.SetMouseOver(true, completed ? Color.green :
                    Color.white);
                inspectionSystemsOverlayTargets.Add(target);
            } catch {
            } finally {
                suppressMouseOverObservation = previousSuppression;
            }
        }

        private static void AddInspectionSystemsOverlaySolidSystem(
            InteractiveObject system, bool completed, HashSet<int> highlighted)
        {
            if (system == null || highlighted == null)
                return;
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (part == null)
                    continue;
                int partId;
                try {
                    partId = part.GetInstanceID();
                } catch {
                    continue;
                }
                if (!highlighted.Add(partId))
                    continue;
                try {
                    InspectionOverlayPartState state =
                        new InspectionOverlayPartState();
                    state.Color = part.GetColor();
                    state.IsUnmounted = part.IsUnmounted;
                    state.IsExamined = part.IsExamined;
                    state.MountMode = part.mountMode;
                    state.ReplacedShader = part.replacedShader;
                    state.Layer = part.gameObject != null ?
                        part.gameObject.layer : InspectionPreviewPartLayer;
                    inspectionSystemsOverlaySolidOriginalStates[partId] =
                        state;
                    inspectionSystemsOverlaySolidTargets.Add(part);

                    part.IsUnmounted = false;
                    part.IsExamined = completed;
                    part.mountMode = true;
                    part.ReplaceShader(false);
                    SetInspectionSystemsOverlayMountedLayers(part, state.Layer);
                    part.SwitchSolidColor(true);
                    part.Alpha1();
                    if (completed)
                        SetInspectionSystemsOverlayCompletedColor(part);
                } catch (Exception exception) {
                    LogDiagnostic("inspection overlay solid failed system=" +
                        SafeIoId(system) + " part=" + SafePartId(part) +
                        " exception=" + exception.GetType().Name);
                }
            }
        }

        private static void SetInspectionSystemsOverlayMountedLayers(
            PartScript part, int colliderLayer)
        {
            if (part == null)
                return;
            part.SetLayerRecursively(InspectionMountedPartLayer);
            foreach (Collider collider in
                part.GetComponentsInChildren<Collider>(true)) {
                if (collider != null && collider.gameObject != null)
                    collider.gameObject.layer = colliderLayer;
            }
        }

        private static void SetInspectionSystemsOverlayCompletedColor(
            PartScript part)
        {
            if (part == null)
                return;
            foreach (Renderer renderer in
                part.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null)
                    continue;
                foreach (Material material in renderer.sharedMaterials) {
                    if (material == null ||
                        !material.HasProperty(InspectionSolidColorProperty))
                        continue;
                    int materialId = material.GetInstanceID();
                    if (!inspectionSystemsOverlaySolidOriginalColors.
                        ContainsKey(materialId)) {
                        inspectionSystemsOverlaySolidOriginalColors[materialId] =
                            material.GetColor(InspectionSolidColorProperty);
                    }
                    material.SetColor(InspectionSolidColorProperty,
                        inspectionCompletedMissingSystemColor);
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
                foreach (Material material in renderer.sharedMaterials) {
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

        private static bool IsSystemFullyUnmounted(InteractiveObject system)
        {
            List<string> specification = GetSystemSpecificationPartIds(system);
            if (specification.Count == 0)
                return false;
            List<int> missing = GetMissingSpecificationSlots(system,
                specification);
            return missing.Count >= specification.Count;
        }

        private static void UpdateInspectionSystemsOverlayIndicator(
            CarLoader loader)
        {
            int completed;
            int total;
            GetInspectionSystemsProgress(loader, out completed, out total);
            string template = ModLocalization.Get(
                "LOC_SharpEyeSystemsProgress");
            string text;
            try {
                text = string.Format(CultureInfo.InvariantCulture, template,
                    completed, total);
            } catch {
                text = completed.ToString(CultureInfo.InvariantCulture) +
                    " / " + total.ToString(CultureInfo.InvariantCulture);
            }
            try {
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.SetBonusTextDescription(text);
                indicatorSystem = null;
                indicatorText = text;
                indicatorDirty = false;
            } catch {
            }
        }

        private static void RestoreInspectionSystemsOverlayIndicator()
        {
            if (!inspectionSystemsOverlayActive ||
                string.IsNullOrEmpty(indicatorText))
                return;
            try {
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.SetBonusTextDescription(indicatorText);
            } catch {
            }
        }

        private static void GetInspectionSystemsProgress(CarLoader loader,
            out int completed, out int total)
        {
            completed = 0;
            total = 0;
            if (loader == null)
                return;

            int skillLevel = GetInspectionSkillLevel();
            BodyPassState bodyState = skillLevel > 0 ?
                GetBodyPassState(loader) : null;
            if (bodyState != null && bodyState.Total > 0) {
                total++;
                if (bodyState.ExaminedSlots.Count >= bodyState.Total)
                    completed++;
            }

            List<InteractiveObject> systems = GetInspectionSystems(loader);
            for (int index = 0; index < systems.Count; index++) {
                InteractiveObject system = systems[index];
                if (system == null)
                    continue;
                int requiredLevel = GetRequiredInspectionSkillLevel(system);
                if (requiredLevel < 2 || requiredLevel > skillLevel ||
                    !UsesSharpEyeInspection(system))
                    continue;
                int examined;
                int systemTotal;
                GetSystemProgress(system, out examined, out systemTotal);
                if (systemTotal <= 0)
                    continue;
                total++;
                if (examined >= systemTotal)
                    completed++;
            }
        }

        private static void ClearInspectionSystemsOverlay()
        {
            if (!inspectionSystemsOverlayActive &&
                inspectionSystemsOverlayTargets.Count == 0 &&
                inspectionSystemsOverlaySolidTargets.Count == 0 &&
                inspectionSystemsOverlayHiddenRenderers.Count == 0 &&
                inspectionSystemsOverlayHiddenColliders.Count == 0) {
                if (inspectionSystemListVisible ||
                    inspectionSystemListPanel != null)
                    DestroyInspectionSystemList();
                return;
            }
            bool previousSuppression = suppressMouseOverObservation;
            try {
                suppressMouseOverObservation = true;
                for (int index = 0;
                    index < inspectionSystemsOverlayTargets.Count; index++) {
                    InteractiveObject target =
                        inspectionSystemsOverlayTargets[index];
                    if (target == null)
                        continue;
                    try {
                        target.SetMouseOver(false);
                    } catch {
                    }
                }
                for (int index = 0;
                    index < inspectionSystemsOverlaySolidTargets.Count;
                    index++) {
                    PartScript part =
                        inspectionSystemsOverlaySolidTargets[index];
                    if (part == null)
                        continue;
                    try {
                        InspectionOverlayPartState state;
                        if (!inspectionSystemsOverlaySolidOriginalStates.
                            TryGetValue(part.GetInstanceID(), out state))
                            continue;
                        part.IsExamined = state.IsExamined;
                        part.IsUnmounted = state.IsUnmounted;
                        part.mountMode = state.MountMode;
                        part.SwitchSolidColor(false);
                        RestoreInspectionSystemsOverlaySolidColors(part);
                        part.SetLayerRecursively(state.Layer);
                        part.ReplaceShader(state.ReplacedShader);
                        part.SetColor(state.Color);
                        part.Alpha0();
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
            } finally {
                suppressMouseOverObservation = previousSuppression;
            }
            inspectionSystemsOverlayTargets.Clear();
            inspectionSystemsOverlaySolidTargets.Clear();
            inspectionSystemsOverlaySolidOriginalStates.Clear();
            inspectionSystemsOverlaySolidOriginalColors.Clear();
            inspectionSystemsOverlayHiddenRenderers.Clear();
            inspectionSystemsOverlayHiddenRendererStates.Clear();
            inspectionSystemsOverlayHiddenColliders.Clear();
            inspectionSystemsOverlayHiddenColliderStates.Clear();
            inspectionSystemsOverlayLoader = null;
            inspectionSystemsOverlayActive = false;
            DestroyInspectionSystemList();
            indicatorDirty = true;
            ClearSystemIndicator(true);
            LogDiagnostic("inspection systems overlay hide");
        }

        private static void PrepareNativeInspection()
        {
            capturedExamineSystem = null;
            capturedExamineBody = null;
            ClearBodyHighlight();
            ClearSystemIndicator();
            SetSharpEyeCursorFill(0f);
        }

        private static InteractiveObject ResolveBodyHoverTarget(
            Raycast raycast, InteractiveObject raycastObject)
        {
            Transform hitTransform = GetRaycastHitTransform(raycast);
            InteractiveObject hitObject = GetHitInteractiveObject(hitTransform);
            if (hitObject != null) {
                CarLoader hitLoader = GetCarLoader(hitObject);
                if (IsBodyAggregateObject(hitLoader, hitObject))
                    return hitObject;

                InteractiveObject bodySurface;
                if (TryGetBodySelectionHit(raycast, hitLoader,
                        out bodySurface))
                    return bodySurface;
                return null;
            }

            InteractiveObject observed = observedBodyMouseOverTarget;
            bool observedFresh = observed != null &&
                Time.frameCount - observedBodyMouseOverFrame <= 1;
            if (observedFresh)
                return observed;

            if (Input.GetMouseButton(0) && bodyHighlightTarget != null)
                return bodyHighlightTarget;

            CarLoader hoveredLoader = GetHoveredCarLoader(raycast);
            InteractiveObject bodyTarget;
            return TryGetBodySelectionHit(raycast, hoveredLoader,
                out bodyTarget) ? bodyTarget : null;
        }

        private static InteractiveObject ResolveSystemHoverTarget(
            Raycast raycast, InteractiveObject raycastObject)
        {
            Transform hitTransform = GetRaycastHitTransform(raycast);
            InteractiveObject hitObject = GetHitInteractiveObject(hitTransform);
            if (hitObject != null) {
                CarLoader hitLoader = GetCarLoader(hitObject);
                if (!IsBodyAggregateObject(hitLoader, hitObject))
                    return hitObject;
                return null;
            }

            InteractiveObject observed = observedMouseOverTarget;
            bool observedFresh = observed != null &&
                Time.frameCount - observedMouseOverFrame <= 1;
            if (!observedFresh || IsWholeCarBodyObject(observed))
                return null;

            CarLoader observedLoader = GetCarLoader(observed);
            if (IsBodyAggregateObject(observedLoader, observed))
                return null;
            return observed;
        }

        private static Transform GetRaycastHitTransform(Raycast raycast)
        {
            try {
                return raycast != null ? raycast.hit.transform : null;
            } catch {
                return null;
            }
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
            if (!GlobalState.IsGarageSceneActive ||
                !examineModeSessionActive || !IsExamineGarageModeActive()) {
                capturedExamineBody = null;
                ClearInspectionSystemsOverlay();
                HideInspectionFooterForModeExit();
                HideInspectionResetHints();
                ClearBodyHighlight();
                ClearSystemIndicator();
                return;
            }

            CaptureInspectionResetHintSource();
            EnsureInspectionResetHintSource();
            ShowInspectionSystemsHint();
            LayoutInspectionResetHints();
            UpdateInspectionSystemListUi(raycast);
            if (inspectionSystemsOverlayActive) {
                capturedExamineBody = null;
                capturedExamineSystem = null;
                HideInspectionExamineHint();
                UpdateInspectionResetHints(inspectionSystemsOverlayLoader,
                    null, false);
                ShowInspectionSystemsHint();
                LayoutInspectionResetHints();
                return;
            }
            InteractiveObject bodyObject = capturedExamineBody;
            capturedExamineBody = null;
            if (bodyObject != null) {
                ClearEmptySystemHighlight();
                CarLoader bodyLoader = GetCarLoader(bodyObject);
                if (bodyLoader == null)
                    bodyLoader = GetHoveredCarLoader(raycast);
                if (bodyLoader != null) {
                    DumpInspectionSystems(bodyLoader);
                    ShowBodyHover(bodyLoader, bodyObject);
                    UpdateInspectionResetHints(bodyLoader, bodyObject, true);
                    UpdateBodyHold(bodyLoader);
                    return;
                }
                HideInspectionActionHints();
            }
            ClearBodyHighlight();

            InteractiveObject interactiveObject = ResolveSystemHoverTarget(
                raycast, raycast != null ? raycast.iO : null);
            capturedExamineSystem = null;
            if (interactiveObject == null ||
                IsWholeCarBodyObject(interactiveObject)) {
                CarLoader hoverLoader = GetHoveredCarLoader(raycast);
                if (hoverLoader == null)
                    hoverLoader = inspectionResetLoader;
                UpdateInspectionResetHints(hoverLoader, null, false);
                ClearEmptySystemHighlight();
                ClearSystemIndicator();
                return;
            }
            if (!UsesSharpEyeInspection(interactiveObject)) {
                CarLoader nativeLoader = GetCarLoader(interactiveObject);
                if (nativeLoader == null)
                    nativeLoader = GetHoveredCarLoader(raycast);
                UpdateInspectionResetHints(nativeLoader, null, false);
                return;
            }
            UpdateEmptySystemHighlight(interactiveObject);

            CarLoader loader = GetCarLoader(interactiveObject);
            if (loader == null)
                loader = GetHoveredCarLoader(raycast);
            if (loader == null) {
                HideInspectionActionHints();
                ClearSystemIndicator();
                return;
            }

            DumpInspectionSystems(loader);
            CacheHoveredSystemPart(raycast, interactiveObject, loader);
            int examined;
            int total;
            GetSystemProgress(interactiveObject, out examined, out total);
            if (total <= 0) {
                HideInspectionActionHints();
                ClearSystemIndicator();
                return;
            }
            UpdateInspectionResetHints(loader, interactiveObject, false);
            if (examined >= total)
                HideCompletedNativeExamineHint(interactiveObject);

            SystemPassState state = GetSystemPassState(interactiveObject);
            if (state != null && state.CustomContinuationActive)
                EnsureCustomContinuation(interactiveObject, state);
            if (state != null) {
                UpdateCarrierVisualHold(state);
                UpdateManualCustomHold(interactiveObject, state);
                UpdateNativeVisualHold(interactiveObject, state);
            }
            UpdateSystemIndicator(interactiveObject);
            GetSystemProgress(interactiveObject, out examined, out total);
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
                        if (candidate == null ||
                            GetRequiredInspectionSkillLevel(candidate) <= 1)
                            continue;
                        CarLoader candidateLoader = GetCarLoader(candidate);
                        if (candidateLoader == null)
                            continue;
                        try {
                            if (candidateLoader.GetInstanceID() == loaderId)
                                cached.Add(candidate);
                        } catch {
                        }
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

        private static void DumpInspectionSystems(CarLoader loader)
        {
            if (loader == null)
                return;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return;
            }
            if (!DumpedInspectionLoaderIds.Add(loaderId))
                return;

            try {
                UnhollowerBaseLib.Il2CppReferenceArray<UnityEngine.Object> allSystems =
                    Resources.FindObjectsOfTypeAll(
                        UnhollowerRuntimeLib.Il2CppType.Of<InteractiveObject>());
                List<InteractiveObject> systems =
                    new List<InteractiveObject>();
                int targetLoaderId = loader.GetInstanceID();
                if (allSystems != null) {
                    for (int index = 0; index < allSystems.Length; index++) {
                        UnityEngine.Object rawCandidate = allSystems[index];
                        InteractiveObject candidate = rawCandidate != null ?
                            rawCandidate.TryCast<InteractiveObject>() : null;
                        if (candidate == null)
                            continue;
                        CarLoader candidateLoader = GetCarLoader(candidate);
                        if (candidateLoader == null)
                            continue;
                        try {
                            if (candidateLoader.GetInstanceID() ==
                                targetLoaderId)
                                systems.Add(candidate);
                        } catch {
                        }
                    }
                }

                systems.Sort(delegate(InteractiveObject left,
                    InteractiveObject right) {
                    return string.Compare(SafeIoId(left), SafeIoId(right),
                        StringComparison.OrdinalIgnoreCase);
                });
                if (!InspectionSystemsCache.ContainsKey(loaderId)) {
                    List<InteractiveObject> inspectionSystems =
                        new List<InteractiveObject>();
                    for (int index = 0; index < systems.Count; index++) {
                        InteractiveObject candidate = systems[index];
                        if (candidate != null &&
                            GetRequiredInspectionSkillLevel(candidate) > 1)
                            inspectionSystems.Add(candidate);
                    }
                    inspectionSystems.Sort(CompareInspectionSystems);
                    InspectionSystemsCache[loaderId] = inspectionSystems;
                }

                LogDiagnostic("inspection systems begin car=" +
                    SafeLoaderName(loader) + " count=" +
                    systems.Count.ToString(CultureInfo.InvariantCulture));
                for (int index = 0; index < systems.Count; index++) {
                    InteractiveObject system = systems[index];
                    if (system == null)
                        continue;
                    int nativeTotal = GetRawNativeSystemPartCount(system);
                    int nativeExamined =
                        GetRawNativeSystemExaminedCount(system);
                    bool active = false;
                    try {
                        active = system.gameObject != null &&
                            system.gameObject.activeInHierarchy;
                    } catch {
                    }
                    bool body = IsWholeCarBodyObject(system);
                    LogDiagnostic("inspection system io=" + SafeIoId(system) +
                        " native=" +
                        nativeExamined.ToString(CultureInfo.InvariantCulture) +
                        "/" +
                        nativeTotal.ToString(CultureInfo.InvariantCulture) +
                        " active=" + active.ToString() + " body=" +
                        body.ToString());
                    if (body)
                        LogBodyInteractionSurface(system);
                }
                LogDiagnostic("inspection systems end car=" +
                    SafeLoaderName(loader));
            } catch (Exception exception) {
                LogDiagnostic("inspection systems failed car=" +
                    SafeLoaderName(loader) + " exception=" +
                    exception.GetType().Name);
            }
        }

        private static void LogBodyInteractionSurface(
            InteractiveObject body)
        {
            if (body == null)
                return;
            try {
                int colliderCount = 0;
                int rendererCount = 0;
                int enabledColliders = 0;
                int enabledRenderers = 0;
                foreach (Collider collider in
                    body.GetComponentsInChildren<Collider>(true)) {
                    if (collider == null)
                        continue;
                    colliderCount++;
                    if (collider.enabled)
                        enabledColliders++;
                }
                foreach (Renderer renderer in
                    body.GetComponentsInChildren<Renderer>(true)) {
                    if (renderer == null)
                        continue;
                    rendererCount++;
                    if (renderer.enabled)
                        enabledRenderers++;
                }
                LogDiagnostic("body interaction io=" + SafeIoId(body) +
                    " layer=" + body.gameObject.layer.ToString(
                        CultureInfo.InvariantCulture) +
                    " colliders=" +
                        colliderCount.ToString(CultureInfo.InvariantCulture) +
                    " enabledColliders=" +
                        enabledColliders.ToString(CultureInfo.InvariantCulture) +
                    " renderers=" +
                        rendererCount.ToString(CultureInfo.InvariantCulture) +
                    " enabledRenderers=" +
                        enabledRenderers.ToString(CultureInfo.InvariantCulture));
            } catch (Exception exception) {
                LogDiagnostic("body interaction failed io=" + SafeIoId(body) +
                    " exception=" + exception.GetType().Name);
            }
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

        private static void LogExamineHoverProbe(Raycast raycast,
            InteractiveObject current)
        {
            try {
                InteractiveObject gameHover = null;
                GameScript game = GameScript.Get();
                if (game != null)
                    gameHover = game.IOMouseOverIO;

                Transform hitTransform = null;
                try {
                    if (raycast != null)
                        hitTransform = raycast.hit.transform;
                } catch {
                }

                string hitName = hitTransform != null ?
                    hitTransform.name : "<null>";
                string hierarchy = string.Empty;
                Transform cursor = hitTransform;
                int depth = 0;
                while (cursor != null && depth < 12) {
                    InteractiveObject io = null;
                    try {
                        io = cursor.GetComponent<InteractiveObject>();
                    } catch {
                    }
                    if (io != null) {
                        if (hierarchy.Length > 0)
                            hierarchy += ">";
                        hierarchy += cursor.name + ":" + SafeIoId(io);
                    }
                    cursor = cursor.parent;
                    depth++;
                }
                if (hierarchy.Length == 0)
                    hierarchy = "<none>";

                LogDiagnostic("examine hover probe current=" +
                    SafeIoId(current) + " gameHover=" +
                    SafeIoId(gameHover) + " hit=" + hitName +
                    " ioHierarchy=" + hierarchy);
            } catch (Exception exception) {
                LogDiagnostic("examine hover probe failed exception=" +
                    exception.GetType().Name);
            }
        }

        private static void UpdateEmptySystemHighlight(
            InteractiveObject system)
        {
            if (system == null || IsWholeCarBodyObject(system)) {
                ClearEmptySystemHighlight();
                return;
            }

            int examined;
            int total;
            GetSystemProgress(system, out examined, out total);
            if (total <= 0) {
                ClearEmptySystemHighlight();
                return;
            }
            SystemPassState state = GetSystemPassState(system);
            bool hasMountedParts = state != null && state.HasMountedParts;

            bool changed = emptySystemHighlightTarget == null ||
                emptySystemHighlightTarget.GetInstanceID() !=
                    system.GetInstanceID();
            if (changed)
                ClearEmptySystemHighlight();
            emptySystemHighlightTarget = system;
            try {
                CacheCurrentSystemName(system);
                system.SetMouseOver(true, examined >= total ? Color.green :
                    Color.yellow);
                if (!hasMountedParts) {
                    SetEmptySystemMouseOverLabel(system);
                    if (changed) {
                        LogDiagnostic("empty system outline force system=" +
                            SafeIoId(system) + " total=" +
                            total.ToString(CultureInfo.InvariantCulture));
                    }
                }
            } catch (Exception exception) {
                if (changed)
                    LogDiagnostic("empty system outline failed system=" +
                        SafeIoId(system) + " exception=" +
                        exception.GetType().Name);
            }
        }

        private static void CacheCurrentSystemName(
            InteractiveObject system)
        {
            if (system == null)
                return;
            try {
                GameScript game = GameScript.Get();
                InteractiveObject current = game != null ?
                    game.IOMouseOverIO : null;
                if (current == null || current.GetInstanceID() !=
                    system.GetInstanceID())
                    return;

                UIManager ui = UIManager.Get();
                string value = ToText(ReadMember(
                    ReadMember(ui, "TextDescription"), "text"));
                if (!IsUserFacingSystemName(value) ||
                    LooksLikeTechnicalSystemName(system, value))
                    return;
                SystemNameCache[system.GetInstanceID()] = value;
            } catch {
            }
        }

        private static void SetEmptySystemMouseOverLabel(
            InteractiveObject system)
        {
            if (system == null)
                return;
            try {
                GameScript game = GameScript.Get();
                if (game == null)
                    return;

                string displayName = GetLocalizedSystemName(system);
                if (!string.IsNullOrEmpty(displayName)) {
                    try {
                        suppressMouseOverObservation = true;
                        game.SetIOMouseOver(system.gameObject, displayName,
                            system);
                    } finally {
                        suppressMouseOverObservation = false;
                    }
                    SetMouseOverDescription(displayName);
                }
            } catch {
            }
        }

        private static void SetMouseOverDescription(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return;
            try {
                UIManager ui = UIManager.Get();
                UnityEngine.UI.Text text = ui != null ?
                    ReadMember(ui, "TextDescription") as UnityEngine.UI.Text :
                    null;
                if (text != null)
                    text.text = displayName;
            } catch {
            }
        }

        private static void ClearEmptySystemHighlight()
        {
            if (emptySystemHighlightTarget == null)
                return;
            try {
                emptySystemHighlightTarget.SetMouseOver(false);
            } catch {
            }
            emptySystemHighlightTarget = null;
        }

        private static void ShowBodyHover(CarLoader loader,
            InteractiveObject bodyObject)
        {
            if (loader == null || bodyObject == null)
                return;

            string displayName = GetCarDisplayName(loader);
            BodyPassState bodyState = GetBodyPassState(loader);
            bool bodyComplete = bodyState != null && bodyState.Total > 0 &&
                bodyState.ExaminedSlots.Count >= bodyState.Total;

            int loaderId = loader.GetInstanceID();
            bool changed = bodyHighlightLoaderId != loaderId;
            if (changed)
                ClearBodyHighlight();
            bodyHighlightTarget = bodyObject;
            if (changed || activeBodyHighlightTargets.Count == 0) {
                List<InteractiveObject> targets =
                    GetBodyHighlightTargets(loader);
                bool previousObservationSuppression =
                    suppressMouseOverObservation;
                try {
                    suppressMouseOverObservation = true;
                    for (int index = 0; index < targets.Count; index++) {
                        InteractiveObject target = targets[index];
                        if (target == null)
                            continue;
                        try {
                            target.SetMouseOver(true, bodyComplete ?
                                Color.green : Color.yellow);
                            activeBodyHighlightTargets.Add(target);
                        } catch {
                        }
                    }
                } finally {
                    suppressMouseOverObservation =
                        previousObservationSuppression;
                }
                bodyHighlightLoaderId = loaderId;
            }
            SetMouseOverDescription(!string.IsNullOrEmpty(displayName) ?
                displayName : GetBodyDisplayName());
            if (changed)
                LogDiagnostic("body hover active io=" + SafeIoId(bodyObject));
            UpdateBodyIndicator(loader, bodyObject);
        }

        private static void ClearBodyHighlight()
        {
            bool previousObservationSuppression =
                suppressMouseOverObservation;
            try {
                suppressMouseOverObservation = true;
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
            } finally {
                suppressMouseOverObservation =
                    previousObservationSuppression;
            }
            activeBodyHighlightTargets.Clear();
            bodyHighlightLoaderId = -1;
            bodyHighlightTarget = null;
        }

        private static string GetCarDisplayName(CarLoader loader)
        {
            if (loader == null)
                return string.Empty;

            int loaderId;
            try {
                loaderId = loader.GetInstanceID();
            } catch {
                return string.Empty;
            }
            string cached;
            if (CarDisplayNameCache.TryGetValue(loaderId, out cached))
                return cached;

            string carId = ToText(ReadMember(loader, "carToLoad"));
            try {
                GameManager manager = GlobalState.GameManager;
                if (manager == null)
                    manager = Singleton<GameManager>.Instance;
                if (manager != null && manager.CarBundleLoader != null &&
                    !string.IsNullOrEmpty(carId)) {
                    foreach (CarConfigData car in
                        manager.CarBundleLoader.CarNamesData) {
                        if (car == null || !string.Equals(car.CarID, carId,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrEmpty(car.CarName)) {
                            CarDisplayNameCache[loaderId] = car.CarName;
                            return car.CarName;
                        }
                        break;
                    }
                }
            } catch {
            }

            string name = ToText(ReadMember(loader, "CarName"));
            if (string.IsNullOrEmpty(name))
                name = ToText(ReadMember(loader, "carName"));
            if (string.IsNullOrEmpty(name)) {
                try {
                    name = loader.GetName();
                } catch {
                }
            }
            if (string.IsNullOrEmpty(name)) {
                try {
                    name = loader.CarBrand;
                } catch {
                }
            }
            if (string.IsNullOrEmpty(name))
                name = carId ?? string.Empty;
            CarDisplayNameCache[loaderId] = name;
            return name;
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
            CarLoader loader, out InteractiveObject bodyTarget)
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
            try {
                if (raycast.hit.transform != null) {
                    Vector3 delta = raycast.hit.point - origin;
                    if (delta.sqrMagnitude > 0.000001f)
                        direction = delta.normalized;
                    mechanicalDistance = raycast.hit.distance;
                }
            } catch {
            }
            if (direction.sqrMagnitude <= 0.000001f)
                return false;

            Ray ray = new Ray(origin, direction);
            float maxDistance = mechanicalDistance < float.MaxValue ?
                mechanicalDistance : 25f;
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
            if (mechanicalDistance < float.MaxValue &&
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

        private static void UpdateBodyHold(CarLoader loader)
        {
            BodyPassState state = GetBodyPassState(loader);
            if (state == null || state.Total <= 0) {
                if (state != null)
                    ResetBodyHold(state);
                return;
            }
            if (state.ExaminedSlots.Count >= state.Total) {
                ResetBodyHold(state);
                return;
            }
            if (!Input.GetMouseButton(0)) {
                ResetBodyHold(state);
                return;
            }

            state.HoldProgress = Mathf.Clamp01(state.HoldProgress +
                Time.deltaTime / GetSharpEyeHoldSeconds());
            SetSharpEyeCursorFill(state.HoldProgress);
            if (state.HoldProgress < 1f)
                return;

            state.HoldProgress = 0f;
            SetSharpEyeCursorFill(0f);
            if (ProcessOneBodyStep(loader, state)) {
                MarkInspectionProgressChanged();
                if (state.ExaminedSlots.Count >= state.Total)
                    ClearBodyHighlight();
                PlaySwitchModeSound();
            }
        }

        private static bool UpdateInspectionResetInput(Raycast raycast,
            InteractiveObject target, bool bodyTarget)
        {
            if (Input.GetMouseButton(0)) {
                inspectionSystemResetHoldProgress = 0f;
                inspectionVehicleResetHoldProgress = 0f;
                inspectionResetTargetId = -1;
                return false;
            }

            CarLoader loader = target != null ? GetCarLoader(target) : null;
            if (loader == null)
                loader = GetHoveredCarLoader(raycast);
            if (loader != null)
                inspectionResetLoader = loader;
            else
                loader = inspectionResetLoader;

            bool targetHasProgress = target != null && loader != null &&
                HasInspectionProgress(loader, target, bodyTarget);
            bool vehicleHasProgress = loader != null &&
                HasVehicleInspectionProgress(loader);
            bool resetAllDown = Input.GetKey(KeyCode.Space);
            if (!resetAllDown || !vehicleHasProgress) {
                inspectionVehicleResetHoldProgress = 0f;
                inspectionVehicleResetTriggered = false;
            } else if (!inspectionVehicleResetTriggered) {
                inspectionVehicleResetHoldProgress += Time.deltaTime;
                if (inspectionVehicleResetHoldProgress >=
                    GetSharpEyeHoldSeconds()) {
                    ResetVehicleInspection(loader);
                    inspectionVehicleResetTriggered = true;
                    inspectionVehicleResetHoldProgress = 0f;
                    inspectionSystemResetHoldProgress = 0f;
                    inspectionResetTargetId = -1;
                    return true;
                }
            }

            bool resetSystemDown = Input.GetKey(KeyCode.LeftAlt);
            if (!resetSystemDown) {
                inspectionSystemResetHoldProgress = 0f;
                inspectionSystemResetTriggered = false;
                inspectionResetTargetId = -1;
                return false;
            }
            if (!targetHasProgress ||
                (!bodyTarget && !UsesSharpEyeInspection(target))) {
                inspectionSystemResetHoldProgress = 0f;
                inspectionResetTargetId = -1;
                return false;
            }

            int targetId;
            try {
                targetId = target.GetInstanceID();
            } catch {
                return false;
            }
            if (inspectionResetTargetId != targetId) {
                inspectionResetTargetId = targetId;
                inspectionSystemResetHoldProgress = 0f;
                inspectionSystemResetTriggered = false;
            }
            if (inspectionSystemResetTriggered)
                return false;

            inspectionSystemResetHoldProgress += Time.deltaTime;
            if (inspectionSystemResetHoldProgress < GetSharpEyeHoldSeconds())
                return false;

            ResetInspectionTarget(loader, target, bodyTarget);
            inspectionSystemResetTriggered = true;
            inspectionSystemResetHoldProgress = 0f;
            return true;
        }

        private static void ResetInspectionTarget(CarLoader loader,
            InteractiveObject target, bool bodyTarget)
        {
            if (target == null)
                return;
            RestoreAllCarriers();
            if (bodyTarget) {
                if (loader != null)
                    ResetBodyInspection(loader);
                LogDiagnostic("inspection reset body car=" +
                    SafeLoaderName(loader));
            } else {
                ResetSystemInspection(target);
                LogDiagnostic("inspection reset system io=" +
                    SafeIoId(target));
            }
            repeatClickArmed = false;
            MarkInspectionProgressChanged();
            ClearSystemIndicator();
            ClearBodyHighlight();
            PlaySwitchModeSound();
        }

        private static void ResetVehicleInspection(CarLoader loader)
        {
            if (loader == null)
                return;
            RestoreAllCarriers();
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
            repeatClickArmed = false;
            MarkInspectionProgressChanged();
            ClearSystemIndicator();
            ClearBodyHighlight();
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

            SystemPassState state;
            if (SystemPassStates.TryGetValue(systemId, out state))
                RestoreCarrier(state);
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (part == null || IsUnmountedPart(part))
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
            try {
                part.SwitchSolidColor(false);
            } catch {
            }
        }

        private static void ResetInspectionResetInput(bool destroyHints)
        {
            inspectionResetTargetId = -1;
            inspectionSystemResetHoldProgress = 0f;
            inspectionVehicleResetHoldProgress = 0f;
            inspectionSystemResetTriggered = false;
            inspectionVehicleResetTriggered = false;
            inspectionResetLoader = null;
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

        private static void UpdateInspectionSystemListUi(Raycast raycast)
        {
            if (!inspectionSystemsOverlayActive) {
                if (inspectionSystemListVisible)
                    DestroyInspectionSystemList();
                return;
            }

            inspectionSystemListVisible = true;
            CarLoader loader = inspectionSystemsOverlayLoader;
            if (loader == null)
                loader = GetHoveredCarLoader(raycast);
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
                    entries.Count * InspectionSystemListRowHeight +
                    (entries.Count - 1) * InspectionSystemListRowGap);
                UnityEngine.UI.Image panelImage =
                    panelObject.GetComponent<UnityEngine.UI.Image>();
                if (panelImage != null) {
                    panelImage.enabled = false;
                    panelImage.raycastTarget = false;
                }
                inspectionSystemListPanel = panelRect;

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
            internal bool Available;
            internal bool Completed;
        }

        private static List<InspectionSystemListEntry>
            GetInspectionSystemListEntries(CarLoader loader)
        {
            List<InspectionSystemListEntry> entries =
                new List<InspectionSystemListEntry>();
            int skillLevel = GetInspectionSkillLevel();
            BodyPassState bodyState = GetBodyPassState(loader);
            if (bodyState != null && bodyState.Total > 0) {
                entries.Add(new InspectionSystemListEntry {
                    Name = GetBodyDisplayName(),
                    Available = skillLevel >= 1,
                    Completed = bodyState.ExaminedSlots.Count >= bodyState.Total
                });
            }

            List<InteractiveObject> systems = GetInspectionSystems(loader);
            for (int index = 0; index < systems.Count; index++) {
                InteractiveObject system = systems[index];
                if (system == null)
                    continue;
                int examined;
                int total;
                GetSystemProgress(system, out examined, out total);
                if (total <= 0)
                    continue;
                int requiredLevel = GetRequiredInspectionSkillLevel(system);
                entries.Add(new InspectionSystemListEntry {
                    Name = GetLocalizedSystemName(system),
                    Available = skillLevel >= requiredLevel,
                    Completed = examined >= total
                });
            }
            return entries;
        }

        private static void AddInspectionSystemListRow(
            RectTransform panelRect, UnityEngine.UI.Image backgroundSource,
            UnityEngine.UI.Text textSource, InspectionSystemListEntry entry,
            int index)
        {
            GameObject rowObject = UnityEngine.Object.Instantiate(
                backgroundSource.gameObject) as GameObject;
            if (rowObject == null)
                return;
            rowObject.name = "System_" +
                (index + 1).ToString(CultureInfo.InvariantCulture);
            RectTransform rowRect = rowObject.GetComponent<RectTransform>();
            UnityEngine.UI.Image rowImage =
                rowObject.GetComponent<UnityEngine.UI.Image>();
            if (rowRect == null || rowImage == null) {
                UnityEngine.Object.Destroy(rowObject);
                return;
            }
            rowRect.SetParent(panelRect, false);
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchoredPosition = new Vector2(0f,
                -index * (InspectionSystemListRowHeight +
                    InspectionSystemListRowGap));
            rowRect.sizeDelta = new Vector2(0f,
                InspectionSystemListRowHeight);
            rowImage.raycastTarget = false;

            Color stateColor = !entry.Available ?
                inspectionSystemListUnavailableColor : entry.Completed ?
                inspectionSystemListCompletedColor : Color.white;
            Color stateTextColor = entry.Available && !entry.Completed ?
                Color.black : Color.white;
            AddInspectionSystemListTag(rowRect, backgroundSource, textSource,
                (index + 1).ToString(CultureInfo.InvariantCulture),
                stateColor, stateTextColor, true);
            AddInspectionSystemListName(rowRect, textSource, entry.Name);
            string statusKey = !entry.Available ?
                "LOC_SharpEyeSystemUnavailable" : entry.Completed ?
                "LOC_SharpEyeSystemInspected" :
                "LOC_SharpEyeSystemNotInspected";
            AddInspectionSystemListTag(rowRect, backgroundSource, textSource,
                GetInspectionHintLabel(statusKey), stateColor, stateTextColor,
                false);
            rowObject.SetActive(true);
        }

        private static void AddInspectionSystemListTag(RectTransform rowRect,
            UnityEngine.UI.Image backgroundSource,
            UnityEngine.UI.Text textSource, string value, Color background,
            Color foreground, bool number)
        {
            GameObject tagObject = UnityEngine.Object.Instantiate(
                backgroundSource.gameObject) as GameObject;
            if (tagObject == null)
                return;
            tagObject.name = number ? "Number" : "Status";
            RectTransform tagRect = tagObject.GetComponent<RectTransform>();
            UnityEngine.UI.Image tagImage =
                tagObject.GetComponent<UnityEngine.UI.Image>();
            if (tagRect == null || tagImage == null) {
                UnityEngine.Object.Destroy(tagObject);
                return;
            }
            tagRect.SetParent(rowRect, false);
            tagRect.anchorMin = new Vector2(number ? 0f : 1f, 0f);
            tagRect.anchorMax = new Vector2(number ? 0f : 1f, 1f);
            tagRect.pivot = new Vector2(number ? 0f : 1f, 0.5f);
            tagRect.anchoredPosition = Vector2.zero;
            tagRect.sizeDelta = new Vector2(number ?
                InspectionSystemListNumberWidth :
                InspectionSystemListStatusWidth, 0f);
            tagImage.color = background;
            tagImage.raycastTarget = false;

            GameObject textObject = UnityEngine.Object.Instantiate(
                textSource.gameObject) as GameObject;
            if (textObject == null)
                return;
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            UnityEngine.UI.Text text =
                textObject.GetComponent<UnityEngine.UI.Text>();
            if (textRect == null || text == null) {
                UnityEngine.Object.Destroy(textObject);
                return;
            }
            textRect.SetParent(tagRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            text.text = value;
            text.fontSize = number ? 9 : 8;
            text.alignment = TextAnchor.MiddleCenter;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 7;
            text.resizeTextMaxSize = number ? 9 : 8;
            text.color = foreground;
            text.raycastTarget = false;
            if (text.canvasRenderer != null)
                text.canvasRenderer.SetColor(foreground);
            textObject.SetActive(true);
            tagObject.SetActive(true);
        }

        private static void AddInspectionSystemListName(RectTransform rowRect,
            UnityEngine.UI.Text textSource, string name)
        {
            GameObject textObject = UnityEngine.Object.Instantiate(
                textSource.gameObject) as GameObject;
            if (textObject == null)
                return;
            textObject.name = "Name";
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            UnityEngine.UI.Text text =
                textObject.GetComponent<UnityEngine.UI.Text>();
            if (textRect == null || text == null) {
                UnityEngine.Object.Destroy(textObject);
                return;
            }
            textRect.SetParent(rowRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(
                InspectionSystemListNumberWidth + 6f, 0f);
            textRect.offsetMax = new Vector2(
                -InspectionSystemListStatusWidth - 6f, 0f);
            string displayName = string.IsNullOrEmpty(name) ?
                "?" : name.Replace("\r", " ").Replace("\n", " ");
            text.text = displayName.ToUpperInvariant();
            text.fontSize = 9;
            text.alignment = TextAnchor.MiddleLeft;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 7;
            text.resizeTextMaxSize = 9;
            text.color = Color.white;
            text.raycastTarget = false;
            if (text.canvasRenderer != null)
                text.canvasRenderer.SetColor(Color.white);
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
            InteractiveObject target, bool bodyTarget)
        {
            if (loader == null) {
                HideInspectionActionHints();
                return;
            }
            if (Input.GetMouseButton(0)) {
                HideInspectionActionHints();
                SuppressInspectionFooterForHold();
                return;
            }

            bool hasTargetProgress = target != null &&
                HasInspectionProgress(loader, target, bodyTarget);
            bool canResetTarget = hasTargetProgress &&
                ((bodyTarget && GetInspectionSkillLevel() > 0) ||
                    (!bodyTarget && UsesSharpEyeInspection(target)));
            bool hasVehicleProgress = HasVehicleInspectionProgress(loader);
            bool showExamineHint = target != null &&
                ShouldShowFallbackExamineHint(loader, target, bodyTarget);

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
                    UiIntegrationBridge.SetNativeFooterHintHoldProgress(
                        inspectionExamineHint,
                        GetInspectionExamineHintProgress(loader, target,
                            bodyTarget));
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
                    UiIntegrationBridge.SetNativeFooterHintHoldProgress(
                        inspectionSystemResetHint,
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
                    UiIntegrationBridge.SetNativeFooterHintHoldProgress(
                        inspectionVehicleResetHint,
                        inspectionVehicleResetHoldProgress /
                            GetSharpEyeHoldSeconds());
                } else {
                    HideInspectionVehicleResetHint();
                }
            } else {
                HideInspectionResetActionHints();
            }

            ShowInspectionSystemsHint();
            LayoutInspectionResetHints();
        }

        private static void ShowInspectionSystemsHint()
        {
            if (inspectionResetHintSource == null)
                return;
            string label = GetInspectionHintLabel(
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
            if (!inspectionShowSystemsHintVisible ||
                !string.Equals(inspectionShowSystemsHintLabel, label,
                    StringComparison.Ordinal)) {
                UiIntegrationBridge.UpdateNativeFooterHint(
                    inspectionShowSystemsHint, label, true);
                ApplyInspectionHintVisualStyle(inspectionShowSystemsHint);
                inspectionShowSystemsHintLabel = label;
                inspectionShowSystemsHintVisible = true;
            }
        }

        private static bool ShouldShowFallbackExamineHint(CarLoader loader,
            InteractiveObject target, bool bodyTarget)
        {
            if (bodyTarget) {
                BodyPassState bodyState = GetBodyPassState(loader);
                return bodyState != null && bodyState.Total > 0 &&
                    bodyState.ExaminedSlots.Count < bodyState.Total;
            }
            if (!UsesSharpEyeInspection(target))
                return false;
            SystemPassState state = GetSystemPassState(target);
            if (state == null || state.Total <= 0)
                return false;
            int examined;
            int total;
            GetSystemProgress(target, out examined, out total);
            if (total <= 0 || examined >= total)
                return false;

            int nativeTotal = GetRawNativeSystemPartCount(target);
            int nativeExamined = GetRawNativeSystemExaminedCount(target);
            bool nativePending = nativeTotal > 0 &&
                nativeExamined < nativeTotal;
            if (!nativePending && !HasPendingCustomStep(target, state))
                return false;
            return !IsNativeExamineUiActive();
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
            int nativeTotal = GetRawNativeSystemPartCount(target);
            int nativeExamined = GetRawNativeSystemExaminedCount(target);
            LogDiagnostic("inspection examine hint show system=" +
                SafeIoId(target) + " progress=" +
                examined.ToString(CultureInfo.InvariantCulture) + "/" +
                total.ToString(CultureInfo.InvariantCulture) + " native=" +
                nativeExamined.ToString(CultureInfo.InvariantCulture) + "/" +
                nativeTotal.ToString(CultureInfo.InvariantCulture) +
                " customPending=" +
                (state != null && HasPendingCustomStep(target, state)).ToString());
        }

        private static float GetInspectionExamineHintProgress(
            CarLoader loader, InteractiveObject target, bool bodyTarget)
        {
            if (!Input.GetMouseButton(0))
                return 0f;
            if (bodyTarget) {
                BodyPassState bodyState = GetBodyPassState(loader);
                return bodyState != null ? bodyState.HoldProgress : 0f;
            }
            SystemPassState state = GetSystemPassState(target);
            if (state == null)
                return 0f;
            return Mathf.Max(state.ManualHoldProgress,
                Mathf.Max(state.CarrierVisualProgress,
                    state.NativeVisualProgress));
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

        private static bool HasInspectionProgress(CarLoader loader,
            InteractiveObject target, bool bodyTarget)
        {
            if (bodyTarget) {
                BodyPassState bodyState = GetBodyPassState(loader);
                return bodyState != null && bodyState.Total > 0 &&
                    bodyState.ExaminedSlots.Count > 0;
            }
            if (!UsesSharpEyeInspection(target))
                return false;
            int examined;
            int total;
            GetSystemProgress(target, out examined, out total);
            return total > 0 && examined > 0;
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

        private static void LayoutInspectionResetHints()
        {
            if (inspectionExamineHint != null &&
                inspectionExamineHint.Rect != null) {
                inspectionExamineHint.Rect.SetAsLastSibling();
                ApplyInspectionHintVisualStyle(inspectionExamineHint);
            }
            if (inspectionSystemResetHint != null &&
                inspectionSystemResetHint.Rect != null) {
                inspectionSystemResetHint.Rect.SetAsLastSibling();
                ApplyInspectionHintVisualStyle(inspectionSystemResetHint);
            }
            if (inspectionVehicleResetHint != null &&
                inspectionVehicleResetHint.Rect != null) {
                inspectionVehicleResetHint.Rect.SetAsLastSibling();
                ApplyInspectionHintVisualStyle(inspectionVehicleResetHint);
            }
            if (inspectionShowSystemsHint != null &&
                inspectionShowSystemsHint.Rect != null) {
                inspectionShowSystemsHint.Rect.SetAsLastSibling();
                ApplyInspectionHintVisualStyle(inspectionShowSystemsHint);
            }
            UpdateInspectionHintHostVisibility();
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

        private static void HideInspectionExamineHint()
        {
            if (!inspectionExamineHintVisible)
                return;
            UiIntegrationBridge.SetNativeFooterHintHoldProgress(
                inspectionExamineHint, 0f);
            UiIntegrationBridge.UpdateNativeFooterHint(
                inspectionExamineHint, inspectionExamineHintLabel ??
                    string.Empty, false);
            inspectionExamineHintVisible = false;
        }

        private static void HideInspectionSystemResetHint()
        {
            if (!inspectionSystemResetHintVisible)
                return;
            UiIntegrationBridge.SetNativeFooterHintHoldProgress(
                inspectionSystemResetHint, 0f);
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
            UiIntegrationBridge.SetNativeFooterHintHoldProgress(
                inspectionVehicleResetHint, 0f);
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
                inspectionShowSystemsHintLabel ??
                    GetInspectionHintLabel("LOC_SharpEyeShowSystems"), false);
            inspectionShowSystemsHintVisible = false;
        }

        private static void HideInspectionActionHints()
        {
            HideInspectionExamineHint();
            HideInspectionResetActionHints();
            UpdateInspectionHintHostVisibility();
        }

        private static void HideInspectionResetHints()
        {
            HideInspectionExamineHint();
            HideInspectionResetActionHints();
            HideInspectionShowSystemsHint();
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
            inspectionExamineHintLabel = null;
            inspectionSystemResetHintLabel = null;
            inspectionVehicleResetHintLabel = null;
            inspectionShowSystemsHintLabel = null;
        }

        private static void ResetBodyHold(BodyPassState state)
        {
            if (state == null)
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
            if (loader == null || slot == null || string.IsNullOrEmpty(slot.Id) ||
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

        private static void UpdateBodyIndicator(CarLoader loader,
            InteractiveObject bodyObject)
        {
            BodyPassState state = GetBodyPassState(loader);
            if (state == null || state.Total <= 0) {
                ClearSystemIndicator();
                return;
            }

            int examined = Math.Min(state.Total, state.ExaminedSlots.Count);
            string text = examined.ToString(CultureInfo.InvariantCulture) +
                " / " + state.Total.ToString(CultureInfo.InvariantCulture);
            try {
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.SetBonusTextDescription(text);
                indicatorSystem = bodyObject;
                indicatorText = text;
                indicatorDirty = false;
            } catch {
            }
        }

        private static bool IsExamineGarageModeActive()
        {
            try {
                GameMode mode = GameMode.Get();
                return mode != null &&
                    mode.GetCurrentMode() == gameMode.ExamineGarage;
            } catch {
                return false;
            }
        }

        internal static void ObserveMouseOverTarget(InteractiveObject target)
        {
            if (!GlobalState.IsGarageSceneActive ||
                !examineModeSessionActive || !IsExamineGarageModeActive() ||
                suppressMouseOverObservation)
                return;
            observedMouseOverTarget = target;
            observedMouseOverFrame = Time.frameCount;
            if (!IsWholeCarBodyObject(target)) {
                observedBodyMouseOverTarget = null;
                observedBodyMouseOverFrame = -1;
                return;
            }

            bool changed = observedBodyMouseOverTarget == null ||
                observedBodyMouseOverTarget.GetInstanceID() !=
                    target.GetInstanceID() ||
                Time.frameCount - observedBodyMouseOverFrame > 1;
            observedBodyMouseOverTarget = target;
            observedBodyMouseOverFrame = Time.frameCount;
            if (changed)
                LogDiagnostic("body mouseover observed io=" + SafeIoId(target));
        }

        internal static void HideBodyMouseOverLabel(InteractiveObject target)
        {
            if (!GlobalState.IsGarageSceneActive || target == null ||
                !IsWholeCarBodyObject(target) || IsExamineGarageModeActive())
                return;

            try {
                UIManager ui = UIManager.Get();
                UnityEngine.UI.Text text = ui != null ?
                    ReadMember(ui, "TextDescription") as UnityEngine.UI.Text : null;
                if (text != null)
                    text.text = string.Empty;
            } catch {
            }
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
            HideNativeExamineHint();
            if (inspectionHintHost == null ||
                inspectionHintHost.gameObject == null ||
                !inspectionHintHost.name.StartsWith("_HoldExamine",
                    StringComparison.Ordinal))
                return;
            if (!inspectionFooterHoldSuppressed) {
                inspectionFooterHoldWasActiveSelf =
                    inspectionHintHost.gameObject.activeSelf;
                inspectionFooterHoldSuppressed = true;
            }
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

        private static void HideCompletedNativeExamineHint(
            InteractiveObject target)
        {
            if (target != null && HideNativeExamineHint())
                LogDiagnostic("completed system native examine hint hidden " +
                    "system=" + SafeIoId(target));
        }

        internal static bool ShouldAllowNativeExamineHintShow(
            ControlDescription control)
        {
            if (!GlobalState.IsGarageSceneActive ||
                !examineModeSessionActive || !IsExamineGarageModeActive() ||
                control == null || control.gameObject == null ||
                control.transform == null || control.transform.parent == null ||
                !string.Equals(control.gameObject.name, "ControlDescription",
                    StringComparison.Ordinal) ||
                !control.transform.parent.name.StartsWith("_HoldExamine",
                    StringComparison.Ordinal))
                return true;

            string reason = null;
            InteractiveObject system = capturedExamineSystem;
            if (inspectionSystemsOverlayActive)
                reason = "overlay";
            else if (Input.GetMouseButton(0))
                reason = "hold";
            else if (system != null && UsesSharpEyeInspection(system) &&
                GetRawNativeSystemPartCount(system) <= 0)
                reason = "zero-native";

            if (reason == null) {
                inspectionNativeHintSuppressionDiagnosticKey = null;
                return true;
            }

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

        private static void CacheHoveredSystemPart(Raycast raycast,
            InteractiveObject interactiveObject, CarLoader loader)
        {
            if (interactiveObject == null || loader == null ||
                GetSystemSpecificationCount(interactiveObject) > 0)
                return;

            PartScript part = null;
            try {
                part = InvokeNoArgs(GameScript.Get(),
                    "GetPartMouseOver") as PartScript;
            } catch {
            }
            if (part == null && raycast != null) {
                part = ReadMember(raycast, "partScript") as PartScript ??
                    ReadMember(raycast, "PartScript") as PartScript;
            }
            if (part == null && raycast != null) {
                try {
                    Transform hitTransform = raycast.hit.transform;
                    if (hitTransform != null)
                        part = hitTransform.GetComponentInParent<PartScript>();
                } catch {
                }
            }
            if (part == null)
                return;

            try {
                CarLoader partLoader = part.GetComponentInParent<CarLoader>();
                if (partLoader != null &&
                    partLoader.GetInstanceID() != loader.GetInstanceID())
                    return;
            } catch {
            }

            int systemId = interactiveObject.GetInstanceID();
            SystemSinglePartFallback[systemId] = part;
            SystemSpecificationCountCache.Remove(systemId);
            SystemPassStates.Remove(systemId);
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
                if (part == null || IsUnmountedPart(part))
                    continue;
                state.HasMountedParts = true;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id))
                    continue;
                try {
                    if (part.IsExamined)
                        state.ExaminedPartInstanceIds.Add(part.GetInstanceID());
                } catch {
                }
            }
            List<string> specification = GetSystemSpecificationPartIds(system);
            state.Total = Math.Max(specification.Count,
                state.ExaminedPartInstanceIds.Count + CountUnexaminedPresentParts(
                    parts, state.ExaminedPartInstanceIds));
            SystemPassStates.Add(systemId, state);
            return state;
        }

        private static int CountUnexaminedPresentParts(
            List<PartScript> parts, HashSet<int> examinedPartInstanceIds)
        {
            if (parts == null)
                return 0;
            int count = 0;
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (part == null || IsUnmountedPart(part))
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id))
                    continue;
                if (examinedPartInstanceIds == null ||
                    !examinedPartInstanceIds.Contains(part.GetInstanceID()))
                    count++;
            }
            return count;
        }

        private static void AddNativeCarrierCandidate(SystemPassState state,
            PartScript part)
        {
            if (state == null || part == null)
                return;
            int instanceId = part.GetInstanceID();
            for (int index = 0; index < state.NativeCarrierCandidates.Count;
                index++) {
                PartScript current = state.NativeCarrierCandidates[index];
                if (current != null && current.GetInstanceID() == instanceId)
                    return;
            }
            state.NativeCarrierCandidates.Add(part);
        }

        private static void TryStartCustomContinuation(InteractiveObject system,
            SystemPassState state)
        {
            if (system == null || state == null ||
                !HasPendingCustomStep(system, state))
                return;

            int nativeTotal = GetRawNativeSystemPartCount(system);
            int nativeExamined = GetRawNativeSystemExaminedCount(system);
            if (nativeTotal > 0 && nativeExamined < nativeTotal)
                return;

            state.CustomContinuationActive = true;
            EnsureCustomContinuation(system, state);
            LogDiagnostic("custom continuation start system=" +
                SafeIoId(system) + " native=" +
                nativeExamined.ToString(CultureInfo.InvariantCulture) + "/" +
                nativeTotal.ToString(CultureInfo.InvariantCulture));
        }

        private static void EnsureCustomContinuation(InteractiveObject system,
            SystemPassState state)
        {
            if (system == null || state == null ||
                !state.CustomContinuationActive)
                return;
            if (!HasPendingCustomStep(system, state)) {
                state.CustomContinuationActive = false;
                RestoreCarrier(state);
                return;
            }
            if (state.CarrierPart != null)
                return;

            for (int index = 0; index < state.NativeCarrierCandidates.Count;
                index++) {
                if (TryArmCarrier(system, state,
                        state.NativeCarrierCandidates[index]))
                    return;
            }

            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript candidate = parts[index];
                if (candidate == null ||
                    !state.ExaminedPartInstanceIds.Contains(
                        candidate.GetInstanceID()))
                    continue;
                if (TryArmCarrier(system, state, candidate))
                    return;
            }

        }

        private static void UpdateCarrierVisualHold(SystemPassState state)
        {
            if (state == null || !state.CustomContinuationActive ||
                state.CarrierPart == null) {
                ResetCarrierVisualHold(state);
                return;
            }

            bool mouseDown = Input.GetMouseButton(0);
            if (!state.CarrierVisualStateLogged) {
                state.CarrierVisualStateLogged = true;
                LogDiagnostic("carrier visual update carrier=" +
                    SafePartId(state.CarrierPart) + " mouse=" +
                    mouseDown.ToString() + " nativeUi=" +
                    IsNativeExamineUiActive().ToString());
            }

            if (!mouseDown) {
                ResetCarrierVisualHold(state);
                return;
            }

            if (!state.CarrierVisualMouseDownLogged) {
                state.CarrierVisualMouseDownLogged = true;
                LogDiagnostic("carrier visual mouse down carrier=" +
                    SafePartId(state.CarrierPart));
            }

            state.CarrierVisualProgress = Mathf.Clamp01(
                state.CarrierVisualProgress + Time.deltaTime /
                GetSharpEyeHoldSeconds());
            SetSharpEyeCursorFill(state.CarrierVisualProgress);
        }

        private static void ResetCarrierVisualHold(SystemPassState state)
        {
            if (state == null)
                return;
            state.CarrierVisualProgress = 0f;
            if (state.ManualHoldProgress <= 0f)
                SetSharpEyeCursorFill(0f);
        }

        private static void UpdateNativeVisualHold(
            InteractiveObject system, SystemPassState state)
        {
            if (system == null || state == null ||
                state.CustomContinuationActive) {
                ResetNativeVisualHold(state);
                return;
            }

            int nativeTotal = GetRawNativeSystemPartCount(system);
            int nativeExamined = GetRawNativeSystemExaminedCount(system);
            if (nativeTotal <= 0 || nativeExamined >= nativeTotal ||
                !Input.GetMouseButton(0)) {
                ResetNativeVisualHold(state);
                return;
            }

            state.NativeVisualProgress = Mathf.Clamp01(
                state.NativeVisualProgress + Time.deltaTime /
                GetSharpEyeHoldSeconds());
            SetSharpEyeCursorFill(state.NativeVisualProgress);
        }

        private static void ResetNativeVisualHold(SystemPassState state)
        {
            if (state == null)
                return;
            state.NativeVisualProgress = 0f;
            if (state.ManualHoldProgress <= 0f &&
                state.CarrierVisualProgress <= 0f)
                SetSharpEyeCursorFill(0f);
        }

        private static void UpdateManualCustomHold(InteractiveObject system,
            SystemPassState state)
        {
            if (system == null || state == null)
                return;

            int nativeTotal = GetRawNativeSystemPartCount(system);
            bool canUseManualDriver = nativeTotal <= 0;
            if (!canUseManualDriver && state.CustomContinuationActive &&
                state.CarrierPart == null)
                canUseManualDriver =
                    GetRawNativeSystemExaminedCount(system) >= nativeTotal;
            bool needsManualDriver = canUseManualDriver &&
                HasPendingCustomStep(system, state);
            if (!needsManualDriver) {
                ResetManualCustomHold(state);
                return;
            }

            state.CustomContinuationActive = true;
            if (!Input.GetMouseButton(0)) {
                ResetManualCustomHold(state);
                return;
            }

            state.ManualHoldProgress = Mathf.Clamp01(
                state.ManualHoldProgress + Time.deltaTime /
                GetSharpEyeHoldSeconds());
            SetSharpEyeCursorFill(state.ManualHoldProgress);
            if (state.ManualHoldProgress < 1f)
                return;

            state.ManualHoldProgress = 0f;
            SetSharpEyeCursorFill(0f);
            if (!ProcessOneCustomSystemStep(system)) {
                state.CustomContinuationActive = false;
                return;
            }

            state.PassStarted = true;
            indicatorDirty = true;
            PlaySwitchModeSound();
            LogDiagnostic("manual custom examine success system=" +
                SafeIoId(system));
        }

        private static void ResetManualCustomHold(SystemPassState state)
        {
            if (state == null)
                return;
            state.ManualHoldProgress = 0f;
            if (state.CarrierVisualProgress <= 0f)
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

        private static bool TryArmCarrier(InteractiveObject system,
            SystemPassState state, PartScript part)
        {
            if (system == null || state == null || part == null ||
                IsUnmountedPart(part))
                return false;
            try {
                if (!part.IsExamined)
                    return false;
            } catch {
                return false;
            }

            LogDiagnostic("carrier arm ENTER system=" +
                SafeIoId(system) + " part=" + SafePartId(part));
            int nativeTotal = GetRawNativeSystemPartCount(system);
            int before = GetRawNativeSystemExaminedCount(system);
            if (!WriteMember(part, "IsExamined", false)) {
                LogDiagnostic("carrier arm WRITE FAILED system=" +
                    SafeIoId(system) + " part=" + SafePartId(part));
                return false;
            }
            LogDiagnostic("carrier arm WRITTEN system=" +
                SafeIoId(system) + " part=" + SafePartId(part));
            if (nativeTotal > 0) {
                int after = GetRawNativeSystemExaminedCount(system);
                if (after >= before) {
                    WriteMember(part, "IsExamined", true);
                    return false;
                }
            }

            state.CarrierPart = part;
            state.CarrierVisualStateLogged = false;
            state.CarrierVisualMouseDownLogged = false;
            LogDiagnostic("custom continuation armed system=" +
                SafeIoId(system) + " carrier=" + SafePartId(part));
            return true;
        }

        private static void RestoreCarrier(SystemPassState state)
        {
            if (state == null || state.CarrierPart == null)
                return;
            PartScript carrier = state.CarrierPart;
            state.CarrierPart = null;
            ResetCarrierVisualHold(state);
            try {
                if (!carrier.IsExamined) {
                    LogDiagnostic("carrier restore ENTER part=" +
                        SafePartId(carrier));
                    WriteMember(carrier, "IsExamined", true);
                    LogDiagnostic("carrier restore RETURN part=" +
                        SafePartId(carrier));
                }
            } catch (Exception exception) {
                LogDiagnostic("carrier restore failed part=" +
                    SafePartId(carrier) + " exception=" +
                    exception.GetType().Name);
            }
        }

        private static void RestoreAllCarriers()
        {
            foreach (KeyValuePair<int, SystemPassState> pair in
                SystemPassStates)
                RestoreCarrier(pair.Value);
        }

        private static bool HasPendingCustomStep(InteractiveObject system,
            SystemPassState state)
        {
            if (system == null || state == null)
                return false;
            List<PartScript> parts = GetSystemParts(system);
            for (int index = 0; index < parts.Count; index++) {
                PartScript part = parts[index];
                if (part == null || IsUnmountedPart(part))
                    continue;
                string id = SafePartId(part);
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id))
                    continue;
                if (!state.ExaminedPartInstanceIds.Contains(part.GetInstanceID()))
                    return true;
            }

            List<string> specification = GetSystemSpecificationPartIds(system);
            List<int> missing = GetMissingSpecificationSlots(system,
                specification);
            for (int index = 0; index < missing.Count; index++) {
                if (!state.ExaminedMissingSlots.Contains(missing[index]))
                    return true;
            }
            return false;
        }

        private static int GetRawNativeSystemPartCount(InteractiveObject system)
        {
            if (system == null)
                return 0;
            try {
                return Math.Max(0, system.GetAmountOfParts());
            } catch {
                return 0;
            }
        }

        private static int GetRawNativeSystemExaminedCount(
            InteractiveObject system)
        {
            if (system == null)
                return 0;
            try {
                return Math.Max(0, system.GetAmountOfExaminedParts());
            } catch {
                return 0;
            }
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
                if (part == null || IsUnmountedPart(part))
                    continue;

                string id = SafePartId(part);
                int instanceId = part.GetInstanceID();
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id) ||
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
                        state.PassStarted = true;
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
                if (state.ExaminedMissingSlots.Contains(candidate))
                    continue;

                state.ExaminedMissingSlots.Add(candidate);
                slotIndex = candidate;
                partId = specification[candidate];
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
                if (part == null || IsUnmountedPart(part))
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
            if (loader == null || part == null)
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
            if (loader == null || key == null || string.IsNullOrEmpty(key.Id) ||
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
                string.IsNullOrEmpty(message))
                return;
            diagnosticLineCount++;
            try {
                File.AppendAllText(DiagnosticLogPath,
                    Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                    " " + message + Environment.NewLine);
            } catch {
            }
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

        private static string SafeLogText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("\r", " ").Replace("\n", " ");
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

        private static List<PurchaseKey> BuildSystemShoppingList(
            CarLoader loader, InteractiveObject system)
        {
            Dictionary<PurchaseKey, int> desired =
                new Dictionary<PurchaseKey, int>();
            List<PurchaseKey> order = new List<PurchaseKey>();
            Dictionary<string, int> wheelCounts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> partIds = GetSystemSpecificationPartIds(system);
            for (int index = 0; index < partIds.Count; index++) {
                string id = partIds[index];
                if (string.IsNullOrEmpty(id) || !IsPurchasablePart(id))
                    continue;
                if (IsWheelId(id)) {
                    int count;
                    wheelCounts.TryGetValue(id, out count);
                    wheelCounts[id] = count + 1;
                    continue;
                }
                AddDesired(desired, order,
                    new PurchaseKey(id, PurchaseKind.Part));
            }
            AppendSystemWheels(loader, wheelCounts, desired, order);
            return BuildRemainingShoppingQueue(desired, order);
        }

        private static List<PurchaseKey> BuildRemainingShoppingQueue(
            Dictionary<PurchaseKey, int> desired, List<PurchaseKey> order)
        {
            Dictionary<PurchaseKey, int> remaining =
                new Dictionary<PurchaseKey, int>(desired);
            SubtractPerfectInventoryParts(remaining);
            SubtractPerfectWarehouseParts(remaining);
            if (!SubtractCurrentShopList(remaining))
                SubtractObservedShopList(remaining);
            SubtractPendingShopList(remaining);

            List<PurchaseKey> queue = new List<PurchaseKey>();
            for (int index = 0; index < order.Count; index++) {
                PurchaseKey key = order[index];
                int count;
                if (!remaining.TryGetValue(key, out count) || count <= 0)
                    continue;
                for (int amount = 0; amount < count; amount++)
                    queue.Add(key);
            }
            return queue;
        }

        private static void QueueSystemShoppingList(CarLoader loader,
            InteractiveObject system)
        {
            if (loader == null || system == null || IsWholeCarBodyObject(system))
                return;

            try {
                List<PurchaseKey> additions =
                    BuildSystemShoppingList(loader, system);
                QueueShoppingListAdditions(additions);
            } catch (Exception exception) {
                ModLogger.Log("[SharpEye] Failed to prepare system shopping list." +
                    Environment.NewLine + exception, Types.LoggingLevels.Error);
            }
        }

        private static void QueueShoppingListAdditions(List<PurchaseKey> additions)
        {
            if (additions == null || additions.Count == 0)
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

        private static void SubtractPendingShopList(
            Dictionary<PurchaseKey, int> remaining)
        {
            foreach (KeyValuePair<PurchaseKey, int> pair in
                PendingShoppingListCounts) {
                int count;
                if (!remaining.TryGetValue(pair.Key, out count) || count <= 0)
                    continue;
                remaining[pair.Key] = Math.Max(0, count - pair.Value);
            }
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

        private static int GetSystemSpecificationCount(
            InteractiveObject interactiveObject)
        {
            if (interactiveObject == null)
                return 0;

            int instanceId = interactiveObject.GetInstanceID();
            int cached;
            if (SystemSpecificationCountCache.TryGetValue(instanceId,
                    out cached))
                return cached;

            int examined;
            int total;
            GetSystemProgress(interactiveObject, out examined, out total);
            if (total > 0)
                SystemSpecificationCountCache[instanceId] = total;
            return total;
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

        private static int GetNativeSystemPartCount(
            InteractiveObject interactiveObject)
        {
            if (interactiveObject == null)
                return 0;
            try {
                return Math.Max(0, interactiveObject.GetAmountOfParts());
            } catch {
                return 0;
            }
        }

        private static int GetNativeSystemExaminedCount(
            InteractiveObject interactiveObject)
        {
            if (interactiveObject == null)
                return 0;
            try {
                return Math.Max(0, interactiveObject.GetAmountOfExaminedParts());
            } catch {
                return 0;
            }
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
            PartScript fallbackPart;
            if (SystemSinglePartFallback.TryGetValue(
                    interactiveObject.GetInstanceID(), out fallbackPart) &&
                fallbackPart != null) {
                seen.Add(fallbackPart.GetInstanceID());
                result.Add(fallbackPart);
            }
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

        private static void UpdateSystemIndicator(InteractiveObject system)
        {
            if (system == null) {
                return;
            }
            if (!indicatorDirty && indicatorSystem != null &&
                indicatorSystem.GetInstanceID() == system.GetInstanceID() &&
                !string.IsNullOrEmpty(indicatorText)) {
                try {
                    UIManager ui = UIManager.Get();
                    if (ui != null)
                        ui.SetBonusTextDescription(indicatorText);
                } catch {
                }
                return;
            }

            int examined;
            int total;
            GetSystemProgress(system, out examined, out total);
            string text = examined.ToString(CultureInfo.InvariantCulture) +
                " / " + total.ToString(CultureInfo.InvariantCulture);
            try {
                UIManager ui = UIManager.Get();
                if (ui != null)
                    ui.SetBonusTextDescription(text);
                indicatorSystem = system;
                indicatorText = text;
                indicatorDirty = false;
            } catch {
            }
        }

        private static void ClearSystemIndicator(bool force = false)
        {
            ClearEmptySystemHighlight();
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

        private static void AppendSystemWheels(CarLoader loader,
            Dictionary<string, int> wheelCounts,
            Dictionary<PurchaseKey, int> desired, List<PurchaseKey> order)
        {
            if (loader == null || wheelCounts == null || wheelCounts.Count == 0)
                return;

            object wheels = InvokeNoArgs(loader, "GetWheels");
            VisitCollection(wheels, delegate(object value) {
                if (value == null)
                    return;

                int size = ToRoundedInt(ReadMember(value, "Size"));
                int width = ToRoundedInt(ReadMember(value, "Width"));
                int profile = ToRoundedInt(ReadMember(value, "Profile"));
                int et = ToRoundedInt(ReadMember(value, "ET"));
                string rimId = ToText(ReadMember(value, "Rim"));
                string tireId = ToText(ReadMember(value, "Tire"));

                int rimCount;
                if (!string.IsNullOrEmpty(rimId) &&
                    wheelCounts.TryGetValue(rimId, out rimCount) &&
                    rimCount > 0) {
                    AddDesired(desired, order, new PurchaseKey(rimId,
                        PurchaseKind.Rim, size, 0, 0, et));
                    wheelCounts[rimId] = rimCount - 1;
                }

                int tireCount;
                if (!string.IsNullOrEmpty(tireId) &&
                    wheelCounts.TryGetValue(tireId, out tireCount) &&
                    tireCount > 0) {
                    AddDesired(desired, order, new PurchaseKey(tireId,
                        PurchaseKind.Tire, size, width, profile));
                    wheelCounts[tireId] = tireCount - 1;
                }
            });
        }

        private static void AddDesired(Dictionary<PurchaseKey, int> desired,
            List<PurchaseKey> order, PurchaseKey key)
        {
            int count;
            if (desired.TryGetValue(key, out count)) {
                desired[key] = count + 1;
                return;
            }
            desired.Add(key, 1);
            order.Add(key);
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
                if (part == null || IsUnmountedPart(part) ||
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

        private static bool SubtractCurrentShopList(
            Dictionary<PurchaseKey, int> remaining)
        {
            UIManager ui = UIManager.Get();
            object shopListWindow = ReadMember(ui, "ShopListWindow");
            object items = ReadMember(shopListWindow, "items");
            if (items == null)
                return false;

            VisitCollection(items, delegate(object value) {
                if (value == null)
                    return;
                string id = ToText(ReadMember(value, "ID"));
                if (string.IsNullOrEmpty(id))
                    return;

                PurchaseKey key = CreateShopListKey(id,
                    ReadMember(value, "AdditionalData"));
                int count;
                if (key == null || !remaining.TryGetValue(key, out count) ||
                    count <= 0)
                    return;

                int amount = Math.Max(1, ToInt(ReadMember(value, "Amount"), 1));
                remaining[key] = Math.Max(0, count - amount);
            });
            return true;
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

        private static void SubtractObservedShopList(
            Dictionary<PurchaseKey, int> remaining)
        {
            foreach (KeyValuePair<PurchaseKey, int> pair in ObservedShopList) {
                int count;
                if (!remaining.TryGetValue(pair.Key, out count) || count <= 0)
                    continue;
                remaining[pair.Key] = Math.Max(0, count - pair.Value);
            }
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
    internal static class SharpEyeExamineRandomPartPatch
    {
        private static MethodBase TargetMethod()
        {
            return SharpEyeShoppingListFeature.FindExamineRandomPartMethod();
        }

        private static bool Prefix(InteractiveObject __instance, bool __0,
            ref bool __result)
        {
            return SharpEyeShoppingListFeature.HandleExamineRandomPartPrefix(
                __instance, __0, ref __result);
        }

        private static void Postfix(InteractiveObject __instance, bool __0,
            ref bool __result)
        {
            SharpEyeShoppingListFeature.HandleExamineRandomPart(__instance, __0,
                ref __result);
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

        private static void Prefix(PartScript __instance, out bool __state)
        {
            __state = false;
            try {
                __state = __instance != null && __instance.IsExamined;
            } catch {
            }
        }

        private static void Postfix(PartScript __instance, bool __0,
            bool __state)
        {
            SharpEyeShoppingListFeature.HandlePartExamine(__instance, __0,
                __state);
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
    internal static class SharpEyeBodyMouseOverLabelPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GameScript),
                nameof(GameScript.SetIOMouseOver),
                new Type[] { typeof(GameObject), typeof(string),
                    typeof(InteractiveObject) });
        }

        private static void Postfix(InteractiveObject __2)
        {
            SharpEyeShoppingListFeature.ObserveMouseOverTarget(__2);
            SharpEyeShoppingListFeature.HideBodyMouseOverLabel(__2);
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
