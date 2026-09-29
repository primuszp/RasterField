using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RasterField
{
    /// <summary>
    /// Persisted per-user app preferences — recent files, window geometry, last-used palette and
    /// stretch — stored as a small JSON file next to the palette folders
    /// (<c>%AppData%/RasterField/settings.json</c> on Windows, the XDG/Library equivalents
    /// elsewhere). Loading or saving never throws: a missing/corrupt file just starts fresh, and
    /// a failed save is silently skipped rather than crashing the app.
    /// </summary>
    public sealed class AppSettings
    {
        private const int MaxRecentFiles = 10;

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public List<string> RecentFiles { get; set; } = new();

        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public double? WindowX { get; set; }
        public double? WindowY { get; set; }
        public bool WindowMaximized { get; set; }

        public string? LastPaletteName { get; set; }
        public bool LastPaletteReversed { get; set; }
        public int LastStretchIndex { get; set; } = 2; // "2 – 98 %", matching PopulateControls' own default

        public string? LastOpenDirectory { get; set; }

        /// <summary>The user's theme preference; "System" (the default) follows the OS light/dark setting live.</summary>
        public ThemeMode Theme { get; set; } = ThemeMode.System;

        /// <summary>UI language: <c>auto</c> (follow the OS), <c>en</c> or <c>hu</c>.</summary>
        public string Language { get; set; } = "auto";

        /// <summary>Left / right dock widths (0 = collapsed).</summary>
        public double LeftDockWidth { get; set; } = 290;
        public double RightDockWidth { get; set; } = 300;

        /// <summary>The last project file opened or saved.</summary>
        public string? LastProjectPath { get; set; }

        /// <summary>Path of the settings file, creating the containing folder if needed.</summary>
        public static string SettingsPath()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create);
            return Path.Combine(baseDir, "RasterField", "settings.json");
        }

        /// <summary>Loads the settings file, or returns defaults when it is missing or unreadable.</summary>
        public static AppSettings Load()
        {
            try
            {
                string path = SettingsPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        loaded.RecentFiles ??= new List<string>();
                        return loaded;
                    }
                }
            }
            catch
            {
                // Corrupt or unreadable settings file: fall back to defaults rather than crash.
            }
            return new AppSettings();
        }

        /// <summary>Writes the settings file; failures (read-only profile, disk full, …) are swallowed.</summary>
        public void Save()
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
            }
            catch
            {
                // Best-effort persistence; losing preferences is fine, crashing on save is not.
            }
        }

        /// <summary>Moves <paramref name="path"/> to the front of the recent-files list, trimmed to <see cref="MaxRecentFiles"/>.</summary>
        public void AddRecentFile(string path)
        {
            RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, path);
            while (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }
    }
}
