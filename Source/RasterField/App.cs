using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace RasterField
{
    /// <summary>Avalonia application entry point (code-only, no XAML).</summary>
    public sealed class App : Application
    {
        public override void Initialize()
        {
            // Shown as the bold application menu in the macOS menu bar (and in the Dock tooltip
            // when run outside an .app bundle) instead of Avalonia's "Avalonia Application".
            Name = "RasterField";
            Styles.Add(AppStyles.CreateFluentTheme());
            Styles.Add(AppStyles.Create());
            AppStyles.AddResources(Resources);
            L.SetLanguage(AppSettings.Load().Language);

            // Must be set here, before the platform builds the menu bar.
            BuildApplicationMenu();

            // Follow the OS light/dark setting by default (live — FluentTheme reacts to it
            // automatically), unless the user has explicitly chosen one in Settings.
            RequestedThemeVariant = AppSettings.Load().Theme switch
            {
                ThemeMode.Light => Avalonia.Styling.ThemeVariant.Light,
                ThemeMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }

        private MainWindow? Main => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

        /// <summary>
        /// The macOS application menu, localised like the rest of the app (Avalonia's default items
        /// are English-only, so they are switched off in <see cref="Program.BuildAvaloniaApp"/>):
        /// About, Settings… (⌘,), Hide (⌘H) and Quit (⌘Q) — Quit goes through the main window's
        /// close, so the warning about unsaved layers still applies. Rebuilt after a language change.
        /// </summary>
        public void BuildApplicationMenu()
        {
            if (!OperatingSystem.IsMacOS()) return;
            NativeMenuItem Item(string header, Action run, KeyGesture? gesture = null)
            {
                var item = new NativeMenuItem(header) { Gesture = gesture };
                item.Click += (_, _) => run();
                return item;
            }
            var menu = new NativeMenu
            {
                Item(L.T("About RasterField…"), () => _ = Main?.ShowAboutAsync()),
                new NativeMenuItemSeparator(),
                Item(L.T("Settings…"), () => _ = Main?.ShowSettingsAsync(), new KeyGesture(Key.OemComma, KeyModifiers.Meta)),
                new NativeMenuItemSeparator(),
                Item(L.T("Hide RasterField"), () => (TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime)?.TryEnterBackground(), new KeyGesture(Key.H, KeyModifiers.Meta)),
                new NativeMenuItemSeparator(),
                Item(L.T("Quit RasterField"), () => Main?.Close(), new KeyGesture(Key.Q, KeyModifiers.Meta)),
            };
            NativeMenu.SetMenu(this, menu);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new MainWindow();
                desktop.MainWindow = window;
                if (Program.StartupPaths.Count > 0)
                    window.Opened += async (_, _) => await window.OpenPathsAsync(Program.StartupPaths);

                // macOS: files opened from the Finder, dropped on the Dock icon or chosen with
                // "Open With" arrive as an activation (they are not command-line arguments there).
                if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                {
                    activatable.Activated += async (_, e) =>
                    {
                        if (e is not FileActivatedEventArgs files) return;
                        var paths = files.Files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
                        if (paths.Count > 0) await window.OpenPathsAsync(paths);
                    };
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
