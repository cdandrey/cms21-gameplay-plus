#if NET6_0_OR_GREATER
using Il2Cpp;
#else
using CMS;
#endif

namespace Cms21GameplayPlus
{
    internal static partial class SharpEyeShoppingListFeature
    {
        private struct InspectionTargetState
        {
            internal int Examined;
            internal int Available;
            internal int Full;
            internal bool Completed;

            internal bool CanExamine
            {
                get { return Available > 0 && !Completed; }
            }

            internal bool HasProgress
            {
                get { return Examined > 0; }
            }
        }

        private static class InspectionProcessSystem
        {
            internal static InspectionTargetState GetTargetState(
                InspectionHoverState hover, int skillLevel)
            {
                InspectionTargetState result = new InspectionTargetState();
                if (skillLevel <= 0 || hover.Current == null)
                    return result;

                if (hover.System != null) {
                    GetSystemAvailableProgress(hover.System, skillLevel,
                        out result.Examined, out result.Available,
                        out result.Full);
                    result.Completed = result.Available > 0 &&
                        result.Examined >= result.Available;
                    return result;
                }

                if (hover.Body == null || hover.Loader == null)
                    return result;
                BodyPassState bodyState = GetBodyPassState(hover.Loader);
                if (bodyState == null || bodyState.Total <= 0)
                    return result;
                result.Available = bodyState.Total;
                result.Full = bodyState.Total;
                result.Examined = System.Math.Min(result.Available,
                    bodyState.ExaminedSlots.Count);
                result.Completed = result.Examined >= result.Available;
                return result;
            }

            internal static bool UpdateExamineHold(InspectionHoverState hover)
            {
                if (hover.Body != null && hover.Loader != null)
                    return UpdateBodyHold(hover.Loader);
                if (hover.System == null)
                    return false;
                SystemPassState state = GetSystemPassState(hover.System);
                return state != null &&
                    UpdateManualCustomHold(hover.System, state);
            }

            internal static void CancelExamineHold(InspectionHoverState hover)
            {
                if (hover.Body != null && hover.Loader != null) {
                    ResetBodyHold(GetBodyPassState(hover.Loader));
                    return;
                }
                if (hover.System != null)
                    ResetManualCustomHold(GetSystemPassState(hover.System));
            }

            internal static bool HasVehicleProgress(CarLoader loader)
            {
                return loader != null && HasVehicleInspectionProgress(loader);
            }

            internal static bool ResetTarget(InspectionHoverState hover)
            {
                if (hover.Current == null || hover.Loader == null)
                    return false;
                if (hover.IsBody) {
                    ResetBodyInspection(hover.Loader);
                    LogDiagnostic("inspection reset body car=" +
                        SafeLoaderName(hover.Loader));
                } else {
                    ResetSystemInspection(hover.System);
                    LogDiagnostic("inspection reset system io=" +
                        SafeIoId(hover.System));
                }
                MarkInspectionProgressChanged();
                PlaySwitchModeSound();
                return true;
            }

            internal static bool ResetVehicle(CarLoader loader)
            {
                if (loader == null)
                    return false;
                ResetVehicleInspection(loader);
                return true;
            }
        }
    }
}
