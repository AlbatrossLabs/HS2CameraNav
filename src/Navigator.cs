using Studio;
using UnityEngine;
using UnityEngine.EventSystems;
using CameraData = Studio.CameraControl.CameraData;
using StudioCamera = Studio.CameraControl;

namespace HS2CameraNav
{
    /// <summary>
    /// Camera math for the Unity and Blender schemes.
    ///
    /// Studio's camera is a pivot camera: cameraData.pos is the pivot (target), cameraData.rotate the Euler
    /// orientation, cameraData.distance.z the negative distance from the pivot. The rendered camera sits at
    /// Euler(rotate) * distance + pos, all in the space of CameraControl.transBase when that is set.
    /// Everything here works in that same space, so it behaves the same with or without transBase.
    /// </summary>
    internal static class Navigator
    {
        private enum Mode
        {
            None,
            Orbit,
            Pan,
            Zoom,
            Look, // Unity: right mouse, look around in place + WASD fly
        }

        private static Mode _mode = Mode.None;

        // Studio hides the cursor while the camera is being dragged and warps it back to where the drag
        // started every frame (GameCursor.SetCursorLock). Input.mousePosition then reads "moved, jumped back"
        // and the camera twitches. Studio's own controls read the raw mouse axes instead, so we do too.
        // The axes are in Unity's units, not pixels; PixelsPerAxis converts them, measured while the mouse
        // moves freely (no button held) so it matches this mouse and screen.
        private const float DefaultPixelsPerAxis = 10f; // Unity's default mouse sensitivity is 0.1
        private static float _pixelsPerAxis = DefaultPixelsPerAxis;
        private static float _calPixels, _calAxis;
        private static Vector3 _calLastMouse;
        private static int _calSkipFrames = 2;

        /// <summary>True while Unity fly mode owns the keyboard (Studio's W/E/R/Q shortcuts are paused).</summary>
        public static bool Flying => _mode == Mode.Look;

        private static CameraNavPlugin P => CameraNavPlugin.Instance;

        // ---------------------------------------------------------------- mouse

        /// <summary>Replaces CameraControl.InputMouseProc. Returns true while the camera is being controlled.</summary>
        public static bool MouseProc(StudioCamera cc, CameraData data)
        {
            var scheme = P.Scheme.Value;
            var blocked = PointerBlocked();

            var anyHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (!anyHeld)
            {
                _mode = Mode.None;
            }
            else if (_mode == Mode.None && !blocked)
            {
                // The first button pressed decides the mode until every button is released.
                for (var b = 0; b < 3; b++)
                {
                    if (!Input.GetMouseButtonDown(b)) continue;
                    _mode = scheme == NavScheme.Unity ? UnityMode(b) : BlenderMode(b);
                    break;
                }
            }

            var delta = MouseDelta();

            var used = false;
            switch (_mode)
            {
                case Mode.Orbit:
                    Orbit(data, delta, P.OrbitSpeed.Value, false);
                    used = true;
                    break;
                case Mode.Look:
                    Orbit(data, delta, P.LookSpeed.Value, true);
                    Fly(data);
                    used = true;
                    break;
                case Mode.Pan:
                    Pan(cc, data, delta.x, delta.y);
                    used = true;
                    break;
                case Mode.Zoom:
                    var amount = scheme == NavScheme.Unity ? delta.x + delta.y : delta.y;
                    if (P.InvertDragZoom.Value) amount = -amount;
                    Dolly(data, Mathf.Exp(-amount * 0.005f * P.DragZoomSpeed.Value));
                    used = true;
                    break;
            }

            if (!blocked) used |= Wheel(cc, data, scheme);
            return used;
        }

        private static Mode UnityMode(int button)
        {
            var alt = Alt();
            switch (button)
            {
                case 0: // Alt+Left orbit, Alt+Ctrl+Left pan
                    if (alt) return Ctrl() ? Mode.Pan : Mode.Orbit;
                    return P.KeepLeftDragOrbit.Value ? Mode.Orbit : Mode.None;
                case 1: // Alt+Right zoom, Right look/fly
                    return alt ? Mode.Zoom : Mode.Look;
                default: // Middle pan
                    return Mode.Pan;
            }
        }

