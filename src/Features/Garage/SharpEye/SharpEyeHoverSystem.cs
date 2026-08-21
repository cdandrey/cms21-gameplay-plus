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
        private struct InspectionHoverState
        {
            internal CarLoader Loader;
            internal InteractiveObject System;
            internal InteractiveObject Body;

            internal InteractiveObject Current
            {
                get { return System ?? Body; }
            }

            internal bool IsBody
            {
                get { return Body != null; }
            }
        }

        private static class InspectionHoverSystem
        {
            internal static InspectionHoverState Resolve(Raycast raycast,
                int skillLevel)
            {
                InspectionHoverState state = new InspectionHoverState();
                state.Loader = inspectionSystemsOverlayLoader ??
                    ResolveInspectionLoader(raycast);
                if (!inspectionSystemsOverlayActive || skillLevel <= 0 ||
                    state.Loader == null)
                    return state;

                Vector3 mousePosition = Input.mousePosition;
                Transform hitTransform =
                    GetInspectionOverlayPointerHitTransform(mousePosition);
                InteractiveObject system =
                    ResolveInspectionSystemsOverlayPartSystem(hitTransform);
                if (system != null) {
                    int examined;
                    int available;
                    int full;
                    GetSystemAvailableProgress(system, skillLevel,
                        out examined, out available, out full);
                    if (available > 0 && UsesSharpEyeInspection(system)) {
                        state.System = system;
                        return state;
                    }
                }

                InteractiveObject body;
                if (TryGetBodySelectionHit(raycast, state.Loader,
                        mousePosition, out body))
                    state.Body = body;
                return state;
            }

        }
    }
}
