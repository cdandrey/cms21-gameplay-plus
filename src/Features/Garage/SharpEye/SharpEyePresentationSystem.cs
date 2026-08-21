#if NET6_0_OR_GREATER
using Il2Cpp;
#else
using CMS;
#endif

namespace Cms21GameplayPlus
{
    internal static partial class SharpEyeShoppingListFeature
    {
        private static class InspectionPresentationSystem
        {
            internal static void Update(InspectionHoverState hover,
                InspectionTargetState targetState, int skillLevel)
            {
                if (skillLevel <= 0) {
                    ClearSystemIndicator();
                    SetMouseOverDescription(GetInspectionHintLabel(
                        "LOC_SharpEyeInspectionUnavailableSkill"));
                    return;
                }

                if (hover.System != null) {
                    SetMouseOverDescription(GetLocalizedSystemName(
                        hover.System));
                    SetInspectionIndicator(hover.System,
                        targetState.Examined, targetState.Available);
                    return;
                }

                if (hover.Body != null && hover.Loader != null) {
                    SetMouseOverDescription(GetBodyDisplayName());
                    SetInspectionIndicator(hover.Body,
                        targetState.Examined, targetState.Available);
                    return;
                }

                ClearSystemIndicator();
                ClearMouseOverDescription();
            }

            internal static void Clear()
            {
                ClearSystemIndicator(true);
                ClearMouseOverDescription(null, true);
            }
        }
    }
}
