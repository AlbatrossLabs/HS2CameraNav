using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace HS2CameraNav
{
    internal enum ConflictScheme
    {
        Unity,
        Blender,
        All,
    }

    internal sealed class HotkeyConflict
    {
        public ConflictScheme Scheme;
        public string Plugin;
        public string Setting;
        public string Keys;
        public string Note;
    }

    /// <summary>
    /// Finds other plugins' hotkeys (BepInEx KeyboardShortcut and KeyCode settings) that clash with
    /// Camera Nav's controls, tags each with the scheme it affects, and lists them in F1.
    /// Rescans whenever any plugin's setting changes, so rebinding in F1 updates the list right away.
    /// </summary>
    internal static class HotkeyConflicts
    {
        private static readonly HashSet<KeyCode> FlyKeys = new HashSet<KeyCode>
        {
            KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Q, KeyCode.E,
        };

        private static readonly HashSet<KeyCode> ShiftKeys = new HashSet<KeyCode>
        {
            KeyCode.LeftShift, KeyCode.RightShift,
        };

        // Navigator.NumpadProc reacts to these whatever modifiers are held.
        private static readonly HashSet<KeyCode> NumpadKeys = new HashSet<KeyCode>
        {
            KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4,
            KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9, KeyCode.KeypadPeriod,
        };

        public static List<HotkeyConflict> Current { get; private set; } = new List<HotkeyConflict>();

        private static readonly HashSet<ConfigFile> Watched = new HashSet<ConfigFile>();
        private static bool _dirty;
        private static bool _announcedOnce;
        private static string _lastDetails;
        private static GUIStyle _wrap;

        private static CameraNavPlugin P => CameraNavPlugin.Instance;

        public static void MarkDirty() => _dirty = true;

        /// <summary>Called from the plugin's Update. Rescans at most once per frame.</summary>
        public static void Tick()
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                Scan();
            }
            catch (Exception e)
            {
                CameraNavPlugin.Logger.LogWarning("Hotkey conflict scan failed: " + e);
            }
        }

        /// <summary>
        /// True for a shortcut that Unity fly mode would set off: a fly key (W/A/S/D/Q/E), alone or with Shift
        /// (Shift is the fly boost). Ctrl/Alt combos aren't something you press while flying, so they're left alone.
        /// </summary>
        public static bool IsFlyKeyShortcut(KeyboardShortcut s)
        {
            if (!FlyKeys.Contains(s.MainKey)) return false;
            foreach (var m in s.Modifiers)
                if (!ShiftKeys.Contains(m)) return false;
            return true;
        }

        private static void Scan()
        {
            var list = new List<HotkeyConflict>();

            foreach (var info in Chainloader.PluginInfos.Values)
            {
                var plugin = info?.Instance;
                var cfg = plugin != null ? plugin.Config : null;
                if (cfg == null) continue;

                // Watch every config, ours included, so rebinding anything (or changing our own keys) rescans.
                if (Watched.Add(cfg)) cfg.SettingChanged += (s, e) => _dirty = true;
                if (plugin == P) continue;

                KeyValuePair<ConfigDefinition, ConfigEntryBase>[] entries;
                try { entries = cfg.ToArray(); }
                catch { continue; }

                var name = info.Metadata?.Name ?? info.Metadata?.GUID ?? "?";
                foreach (var kv in entries)
                {
                    var value = kv.Value?.BoxedValue;
                    if (value is KeyboardShortcut ks) CheckShortcut(list, name, kv.Key.Key, ks);
                    else if (value is KeyCode kc) CheckKeyCode(list, name, kv.Key.Key, kc);
                }
            }

            Current = list.OrderBy(c => c.Scheme).ThenBy(c => c.Plugin).ThenBy(c => c.Setting).ToList();
            Report();
        }

        private static void CheckShortcut(List<HotkeyConflict> list, string plugin, string setting, KeyboardShortcut ks)
        {
            if (ks.MainKey == KeyCode.None) return;
            var keys = Pretty(ks);

            if (IsFlyKeyShortcut(ks))
            {
                list.Add(new HotkeyConflict
                {
                    Scheme = ConflictScheme.Unity, Plugin = plugin, Setting = setting, Keys = keys,
                    Note = P.BlockWhileFlying.Value
                        ? "Blocked while flying. Works normally otherwise."
                        : "Fires while flying. Turn on Block while flying, or rebind it.",
                });
            }

            if (P.NumpadViews.Value && NumpadKeys.Contains(ks.MainKey))
            {
                list.Add(new HotkeyConflict
                {
                    Scheme = ConflictScheme.Blender, Plugin = plugin, Setting = setting, Keys = keys,
                    Note = "Rebind it, or turn off Numpad views.",
                });
            }

            if (SameShortcut(ks, P.CycleSchemeKey.Value))
            {
                list.Add(new HotkeyConflict
                {
                    Scheme = ConflictScheme.All, Plugin = plugin, Setting = setting, Keys = keys,
                    Note = "Same key as Camera Nav's Cycle scheme. Rebind one of them.",
                });
            }
        }

        // A plain KeyCode setting is usually read with Input.GetKey/GetKeyDown, which ignores modifiers
        // and can't be intercepted, so these are listed only.
        private static void CheckKeyCode(List<HotkeyConflict> list, string plugin, string setting, KeyCode kc)
        {
            if (kc == KeyCode.None) return;
            var keys = KeyName(kc);

            if (FlyKeys.Contains(kc))
            {
                list.Add(new HotkeyConflict
                {
                    Scheme = ConflictScheme.Unity, Plugin = plugin, Setting = setting, Keys = keys,
                    Note = "Plain key setting, so it can't be blocked while flying. Rebind it.",
                });
            }

            if (P.NumpadViews.Value && NumpadKeys.Contains(kc))
            {
                list.Add(new HotkeyConflict
                {
                    Scheme = ConflictScheme.Blender, Plugin = plugin, Setting = setting, Keys = keys,
                    Note = "Rebind it, or turn off Numpad views.",
                });
            }

            if (kc == P.CycleSchemeKey.Value.MainKey)
            {
                list.Add(new HotkeyConflict
                {
                    Scheme = ConflictScheme.All, Plugin = plugin, Setting = setting, Keys = keys,
                    Note = "Fires along with Camera Nav's Cycle scheme. Rebind one of them.",
                });
            }
        }

        private static bool SameShortcut(KeyboardShortcut a, KeyboardShortcut b)
        {
            if (a.MainKey == KeyCode.None || a.MainKey != b.MainKey) return false;
            var am = new HashSet<string>(a.Modifiers.Select(ModName));
            var bm = new HashSet<string>(b.Modifiers.Select(ModName));
            return am.SetEquals(bm);
        }

        // ---------------------------------------------------------------- reporting

        public static string Summary()
        {
            var parts = new List<string>();
            AddCount(parts, ConflictScheme.Unity, "Unity");
            AddCount(parts, ConflictScheme.Blender, "Blender");
            AddCount(parts, ConflictScheme.All, "all-scheme");
            return parts.Count == 0
                ? "Camera Nav: no hotkey conflicts."
                : "Camera Nav: " + string.Join(", ", parts.ToArray()) + ". See F1 > Camera Nav.";
        }

        private static void AddCount(List<string> parts, ConflictScheme scheme, string label)
        {
            var n = Current.Count(c => c.Scheme == scheme);
            if (n > 0) parts.Add(n + " " + label + (n == 1 ? " conflict" : " conflicts"));
        }

        private static void Report()
        {
            var sb = new StringBuilder();
            foreach (var c in Current)
                sb.Append('[').Append(Tag(c.Scheme)).Append("] ").Append(c.Plugin).Append(" > ").Append(c.Setting)
                  .Append(": ").Append(c.Keys).Append(". ").Append(c.Note).Append('\n');
            var details = sb.ToString();
            if (details == _lastDetails) return;

            var first = !_announcedOnce;
            _announcedOnce = true;
            _lastDetails = details;

            var summary = Summary();
            CameraNavPlugin.Logger.LogInfo(Current.Count == 0 ? summary : summary + "\n" + details.TrimEnd());

            // On screen: at startup only if there's something to fix; afterwards whenever the list changes.
            if (!P.ShowConflictMessage.Value) return;
            if (first && Current.Count == 0) return;
            CameraNavPlugin.Logger.LogMessage(Current.Count == 0 ? "Camera Nav: no hotkey conflicts left." : summary);
        }

        public static string Tag(ConflictScheme s)
        {
            switch (s)
            {
                case ConflictScheme.Unity: return "Unity (fly)";
                case ConflictScheme.Blender: return "Blender (numpad)";
                default: return "All schemes";
            }
        }

        /// <summary>ConfigurationManager custom drawer for the conflict list in F1.</summary>
        public static void Draw(ConfigEntryBase entry)
        {
            if (_wrap == null) _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            if (Current.Count == 0)
            {
                GUILayout.Label("No other plugin's hotkeys clash with Camera Nav.", _wrap);
            }
            else
            {
                foreach (var c in Current)
                {
                    GUILayout.Label("<b>" + Tag(c.Scheme) + "</b>   " + c.Plugin + " > " + c.Setting + ":  <b>" + c.Keys + "</b>", _wrap);
                    GUILayout.Label("      " + c.Note, _wrap);
                }
            }

            if (GUILayout.Button("Rescan", GUILayout.ExpandWidth(false))) MarkDirty();
            GUILayout.EndVertical();
        }

        // ---------------------------------------------------------------- key names

        private static string Pretty(KeyboardShortcut ks)
        {
            var mods = ks.Modifiers.Select(ModName).Distinct().ToList();
            mods.Add(KeyName(ks.MainKey));
            return string.Join("+", mods.ToArray());
        }

        private static string ModName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt: return "Alt";
                default: return KeyName(k);
            }
        }

        private static string KeyName(KeyCode k)
        {
            if (k == KeyCode.KeypadPeriod) return "Numpad .";
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return "Numpad " + (k - KeyCode.Keypad0);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
            return k.ToString();
        }
    }
}
