using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KKAPI;
using KKAPI.Studio;
using UnityEngine;

namespace HS2CameraNav
{
    public enum NavScheme
    {
        Vanilla,
        Unity,
        Blender,
    }

    /// <summary>
    /// Replaces Studio's mouse camera controls with Unity-editor or Blender style navigation.
    /// Vanilla = Studio's own controls, untouched.
    /// </summary>
    [BepInPlugin(Guid, PluginName, Version)]
    [BepInProcess("StudioNEOV2")]
    [BepInDependency(KoikatuAPI.GUID)]
    public class CameraNavPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.albatrosslabs.hs2.cameranav";
        public const string PluginName = "Camera Nav";
        public const string Version = "1.0.0";

        internal static new ManualLogSource Logger;
        internal static CameraNavPlugin Instance;

        internal ConfigEntry<NavScheme> Scheme;
        internal ConfigEntry<KeyboardShortcut> CycleSchemeKey;
        internal ConfigEntry<bool> KeepLeftDragOrbit;

        internal ConfigEntry<float> OrbitSpeed;
        internal ConfigEntry<float> PanSpeed;
        internal ConfigEntry<float> ZoomStep;
        internal ConfigEntry<float> DragZoomSpeed;
        internal ConfigEntry<float> MinDistance;
        internal ConfigEntry<bool> InvertOrbitX;
        internal ConfigEntry<bool> InvertOrbitY;
        internal ConfigEntry<bool> InvertWheelZoom;
        internal ConfigEntry<bool> InvertDragZoom;

        internal ConfigEntry<float> FlySpeed;
        internal ConfigEntry<float> FlyBoost;
        internal ConfigEntry<float> LookSpeed;

        internal ConfigEntry<bool> EmulateThreeButton;
        internal ConfigEntry<bool> NumpadViews;
        internal ConfigEntry<float> NumpadStep;

        internal ConfigEntry<bool> BlockWhileFlying;
        internal ConfigEntry<bool> ShowConflictMessage;
        internal ConfigEntry<string> ConflictList;

        private void Awake()
        {
            Logger = base.Logger;
            Instance = this;

            Scheme = Config.Bind("1. General", "Navigation scheme", NavScheme.Unity,
                "Vanilla = Studio's own controls. Unity = Unity editor scene view. Blender = Blender 3D viewport.");
            CycleSchemeKey = Config.Bind("1. General", "Cycle scheme", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftShift),
                "Switch Vanilla > Unity > Blender.");
            KeepLeftDragOrbit = Config.Bind("1. General", "Keep left-drag orbit", true,
                "Plain left-drag on empty space still orbits, like vanilla Studio. Left-click in the viewport selects nothing in Studio, so this costs nothing. Turn off for strict Unity/Blender behavior.");

            OrbitSpeed = Config.Bind("2. Speeds", "Orbit speed", 0.3f,
                new ConfigDescription("Degrees of rotation per pixel of mouse movement.", new AcceptableValueRange<float>(0.02f, 2f)));
            PanSpeed = Config.Bind("2. Speeds", "Pan speed", 1f,
                new ConfigDescription("1 = the scene stays under the cursor while panning.", new AcceptableValueRange<float>(0.1f, 5f)));
            ZoomStep = Config.Bind("2. Speeds", "Wheel zoom step", 0.15f,
                new ConfigDescription("How much one wheel notch moves toward the pivot (0.15 = 15% of the distance).", new AcceptableValueRange<float>(0.02f, 0.6f)));
            DragZoomSpeed = Config.Bind("2. Speeds", "Drag zoom speed", 1f,
                new ConfigDescription("Speed of Alt+Right-drag (Unity) and Ctrl+Middle-drag (Blender) zoom.", new AcceptableValueRange<float>(0.1f, 5f)));
            MinDistance = Config.Bind("2. Speeds", "Closest zoom distance", 0.02f,
                new ConfigDescription("Zooming in never gets the camera closer to the pivot than this.", new AcceptableValueRange<float>(0.001f, 1f)));
            InvertOrbitX = Config.Bind("3. Directions", "Invert orbit horizontal", false, "");
            InvertOrbitY = Config.Bind("3. Directions", "Invert orbit vertical", false, "");
            InvertWheelZoom = Config.Bind("3. Directions", "Invert wheel zoom", false, "");
            InvertDragZoom = Config.Bind("3. Directions", "Invert drag zoom", false, "");

            FlySpeed = Config.Bind("4. Unity", "Fly speed", 1.5f,
                new ConfigDescription("Units per second for Right-mouse + WASD/QE. Scroll while holding right mouse to change it.", new AcceptableValueRange<float>(0.05f, 50f)));
            FlyBoost = Config.Bind("4. Unity", "Fly boost (Shift)", 3f,
                new ConfigDescription("Speed multiplier while holding Shift in fly mode.", new AcceptableValueRange<float>(1f, 10f)));
            LookSpeed = Config.Bind("4. Unity", "Look speed", 0.2f,
                new ConfigDescription("Degrees per pixel when looking around with right mouse.", new AcceptableValueRange<float>(0.02f, 2f)));

            EmulateThreeButton = Config.Bind("5. Blender", "Emulate 3 button mouse", false,
                "Alt+Left-drag acts as Middle-drag (Blender preference for mice/trackpads without a middle button).");
            NumpadViews = Config.Bind("5. Blender", "Numpad views", true,
                "Numpad 1/3/7 front/right/top (Ctrl = opposite), 9 flip to opposite, 2/4/6/8 orbit in steps, Numpad . centers on the selected object.");
            NumpadStep = Config.Bind("5. Blender", "Numpad orbit step", 15f,
                new ConfigDescription("Degrees per Numpad 2/4/6/8 press.", new AcceptableValueRange<float>(1f, 90f)));

            BlockWhileFlying = Config.Bind("6. Hotkey conflicts", "Block while flying", true,
                "While flying in Unity mode (right mouse held), other plugins' hotkeys on W/A/S/D/Q/E, plain or with Shift, don't fire. They work normally the rest of the time. Covers plugins that use BepInEx hotkey settings (most do).");
            ShowConflictMessage = Config.Bind("6. Hotkey conflicts", "Show conflict message", true,
                "Show an on-screen message when other plugins' hotkeys clash with Camera Nav's controls.");
            ConflictList = Config.Bind("6. Hotkey conflicts", "Conflicts found", "",
                new ConfigDescription("Other plugins' hotkeys that clash with Camera Nav, tagged with the scheme they affect. Rebinding one in F1 updates this list.",
                    null, new ConfigurationManagerAttributes { CustomDrawer = HotkeyConflicts.Draw, HideDefaultButton = true, HideSettingName = true, Order = -10 }));

            Harmony.CreateAndPatchAll(typeof(Patches), Guid);

            // Scan once Studio has finished loading, when every plugin has bound its settings.
            StudioAPI.StudioLoadedChanged += (s, e) => HotkeyConflicts.MarkDirty();
        }

        private void Start()
        {
            if (StudioAPI.StudioLoaded) HotkeyConflicts.MarkDirty();
        }

        private void Update()
        {
            Navigator.Calibrate();
            HotkeyConflicts.Tick();

            if (CycleSchemeKey.Value.IsDown())
            {
                Scheme.Value = (NavScheme)(((int)Scheme.Value + 1) % 3);
                Logger.LogMessage("Camera Nav: " + Scheme.Value + " controls");
            }
        }
    }
}
