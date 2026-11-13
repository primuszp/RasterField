using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using RasterField.Rendering;

using static RasterField.L;

namespace RasterField
{
    /// <summary>
    /// A small dialog for building or tweaking a palette: drag gradient stops on
    /// <see cref="PaletteEditorControl"/>, fine-tune the selected stop's colour and position,
    /// preview the result live in the main viewer, then save it into the running session's
    /// palette list and/or as a <c>.pal</c> file.
    /// </summary>
    public sealed class PaletteEditorWindow : Window
    {

        private readonly PaletteEditorControl _editor = new PaletteEditorControl();
        private readonly TextBox _nameBox = new TextBox { Watermark = T("Palette name") };
        // Explicit widths: the FluentTheme NumericUpDown's spin-button chrome needs more room than
        // its natural content alone suggests — squeeze it into a too-narrow Grid column (as these
        // used to be, at 55-60px) and the digits get crowded out entirely while the buttons still
        // claim the space, leaving what looks like an oversized, numberless control.
        private readonly NumericUpDown _posBox = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 1, FormatString = "0.#", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly NumericUpDown _rBox = new NumericUpDown { Minimum = 0, Maximum = 255, Increment = 1, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly NumericUpDown _gBox = new NumericUpDown { Minimum = 0, Maximum = 255, Increment = 1, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly NumericUpDown _bBox = new NumericUpDown { Minimum = 0, Maximum = 255, Increment = 1, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _hexBox = new TextBox { Watermark = "#RRGGBB", Width = 90, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = Avalonia.Media.TextAlignment.Center };
        private readonly ComboBox _startFromBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        private readonly PaletteLibrary _library;
        private readonly Action<Palette> _onPreview;
        private readonly Action<Palette> _onSavedToLibrary;
        private bool _syncing;

        public PaletteEditorWindow(PaletteLibrary library, Palette? seed, Action<Palette> onPreview, Action<Palette> onSavedToLibrary)
        {
            _library = library;
            _onPreview = onPreview;
            _onSavedToLibrary = onSavedToLibrary;

            Title = T("Palette editor");
            Width = 560;
            Height = 500;
            Background = AppTheme.PanelBackground;
            CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Content = BuildLayout();
            WireEvents();

            _startFromBox.ItemsSource = library.Names.ToList();
            if (seed != null)
            {
                _nameBox.Text = seed.Name;
                _editor.SetStops(seed.ExtractStops(8));
            }
        }

        private DockPanel BuildLayout()
        {
            var root = new StackPanel { Margin = new Avalonia.Thickness(20, 8, 20, 20), Spacing = 4 };

            // Start from an existing palette.
            root.Children.Add(AppTheme.SectionLabel(T("Start from:").TrimEnd(':')));
            var loadBtn = new Button { Content = T("Load"), Margin = new Avalonia.Thickness(6, 0, 0, 0), MinWidth = 72 };
            loadBtn.Click += (_, _) => LoadSeed();
            Grid.SetColumn(loadBtn, 1);
            root.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _startFromBox, loadBtn } });

            root.Children.Add(AppTheme.SectionLabel(T("Name")));
            root.Children.Add(_nameBox);

            root.Children.Add(AppTheme.SectionLabel(T("Gradient")));
            root.Children.Add(new TextBlock
            {
                Text = T("Gradient — click the bar to add a stop, drag a marker to move it, Delete to remove"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = AppTheme.FontCaption,
                Foreground = AppTheme.TextSecondary,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
            });
            root.Children.Add(_editor);

            // The selected stop: five equal columns, each a caption over its field.
            root.Children.Add(AppTheme.SectionLabel(T("Selected stop")));
            var editRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,*,8,*,8,*") };
            void Col(string caption, Control field, int i)
            {
                field.Width = double.NaN;
                field.HorizontalAlignment = HorizontalAlignment.Stretch;
                var cell = new StackPanel { Children = { AppTheme.Caption(caption), field } };
                Grid.SetColumn(cell, i * 2);
                editRow.Children.Add(cell);
            }
            Col(T("Pos %"), _posBox, 0);
            Col("R", _rBox, 1);
            Col("G", _gBox, 2);
            Col("B", _bBox, 3);
            Col(T("Hex"), _hexBox, 4);
            root.Children.Add(editRow);

            var addBtn = new Button { Content = T("Add stop") };
            addBtn.Click += (_, _) => _editor.AddStopAt(0.5);
            var removeBtn = new Button { Content = T("Remove stop") };
            removeBtn.Click += (_, _) => _editor.RemoveSelected();
            var reverseBtn = new Button { Content = T("Reverse") };
            reverseBtn.Click += (_, _) => _editor.Reverse();
            root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Avalonia.Thickness(0, 10, 0, 0), Children = { addBtn, removeBtn, reverseBtn } });

