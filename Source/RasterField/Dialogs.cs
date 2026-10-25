using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using static RasterField.L;

namespace RasterField
{
    /// <summary>
    /// The app's modal dialogs, built one way: the same window shape and margins, a body, and a
    /// button row in the platform's order — the primary action last on macOS and Linux, first
    /// on Windows — with the primary button as the accented default (Enter) and Cancel bound to
    /// Esc. Each dialog keeps only its own controls and logic.
    /// </summary>
    internal static class Dialogs
    {
        /// <summary>Outer margin of every dialog (macOS HIG: 20 pt).</summary>
        private const double Margin = 20;

        /// <summary>A primary (accented, Enter) button for <see cref="ShowAsync{TResult}"/>; create it first when the dialog enables/disables it.</summary>
        public static Button Primary(string text)
        {
            var button = new Button { Content = text, IsDefault = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Classes.Add("accent");
            return button;
        }

        /// <summary>A secondary button (e.g. "Export CSV…") placed before the primary/cancel pair.</summary>
        public static Button Secondary(string text) =>
            new Button { Content = text, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };

        /// <summary>
        /// Shows <paramref name="body"/> with <paramref name="primary"/> and Cancel. Resolves to
        /// <paramref name="accept"/>()'s value when the primary button (or Enter) is used, and to
        /// <paramref name="cancelValue"/> on Cancel, Esc or closing the window.
        /// </summary>
        public static async Task<TResult> ShowAsync<TResult>(Window owner, string title, Control body, Button primary, Func<TResult> accept, TResult cancelValue,
            double width = 400, Action<Window>? opened = null, params Button[] secondary)
        {
            var tcs = new TaskCompletionSource<TResult>();
            var window = Create(title, width);
            var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            primary.Click += (_, _) => { tcs.TrySetResult(accept()); window.Close(); };
            cancel.Click += (_, _) => window.Close();
            window.Content = Layout(body, ButtonRow(primary, cancel, secondary));
            window.Closed += (_, _) => tcs.TrySetResult(cancelValue);
            if (opened != null) window.Opened += (_, _) => opened(window);
            await window.ShowDialog(owner);
            return await tcs.Task;
        }

        /// <summary>
        /// An informational dialog with a single Close button (Enter and Esc both close it), plus
        /// optional secondary actions such as "Export CSV…".
        /// </summary>
        public static Task InfoAsync(Window owner, string title, Control body, double width = 460, double? height = null, params Button[] secondary)
        {
            var window = Create(title, width, height);
            var close = new Button { Content = T("Close"), IsDefault = true, IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            close.Classes.Add("accent");
            close.Click += (_, _) => window.Close();
            window.Content = Layout(body, ButtonRow(close, null, secondary));
            return window.ShowDialog(owner);
        }

        /// <summary>A message with an OK button.</summary>
        public static Task MessageAsync(Window owner, string title, string message) =>
            InfoAsyncWithOk(owner, title, Paragraph(message));

        /// <summary>Asks a yes/no question; <paramref name="confirmText"/> names the action ("Discard", "Overwrite"…).</summary>
        public static Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText) =>
            ShowAsync(owner, title, Paragraph(message), Primary(confirmText), () => true, false, width: 460);

        /// <summary>Asks for one line of text; <see langword="null"/> when cancelled.</summary>
        public static Task<string?> AskTextAsync(Window owner, string title, string label, string value, string? okText = null)
        {
            var box = new TextBox { Text = value };
            var body = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, box } };
            return ShowAsync<string?>(owner, title, body, Primary(okText ?? T("OK")), () => box.Text, null, width: 400,
                opened: _ => { box.Focus(); box.SelectAll(); });
        }

