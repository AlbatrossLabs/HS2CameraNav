using HarmonyLib;
using Studio;
using CameraData = Studio.CameraControl.CameraData;
using StudioCamera = Studio.CameraControl;

namespace HS2CameraNav
{
    /// <summary>
    /// Studio's CameraControl.LateUpdate calls, in order: InputMouseWheelZoomProc, InputMouseProc, InputKeyProc,
    /// each only if the one before it didn't take control this frame, and InputMouseProc only when Studio's
    /// noCtrlCondition allows it. Replacing those three methods keeps every one of Studio's own guards
    /// (loading screens, camera objects, gizmo drags) in place.
    /// </summary>
    internal static class Patches
    {
        private static bool Active => CameraNavPlugin.Instance.Scheme.Value != NavScheme.Vanilla;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(StudioCamera), "InputMouseWheelZoomProc")]
        private static bool WheelPrefix(ref bool __result)
        {
            if (!Active) return true;
            __result = false; // wheel is handled inside MouseProc
            return false;
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(StudioCamera), "InputMouseProc")]
        private static bool MousePrefix(StudioCamera __instance, CameraData ___cameraData, ref bool ___dragging, ref bool __result)
        {
            if (!Active) return true;
            __result = Navigator.MouseProc(__instance, ___cameraData);
            ___dragging = __result;
            return false;
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(StudioCamera), "InputKeyProc")]
        private static bool KeyPrefix(StudioCamera __instance, CameraData ___cameraData, ref bool __result)
        {
            if (!Active) return true;

            if (Navigator.Flying)
            {
                __result = false; // WASD belongs to fly mode; Studio's A = reset camera must not fire
                return false;
            }

            if (Navigator.NumpadProc(__instance, ___cameraData))
            {
                __result = true;
                return false;
            }

            return true; // arrows, PageUp/Down, Home/End and the rest keep working as in vanilla
        }

        /// <summary>Studio's W/E/R/Q gizmo shortcuts would fire while flying with WASD/QE. Pause them.</summary>
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ShortcutKeyCtrl), "Update")]
        private static bool ShortcutPrefix()
        {
            return !(Active && Navigator.Flying);
        }
    }
}
