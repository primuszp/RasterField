using System;
using System.Text;
using Avalonia.Input;

namespace RasterField
{
    /// <summary>
    /// Keyboard shortcuts as the platform shows them: on macOS the menu glyphs in the system's
    /// order (⌃⌥⇧⌘ then the key, e.g. ⇧⌘S), elsewhere "Ctrl+Shift+S". Commands are declared with
    /// Ctrl; <see cref="ForPlatform"/> maps that to ⌘ on macOS, where Ctrl is not the command key.
    /// </summary>
    internal static class Shortcuts
    {
        /// <summary>The gesture to use on this platform: <paramref name="mac"/> verbatim on macOS when given, else Ctrl mapped to ⌘ there.</summary>
        public static KeyGesture? ForPlatform(KeyGesture? gesture, KeyGesture? mac = null)
        {
            if (!OperatingSystem.IsMacOS()) return gesture;
            if (mac != null) return mac;
            if (gesture == null) return null;
            var mods = gesture.KeyModifiers;
            if (mods.HasFlag(KeyModifiers.Control)) mods = (mods & ~KeyModifiers.Control) | KeyModifiers.Meta;
            return new KeyGesture(gesture.Key, mods);
        }

        /// <summary>Whether a key event is <paramref name="gesture"/> (numeric-keypad +/− count as the main-row keys).</summary>
        public static bool Matches(KeyGesture gesture, KeyEventArgs e) =>
            gesture.KeyModifiers == e.KeyModifiers &&
            (gesture.Key == e.Key
             || (gesture.Key == Key.OemPlus && e.Key == Key.Add)
             || (gesture.Key == Key.OemMinus && e.Key == Key.Subtract));

        /// <summary>A gesture as text, in the platform's notation.</summary>
        public static string Text(KeyGesture gesture)
        {
            var m = gesture.KeyModifiers;
            if (OperatingSystem.IsMacOS())
            {
                var sb = new StringBuilder();
                if (m.HasFlag(KeyModifiers.Control)) sb.Append('⌃');
                if (m.HasFlag(KeyModifiers.Alt)) sb.Append('⌥');
                if (m.HasFlag(KeyModifiers.Shift)) sb.Append('⇧');
                if (m.HasFlag(KeyModifiers.Meta)) sb.Append('⌘');
                return sb.Append(KeyText(gesture.Key)).ToString();
            }
            var parts = new StringBuilder();
            if (m.HasFlag(KeyModifiers.Control)) parts.Append("Ctrl+");
            if (m.HasFlag(KeyModifiers.Alt)) parts.Append("Alt+");
            if (m.HasFlag(KeyModifiers.Shift)) parts.Append("Shift+");
            if (m.HasFlag(KeyModifiers.Meta)) parts.Append("Win+");
            return parts.Append(KeyText(gesture.Key)).ToString();
        }

        /// <summary>A single key as text (a plain-letter tool shortcut, or the key part of a gesture).</summary>
        public static string KeyText(Key key) => key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            Key.OemPlus or Key.Add => "+",
            Key.OemMinus or Key.Subtract => "−",
            Key.OemComma => ",",
            Key.Enter => OperatingSystem.IsMacOS() ? "↩" : "Enter",
            Key.Escape => OperatingSystem.IsMacOS() ? "⎋" : "Esc",
            Key.Back => OperatingSystem.IsMacOS() ? "⌫" : "Backspace",
            Key.Tab => OperatingSystem.IsMacOS() ? "⇥" : "Tab",
            _ => key.ToString(),
        };
    }
}
