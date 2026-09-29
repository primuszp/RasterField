using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static RasterField.L;

namespace RasterField
{
    /// <summary>
    /// The app bar across the top of the window, modelled on apps such as Claude: a sidebar toggle
    /// and the document title on the left, a command search pill and the inspector toggle on the
    /// right, on one quiet hairline-bordered bar.
    /// <para>On macOS it is the unified title bar: the client area extends under the system title
    /// bar (see the constructor), the traffic lights sit in its left inset, and the menus live in
    /// the system menu bar. On Windows/Linux it sits under the system title bar and starts with a
    /// ☰ button that opens the same menus as one dropdown instead of an in-window menu strip.</para>
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>Height of the unified title bar on macOS (the system centres the traffic lights in it).</summary>
        internal const double MacTitleBarHeight = 44;

        /// <summary>Height of the app bar on Windows/Linux, below the system title bar.</summary>
        private const double AppBarHeight = 40;

        /// <summary>The ☰ dropdown holding the menus on Windows/Linux (null on macOS).</summary>
        private MenuFlyout? _menuFlyout;

        /// <summary>Room left of the first title-bar control for the three traffic-light buttons.</summary>
        private const double TrafficLightInset = 80;

        private Border? _titleBar;
        private readonly TextBlock _titleText = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly TextBlock _titleDocText = new TextBlock
        {
            FontSize = AppTheme.FontBody,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly Border _titleDocChip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        private readonly TextBlock _searchLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _searchShortcut = new TextBlock { Text = "⌘K", VerticalAlignment = VerticalAlignment.Center };
        private Border? _searchPill;
        private readonly Shape[] _titleIconStrokes = new Shape[7];

        private Border BuildTitleBar()
        {
            bool mac = OperatingSystem.IsMacOS();
            var leftToggle = TitleBarButton(SidebarIcon(leftSide: true, 0), T("Show / hide the layers panel (F9)"), ToggleLeftDock);
            var rightToggle = TitleBarButton(SidebarIcon(leftSide: false, 2), T("Show / hide the properties panel (F10)"), ToggleRightDock);

            _titleDocChip.Child = _titleDocText;
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(mac ? TrafficLightInset : 8, 0, 0, 0),
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!mac) left.Children.Add(BuildMenuButton());
            left.Children.Add(leftToggle);
            left.Children.Add(_titleText);
            left.Children.Add(_titleDocChip);

            // The "search" pill: a quiet, rounded field-lookalike that opens the command palette.
            _searchLabel.Text = T("Search commands");
            _searchLabel.FontSize = AppTheme.FontBody;
            _searchShortcut.FontSize = AppTheme.FontCaption;
            var magnifier = new Path
            {
                Data = Geometry.Parse("M6,1 A5,5 0 1 1 6,11 A5,5 0 1 1 6,1 Z M9.6,9.6 L13,13"),
                StrokeThickness = 1.4,
                StrokeLineCap = PenLineCap.Round,
                Width = 14, Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _titleIconStrokes[4] = magnifier;
            var pillContent = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(magnifier, Dock.Left);
            DockPanel.SetDock(_searchShortcut, Dock.Right);
            _searchLabel.Margin = new Thickness(8, 0, 12, 0);
            pillContent.Children.Add(magnifier);
            pillContent.Children.Add(_searchShortcut);
            pillContent.Children.Add(_searchLabel);
            _searchPill = new Border
            {
                Child = pillContent,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0),
                Height = 28,
                MinWidth = 220,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _searchShortcut.Text = mac ? "⌘K" : "Ctrl+K";
            ToolTip.SetTip(_searchPill, mac ? T("Command palette (⌘K)") : T("Command palette (Ctrl+K)"));
            _searchPill.PointerPressed += (_, e) => { e.Handled = true; _ = ShowCommandPaletteAsync(); };
            _searchPill.PointerEntered += (_, _) => ApplyTitleBarTheme(hoverSearch: true);
            _searchPill.PointerExited += (_, _) => ApplyTitleBarTheme();

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 10, 0),
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _searchPill, rightToggle },
            };

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(left, Dock.Left);
            row.Children.Add(right);
            row.Children.Add(left);
            row.Children.Add(new Panel()); // the empty middle: pure drag area

            var bar = new Border
            {
                Height = mac ? MacTitleBarHeight : AppBarHeight,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = row,
            };

            // Drag the window from any empty part of the bar; double-click zooms, as on every Mac
            // window. (Elsewhere the system title bar above it already does this.)
            if (mac) bar.PointerPressed += (_, e) =>
            {
                if (e.Handled || !e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2) ToggleMaximized();
                else BeginMoveDrag(e);
            };

            _titleBar = bar;
            UpdateTitleBarText();
            ApplyTitleBarTheme();
            return bar;
        }

        /// <summary>The ☰ button: every top-level menu of <see cref="BuildMenuModel"/> as one dropdown.</summary>
        private Button BuildMenuButton()
        {
            var icon = new Path
            {
                Data = Geometry.Parse("M2,4 H16 M2,9 H16 M2,14 H16"),
                StrokeThickness = 1.4,
                StrokeLineCap = PenLineCap.Round,
                Width = 18, Height = 18,
            };
            _titleIconStrokes[6] = icon;
            _menuFlyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            PopulateMenuFlyout();
            var button = new Button { Content = icon, Flyout = _menuFlyout };
            button.Classes.Add("titlebar");
            ToolTip.SetTip(button, T("Menu"));
            return button;
        }

        /// <summary>(Re)fills the ☰ dropdown from the menu model — at startup and after a language / bookmark change.</summary>
        private void PopulateMenuFlyout()
        {
            if (_menuFlyout == null) return;
            _menuFlyout.Items.Clear();
            foreach (var top in BuildMenuModel()) _menuFlyout.Items.Add(ToMenuItem(top));
        }

        private static Button TitleBarButton(Control icon, string tip, Action run)
        {
            var b = new Button { Content = icon };
            b.Classes.Add("titlebar");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => run();
            return b;
        }

        /// <summary>The familiar macOS "sidebar" glyph: a rounded window outline with a divider on one side.</summary>
        private Panel SidebarIcon(bool leftSide, int strokeSlot)
        {
            var frame = new Path
            {
                Data = Geometry.Parse("M3.5,1.5 H14.5 A2,2 0 0 1 16.5,3.5 V12.5 A2,2 0 0 1 14.5,14.5 H3.5 A2,2 0 0 1 1.5,12.5 V3.5 A2,2 0 0 1 3.5,1.5 Z"),
                StrokeThickness = 1.3,
            };
            var divider = new Path
            {
                Data = Geometry.Parse(leftSide ? "M6.5,1.5 V14.5" : "M11.5,1.5 V14.5"),
                StrokeThickness = 1.3,
            };
            _titleIconStrokes[strokeSlot] = frame;
            _titleIconStrokes[strokeSlot + 1] = divider;
            return new Panel { Width = 18, Height = 16, Children = { frame, divider } };
        }

        /// <summary>Keeps the title bar's text in step with the project / active layer.</summary>
        private void UpdateTitleBarText()
        {
            if (_titleBar == null) return;
            _titleText.Text = _projectPath != null ? System.IO.Path.GetFileNameWithoutExtension(_projectPath) : "RasterField";
            string? layer = _view.ActiveLayer is { IsFrame: false } a ? a.Name : null;
            _titleDocText.Text = layer ?? "";
            _titleDocChip.IsVisible = layer != null;
        }

        private void ApplyTitleBarTheme(bool hoverSearch = false)
        {
            if (_titleBar == null) return;
            _titleBar.Background = AppTheme.WindowBackground;
            _titleBar.BorderBrush = AppTheme.Border;
            _titleText.Foreground = AppTheme.TextPrimary;
            _titleDocText.Foreground = AppTheme.TextSecondary;
            _titleDocChip.Background = AppTheme.BarBackground;
            foreach (var s in _titleIconStrokes) if (s != null) s.Stroke = AppTheme.TextSecondary;
            if (_searchPill != null)
            {
                _searchPill.Background = hoverSearch ? AppTheme.BarBackground : AppTheme.PanelBackground;
                _searchPill.BorderBrush = AppTheme.Border;
            }
            _searchLabel.Foreground = AppTheme.TextSecondary;
            _searchShortcut.Foreground = AppTheme.TextSecondary;
        }
    }
}