            // Footer: saving on the right, the library save as the primary action.
            var saveLibBtn = new Button { Content = T("Save to palette list") };
            saveLibBtn.Classes.Add("accent");
            saveLibBtn.Click += (_, _) => SaveToLibrary();
            var saveFileBtn = new Button { Content = T("Save as .pal file…") };
            saveFileBtn.Click += async (_, _) => await SaveAsFileAsync();
            var closeBtn = new Button { Content = T("Close"), IsCancel = true, MinWidth = 88 };
            closeBtn.Click += (_, _) => Close();
            var footer = new Border
            {
                BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
                BorderBrush = AppTheme.Border,
                Padding = new Avalonia.Thickness(20, 12),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { saveFileBtn, closeBtn, saveLibBtn } },
            };
            var layout = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
            layout.Children.Add(new ScrollViewer { Content = root });
            return layout;
        }

        private void WireEvents()
        {
            _editor.Changed += (_, _) => { Preview(); RefreshSelectedFields(); };
            _editor.SelectionChanged += (_, _) => RefreshSelectedFields();

            _posBox.ValueChanged += (_, _) => { if (!_syncing) _editor.SetSelectedPosition((double)(_posBox.Value ?? 0) / 100.0); };
            _rBox.ValueChanged += (_, _) => ApplyRgbBoxes();
            _gBox.ValueChanged += (_, _) => ApplyRgbBoxes();
            _bBox.ValueChanged += (_, _) => ApplyRgbBoxes();
            _hexBox.LostFocus += (_, _) => ApplyHexBox();

            RefreshSelectedFields();
            Preview();
        }

        private void LoadSeed()
        {
            string? name = _startFromBox.SelectedItem as string;
            Palette? p = name != null ? _library.Get(name) : null;
            if (p == null) return;
            _nameBox.Text = p.Name;
            _editor.SetStops(p.ExtractStops(8));
        }

        private void RefreshSelectedFields()
        {
            var s = _editor.Selected;
            _syncing = true;
            if (s != null)
            {
                _posBox.Value = (decimal)(s.Position * 100.0);
                _rBox.Value = s.Color.R;
                _gBox.Value = s.Color.G;
                _bBox.Value = s.Color.B;
                _hexBox.Text = s.Color.ToString();
            }
            _syncing = false;
        }

        private void ApplyRgbBoxes()
        {
            if (_syncing || _editor.Selected == null) return;
            byte r = (byte)(_rBox.Value ?? 0);
            byte g = (byte)(_gBox.Value ?? 0);
            byte b = (byte)(_bBox.Value ?? 0);
            _editor.SetSelectedColor(new ColorRgba(r, g, b));
            _syncing = true; _hexBox.Text = _editor.Selected!.Color.ToString(); _syncing = false;
        }

        private void ApplyHexBox()
        {
            if (_syncing || _editor.Selected == null || string.IsNullOrWhiteSpace(_hexBox.Text)) return;
            try
            {
                var c = ColorRgba.ParseHex(_hexBox.Text!);
                _editor.SetSelectedColor(c);
                _syncing = true; _rBox.Value = c.R; _gBox.Value = c.G; _bBox.Value = c.B; _syncing = false;
            }
            catch (FormatException) { /* leave the text box as typed; user is probably mid-edit */ }
        }

        private string CurrentName() => string.IsNullOrWhiteSpace(_nameBox.Text) ? T("Custom palette") : _nameBox.Text!.Trim();

        private void Preview() => _onPreview(_editor.Bake(CurrentName()));

        private void SaveToLibrary()
        {
            var palette = _editor.Bake(CurrentName());
            _library.Add(palette);
            _onSavedToLibrary(palette);
            TrySavePalFile(palette, PaletteStorage.UserPaletteDirectory());
        }

        private async Task SaveAsFileAsync()
        {
            var path = await Dialogs.SavePathAsync(this, T("Save palette"), CurrentName() + ".pal", FileTypes.Only(FileTypes.Palette));
            if (string.IsNullOrEmpty(path)) return;

            var palette = _editor.Bake(CurrentName());
            PaletteFile.Save(palette, path!);
        }

        private static void TrySavePalFile(Palette palette, string? directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string safeName = string.Join("_", palette.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                PaletteFile.Save(palette, System.IO.Path.Combine(directory, safeName + ".pal"));
            }
            catch (Exception)
            {
                // best effort: the palette is still available for this session via the library
            }
        }
    }
}