        private static Mode BlenderMode(int button)
        {
            var middle = button == 2 || (button == 0 && P.EmulateThreeButton.Value && Alt());
            if (middle)
            {
                if (Shift()) return Mode.Pan;
                if (Ctrl()) return Mode.Zoom;
                return Mode.Orbit;
            }

            if (button == 0) return P.KeepLeftDragOrbit.Value ? Mode.Orbit : Mode.None;
            return Mode.None; // right mouse is Blender's context menu; leave it alone
        }

        private static bool Wheel(StudioCamera cc, CameraData data, NavScheme scheme)
        {
            var scroll = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(scroll, 0f)) return false;

            if (_mode == Mode.Look)
            {
                // Unity: scrolling while flying changes fly speed.
                P.FlySpeed.Value = Mathf.Clamp(P.FlySpeed.Value * Mathf.Pow(1.25f, scroll), 0.05f, 50f);
                CameraNavPlugin.Logger.LogMessage($"Fly speed {P.FlySpeed.Value:0.##}");
                return true;
            }

            if (scheme == NavScheme.Blender && !Alt())
            {
                // Blender: Ctrl+wheel pans sideways, Shift+wheel pans up/down.
                const float notchPixels = 40f;
                if (Ctrl()) { Pan(cc, data, scroll * notchPixels, 0f); return true; }
                if (Shift()) { Pan(cc, data, 0f, scroll * notchPixels); return true; }
            }

            if (P.InvertWheelZoom.Value) scroll = -scroll;
            Dolly(data, Mathf.Pow(1f - P.ZoomStep.Value, scroll));
            return true;
        }

        // ---------------------------------------------------------------- mouse movement

        /// <summary>This frame's mouse movement in (approximate) screen pixels, unaffected by Studio's cursor lock.</summary>
        private static Vector3 MouseDelta() =>
            new Vector3(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"), 0f) * _pixelsPerAxis;

        /// <summary>
        /// Called every frame from the plugin's Update. While no button is held the cursor moves freely, so
        /// comparing its pixel movement with the raw axes gives the pixels-per-axis-unit for this setup.
        /// </summary>
        public static void Calibrate()
        {
            var mouse = Input.mousePosition;
            var moved = (mouse - _calLastMouse).magnitude;
            _calLastMouse = mouse;

            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2))
            {
                _calSkipFrames = 3; // Studio warps the cursor back when a drag ends; ignore those frames
                return;
            }
            if (_calSkipFrames > 0) { _calSkipFrames--; return; }

            var axis = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")).magnitude;
            if (axis < 0.05f || moved < 1f) return;

            var sample = moved / axis;
            if (sample < 1f || sample > 200f) return; // window lost focus, cursor teleported, etc.

