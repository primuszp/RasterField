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
        private static readonly FilePickerFileType[] PalFileTypes =
            { new("DigiTerra palette (*.pal)") { Patterns = new[] { "*.pal" } } };

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
        private readonly TextBox _hexBox = new TextBox { Watermark = T("#RRGGBB"), Width = 90 };
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
            Height = 420;
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

        private ScrollViewer BuildLayout()
        {
            var root = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 8 };

            var startRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            startRow.Children.Add(new TextBlock { Text = T("Start from:"), VerticalAlignment = VerticalAlignment.Center });
            startRow.Children.Add(_startFromBox);
            var loadBtn = new Button { Content = T("Load") };
            loadBtn.Click += (_, _) => LoadSeed();
            startRow.Children.Add(loadBtn);
            root.Children.Add(startRow);

            root.Children.Add(new TextBlock { Text = T("Name") });
            root.Children.Add(_nameBox);

            root.Children.Add(new TextBlock { Text = T("Gradient — click the bar to add a stop, drag a marker to move it, Delete to remove") });
            root.Children.Add(_editor);

            var editRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,90,Auto,80,Auto,80,Auto,80,Auto,90"), Margin = new Avalonia.Thickness(0, 6) };
            void Col(Control c, int i) { Grid.SetColumn(c, i); editRow.Children.Add(c); }
            Col(new TextBlock { Text = T("Pos %"), VerticalAlignment = VerticalAlignment.Center }, 0);
            Col(_posBox, 1);
            Col(new TextBlock { Text = T("R"), VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(8, 0, 0, 0) }, 2);
            Col(_rBox, 3);
            Col(new TextBlock { Text = T("G"), VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(8, 0, 0, 0) }, 4);
            Col(_gBox, 5);
            Col(new TextBlock { Text = T("B"), VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(8, 0, 0, 0) }, 6);
            Col(_bBox, 7);
            Col(new TextBlock { Text = T("Hex"), VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(8, 0, 0, 0) }, 8);
            Col(_hexBox, 9);
            root.Children.Add(editRow);

            var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var addBtn = new Button { Content = T("Add stop") };
            addBtn.Click += (_, _) => _editor.AddStopAt(0.5);
            var removeBtn = new Button { Content = T("Remove stop") };
            removeBtn.Click += (_, _) => _editor.RemoveSelected();
            var reverseBtn = new Button { Content = T("Reverse") };
            reverseBtn.Click += (_, _) => _editor.Reverse();
            toolRow.Children.Add(addBtn);
            toolRow.Children.Add(removeBtn);
            toolRow.Children.Add(reverseBtn);
            root.Children.Add(toolRow);

            var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
            var saveLibBtn = new Button { Content = T("Save to palette list") };
            saveLibBtn.Click += (_, _) => SaveToLibrary();
            var saveFileBtn = new Button { Content = T("Save as .pal file…") };
            saveFileBtn.Click += async (_, _) => await SaveAsFileAsync();
            var closeBtn = new Button { Content = T("Close") };
            closeBtn.Click += (_, _) => Close();
            bottomRow.Children.Add(saveLibBtn);
            bottomRow.Children.Add(saveFileBtn);
            bottomRow.Children.Add(closeBtn);
            root.Children.Add(bottomRow);

            return new ScrollViewer { Content = root };
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
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Save palette"),
                DefaultExtension = "pal",
                SuggestedFileName = CurrentName() + ".pal",
                FileTypeChoices = PalFileTypes,
            });
            var path = file?.TryGetLocalPath();
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
