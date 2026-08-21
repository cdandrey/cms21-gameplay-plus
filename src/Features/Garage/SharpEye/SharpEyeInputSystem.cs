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
        private static class InspectionInputSystem
        {
            private static InspectionHoverState activeExamineHover;
            private static InspectionHoverState activeResetHover;
            private static bool examineHeld;

            internal static bool IsExamineHeld
            {
                get { return examineHeld; }
            }

            internal static bool Tick(Raycast raycast,
                InspectionHoverState hover, InspectionTargetState targetState,
                int skillLevel)
            {
                examineHeld = Input.GetMouseButton(0);
                if (skillLevel <= 0) {
                    CancelActiveExamineHold();
                    ClearActiveResetHold();
                    ResetInspectionResetState();
                    return false;
                }

                if (Input.GetKeyDown(KeyCode.Tab))
                    InspectionUiSystem.ToggleSystemTable();

                bool changed = UpdateResetInput(raycast, hover, targetState);
                if (changed)
                    return true;
                return UpdateExamineInput(hover, targetState);
            }

            internal static void Reset()
            {
                examineHeld = false;
                CancelActiveExamineHold();
                ClearActiveResetHold();
                ResetInspectionResetState();
            }

            private static bool UpdateExamineInput(InspectionHoverState hover,
                InspectionTargetState targetState)
            {
                InteractiveObject target = hover.Current;
                if (activeExamineHover.Current != target) {
                    CancelActiveExamineHold();
                    activeExamineHover = hover;
                }

                if (target == null || !examineHeld ||
                    !targetState.CanExamine) {
                    InspectionProcessSystem.CancelExamineHold(hover);
                    return false;
                }

                return InspectionProcessSystem.UpdateExamineHold(hover);
            }

            private static void CancelActiveExamineHold()
            {
                if (activeExamineHover.Current != null)
                    InspectionProcessSystem.CancelExamineHold(
                        activeExamineHover);
                activeExamineHover = new InspectionHoverState();
            }

            private static bool UpdateResetInput(Raycast raycast,
                InspectionHoverState hover, InspectionTargetState targetState)
            {
                if (examineHeld) {
                    ClearActiveResetHold();
                    inspectionSystemResetHoldProgress = 0f;
                    inspectionVehicleResetHoldProgress = 0f;
                    return false;
                }

                CarLoader loader = hover.Loader ?? ResolveInspectionLoader(
                    raycast);
                if (loader != null)
                    inspectionResetLoader = loader;
                else
                    loader = inspectionResetLoader;

                bool vehicleHasProgress =
                    InspectionProcessSystem.HasVehicleProgress(loader);
                bool resetAllDown = Input.GetKey(KeyCode.Space);
                if (resetAllDown)
                    ClearActiveResetHold();
                if (!resetAllDown || !vehicleHasProgress) {
                    inspectionVehicleResetHoldProgress = 0f;
                    inspectionVehicleResetTriggered = false;
                } else if (!inspectionVehicleResetTriggered) {
                    inspectionVehicleResetHoldProgress += Time.deltaTime;
                    if (inspectionVehicleResetHoldProgress >=
                        GetSharpEyeHoldSeconds()) {
                        bool changed = InspectionProcessSystem.ResetVehicle(
                            loader);
                        inspectionVehicleResetTriggered = changed;
                        inspectionVehicleResetHoldProgress = 0f;
                        inspectionSystemResetHoldProgress = 0f;
                        if (changed)
                            return true;
                    }
                }

                bool resetSystemDown = Input.GetKey(KeyCode.LeftAlt) ||
                    Input.GetKey(KeyCode.RightAlt);
                if (!resetSystemDown) {
                    ClearActiveResetHold();
                    inspectionSystemResetHoldProgress = 0f;
                    inspectionSystemResetTriggered = false;
                    return false;
                }
                if (resetAllDown)
                    return false;

                if (activeResetHover.Current == null) {
                    bool targetHasProgress = hover.Current != null &&
                        targetState.HasProgress;
                    if (!targetHasProgress ||
                        (!hover.IsBody && (hover.System == null ||
                            targetState.Available <= 0)))
                        return false;

                    activeResetHover = hover;
                    inspectionSystemResetHoldProgress = 0f;
                    inspectionSystemResetTriggered = false;
                    LogDiagnostic("inspection reset hold begin target=" +
                        SafeIoId(activeResetHover.Current));
                }

                if (inspectionSystemResetTriggered)
                    return false;

                inspectionSystemResetHoldProgress += Time.deltaTime;
                if (inspectionSystemResetHoldProgress <
                    GetSharpEyeHoldSeconds())
                    return false;

                bool targetChanged = InspectionProcessSystem.ResetTarget(
                    activeResetHover);
                inspectionSystemResetTriggered = targetChanged;
                inspectionSystemResetHoldProgress = 0f;
                if (targetChanged)
                    ClearActiveResetTargetAfterTrigger();
                return targetChanged;
            }

            private static void ClearActiveResetHold()
            {
                activeResetHover = new InspectionHoverState();
            }

            private static void ClearActiveResetTargetAfterTrigger()
            {
                activeResetHover = new InspectionHoverState();
            }
        }
    }
}
