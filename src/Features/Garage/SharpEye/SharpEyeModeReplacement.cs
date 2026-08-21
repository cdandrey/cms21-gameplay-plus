using System.Globalization;
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
        private static class NativeInspectionModeReplacement
        {
            private static int lastTickFrame = -1;
            private static bool hasReturnMode;
            private static gameMode returnMode;
            private static int proximityLoaderId = -1;
            private static bool proximityReady;
            private static Bounds proximityLocalBounds;
            private static float proximityExitDistance;
            private static Transform proximityRoot;

            internal static void OnGameModeChanged(gameMode currentMode)
            {
                if (!IsInspectionSceneActive())
                    return;

                bool wasActive = examineModeSessionActive;
                bool active = IsInspectionMode(currentMode);
                if (!active && !IsTransientUiMode(currentMode)) {
                    returnMode = currentMode;
                    hasReturnMode = true;
                }
                examineModeSessionActive = active;
                LogDiagnostic("inspection mode replacement current=" +
                    currentMode.ToString() + " wasActive=" +
                    wasActive.ToString() + " active=" +
                    examineModeSessionActive.ToString());
                indicatorDirty = true;

                if (!wasActive && examineModeSessionActive) {
                    BeginSession();
                    return;
                }
                if (wasActive && !examineModeSessionActive)
                    EndSession();
            }

            internal static bool ShouldRunNativeRaycast(Raycast raycast)
            {
                capturedExamineSystem = null;
                if (!IsInspectionSceneActive() || !examineModeSessionActive ||
                    !IsExamineGarageModeActive() || raycast == null)
                    return true;

                return false;
            }

            internal static void Tick(Raycast raycast)
            {
                if (!IsInspectionSceneActive() || !examineModeSessionActive ||
                    !IsExamineGarageModeActive()) {
                    lastTickFrame = -1;
                    return;
                }
                if (lastTickFrame == Time.frameCount)
                    return;
                lastTickFrame = Time.frameCount;

                int skillLevel = GetInspectionSkillLevel();
                InspectionVisualSystem.EnsureInitialized(raycast, skillLevel);
                if (!InspectionVisualSystem.IsActive) {
                    capturedExamineSystem = null;
                    InspectionInputSystem.Reset();
                    InspectionVisualSystem.ClearHover();
                    InspectionUiSystem.Tick(new InspectionHoverState(),
                        new InspectionTargetState(), skillLevel);
                    InspectionPresentationSystem.Clear();
                    return;
                }
                if (TryExitForDistance(InspectionVisualSystem.Loader))
                    return;

                InspectionHoverState hover = InspectionHoverSystem.Resolve(
                    raycast, skillLevel);
                capturedExamineSystem = hover.System;
                InspectionTargetState targetState =
                    InspectionProcessSystem.GetTargetState(hover, skillLevel);
                LogInspectionOverlayRaycastProbe(raycast,
                    raycast != null ? raycast.iO : null, hover.System,
                    hover.Body, hover.Current);

                if (InspectionInputSystem.Tick(raycast, hover, targetState,
                        skillLevel)) {
                    targetState = InspectionProcessSystem.GetTargetState(
                        hover, skillLevel);
                    InspectionVisualSystem.SyncInspectionState();
                    InspectionVisualSystem.ClearHover();
                }
                InspectionVisualSystem.ApplyHover(hover, targetState,
                    skillLevel);
                InspectionPresentationSystem.Update(hover, targetState,
                    skillLevel);
                InspectionUiSystem.Tick(hover, targetState, skillLevel);
            }

            internal static bool ShouldAllowNativeMouseOver()
            {
                return !IsInspectionSceneActive() ||
                    !examineModeSessionActive || !IsExamineGarageModeActive();
            }

            private static bool TryExitForDistance(CarLoader loader)
            {
                if (loader == null || !EnsureProximity(loader) ||
                    proximityRoot == null)
                    return false;

                Camera camera = bodySelectionCamera;
                if (camera == null) {
                    camera = Camera.main;
                    bodySelectionCamera = camera;
                }
                if (camera == null)
                    return false;

                Vector3 position = proximityRoot.InverseTransformPoint(
                    camera.transform.position);
                Vector3 nearest = proximityLocalBounds.ClosestPoint(position);
                float dx = position.x - nearest.x;
                float dz = position.z - nearest.z;
                float distance = Mathf.Sqrt(dx * dx + dz * dz);
                if (distance <= proximityExitDistance)
                    return false;

                GameMode mode = GameMode.Get();
                if (mode == null)
                    return false;
                gameMode target;
                if (hasReturnMode) {
                    target = returnMode;
                } else {
                    target = mode.GetPreviousMode();
                    if (IsInspectionMode(target) || IsTransientUiMode(target))
                        return false;
                }

                LogDiagnostic("inspection distance exit car=" +
                    SafeLoaderName(loader) + " distance=" +
                    distance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " width=" + proximityExitDistance.ToString("0.00",
                        CultureInfo.InvariantCulture) + " mode=" +
                    target.ToString());
                mode.SetCurrentMode(target);
                return true;
            }

            private static bool EnsureProximity(CarLoader loader)
            {
                int loaderId;
                try {
                    loaderId = loader.GetInstanceID();
                } catch {
                    return false;
                }
                if (proximityReady && proximityLoaderId == loaderId)
                    return true;

                ResetProximity();
                Transform root;
                try {
                    root = loader.GetRootTransform();
                } catch {
                    root = loader.transform;
                }
                if (root == null)
                    return false;

                Bounds localBounds = new Bounds();
                bool hasBounds = false;
                var surfaces = GetBodySelectionSurfaces(loader);
                for (int index = 0; index < surfaces.Count; index++) {
                    BodySelectionSurface surface = surfaces[index];
                    if (surface == null || surface.Collider == null)
                        continue;
                    EncapsulateLocalBounds(root, surface.Collider.bounds,
                        ref localBounds, ref hasBounds);
                }
                if (!hasBounds) {
                    Renderer[] renderers =
                        root.GetComponentsInChildren<Renderer>(true);
                    for (int index = 0; index < renderers.Length; index++) {
                        Renderer renderer = renderers[index];
                        if (renderer != null)
                            EncapsulateLocalBounds(root, renderer.bounds,
                                ref localBounds, ref hasBounds);
                    }
                }
                if (!hasBounds)
                    return false;

                float width = Mathf.Min(Mathf.Abs(localBounds.size.x),
                    Mathf.Abs(localBounds.size.z));
                if (width <= 0.01f)
                    return false;

                proximityLoaderId = loaderId;
                proximityRoot = root;
                proximityLocalBounds = localBounds;
                proximityExitDistance = width;
                proximityReady = true;
                LogDiagnostic("inspection distance guard car=" +
                    SafeLoaderName(loader) + " width=" +
                    width.ToString("0.00", CultureInfo.InvariantCulture));
                return true;
            }

            private static void EncapsulateLocalBounds(Transform root,
                Bounds worldBounds, ref Bounds localBounds, ref bool hasBounds)
            {
                Vector3 min = worldBounds.min;
                Vector3 max = worldBounds.max;
                for (int x = 0; x < 2; x++) {
                    for (int y = 0; y < 2; y++) {
                        for (int z = 0; z < 2; z++) {
                            Vector3 world = new Vector3(x == 0 ? min.x : max.x,
                                y == 0 ? min.y : max.y,
                                z == 0 ? min.z : max.z);
                            Vector3 local = root.InverseTransformPoint(world);
                            if (!hasBounds) {
                                localBounds = new Bounds(local, Vector3.zero);
                                hasBounds = true;
                            } else {
                                localBounds.Encapsulate(local);
                            }
                        }
                    }
                }
            }

            private static bool IsTransientUiMode(gameMode mode)
            {
                return string.Equals(mode.ToString(), "UI",
                    System.StringComparison.OrdinalIgnoreCase);
            }

            private static void ResetProximity()
            {
                proximityLoaderId = -1;
                proximityReady = false;
                proximityLocalBounds = new Bounds();
                proximityExitDistance = 0f;
                proximityRoot = null;
            }

            private static void BeginSession()
            {
                lastTickFrame = -1;
                ResetProximity();
                InspectionInputSystem.Reset();
                InspectionVisualSystem.Exit();
                BodyPartsCache.Clear();
                BodyHighlightTargetsCache.Clear();
                BodySelectionSurfacesCache.Clear();
                InspectionSystemsCache.Clear();
                SystemWheelPartsCache.Clear();
                bodySelectionCamera = null;
                ResetInspectionOverlayRaycastDiagnostics();
                if (inspectionResetHintSource == null)
                    inspectionResetHintSourceSearchAttempted = false;
                cachedInspectionSkillLevel = -1;
                cachedInspectionSkillId = null;
                inspectionSkillDiagnosticsLogged = false;
                inspectionSystemListDirty = true;
                ResetInspectionVehicleProgressCache();
                HideNativeExamineHint();
                InspectionUiSystem.OnEnter();
                InspectionPresentationSystem.Clear();
                GetInspectionSkillLevel();
                InspectionVisualSystem.ClearHover();
            }

            private static void EndSession()
            {
                lastTickFrame = -1;
                ResetProximity();
                InspectionInputSystem.Reset();
                InspectionVisualSystem.Exit();
                InspectionUiSystem.OnExit();
                DestroySharpEyeCursorTimer();
                InspectionPresentationSystem.Clear();
            }
        }
    }
}