            // Running totals that slowly forget, so one odd frame can't swing it.
            _calPixels = _calPixels * 0.98f + moved;
            _calAxis = _calAxis * 0.98f + axis;
            if (_calAxis > 2f) _pixelsPerAxis = Mathf.Clamp(_calPixels / _calAxis, 1f, 200f);
        }

        // ---------------------------------------------------------------- keyboard

        private static readonly KeyCode[] NumpadKeys =
        {
            KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4,
            KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9, KeyCode.KeypadPeriod,
        };

        /// <summary>
        /// Blender numpad views. Called in place of Studio's InputKeyProc while one of these keys is held,
        /// so Studio's own numpad rotation doesn't also run. Returns false when none of them is held.
        /// </summary>
        public static bool NumpadProc(StudioCamera cc, CameraData data)
        {
            if (P.Scheme.Value != NavScheme.Blender || !P.NumpadViews.Value) return false;

            var held = false;
            foreach (var k in NumpadKeys) held |= Input.GetKey(k);
            if (!held) return false;

            var ctrl = Ctrl();
            var step = P.NumpadStep.Value;
            var r = data.rotate;

            // Characters face +Z, so "front" puts the camera on +Z looking back at them.
            if (Input.GetKeyDown(KeyCode.Keypad1)) r = ctrl ? new Vector3(0, 0, 0) : new Vector3(0, 180, 0);
            else if (Input.GetKeyDown(KeyCode.Keypad3)) r = ctrl ? new Vector3(0, 90, 0) : new Vector3(0, 270, 0);
            else if (Input.GetKeyDown(KeyCode.Keypad7)) r = ctrl ? new Vector3(-90, 180, 0) : new Vector3(90, 180, 0);
            else if (Input.GetKeyDown(KeyCode.Keypad9)) r = new Vector3(-r.x, r.y + 180f, r.z);
            else if (Input.GetKeyDown(KeyCode.Keypad8)) r.x += step;
            else if (Input.GetKeyDown(KeyCode.Keypad2)) r.x -= step;
            else if (Input.GetKeyDown(KeyCode.Keypad4)) r.y += step;
            else if (Input.GetKeyDown(KeyCode.Keypad6)) r.y -= step;
            else if (Input.GetKeyDown(KeyCode.KeypadPeriod)) FocusSelected(cc, data);

            data.rotate = new Vector3(Wrap(r.x), Wrap(r.y), r.z);
            return true;
        }

        // ---------------------------------------------------------------- moves

        private static void Orbit(CameraData data, Vector3 delta, float speed, bool inPlace)
        {
            var camPos = data.rotation * data.distance + data.pos;

            var sx = P.InvertOrbitX.Value ? -1f : 1f;
            var sy = P.InvertOrbitY.Value ? -1f : 1f;
            var r = data.rotate;
            r.y = Wrap(r.y + sx * delta.x * speed);
            r.x = Wrap(r.x - sy * delta.y * speed);
            if (inPlace)
            {
                // First-person look stops just short of straight up/down instead of flipping over.
                var pitch = r.x > 180f ? r.x - 360f : r.x;
                r.x = Wrap(Mathf.Clamp(pitch, -89f, 89f));
            }
            data.rotate = r;

            // Looking around in place: keep the camera where it is and swing the pivot instead.
            if (inPlace) data.pos = camPos - data.rotation * data.distance;
        }

        private static void Pan(StudioCamera cc, CameraData data, float dxPixels, float dyPixels)
        {
            // Scale pixels to world units at the pivot's depth so the scene stays under the cursor.
            var d = Distance(data);
            var fov = cc.fieldOfView > 0f ? cc.fieldOfView : 23f;
            var unitsPerPixel = 2f * d * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
            unitsPerPixel *= P.PanSpeed.Value;

            var q = data.rotation;
            data.pos -= (q * Vector3.right) * (dxPixels * unitsPerPixel) + (q * Vector3.up) * (dyPixels * unitsPerPixel);
        }

        private static void Dolly(CameraData data, float factor)
        {
            var d = Mathf.Max(Distance(data), P.MinDistance.Value) * factor;
            d = Mathf.Max(d, P.MinDistance.Value);
            var dist = data.distance;
            dist.z = -d;
            data.distance = dist;
        }

        private static void Fly(CameraData data)
        {
            var dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir.z += 1;
            if (Input.GetKey(KeyCode.S)) dir.z -= 1;
            if (Input.GetKey(KeyCode.D)) dir.x += 1;
            if (Input.GetKey(KeyCode.A)) dir.x -= 1;
            if (Input.GetKey(KeyCode.E)) dir.y += 1;
            if (Input.GetKey(KeyCode.Q)) dir.y -= 1;
            if (dir == Vector3.zero) return;

            var speed = P.FlySpeed.Value * (Shift() ? P.FlyBoost.Value : 1f) * Time.unscaledDeltaTime;
            data.pos += data.rotation * dir.normalized * speed;
        }

        private static void FocusSelected(StudioCamera cc, CameraData data)
        {
            var studio = Studio.Studio.Instance;
            var node = studio?.treeNodeCtrl?.selectNode;
            if (node == null || !studio.dicInfo.TryGetValue(node, out var info) || info?.guideObject?.transformTarget == null)
            {
                CameraNavPlugin.Logger.LogMessage("Camera Nav: select something in the workspace first");
                return;
            }

            var world = info.guideObject.transformTarget.position;
            data.pos = cc.transBase != null ? cc.transBase.InverseTransformPoint(world) : world;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Over Studio UI or grabbing a gizmo: don't start a camera drag or zoom.</summary>
        private static bool PointerBlocked()
        {
            var es = EventSystem.current;
            if (es != null && es.IsPointerOverGameObject()) return true;
            return Singleton<GuideObjectManager>.IsInstance() && Singleton<GuideObjectManager>.Instance.isOperationTarget;
        }

        private static float Distance(CameraData data) => Mathf.Max(0f, -data.distance.z);

        private static float Wrap(float angle)
        {
            angle %= 360f;
            return angle < 0f ? angle + 360f : angle;
        }

        private static bool Alt() => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        private static bool Ctrl() => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        private static bool Shift() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }
}