        /// <summary>
        /// The platform's Save panel; the local path chosen, or <see langword="null"/> when cancelled.
        /// The default extension comes from <paramref name="suggestedFileName"/>. No start folder is
        /// forced, so macOS opens the panel where the user last saved, as native apps do.
        /// </summary>
        public static async Task<string?> SavePathAsync(TopLevel owner, string title, string suggestedFileName, IReadOnlyList<FilePickerFileType> types)
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedFileName,
                DefaultExtension = Path.GetExtension(suggestedFileName).TrimStart('.'),
                FileTypeChoices = types,
                ShowOverwritePrompt = true,
            });
            string? path = file?.TryGetLocalPath();
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // ---- building blocks shared by the dialogs ------------------------------------------------

        /// <summary>A small caption above a control.</summary>
        public static StackPanel Captioned(string caption, Control control) =>
            new StackPanel { Spacing = 3, Children = { AppTheme.Caption(caption), control } };

        /// <summary>Controls side by side.</summary>
        public static StackPanel Row(params Control[] controls)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            foreach (var c in controls) row.Children.Add(c);
            return row;
        }

        /// <summary>A two-column form: labels sized to the longest one (so translations fit), fields beside them.</summary>
        public static Grid Form(params (string Label, Control Field)[] rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 12 };
            for (int r = 0; r < rows.Length; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = rows[r].Label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(label, r);
                Grid.SetRow(rows[r].Field, r);
                Grid.SetColumn(rows[r].Field, 1);
                grid.Children.Add(label);
                grid.Children.Add(rows[r].Field);
            }
            return grid;
        }

        /// <summary>Wrapped body text.</summary>
        public static TextBlock Paragraph(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };

        /// <summary>A muted, smaller explanation under a control or at the top of a dialog.</summary>
        public static TextBlock Hint(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = AppTheme.FontCaption,
            Foreground = AppTheme.TextSecondary,
        };

        /// <summary>A vertical stack of a dialog's controls with the standard spacing.</summary>
        public static StackPanel Stack(params Control[] controls)
        {
            var stack = new StackPanel { Spacing = 10 };
            foreach (var c in controls) stack.Children.Add(c);
            return stack;
        }

        // ---- internals -------------------------------------------------------------------------------

        private static Task InfoAsyncWithOk(Window owner, string title, Control body)
        {
            var window = Create(title, 440);
            var ok = new Button { Content = T("OK"), IsDefault = true, IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => window.Close();
            window.Content = Layout(body, ButtonRow(ok, null, Array.Empty<Button>()));
            return window.ShowDialog(owner);
        }

        /// <summary>A bare dialog window in the app's shape, for dialogs with their own layout (the command palette).</summary>
        public static Window CreateWindow(string title, double width) => Create(title, width);

        private static Window Create(string title, double width, double? height = null)
        {
            var window = new Window
            {
                Title = title,
                Width = width,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = height != null,
                ShowInTaskbar = false,
                Background = AppTheme.PanelBackground,
            };
            if (height is double h) window.Height = h;
            else window.SizeToContent = SizeToContent.Height;
            return window;
        }

        private static DockPanel Layout(Control body, Control buttons)
        {
            var root = new DockPanel { Margin = new Thickness(Margin) };
            buttons.Margin = new Thickness(0, 16, 0, 0);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(body);
            return root;
        }

        /// <summary>Secondary actions on the left; the primary/cancel pair on the right, in the platform's order.</summary>
        private static DockPanel ButtonRow(Button primary, Button? cancel, Button[] secondary)
        {
            var row = new DockPanel();
            var main = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            if (cancel == null) main.Children.Add(primary);
            else if (OperatingSystem.IsWindows()) { main.Children.Add(primary); main.Children.Add(cancel); }
            else { main.Children.Add(cancel); main.Children.Add(primary); }
            DockPanel.SetDock(main, Dock.Right);
            row.Children.Add(main);
            if (secondary.Length > 0)
            {
                var extra = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var b in secondary) extra.Children.Add(b);
                row.Children.Add(extra);
            }
            return row;
        }
    }
}
