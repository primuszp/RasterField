using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace RasterField
{
    /// <summary>Avalonia application entry point (code-only, no XAML).</summary>
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(AppStyles.CreateFluentTheme());
            Styles.Add(AppStyles.Create());
            AppStyles.AddResources(Resources);
            L.SetLanguage(AppSettings.Load().Language);

            // Follow the OS light/dark setting by default (live — FluentTheme reacts to it
            // automatically), unless the user has explicitly forced one from View ▸ Theme.
            RequestedThemeVariant = AppSettings.Load().Theme switch
            {
                ThemeMode.Light => Avalonia.Styling.ThemeVariant.Light,
                ThemeMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new MainWindow();
                desktop.MainWindow = window;

                if (Program.StartupProject != null)
                    window.Opened += async (_, _) => await window.LoadProjectAsync(Program.StartupProject);
                else if (Program.StartupFiles.Length > 0 || Program.StartupVectorFiles.Length > 0)
                {
                    window.Opened += (_, _) =>
                    {
                        if (Program.StartupFiles.Length > 0)
                        {
                            window.OpenDataset(Program.StartupFiles[0]);
                            for (int i = 1; i < Program.StartupFiles.Length; i++)
                                window.AddLayer(Program.StartupFiles[i]);
                        }
                        foreach (var path in Program.StartupVectorFiles)
                            window.AddVectorLayer(path);
                    };
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
