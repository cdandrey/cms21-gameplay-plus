#if NET6_0_OR_GREATER
using Il2Cpp;
#else
using CMS;
#endif

namespace Cms21GameplayPlus
{
    internal static partial class SharpEyeShoppingListFeature
    {
        private static class InspectionUiSystem
        {
            internal static void Tick(InspectionHoverState hover,
                InspectionTargetState targetState, int skillLevel)
            {
                if (!inspectionSystemsOverlayActive || skillLevel <= 0) {
                    RestoreInspectionFooterAfterHold();
                    HideInspectionActionHints();
                    HideInspectionShowSystemsHint();
                    if (inspectionSystemListVisible ||
                        inspectionSystemListPanel != null)
                        DestroyInspectionSystemList();
                    return;
                }

                bool examining = InspectionInputSystem.IsExamineHeld &&
                    hover.Current != null && targetState.CanExamine;
                if (examining)
                    SuppressInspectionFooterForHold();
                else
                    RestoreInspectionFooterAfterHold();

                UpdateInspectionSystemListUi(hover.Loader ??
                    InspectionVisualSystem.Loader);
                UpdateInspectionResetHints(hover.Loader, hover.Current,
                    hover.IsBody, targetState, skillLevel, examining);
            }

            internal static void ToggleSystemTable()
            {
                if (GetInspectionSkillLevel() <= 0)
                    return;
                if (inspectionSystemListVisible)
                    DestroyInspectionSystemList();
                else {
                    inspectionSystemListVisible = true;
                    inspectionSystemListDirty = true;
                }
                ShowInspectionSystemsHint();
            }

            internal static void OnEnter()
            {
                RestoreInspectionFooterAfterHold();
                CaptureInspectionResetHintSource();
                EnsureInspectionResetHintSource();
            }

            internal static void OnExit()
            {
                RestoreInspectionFooterAfterHold();
                HideInspectionFooterForModeExit();
                HideInspectionResetHints();
                DestroyInspectionSystemList();
            }
        }
    }
}
