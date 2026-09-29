using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using RasterField.ErMapper;
using RasterField.Gdal;
using RasterField.Rasters;
using RasterField.Rendering;
using RasterField.Vectors;

namespace RasterField
{
    /// <summary>Readout describing what is under the pointer, raised by <see cref="RasterView.PointerReadout"/>.</summary>
    public sealed class RasterReadoutEventArgs : EventArgs
    {
        public bool InsideRaster { get; init; }
        public double WorldX { get; init; }
        public double WorldY { get; init; }
        public int Column { get; init; } = -1;
        public int Row { get; init; } = -1;
        public float? Value { get; init; }
    }

    /// <summary>
    /// A cross-platform pan/zoom raster viewer that can show several datasets at once as an
    /// ordered stack of <see cref="RasterLayer"/>s (later in <see cref="Layers"/> = drawn on
    /// top), each with its own independent palette/stretch/gamma/band/RGB-composite display
    /// state, and its own visibility. Pan, zoom and every interactive tool (selection, profile
    /// line, the pointer value readout) operate against the <see cref="ActiveLayer"/>'s own
    /// pixel grid; every other visible layer is projected into that same screen space through its
    /// own georeference, so layers with different origins, cell sizes or even rotation still line
    /// up correctly.
    /// </summary>
    /// <remarks>
    /// For a layer where <see cref="ErsDocument.IsLargeDataset"/> is <see langword="true"/>, the
    /// view never materialises the full raster: it opens a <see cref="RasterSource"/> and, on
    /// every pan/zoom, re-reads only the currently visible window (with a small margin, and
    /// decimated to match the zoom level) for every streaming layer — not just the active one.
    /// </remarks>
    public sealed class RasterView : Control, IDisposable
    {
        private readonly List<RasterLayer> _layers = new List<RasterLayer>();
        // A single stack is the source of truth for paint order. Keeping raster and vector
        // ownership lists as well makes the existing public API and active-raster tools simple.
        private readonly List<object> _drawOrder = new List<object>();
        private int _activeLayerIndex = -1;

        private double _scale = 1.0;
        private double _offsetX;
        private double _offsetY;
        private bool _needsFit = true;

        private bool _panning;
        private Point _panLast;
        private RasterComparisonMode _comparisonMode;
        private RasterLayer? _comparisonFirst;
        private RasterLayer? _comparisonSecond;
        private double _swipePosition = 0.5;
        private bool _draggingSwipe;
        private bool _blinkShowsSecond;
        private Avalonia.Threading.DispatcherTimer? _blinkTimer;

        /// <summary>Which part of the clip-selection rectangle a drag is currently manipulating.</summary>
        private enum SelDrag { None, Create, Move, TL, TR, BL, BR, T, B, L, R }

        private SelDrag _selDrag = SelDrag.None;
        private bool _hasSelection;
        private double _selX0, _selY0, _selX1, _selY1; // selection rectangle in cell (raster pixel) coordinates
        private Point _dragStartScreen;
        private double _dragStartX0, _dragStartY0, _dragStartX1, _dragStartY1;

        // Path tool (profile / measure / zone): vertices in WORLD coordinates, so the path survives
        // switching the active layer (whose cell space differs).
        private PathTool _pathTool;
        private readonly List<(double X, double Y)> _path = new List<(double X, double Y)>();
        private bool _pathFinished;
        private int _pathDragIndex = -1;
        // Pressed on a segment's midpoint handle: the vertex is inserted only once the drag starts.
        private int _pathInsertSegment = -1;
        private bool _pathClickPending;
        private Point _pathPressScreen;

        private const double MinScale = 1.0 / 4096.0;
        private const double MaxScale = 4096.0;

        /// <summary>Extra pixels fetched beyond the viewport in streaming mode, so a small pan doesn't force an immediate re-read.</summary>
        private const int StreamingMargin = 160;
        private readonly SemaphoreSlim _streamReadGate = new SemaphoreSlim(1, 1);
        private Avalonia.Threading.DispatcherTimer? _streamRefreshTimer;
        private CancellationTokenSource? _streamRefreshCts;
        private int _streamGeneration;
        private bool _streamRefreshBusy;

        public RasterView()
        {
            Focusable = true;
            ClipToBounds = true;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty)
            {
                if (_pendingView is { } pv && Bounds.Width > 0)
                {
                    _pendingView = null;
                    SetViewState(pv.X, pv.Y, pv.Wpp);
                }
                else if (_needsFit) ZoomToFit();
                else RefreshStreamingWindow();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _blinkTimer?.Stop();
            CancelStreamingRefresh();
            _streamRefreshTimer?.Stop();
            foreach (var layer in _layers) layer.DisposeSource();
        }

        /// <summary>Releases every layer's rendered bitmap and streaming reader. Safe to call more than once.</summary>
        public void Dispose()
        {
            _blinkTimer?.Stop();
            CancelStreamingRefresh();
            _streamRefreshTimer?.Stop();
            foreach (var layer in _layers) layer.Dispose();
            _layers.Clear();
            _drawOrder.Clear();
            _vectorLayers.Clear();
            _activeLayerIndex = -1;
        }

        /// <summary>Raised after the active layer's display state has changed (a new dataset, a different band, RGB toggle, …).</summary>
        public event EventHandler? RasterLoaded;

        /// <summary>Raised whenever the layer list itself changes — added, removed, reordered, visibility toggled, or the active layer switched.</summary>
        public event EventHandler? LayersChanged;

        /// <summary>Raised on every pointer move with the world position and the active layer's sampled value.</summary>
        public event EventHandler<RasterReadoutEventArgs>? PointerReadout;

        /// <summary>Raised whenever the pan/zoom transform changes.</summary>
        public event EventHandler? ViewChanged;

        /// <summary>Raised when Swipe/Blink comparison starts, changes or stops.</summary>
        public event EventHandler? ComparisonChanged;

        /// <summary>
        /// Raised whenever the clip-selection rectangle changes — while it is being drawn,
        /// moved or resized, and when it is cleared. Read <see cref="CurrentSelection"/> to get
        /// the current value; there is no separate "completed" event, since the rectangle stays
        /// interactively adjustable (drag its body to move it, its handles to resize it) until
        /// the host explicitly acts on it.
        /// </summary>
        public event EventHandler? SelectionChanged;

        /// <summary>Raised whenever the path tool's vertices change (added, moved, removed, cleared).</summary>
        public event EventHandler? PathChanged;

        /// <summary>Raised when the path is finished (double-click, right-click or Enter).</summary>
        public event EventHandler? PathFinished;

        /// <summary>Raised when the undo/redo history changes.</summary>
        public event EventHandler? HistoryChanged;

        /// <summary>
        /// Raised on a left click while <see cref="IdentifyMode"/> is on, with the clicked world
        /// coordinate (the host lists every layer's value there).
        /// </summary>
        public event EventHandler<RasterReadoutEventArgs>? IdentifyRequested;

        /// <summary>Background fill behind the raster.</summary>
        public IBrush Background { get; set; }

        // ---- layers -----------------------------------------------------------------

        /// <summary>Every loaded layer, in display order (later = drawn on top / in front).</summary>
        public IReadOnlyList<RasterLayer> Layers => _layers;

        /// <summary>All raster and vector layers in paint order (later entries are drawn in front).</summary>
        public IReadOnlyList<object> DrawOrder => _drawOrder;

        /// <summary>Index of the active layer within <see cref="Layers"/>, or -1 when there are none.</summary>
        public int ActiveLayerIndex => _activeLayerIndex;

        /// <summary>
        /// The layer every tool (selection, profile line, pointer value readout) and every
        /// display-setting method (<see cref="SetPalette"/>, <see cref="SetActiveBand"/>, …)
        /// currently targets, or <see langword="null"/> when no layer is loaded.
        /// </summary>
        public RasterLayer? ActiveLayer => (uint)_activeLayerIndex < (uint)_layers.Count ? _layers[_activeLayerIndex] : null;

        /// <summary>Gets the active two-layer visual comparison mode.</summary>
        public RasterComparisonMode ComparisonMode => _comparisonMode;

        /// <summary>Gets the first layer used by Swipe or Blink.</summary>
        public RasterLayer? ComparisonFirst => _comparisonFirst;

        /// <summary>Gets the second layer used by Swipe or Blink.</summary>
        public RasterLayer? ComparisonSecond => _comparisonSecond;

        /// <summary>Gets the vertical Swipe divider as a fraction of the viewport width.</summary>
        public double SwipePosition => _swipePosition;

        /// <summary>Starts a two-layer Swipe or Blink comparison.</summary>
        public void StartComparison(RasterComparisonMode mode, RasterLayer first, RasterLayer second,
            double swipePosition = 0.5, int blinkIntervalMilliseconds = 700)
        {
            if (mode == RasterComparisonMode.None) throw new ArgumentOutOfRangeException(nameof(mode));
            if (!_layers.Contains(first)) throw new ArgumentException("The first layer is not loaded.", nameof(first));
            if (!_layers.Contains(second)) throw new ArgumentException("The second layer is not loaded.", nameof(second));
            if (ReferenceEquals(first, second)) throw new ArgumentException("Choose two different layers.", nameof(second));

            _comparisonFirst = first;
            _comparisonSecond = second;
            _comparisonMode = mode;
            _swipePosition = Math.Clamp(swipePosition, 0.02, 0.98);
            _blinkShowsSecond = false;
            _blinkTimer?.Stop();
            if (mode == RasterComparisonMode.Blink)
            {
                _blinkTimer ??= new Avalonia.Threading.DispatcherTimer();
                _blinkTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(blinkIntervalMilliseconds, 100, 5000));
                _blinkTimer.Tick -= OnBlinkTick;
                _blinkTimer.Tick += OnBlinkTick;
                _blinkTimer.Start();
            }
            RefreshStreamingWindow();
            InvalidateVisual();
            ComparisonChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Stops the active visual comparison and restores normal layer-stack rendering.</summary>
        public void StopComparison()
        {
            _blinkTimer?.Stop();
            _comparisonMode = RasterComparisonMode.None;
            _comparisonFirst = null;
            _comparisonSecond = null;
            _draggingSwipe = false;
            InvalidateVisual();
            ComparisonChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnBlinkTick(object? sender, EventArgs e)
        {
            _blinkShowsSecond = !_blinkShowsSecond;
            InvalidateVisual();
        }

        /// <summary>
        /// Loads <paramref name="ersPath"/> as a brand-new additional layer on top of the stack
        /// (existing layers are kept), and makes it the active one.
        /// </summary>
        public RasterLayer AddLayerFromPath(string ersPath, Palette? palette = null)
        {
            if (string.IsNullOrWhiteSpace(ersPath)) throw new ArgumentException("Path is required.", nameof(ersPath));
            if (!File.Exists(ersPath)) throw new FileNotFoundException("Raster dataset not found.", ersPath);

            if (IsGeoTiff(ersPath))
            {
                var opened = GeoTiffDataset.Open(ersPath);
                RecordUndo("Add layer");
                _undoSuppress++;
                try
                {
                    return AddLayerCore(opened.Document, Path.GetFileNameWithoutExtension(ersPath), palette,
                        opened.Source, () => new GdalRasterSource(ersPath));
                }
                catch { opened.Source?.Dispose(); throw; }
                finally { _undoSuppress--; }
            }

            var document = ErsDocument.LoadHeaderOnly(ersPath);
            if (!document.IsLargeDataset) document.LoadRaster();
            return AddLayer(document, Path.GetFileNameWithoutExtension(ersPath), palette);
        }

        /// <summary>Adds <paramref name="document"/> as a brand-new layer on top of the stack, and makes it the active one.</summary>
        public RasterLayer AddLayer(ErsDocument document, string name, Palette? palette = null)
        {
            RecordUndo("Add layer");
            _undoSuppress++;
            try { return AddLayerCore(document, name, palette); }
            finally { _undoSuppress--; }
        }

        private RasterLayer AddLayerCore(ErsDocument document, string name, Palette? palette,
            IRasterSource? source = null, Func<IRasterSource>? sourceFactory = null)
        {
            RasterLayer? frame = _layers.FirstOrDefault(l => l.IsFrame);
            var previousActive = ActiveLayer; // null when this is the very first layer

            var layer = CreateLayer(document, name, source, sourceFactory);
            InitializeLayerDisplay(layer, palette);
            _layers.Add(layer);
            _drawOrder.Add(layer);
            _activeLayerIndex = _layers.Count - 1;

            // The new layer becomes active (the common GIS-viewer convention); keep the same
            // real-world area on screen by re-basing the pan/zoom into its own cell space, so it
            // lines up with whatever was already loaded rather than jumping to a stale viewport.
            if (previousActive == null) { _needsFit = true; ZoomToFit(); }
            else RebaseViewport(previousActive, layer);

            // A real raster now provides the coordinate frame: drop the vector-only stand-in.
            if (frame != null)
            {
                _layers.Remove(frame);
                frame.Dispose();
                _activeLayerIndex = _layers.IndexOf(layer);
            }

            RefreshStreamingWindow();
            InvalidateVisual();
            RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
            return layer;
        }

        /// <summary>Removes the layer at <paramref name="index"/>, disposing its streaming reader/bitmap.</summary>
        public void RemoveLayer(int index)
        {
            if ((uint)index >= (uint)_layers.Count) return;
            if (!_layers[index].IsFrame) RecordUndo("Remove layer");

            var layer = _layers[index];
            if (ReferenceEquals(layer, _comparisonFirst) || ReferenceEquals(layer, _comparisonSecond))
                StopComparison();
            _layers.RemoveAt(index);
            _drawOrder.Remove(layer);
            layer.Dispose();

            if (_layers.Count == 0) _activeLayerIndex = -1;
            else
            {
                if (index < _activeLayerIndex) _activeLayerIndex--;
                _activeLayerIndex = Math.Clamp(_activeLayerIndex, 0, _layers.Count - 1);
            }
            EnsureFrame(layer.IsFrame ? null : layer);

            InvalidateVisual();
            RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Removes this exact raster dataset from the view.</summary>
        public void RemoveLayer(RasterLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            int index = _layers.IndexOf(layer);
            if (index >= 0) RemoveLayer(index);
        }

        /// <summary>
        /// Switches the active layer, re-basing the current view (pan position and on-screen
        /// scale) so the same real-world area stays visible, converted into the new active
        /// layer's own pixel-cell coordinate system.
        /// </summary>
        public void SetActiveLayerIndex(int index)
        {
            if ((uint)index >= (uint)_layers.Count || index == _activeLayerIndex) return;

            var oldActive = ActiveLayer;
            _activeLayerIndex = index;
            var newActive = ActiveLayer;

            if (oldActive != null && newActive != null) RebaseViewport(oldActive, newActive);

            // Tool state is expressed in the old active layer's cell space; rather than also
            // projecting it (rarely worth the complexity for a still-being-drawn selection),
            // simply clear it — consistent with how switching datasets already behaved.
            _hasSelection = false; _selDrag = SelDrag.None;
            oldActive?.DisposeSmooth();

            RefreshStreamingWindow();
            ScheduleSmoothOverlay();
            InvalidateVisual();
            RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Makes the specified loaded layer active.  This identity-based overload is intended
        /// for UI code: a layer card keeps referring to its own dataset even if another action
        /// has reordered the stack before the UI has had a chance to redraw.
        /// </summary>
        public void SetActiveLayer(RasterLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            SetActiveLayerIndex(_layers.IndexOf(layer));
        }

        /// <summary>Moves the layer at <paramref name="index"/> one position higher (drawn more on top).</summary>
        public void MoveLayerUp(int index)
        {
            if ((uint)index < (uint)_layers.Count) MoveInDrawOrder(_layers[index], +1);
        }

        /// <summary>Moves the layer at <paramref name="index"/> one position lower (drawn more toward the back).</summary>
        public void MoveLayerDown(int index)
        {
            if ((uint)index < (uint)_layers.Count) MoveInDrawOrder(_layers[index], -1);
        }

        /// <summary>Moves this exact dataset one position higher in the display stack.</summary>
        public void MoveLayerUp(RasterLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            MoveInDrawOrder(layer, +1);
        }

        /// <summary>Moves this exact dataset one position lower in the display stack.</summary>
        public void MoveLayerDown(RasterLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            MoveInDrawOrder(layer, -1);
        }

        /// <summary>Whether the exact layer can move one slot toward the front of the shared stack.</summary>
        public bool CanMoveLayerUp(object layer) => _drawOrder.IndexOf(layer) is int index && index >= 0 && index < _drawOrder.Count - 1;

        /// <summary>Whether the exact layer can move one slot toward the back of the shared stack.</summary>
        public bool CanMoveLayerDown(object layer) => _drawOrder.IndexOf(layer) > 0;

        private void MoveInDrawOrder(object layer, int delta)
        {
            int index = _drawOrder.IndexOf(layer);
            int newIndex = index + delta;
            if (index < 0 || (uint)newIndex >= (uint)_drawOrder.Count) return;

            RecordUndo("Reorder layers");
            (_drawOrder[index], _drawOrder[newIndex]) = (_drawOrder[newIndex], _drawOrder[index]);

            InvalidateVisual();
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Shows or hides the layer at <paramref name="index"/> without removing it.</summary>
        public void SetLayerVisible(int index, bool visible)
        {
            if ((uint)index >= (uint)_layers.Count) return;
            var layer = _layers[index];
            if (layer.IsVisible == visible) return;

            RecordUndo("Layer visibility");
            layer.IsVisible = visible;
            if (visible) RefreshStreamingWindow(); // a hidden streaming layer's window goes stale while skipped
            InvalidateVisual();
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Changes visibility for this exact dataset, independent of its current stack index.</summary>
        public void SetLayerVisible(RasterLayer layer, bool visible)
        {
            ArgumentNullException.ThrowIfNull(layer);
            int index = _layers.IndexOf(layer);
            if (index >= 0) SetLayerVisible(index, visible);
        }

        // ---- vector layers ------------------------------------------------------------

        private readonly List<VectorLayer> _vectorLayers = new List<VectorLayer>();

        /// <summary>
        /// Every loaded vector (<c>.erv</c>) layer. Their position relative to raster layers is
        /// held by <see cref="DrawOrder"/>.
        /// </summary>
        public IReadOnlyList<VectorLayer> VectorLayers => _vectorLayers;

        /// <summary>Loads <paramref name="ervPath"/> as a new vector layer on top of the vector stack.</summary>
        public VectorLayer AddVectorLayerFromPath(string ervPath)
        {
            if (string.IsNullOrWhiteSpace(ervPath)) throw new ArgumentException("Path is required.", nameof(ervPath));
            if (!File.Exists(ervPath)) throw new FileNotFoundException("ERV header not found.", ervPath);

            var document = ErvDocument.Load(ervPath);
            return AddVectorLayer(document, Path.GetFileNameWithoutExtension(ervPath));
        }

        /// <summary>Adds <paramref name="document"/> as a new vector layer on top of the vector stack.</summary>
        public VectorLayer AddVectorLayer(ErvDocument document, string name)
        {
            ArgumentNullException.ThrowIfNull(document);
            RecordUndo("Add vector layer");
            var layer = new VectorLayer(document, name);
            _vectorLayers.Add(layer);
            _drawOrder.Add(layer);
            EnsureFrame(null);
            InvalidateVisual();
            LayersChanged?.Invoke(this, EventArgs.Empty);
            return layer;
        }

        /// <summary>Removes the vector layer at <paramref name="index"/>.</summary>
        public void RemoveVectorLayer(int index)
        {
            if ((uint)index >= (uint)_vectorLayers.Count) return;
            var layer = _vectorLayers[index];
            RecordUndo("Remove vector layer");
            _vectorLayers.RemoveAt(index);
            _drawOrder.Remove(layer);
            EnsureFrame(null);
            InvalidateVisual();
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Removes this exact vector dataset from the view.</summary>
        public void RemoveVectorLayer(VectorLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            int index = _vectorLayers.IndexOf(layer);
            if (index >= 0) RemoveVectorLayer(index);
        }

        /// <summary>Shows or hides the vector layer at <paramref name="index"/> without removing it.</summary>
        public void SetVectorLayerVisible(int index, bool visible)
        {
            if ((uint)index >= (uint)_vectorLayers.Count) return;
            var layer = _vectorLayers[index];
            if (layer.IsVisible == visible) return;
            RecordUndo("Layer visibility");
            layer.IsVisible = visible;
            InvalidateVisual();
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetVectorLayerVisible(VectorLayer layer, bool visible)
        {
            ArgumentNullException.ThrowIfNull(layer);
            int index = _vectorLayers.IndexOf(layer);
            if (index >= 0) SetVectorLayerVisible(index, visible);
        }

        /// <summary>Sets the colour every object in the vector layer at <paramref name="index"/> is drawn with.</summary>
        public void SetVectorLayerColor(int index, Color color)
        {
            if ((uint)index >= (uint)_vectorLayers.Count || _vectorLayers[index].Color == color) return;
            RecordUndo("Vector colour");
            _vectorLayers[index].Color = color;
            InvalidateVisual();
        }

        public void SetVectorLayerColor(VectorLayer layer, Color color)
        {
            ArgumentNullException.ThrowIfNull(layer);
            int index = _vectorLayers.IndexOf(layer);
            if (index >= 0) SetVectorLayerColor(index, color);
        }

        /// <summary>Sets the line width (points) every object in the vector layer at <paramref name="index"/> is drawn with.</summary>
        public void SetVectorLayerLineWidth(int index, double width)
        {
            if ((uint)index >= (uint)_vectorLayers.Count || !(width > 0)) return;
            RecordUndo("Line width", "width:" + index);
            _vectorLayers[index].LineWidth = width;
            InvalidateVisual();
        }

        public void SetVectorLayerLineWidth(VectorLayer layer, double width)
        {
            ArgumentNullException.ThrowIfNull(layer);
            int index = _vectorLayers.IndexOf(layer);
            if (index >= 0) SetVectorLayerLineWidth(index, width);
        }

        /// <summary>Sets the palette used to colourise a SPECIFIC raster layer (not necessarily the active one).</summary>
        public void SetLayerPalette(int index, Palette palette)
        {
            if ((uint)index >= (uint)_layers.Count || palette == null) return;
            var layer = _layers[index];
            if (layer.Colorizer == null) return;
            RecordUndo("Palette");
            layer.Colorizer.Palette = palette;
            RebuildLayerBitmap(layer);
            if (index == _activeLayerIndex) LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Changes the palette for this exact dataset, independent of its current stack index.</summary>
        public void SetLayerPalette(RasterLayer layer, Palette palette)
        {
            ArgumentNullException.ThrowIfNull(layer);
            int index = _layers.IndexOf(layer);
            if (index >= 0) SetLayerPalette(index, palette);
        }

        /// <summary>Moves the vector layer at <paramref name="index"/> one position higher (drawn more on top, among vector layers).</summary>
        public void MoveVectorLayerUp(int index)
        {
            if ((uint)index < (uint)_vectorLayers.Count) MoveInDrawOrder(_vectorLayers[index], +1);
        }

        /// <summary>Moves the vector layer at <paramref name="index"/> one position lower.</summary>
        public void MoveVectorLayerDown(int index)
        {
            if ((uint)index < (uint)_vectorLayers.Count) MoveInDrawOrder(_vectorLayers[index], -1);
        }

        public void MoveVectorLayerUp(VectorLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            MoveInDrawOrder(layer, +1);
        }

        public void MoveVectorLayerDown(VectorLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            MoveInDrawOrder(layer, -1);
        }

        private static RasterLayer CreateLayer(ErsDocument document, string name, IRasterSource? source = null,
            Func<IRasterSource>? sourceFactory = null)
        {
            ArgumentNullException.ThrowIfNull(document);
            var layer = new RasterLayer(document, name);
            layer.SourceFactory = sourceFactory;
            if (source != null)
            {
                layer.Source = source;
                return layer;
            }
            if (document.Bands.Count == 0)
            {
                if (document.IsLargeDataset)
                {
                    layer.SourceFactory ??= document.OpenSource;
                    layer.Source = layer.SourceFactory();
                }
                else document.LoadRaster();
            }
            return layer;
        }

        private void InitializeLayerDisplay(RasterLayer layer, Palette? palette)
        {
            layer.ShowRgbComposite = layer.BandCount == 3;

            if (layer.Source != null)
            {
                Raster overview = layer.Source.ReadOverview(1024, 1024);
                Palette chosen = palette ?? BuiltInPalettes.Elevation;
                layer.Colorizer = RasterColorizer.Percentile(overview, chosen, 2.0, 98.0);
                layer.Colorizer.NoDataColor = ColorRgba.Transparent;
            }
            else
            {
                layer.Raster = layer.Document.Band ?? throw new InvalidOperationException("The dataset has no raster band.");
                Palette chosen = palette ?? BuiltInPalettes.Elevation;
                layer.Colorizer = RasterColorizer.Percentile(layer.Raster, chosen, 2.0, 98.0);
                layer.Colorizer.NoDataColor = ColorRgba.Transparent;
                layer.BitmapOriginX = 0; layer.BitmapOriginY = 0; layer.BitmapStep = 1.0;
                RebuildLayerBitmap(layer);
            }
        }

        private void RebaseViewport(RasterLayer oldActive, RasterLayer newActive)
        {
            double vw = Bounds.Width, vh = Bounds.Height;
            var oldGeo = oldActive.Document.GeoReference;
            var newGeo = newActive.Document.GeoReference;
            if (vw <= 0 || vh <= 0 || !oldGeo.IsInvertible || !newGeo.IsInvertible) { _needsFit = true; return; }

            Point centreScreen = new Point(vw / 2, vh / 2);
            Point centreOldCell = ScreenToCell(centreScreen);
            var (wx, wy) = oldGeo.PixelToWorld(centreOldCell.X, centreOldCell.Y);
            var (newCol, newRow) = newGeo.WorldToPixel(wx, wy);

            double oldWorldPerCell = AverageWorldPerCell(oldGeo);
            double newWorldPerCell = AverageWorldPerCell(newGeo);
            if (oldWorldPerCell <= 0 || newWorldPerCell <= 0) { _needsFit = true; return; }

            double worldPerScreenPixel = oldWorldPerCell / _scale;
            _scale = Clamp(newWorldPerCell / worldPerScreenPixel, MinScale, MaxScale);
            _offsetX = centreScreen.X - newCol * _scale;
            _offsetY = centreScreen.Y - newRow * _scale;
        }

        private static double AverageWorldPerCell(RasterGeoReference geo)
        {
            var (_, b, c, _, e, f) = geo.GeoTransform;
            return (Math.Sqrt(b * b + e * e) + Math.Sqrt(c * c + f * f)) / 2.0;
        }

        // ---- backward-compatible single-document surface (delegates to ActiveLayer) --------

        /// <summary>The active layer's dataset, or <see langword="null"/>.</summary>
        public ErsDocument? Document => ActiveLayer?.Document;

        /// <summary>The active layer's fully loaded band, or <see langword="null"/> (not loaded / streaming).</summary>
        public Raster? Raster => ActiveLayer?.Raster;

        /// <summary><see langword="true"/> when the active layer is being shown via windowed reads rather than fully loaded.</summary>
        public bool IsStreaming => ActiveLayer?.IsStreaming ?? false;

        /// <summary>Cell columns in the active layer's dataset (0 when none is loaded).</summary>
        public int DatasetWidth => ActiveLayer?.DatasetWidth ?? 0;

        /// <summary>Cell rows in the active layer's dataset (0 when none is loaded).</summary>
        public int DatasetHeight => ActiveLayer?.DatasetHeight ?? 0;

        /// <summary>Number of bands in the active layer's dataset (0 when none is loaded).</summary>
        public int BandCount => ActiveLayer?.BandCount ?? 0;

        /// <summary>Which band the active layer displays (0-based).</summary>
        public int ActiveBand => ActiveLayer?.ActiveBand ?? 0;

        /// <summary>
        /// Switches the displayed band of the active layer, re-stretching and re-rendering.
        /// A no-op when <paramref name="index"/> is already active or out of range.
        /// </summary>
        public void SetActiveBand(int index)
        {
            var layer = ActiveLayer;
            if (layer == null || index == layer.ActiveBand || index < 0 || index >= Math.Max(1, layer.BandCount)) return;
            RecordUndo("Band");
            layer.ActiveBand = index;

            Palette palette = layer.Colorizer?.Palette ?? BuiltInPalettes.Elevation;

            if (layer.Source != null)
            {
                layer.WindowRaster = null; // force a re-read at the new band on the next refresh
                Raster overview = layer.Source.ReadOverview(1024, 1024, layer.ActiveBand);
                layer.Colorizer = RasterColorizer.Percentile(overview, palette, 2.0, 98.0);
                layer.Colorizer.NoDataColor = ColorRgba.Transparent;
                RefreshStreamingWindow();
                InvalidateVisual();
            }
            else if (layer.Document.Bands.Count > layer.ActiveBand)
            {
                layer.Raster = layer.Document.Bands[layer.ActiveBand];
                layer.Colorizer = RasterColorizer.Percentile(layer.Raster, palette, 2.0, 98.0);
                layer.Colorizer.NoDataColor = ColorRgba.Transparent;
                RebuildLayerBitmap(layer);
            }

            RasterLoaded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary><see langword="true"/> when the active layer has (at least) the three bands a true-colour composite needs.</summary>
        public bool CanShowRgbComposite => ActiveLayer?.CanShowRgbComposite ?? false;

        /// <summary>
        /// When <see langword="true"/> (and <see cref="CanShowRgbComposite"/>), the active
        /// layer's bands 1-3 are composited directly as true colour (no palette) instead of
        /// showing <see cref="ActiveBand"/> through its palette.
        /// </summary>
        public bool ShowRgbComposite
        {
            get => ActiveLayer?.ShowRgbComposite ?? false;
            set
            {
                var layer = ActiveLayer;
                if (layer == null) return;
                bool v = value && layer.CanShowRgbComposite;
                if (layer.ShowRgbComposite == v) return;
                RecordUndo("RGB composite");
                layer.ShowRgbComposite = v;

                if (layer.Source != null)
                {
                    layer.WindowRaster = null; layer.WindowRasterG = null; layer.WindowRasterB = null;
                    RefreshStreamingWindow();
                }
                RebuildLayerBitmap(layer);
                RasterLoaded?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>The active layer's colorizer, or <see langword="null"/>.</summary>
        public RasterColorizer? Colorizer => ActiveLayer?.Colorizer;

        /// <summary>Statistics backing the active layer's current display, or <see langword="null"/> when nothing is loaded.</summary>
        public RasterStatistics? CurrentStatistics => ActiveLayer?.CurrentStatistics;

        /// <summary>Current magnification (device-independent pixels per active-layer cell).</summary>
        public double Zoom => _scale;

        /// <summary>Draw a faint per-cell grid (of the active layer) once magnified past 8&#215;.</summary>
        public bool ShowGrid { get; set; }

        /// <summary>
        /// When <see langword="true"/>, left-drag draws a rectangular clip selection instead of
        /// panning (use the middle mouse button, or arrow keys, to pan while active). Once drawn,
        /// the rectangle stays on screen with draggable handles: drag its body to move it, an
        /// edge/corner handle to resize it, <c>Escape</c> to clear it. Turning the mode off
        /// clears any selection. The selection lives in the active layer's cell space.
        /// </summary>
        public bool SelectionMode
        {
            get => _selectionModeField;
            set
            {
                _selectionModeField = value;
                if (!value) { _hasSelection = false; _selDrag = SelDrag.None; }
                Cursor = value ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
                RaiseSelectionChanged();
                InvalidateVisual();
            }
        }
        private bool _selectionModeField;

        /// <summary>The current clip-selection rectangle, in the active layer's cell coordinates, or <see langword="null"/> when there is none.</summary>
        public PixelRect? CurrentSelection
        {
            get
            {
                if (!_hasSelection) return null;
                int x0 = (int)Math.Floor(Math.Min(_selX0, _selX1));
                int y0 = (int)Math.Floor(Math.Min(_selY0, _selY1));
                int x1 = (int)Math.Ceiling(Math.Max(_selX0, _selX1));
                int y1 = (int)Math.Ceiling(Math.Max(_selY0, _selY1));
                int w = x1 - x0, h = y1 - y0;
                return w >= 1 && h >= 1 ? new PixelRect(x0, y0, w, h) : (PixelRect?)null;
            }
        }

        /// <summary>Clears the clip-selection rectangle without changing <see cref="SelectionMode"/>.</summary>
        public void ClearSelection()
        {
            _hasSelection = false;
            _selDrag = SelDrag.None;
            RaiseSelectionChanged();
            InvalidateVisual();
        }

        private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// The active path tool. While one is on, a click adds a vertex (in world coordinates), a
        /// drag on a vertex moves it, a drag elsewhere still pans; double-click, right-click or
        /// <c>Enter</c> finishes the path, <c>Backspace</c> removes the last vertex, <c>Escape</c> clears it.
        /// </summary>
        public PathTool PathToolMode
        {
            get => _pathTool;
            set
            {
                if (_pathTool == value) return;
                _pathTool = value;
                _path.Clear();
                _pathFinished = false;
                _pathDragIndex = -1;
                _pathInsertSegment = -1;
                Cursor = value != PathTool.None ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
                RaisePathChanged();
            }
        }

        /// <summary>The path's vertices, in world coordinates.</summary>
        public IReadOnlyList<(double X, double Y)> CurrentPath => _path;

        /// <summary><see langword="true"/> once the user finished the path (the next click starts a new one).</summary>
        public bool IsPathFinished => _pathFinished;

        /// <summary>Clears the path, keeping the tool on.</summary>
        public void ClearPath()
        {
            _path.Clear();
            _pathFinished = false;
            RaisePathChanged();
        }

        /// <summary>Removes the last vertex.</summary>
        public void RemoveLastPathVertex()
        {
            if (_path.Count == 0) return;
            _path.RemoveAt(_path.Count - 1);
            _pathFinished = false;
            RaisePathChanged();
        }

        /// <summary>Finishes the path (raises <see cref="PathFinished"/>).</summary>
        public void FinishPath()
        {
            if (_path.Count < 2 || _pathFinished) return;
            _pathFinished = true;
            InvalidateVisual();
            PathFinished?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Replaces the path (e.g. to profile along an existing vector line).</summary>
        public void SetPath(IEnumerable<(double X, double Y)> vertices, bool finished = true)
        {
            _path.Clear();
            _path.AddRange(vertices);
            _pathFinished = finished && _path.Count >= 2;
            RaisePathChanged();
            if (_pathFinished) PathFinished?.Invoke(this, EventArgs.Empty);
        }

        private void RaisePathChanged()
        {
            InvalidateVisual();
            PathChanged?.Invoke(this, EventArgs.Empty);
        }

        private (double X, double Y)? ScreenToWorld(Point screen)
        {
            var layer = ActiveLayer;
            if (layer == null) return null;
            Point c = ScreenToCell(screen);
            return layer.Document.GeoReference.PixelToWorld(c.X, c.Y);
        }

        /// <summary>
        /// Whether the path is drawn closed (zone, or a finished measurement): its last → first
        /// edge is then a real segment with its own midpoint handle.
        /// </summary>
        private bool IsPathPolygon => (_pathTool == PathTool.Zone || (_pathTool == PathTool.Measure && _pathFinished)) && _path.Count >= 3;

        /// <summary>
        /// Screen midpoints of the path's segments, as (segment index, point); segment i runs from
        /// vertex i to vertex i+1 (the closing edge of a polygon wraps to 0). Segments too short on
        /// screen for a separate handle are skipped, so it never crowds the vertex handles.
        /// </summary>
        private List<(int Segment, Point Midpoint)> PathMidpoints(RasterLayer active)
        {
            var result = new List<(int, Point)>();
            int n = _path.Count;
            if (n < 2 || !active.Document.GeoReference.IsInvertible) return result;
            int segments = IsPathPolygon ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                Point a = WorldToScreen(active, _path[i].X, _path[i].Y);
                Point b = WorldToScreen(active, _path[(i + 1) % n].X, _path[(i + 1) % n].Y);
                if (Distance(a, b) < 24) continue;
                result.Add((i, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2)));
            }
            return result;
        }

        private int HitTestPathMidpoint(Point screen)
        {
            var active = ActiveLayer;
            if (active == null) return -1;
            foreach (var (segment, midpoint) in PathMidpoints(active))
                if (Distance(midpoint, screen) <= 7) return segment;
            return -1;
        }

        private int HitTestPathVertex(Point screen)
        {
            var active = ActiveLayer;
            if (active == null || !active.Document.GeoReference.IsInvertible) return -1;
            for (int i = _path.Count - 1; i >= 0; i--)
                if (Distance(WorldToScreen(active, _path[i].X, _path[i].Y), screen) <= 8) return i;
            return -1;
        }

        /// <summary>
        /// World units per screen pixel at the current zoom (the "ground sample distance" on
        /// screen), for the active layer, or <see langword="null"/> when nothing is loaded.
        /// </summary>
        public double? GroundSampleDistance
        {
            get
            {
                var doc = Document;
                return doc == null ? (double?)null : AverageWorldPerCell(doc.GeoReference) / _scale;
            }
        }

        /// <summary>
        /// Approximate map scale denominator (as in "1 : N"), assuming the given screen
        /// resolution in dots per inch (96 is the common CSS/DIP baseline; pass the display's
        /// actual DPI — e.g. via <c>TopLevel.RenderScaling * 96</c> — for a closer figure).
        /// </summary>
        public double? MapScaleDenominator(double screenDpi = 96.0)
        {
            double? gsd = GroundSampleDistance;
            if (gsd == null) return null;
            const double metersPerInch = 0.0254;
            return gsd.Value * screenDpi / metersPerInch;
        }

        /// <summary>Use smooth (bilinear) interpolation instead of nearest-neighbour when magnified.</summary>
        public bool SmoothScaling
        {
            get => DisplayResampling != DisplayResampling.Nearest;
            set => DisplayResampling = value ? DisplayResampling.Bilinear : DisplayResampling.Nearest;
        }

        private DisplayResampling _displayResampling = DisplayResampling.Nearest;

        /// <summary>
        /// How magnified cells are drawn: crisp cells, bilinear, or a bicubic Bézier-patch surface
        /// (<see cref="BezierPatchInterpolator"/>) computed for the visible window of the active
        /// layer. Display only — the data is never changed.
        /// </summary>
        public DisplayResampling DisplayResampling
        {
            get => _displayResampling;
            set
            {
                _displayResampling = value;
                RenderOptions.SetBitmapInterpolationMode(this,
                    value == DisplayResampling.Bilinear ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None);
                if (value != DisplayResampling.Bezier)
                    foreach (var l in _layers) l.DisposeSmooth();
                ScheduleSmoothOverlay();
                InvalidateVisual();
            }
        }

        /// <summary>Tension / monotone settings used by the Bézier display mode.</summary>
        public BezierPatchOptions DisplayBezierOptions { get; } = new BezierPatchOptions();

        private bool _identifyMode;

        /// <summary>When on, a left click raises <see cref="IdentifyRequested"/> instead of starting a pan.</summary>
        public bool IdentifyMode
        {
            get => _identifyMode;
            set
            {
                _identifyMode = value;
                Cursor = value ? new Cursor(StandardCursorType.Help) : Cursor.Default;
            }
        }

        /// <summary>Sets a raster layer's draw opacity (0–1).</summary>
        public void SetLayerOpacity(RasterLayer layer, double opacity)
        {
            ArgumentNullException.ThrowIfNull(layer);
            RecordUndo("Opacity", "opacity:" + _layers.IndexOf(layer));
            layer.Opacity = Math.Clamp(opacity, 0.0, 1.0);
            InvalidateVisual();
        }

        /// <summary>Marks a layer as derived/in-memory with a human-readable description of how it was made.</summary>
        public void MarkDerived(object layer, string lineage)
        {
            switch (layer)
            {
                case RasterLayer r: r.IsUnsaved = true; r.Lineage = lineage; break;
                case VectorLayer v: v.IsUnsaved = true; v.Lineage = lineage; break;
                default: return;
            }
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Records that a derived layer has been written to <paramref name="path"/> (renames it after the file).</summary>
        public void MarkSaved(object layer, string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            switch (layer)
            {
                case RasterLayer r: r.IsUnsaved = false; r.Name = name; break;
                case VectorLayer v: v.IsUnsaved = false; v.Name = name; break;
                default: return;
            }
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Copies palette, stretch, gamma and render mode from one raster layer to another (e.g. onto its Bézier-subdivided copy).</summary>
        public void CopyDisplaySettings(RasterLayer from, RasterLayer to)
        {
            ArgumentNullException.ThrowIfNull(from);
            ArgumentNullException.ThrowIfNull(to);
            if (from.Colorizer == null || to.Colorizer == null) return;
            to.Colorizer.Palette = from.Colorizer.Palette;
            to.Colorizer.Minimum = from.Colorizer.Minimum;
            to.Colorizer.Maximum = from.Colorizer.Maximum;
            to.Colorizer.Gamma = from.Colorizer.Gamma;
            to.Colorizer.Mode = from.Colorizer.Mode;
            to.Colorizer.ClassCount = from.Colorizer.ClassCount;
            RebuildLayerBitmap(to);
            if (ReferenceEquals(to, ActiveLayer)) RasterLoaded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Sets a layer's palette, value range, gamma and render mode in one go (e.g. a derived layer's own style).</summary>
        public void ApplyDisplaySettings(RasterLayer layer, Palette palette, double minimum, double maximum,
            double gamma = 1.0, PaletteRenderMode mode = PaletteRenderMode.Continuous)
        {
            ArgumentNullException.ThrowIfNull(layer);
            ArgumentNullException.ThrowIfNull(palette);
            if (layer.Colorizer == null || !(maximum > minimum)) return;
            layer.Colorizer.Palette = palette;
            layer.Colorizer.Minimum = minimum;
            layer.Colorizer.Maximum = maximum;
            layer.Colorizer.Gamma = gamma;
            layer.Colorizer.Mode = mode;
            RebuildLayerBitmap(layer);
            if (ReferenceEquals(layer, ActiveLayer)) RasterLoaded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The cell window of the active layer currently on screen (clamped to the dataset), or <see langword="null"/>.</summary>
        public PixelRect? VisibleCellWindow()
        {
            var layer = ActiveLayer;
            if (layer == null || Bounds.Width <= 0 || Bounds.Height <= 0) return null;
            Point a = ScreenToCell(new Point(0, 0)), b = ScreenToCell(new Point(Bounds.Width, Bounds.Height));
            int x0 = (int)Math.Max(0, Math.Floor(Math.Min(a.X, b.X)));
            int y0 = (int)Math.Max(0, Math.Floor(Math.Min(a.Y, b.Y)));
            int x1 = (int)Math.Min(layer.DatasetWidth, Math.Ceiling(Math.Max(a.X, b.X)));
            int y1 = (int)Math.Min(layer.DatasetHeight, Math.Ceiling(Math.Max(a.Y, b.Y)));
            return x1 > x0 && y1 > y0 ? new PixelRect(x0, y0, x1 - x0, y1 - y0) : null;
        }

        // ---- undo / redo ---------------------------------------------------------------
        //
        // Snapshot-based: before every user-visible change the view records the layer stack and
        // each layer's display state (and document reference). Undo restores that snapshot; a
        // removed layer is kept alive (only its bitmap/streaming reader are released) so it can
        // come back. Continuous controls (sliders) coalesce into one step.

        private sealed class RasterState
        {
            public string Name = ""; public bool Visible; public double Opacity; public LayerBlendMode Blend;
            public ErsDocument Document = null!; public int Band; public bool Rgb;
            public Palette? Palette; public double Min, Max, Gamma; public PaletteRenderMode Mode; public int Classes;
            public bool Unsaved; public string? Lineage; public LayerRecipe? Recipe;
        }

        private sealed class VectorState
        {
            public string Name = ""; public bool Visible; public Color Color; public double Width;
            public ErvDocument Document = null!; public IReadOnlyList<double>? Widths; public IReadOnlyList<string?>? Labels;
            public bool Unsaved; public string? Lineage; public LayerRecipe? Recipe;
        }

        private sealed class ViewSnapshot
        {
            public string Label = "";
            public List<RasterLayer> Layers = new();
            public List<VectorLayer> Vectors = new();
            public List<object> DrawOrder = new();
            public RasterLayer? Active;
            public Dictionary<RasterLayer, RasterState> Rasters = new();
            public Dictionary<VectorLayer, VectorState> VectorStates = new();
        }

        private const int MaxUndo = 100;
        private readonly List<ViewSnapshot> _undo = new();
        private readonly List<ViewSnapshot> _redo = new();
        private int _undoSuppress;
        private string? _lastCoalesceKey;
        private DateTime _lastRecordTime;

        /// <summary>Whether there is a step to undo.</summary>
        public bool CanUndo => _undo.Count > 0;

        /// <summary>Whether there is a step to redo.</summary>
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Label of the next undo step (e.g. "Palette"), or null.</summary>
        public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

        /// <summary>Label of the next redo step, or null.</summary>
        public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

        /// <summary>
        /// Records the current state as an undo step labelled <paramref name="label"/>. Calls with the
        /// same <paramref name="coalesceKey"/> within a second merge into one step (slider drags).
        /// </summary>
        public void RecordUndo(string label, string? coalesceKey = null)
        {
            if (_undoSuppress > 0) return;
            var now = DateTime.UtcNow;
            if (coalesceKey != null && coalesceKey == _lastCoalesceKey && (now - _lastRecordTime).TotalSeconds < 1.0 && _undo.Count > 0)
            {
                _lastRecordTime = now;
                return;
            }
            _lastCoalesceKey = coalesceKey;
            _lastRecordTime = now;
            _undo.Add(Capture(label));
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Forgets all undo/redo steps (e.g. after opening a project).</summary>
        public void ClearHistory()
        {
            _undo.Clear();
            _redo.Clear();
            _lastCoalesceKey = null;
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Undoes the last recorded step.</summary>
        public void Undo()
        {
            if (_undo.Count == 0) return;
            var target = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(Capture(target.Label));
            Restore(target);
        }

        /// <summary>Redoes the last undone step.</summary>
        public void Redo()
        {
            if (_redo.Count == 0) return;
            var target = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(Capture(target.Label));
            Restore(target);
        }

        private ViewSnapshot Capture(string label)
        {
            var snap = new ViewSnapshot
            {
                Label = label,
                Layers = _layers.ToList(),
                Vectors = _vectorLayers.ToList(),
                DrawOrder = _drawOrder.ToList(),
                Active = ActiveLayer,
            };
            foreach (var l in _layers)
            {
                var c = l.Colorizer;
                snap.Rasters[l] = new RasterState
                {
                    Name = l.Name, Visible = l.IsVisible, Opacity = l.Opacity, Blend = l.BlendMode, Document = l.Document,
                    Band = l.ActiveBand, Rgb = l.ShowRgbComposite,
                    Palette = c?.Palette, Min = c?.Minimum ?? 0, Max = c?.Maximum ?? 1, Gamma = c?.Gamma ?? 1,
                    Mode = c?.Mode ?? PaletteRenderMode.Continuous, Classes = c?.ClassCount ?? 8,
                    Unsaved = l.IsUnsaved, Lineage = l.Lineage, Recipe = l.Recipe,
                };
            }
            foreach (var v in _vectorLayers)
            {
                snap.VectorStates[v] = new VectorState
                {
                    Name = v.Name, Visible = v.IsVisible, Color = v.Color, Width = v.LineWidth, Document = v.Document,
                    Widths = v.WidthFactors, Labels = v.Labels, Unsaved = v.IsUnsaved, Lineage = v.Lineage, Recipe = v.Recipe,
                };
            }
            return snap;
        }

        private void Restore(ViewSnapshot snap)
        {
            _undoSuppress++;
            try
            {
                var previousActive = ActiveLayer;
                foreach (var l in _layers)
                    if (!snap.Layers.Contains(l)) l.Dispose(); // releases bitmap/reader only; the document stays for redo

                _layers.Clear(); _layers.AddRange(snap.Layers);
                _vectorLayers.Clear(); _vectorLayers.AddRange(snap.Vectors);
                _drawOrder.Clear(); _drawOrder.AddRange(snap.DrawOrder);
                if ((_comparisonFirst != null && !_layers.Contains(_comparisonFirst)) ||
                    (_comparisonSecond != null && !_layers.Contains(_comparisonSecond)))
                    StopComparison();

                foreach (var (layer, st) in snap.Rasters)
                {
                    bool docChanged = !ReferenceEquals(layer.Document, st.Document);
                    if (docChanged) { layer.DisposeSource(); layer.Document = st.Document; }
                    layer.Name = st.Name; layer.IsVisible = st.Visible; layer.Opacity = st.Opacity; layer.BlendMode = st.Blend;
                    layer.IsUnsaved = st.Unsaved; layer.Lineage = st.Lineage; layer.Recipe = st.Recipe;
                    bool bandChanged = layer.ActiveBand != st.Band || layer.ShowRgbComposite != st.Rgb;
                    layer.ActiveBand = st.Band; layer.ShowRgbComposite = st.Rgb;

                    if (layer.Source == null && layer.Document.Bands.Count == 0 && layer.Document.IsLargeDataset)
                        layer.Source = (layer.SourceFactory ?? layer.Document.OpenSource)();
                    if (layer.Source != null) { if (bandChanged || docChanged || layer.Bitmap == null) { layer.WindowRaster = null; layer.WindowRasterG = null; layer.WindowRasterB = null; } }
                    else if (layer.Document.Bands.Count > 0) layer.Raster = layer.Document.Bands[Math.Min(st.Band, layer.Document.Bands.Count - 1)];

                    if (st.Palette != null)
                    {
                        layer.Colorizer ??= new RasterColorizer(st.Palette, st.Min, st.Max) { NoDataColor = ColorRgba.Transparent };
                        layer.Colorizer.Palette = st.Palette;
                        layer.Colorizer.Minimum = st.Min; layer.Colorizer.Maximum = st.Max; layer.Colorizer.Gamma = st.Gamma;
                        layer.Colorizer.Mode = st.Mode; layer.Colorizer.ClassCount = st.Classes;
                    }
                    if (layer.Source == null) RebuildLayerBitmap(layer);
                }
                foreach (var (v, st) in snap.VectorStates)
                {
                    if (!ReferenceEquals(v.Document, st.Document)) v.ReplaceDocument(st.Document, st.Widths, st.Labels);
                    else v.SetObjectStyles(st.Widths, st.Labels);
                    v.Name = st.Name; v.IsVisible = st.Visible; v.Color = st.Color; v.LineWidth = st.Width;
                    v.IsUnsaved = st.Unsaved; v.Lineage = st.Lineage; v.Recipe = st.Recipe;
                }

                _activeLayerIndex = snap.Active != null ? _layers.IndexOf(snap.Active) : (_layers.Count > 0 ? 0 : -1);
                var active = ActiveLayer;
                if (previousActive != null && active != null && !ReferenceEquals(previousActive, active) && _layers.Contains(previousActive))
                    RebaseViewport(previousActive, active);
                else if (previousActive == null || !_layers.Contains(previousActive)) { _needsFit = true; ZoomToFit(); }
                RefreshStreamingWindow();
            }
            finally { _undoSuppress--; }

            _lastCoalesceKey = null;
            InvalidateVisual();
            RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- vector-only coordinate frame -------------------------------------------------

        /// <summary><see langword="true"/> when at least one real (non-frame) raster layer is loaded.</summary>
        public bool HasRasterLayers => _layers.Any(l => !l.IsFrame);

        /// <summary>
        /// Keeps the invisible frame layer in sync: created when vectors are shown without any real
        /// raster (so vector-only viewing works), removed when a raster exists or no vectors are left.
        /// </summary>
        private void EnsureFrame(RasterLayer? justRemoved)
        {
            var frame = _layers.FirstOrDefault(l => l.IsFrame);
            bool needFrame = !HasRasterLayers && _vectorLayers.Count > 0;
            if (needFrame && frame == null)
            {
                var created = CreateFrameLayer(justRemoved);
                if (created == null) return;
                _layers.Add(created);
                _activeLayerIndex = _layers.Count - 1;
                if (justRemoved == null) { _needsFit = true; ZoomToFit(); }
            }
            else if (!needFrame && frame != null && !HasRasterLayers)
            {
                _layers.Remove(frame);
                frame.Dispose();
                _activeLayerIndex = -1;
            }
        }

        private RasterLayer? CreateFrameLayer(RasterLayer? previous)
        {
            // Extent of all vectors (or keep the last raster's frame, so the view doesn't jump).
            double minX, minY, maxX, maxY;
            string? projection = null, datum = null;
            if (previous != null)
            {
                (minX, minY, maxX, maxY) = previous.Document.GeoReference.WorldBounds();
                projection = previous.Document.Header.CoordinateSpace.Projection;
                datum = previous.Document.Header.CoordinateSpace.Datum;
            }
            else
            {
                var extents = _vectorLayers.Select(v => v.Extent()).Where(e => e != null).Select(e => e!.Value).ToList();
                if (extents.Count == 0) return null;
                minX = extents.Min(e => e.MinX); minY = extents.Min(e => e.MinY);
                maxX = extents.Max(e => e.MaxX); maxY = extents.Max(e => e.MaxY);
                projection = _vectorLayers[0].Document.Header.CoordinateSpace.Projection;
                datum = _vectorLayers[0].Document.Header.CoordinateSpace.Datum;
            }
            double w = Math.Max(maxX - minX, 1e-6), h = Math.Max(maxY - minY, 1e-6);
            double margin = Math.Max(w, h) * 0.05;
            minX -= margin; maxX += margin; minY -= margin; maxY += margin;
            double cell = Math.Max(maxX - minX, maxY - minY) / 1000.0;
            int cols = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cell));
            int rows = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cell));
            var band = new Raster(cols, rows);
            Array.Fill(band.Samples, float.NaN);
            var doc = ErsDocument.Create(band, minX, maxY, cell, cell, projection: projection == "RAW" ? null : projection, datum: datum == "RAW" ? null : datum);
            var layer = new RasterLayer(doc, "(frame)") { IsFrame = true };
            layer.Raster = band;
            layer.Colorizer = new RasterColorizer(BuiltInPalettes.Grayscale, 0, 1) { NoDataColor = ColorRgba.Transparent };
            RebuildLayerBitmap(layer);
            return layer;
        }

        // ---- derived layer recompute / view state --------------------------------------------

        /// <summary>Replaces a raster layer's content in place (a recomputed derived layer), keeping its display settings.</summary>
        public void ReplaceLayerDocument(RasterLayer layer, ErsDocument document, string? lineage = null, LayerRecipe? recipe = null)
        {
            ArgumentNullException.ThrowIfNull(layer);
            ArgumentNullException.ThrowIfNull(document);
            RecordUndo("Recompute layer");
            layer.DisposeSource();
            layer.DisposeSmooth();
            layer.Document = document;
            layer.SourceFactory = null;
            if (document.Bands.Count == 0)
            {
                if (document.IsLargeDataset)
                {
                    layer.SourceFactory = document.OpenSource;
                    layer.Source = layer.SourceFactory();
                }
                else document.LoadRaster();
            }
            layer.ActiveBand = Math.Min(layer.ActiveBand, Math.Max(0, layer.BandCount - 1));
            if (layer.Source == null) layer.Raster = document.Bands[layer.ActiveBand];
            if (lineage != null) layer.Lineage = lineage;
            if (recipe != null) layer.Recipe = recipe;
            layer.IsUnsaved = true;
            if (layer.Source != null) RefreshStreamingWindow(); else RebuildLayerBitmap(layer);
            if (ReferenceEquals(layer, ActiveLayer)) RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Replaces a vector layer's content in place (a recomputed derived layer).</summary>
        public void ReplaceVectorDocument(VectorLayer layer, ErvDocument document, IReadOnlyList<double>? widths, IReadOnlyList<string?>? labels,
            string? lineage = null, LayerRecipe? recipe = null)
        {
            ArgumentNullException.ThrowIfNull(layer);
            RecordUndo("Recompute layer");
            layer.ReplaceDocument(document, widths, labels);
            if (lineage != null) layer.Lineage = lineage;
            if (recipe != null) layer.Recipe = recipe;
            layer.IsUnsaved = true;
            InvalidateVisual();
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The world coordinate at the centre of the view and the world units per screen pixel.</summary>
        public (double X, double Y, double WorldPerPixel)? GetViewState()
        {
            var layer = ActiveLayer;
            if (layer == null || Bounds.Width <= 0) return null;
            Point c = ScreenToCell(new Point(Bounds.Width / 2, Bounds.Height / 2));
            var (x, y) = layer.Document.GeoReference.PixelToWorld(c.X, c.Y);
            return (x, y, AverageWorldPerCell(layer.Document.GeoReference) / _scale);
        }

        /// <summary>Centres the view on a world coordinate at the given scale.</summary>
        public void SetViewState(double x, double y, double worldPerPixel)
        {
            var layer = ActiveLayer;
            if (layer == null || !(worldPerPixel > 0) || !layer.Document.GeoReference.IsInvertible) return;
            if (Bounds.Width <= 0) { _pendingView = (x, y, worldPerPixel); return; }
            _scale = Clamp(AverageWorldPerCell(layer.Document.GeoReference) / worldPerPixel, MinScale, MaxScale);
            var (col, row) = layer.Document.GeoReference.WorldToPixel(x, y);
            _offsetX = Bounds.Width / 2 - col * _scale;
            _offsetY = Bounds.Height / 2 - row * _scale;
            _needsFit = false;
            RaiseViewChanged();
        }

        private (double X, double Y, double Wpp)? _pendingView;

        /// <summary>Fits a world-coordinate rectangle into the view (with a small margin).</summary>
        public void ZoomToWorld(double minX, double minY, double maxX, double maxY)
        {
            double vw = Bounds.Width > 0 ? Bounds.Width : 800, vh = Bounds.Height > 0 ? Bounds.Height : 600;
            double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);
            SetViewState((minX + maxX) / 2, (minY + maxY) / 2, Math.Max(w / vw, h / vh) * 1.06);
        }

        /// <summary>Removes every layer and forgets the history (a new, empty project).</summary>
        public void ClearAll()
        {
            StopComparison();
            foreach (var l in _layers) l.Dispose();
            _layers.Clear();
            _vectorLayers.Clear();
            _drawOrder.Clear();
            _activeLayerIndex = -1;
            _path.Clear();
            _hasSelection = false;
            ClearHistory();
            _needsFit = true;
            InvalidateVisual();
            RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- Bézier display overlay -------------------------------------------------

        /// <summary>Upper bound on the overlay's cell count (≈ a 2.5k × 1.6k screen) — keeps a rebuild well under ~100 ms.</summary>
        private const long MaxSmoothCells = 4_000_000;

        private Avalonia.Threading.DispatcherTimer? _smoothTimer;

        /// <summary>Debounces overlay rebuilds so panning stays fluid; the stale overlay stays correctly placed meanwhile.</summary>
        private void ScheduleSmoothOverlay()
        {
            if (_displayResampling != DisplayResampling.Bezier) return;
            if (_smoothTimer == null)
            {
                _smoothTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _smoothTimer.Tick += (_, _) => { _smoothTimer!.Stop(); UpdateSmoothOverlay(); };
            }
            _smoothTimer.Stop();
            _smoothTimer.Start();
        }

        private void UpdateSmoothOverlay()
        {
            var layer = ActiveLayer;
            if (layer == null || _displayResampling != DisplayResampling.Bezier) return;
            if (layer.Raster == null || layer.ShowRgbComposite || layer.Colorizer == null || _scale < 1.5)
            {
                if (layer.SmoothBitmap != null) { layer.DisposeSmooth(); InvalidateVisual(); }
                return;
            }

            var window = VisibleCellWindow();
            if (window == null) return;
            var (x0, y0, w, h) = (window.Value.X, window.Value.Y, window.Value.Width, window.Value.Height);

            int k = Math.Clamp((int)Math.Ceiling(_scale), 2, 8);
            while (k > 2 && (long)w * h * k * k > MaxSmoothCells) k--;
            if ((long)w * h * k * k > MaxSmoothCells) return;

            var o = DisplayBezierOptions;
            string key = FormattableString.Invariant($"{x0},{y0},{w},{h},{k},{layer.RenderVersion},{o.Tension},{o.Monotone},{o.NoData}");
            if (key == layer.SmoothKey) return;

            var raster = layer.Raster;
            int mx0 = Math.Max(0, x0 - 2), my0 = Math.Max(0, y0 - 2);
            int mx1 = Math.Min(raster.Width, x0 + w + 2), my1 = Math.Min(raster.Height, y0 + h + 2);
            var source = RasterClipper.Crop(raster, mx0, my0, mx1 - mx0, my1 - my0);
            var fine = BezierPatchInterpolator.Subdivide(source, new BezierPatchOptions
            {
                Factor = k, Tension = o.Tension, Monotone = o.Monotone, NoData = o.NoData,
            });
            var inner = RasterClipper.Crop(fine, (x0 - mx0) * k, (y0 - my0) * k, w * k, h * k);
            var image = RasterImageRenderer.Render(inner, layer.Colorizer);

            layer.SmoothBitmap?.Dispose();
            layer.SmoothBitmap = ToBitmap(image);
            layer.SmoothOriginX = x0;
            layer.SmoothOriginY = y0;
            layer.SmoothStep = 1.0 / k;
            layer.SmoothKey = key;
            InvalidateVisual();
        }

        private static BitmapBlendingMode ToAvalonia(LayerBlendMode mode) => mode switch
        {
            LayerBlendMode.Multiply => BitmapBlendingMode.Multiply,
            LayerBlendMode.Screen => BitmapBlendingMode.Screen,
            LayerBlendMode.Overlay => BitmapBlendingMode.Overlay,
            LayerBlendMode.Darken => BitmapBlendingMode.Darken,
            LayerBlendMode.Lighten => BitmapBlendingMode.Lighten,
            LayerBlendMode.SoftLight => BitmapBlendingMode.SoftLight,
            _ => BitmapBlendingMode.SourceOver,
        };

        /// <summary>Sets how a raster layer is composited onto the layers below it.</summary>
        public void SetLayerBlendMode(RasterLayer layer, LayerBlendMode mode)
        {
            ArgumentNullException.ThrowIfNull(layer);
            if (layer.BlendMode == mode) return;
            RecordUndo("Blend mode");
            layer.BlendMode = mode;
            InvalidateVisual();
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Copies a rendered BGRA image into a new Avalonia bitmap.</summary>
        internal static WriteableBitmap ToBitmap(RasterImage image)
        {
            var bmp = new WriteableBitmap(new PixelSize(Math.Max(1, image.Width), Math.Max(1, image.Height)), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using ILockedFramebuffer fb = bmp.Lock();
            int srcStride = image.Stride, dstStride = fb.RowBytes;
            if (srcStride == dstStride)
                Marshal.Copy(image.Pixels, 0, fb.Address, image.Pixels.Length);
            else
                for (int y = 0; y < image.Height; y++)
                    Marshal.Copy(image.Pixels, y * srcStride, fb.Address + y * dstStride, srcStride);
            return bmp;
        }

        // ---- loading ----------------------------------------------------------------

        /// <summary>
        /// Loads an <c>.ers</c> dataset as the view's <i>sole</i> layer — any existing layers are
        /// discarded first. Use <see cref="AddLayerFromPath"/> to add a source alongside what is
        /// already loaded. Auto-stretches (2–98%) and fits it to the control. A dataset over
        /// <see cref="ErsDocument.LargeDatasetCellThreshold"/> cells is <i>not</i> read into
        /// memory here — see <see cref="IsStreaming"/>.
        /// </summary>
        public void LoadErs(string ersPath, Palette? palette = null) => LoadRaster(ersPath, palette);

        /// <summary>Loads an ERS or GeoTIFF dataset as the view's sole raster layer.</summary>
        public void LoadRaster(string path, Palette? palette = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("Raster dataset not found.", path);

            if (IsGeoTiff(path))
            {
                var opened = GeoTiffDataset.Open(path);
                try
                {
                    SetDocumentCore(opened.Document, palette, Path.GetFileNameWithoutExtension(path), opened.Source,
                        () => new GdalRasterSource(path));
                }
                catch { opened.Source?.Dispose(); throw; }
                return;
            }

            var document = ErsDocument.LoadHeaderOnly(path);
            if (!document.IsLargeDataset) document.LoadRaster();
            SetDocumentCore(document, palette, Path.GetFileNameWithoutExtension(path));
        }

        /// <summary>
        /// Displays <paramref name="document"/> as the view's <i>sole</i> layer — any existing
        /// layers are discarded first. Use <see cref="AddLayer"/> to add a source alongside what
        /// is already loaded.
        /// </summary>
        public void SetDocument(ErsDocument document, Palette? palette = null) => SetDocumentCore(document, palette, null);

        private void SetDocumentCore(ErsDocument document, Palette? palette, string? name,
            IRasterSource? source = null, Func<IRasterSource>? sourceFactory = null)
        {
            StopComparison();
            ClearHistory();
            foreach (var l in _layers) l.Dispose();
            _layers.Clear();
            _drawOrder.Clear();
            _vectorLayers.Clear();
            _activeLayerIndex = -1;

            var layer = CreateLayer(document, name ?? "Layer 1", source, sourceFactory);
            InitializeLayerDisplay(layer, palette);
            _layers.Add(layer);
            _drawOrder.Add(layer);
            _activeLayerIndex = 0;

            _needsFit = true;
            ZoomToFit();

            RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsGeoTiff(string path) =>
            path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);

        // ---- colourisation (active layer) ------------------------------------------

        public void SetPalette(Palette palette)
        {
            var layer = ActiveLayer;
            if (layer?.Colorizer == null || palette == null) return;
            if (ReferenceEquals(layer.Colorizer.Palette, palette)) return;
            RecordUndo("Palette");
            layer.Colorizer.Palette = palette;
            RebuildLayerBitmap(layer);
        }

        public void SetValueRange(double min, double max)
        {
            var layer = ActiveLayer;
            if (layer?.Colorizer == null || !(max > min)) return;
            if (layer.Colorizer.Minimum == min && layer.Colorizer.Maximum == max) return;
            RecordUndo("Stretch", "range");
            layer.Colorizer.Minimum = min;
            layer.Colorizer.Maximum = max;
            RebuildLayerBitmap(layer);
        }

        public void AutoRange(RangeMode mode)
        {
            var layer = ActiveLayer;
            Raster? r = layer?.ActiveRaster;
            if (layer?.Colorizer == null || r == null) return;
            (double lo, double hi) = mode switch
            {
                RangeMode.MinMax => (r.Statistics.Minimum, r.Statistics.Maximum),
                RangeMode.TwoSigma => r.Statistics.SigmaRange(2.0),
                _ => RasterHistogram.Build(r).PercentileRange(2.0, 98.0),
            };
            SetValueRange(lo, hi);
        }

        public void SetGamma(double gamma)
        {
            var layer = ActiveLayer;
            if (layer?.Colorizer == null) return;
            RecordUndo("Gamma", "gamma");
            layer.Colorizer.Gamma = gamma <= 0 ? 1.0 : gamma;
            RebuildLayerBitmap(layer);
        }

        public void SetRenderMode(PaletteRenderMode mode, int classCount = 8)
        {
            var layer = ActiveLayer;
            if (layer?.Colorizer == null) return;
            if (layer.Colorizer.Mode == mode && layer.Colorizer.ClassCount == Math.Max(2, classCount)) return;
            RecordUndo("Palette mode");
            layer.Colorizer.Mode = mode;
            layer.Colorizer.ClassCount = Math.Max(2, classCount);
            RebuildLayerBitmap(layer);
        }

        /// <summary>
        /// Re-reads the active layer's displayed band from its document (e.g. after
        /// <see cref="ErsDocument.ReplaceBand"/> — a gap fill) and re-renders, without disturbing
        /// the current pan/zoom. Only meaningful when the layer is fully loaded (not streaming).
        /// </summary>
        public void RefreshFromDocument()
        {
            var layer = ActiveLayer;
            if (layer == null) return;
            layer.Raster = layer.Document.Band;
            if (layer.Raster == null || layer.Colorizer == null) return;
            RebuildLayerBitmap(layer);
        }

        /// <summary>The active layer's currently displayed pixels, colourised (or RGB-composited), as a plain image.</summary>
        public RasterImage? RenderToImage()
        {
            var layer = ActiveLayer;
            if (layer == null) return null;

            if (layer.ShowRgbComposite)
            {
                var bands = layer.RgbBands;
                return bands == null ? null : RgbCompositeRenderer.Render(bands.Value.R, bands.Value.G, bands.Value.B, layer.RgbNeedsAutoStretch);
            }

            Raster? r = layer.ActiveRaster;
            return r != null && layer.Colorizer != null ? RasterImageRenderer.Render(r, layer.Colorizer) : null;
        }

        // ---- view -----------------------------------------------------------------

        public void ZoomToFit()
        {
            double vw = Bounds.Width, vh = Bounds.Height;
            if (vw <= 0 || vh <= 0) { _needsFit = true; return; }

            var layer = ActiveLayer;
            if (layer == null) { _needsFit = true; return; }

            double contentW, contentH;
            if (layer.Source != null) { contentW = layer.DatasetWidth; contentH = layer.DatasetHeight; }
            else if (layer.Bitmap != null) { contentW = layer.Bitmap.PixelSize.Width; contentH = layer.Bitmap.PixelSize.Height; }
            else { _needsFit = true; return; }

            if (contentW <= 0 || contentH <= 0) { _needsFit = true; return; }

            _scale = Clamp(Math.Min(vw / contentW, vh / contentH) * 0.98, MinScale, MaxScale);
            _offsetX = (vw - contentW * _scale) / 2.0;
            _offsetY = (vh - contentH * _scale) / 2.0;
            _needsFit = false;
            RaiseViewChanged();
        }

        public void ZoomBy(double factor) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

        private void ZoomAt(Point pivot, double factor)
        {
            double newScale = Clamp(_scale * factor, MinScale, MaxScale);
            if (Math.Abs(newScale - _scale) < double.Epsilon) return;

            double wx = (pivot.X - _offsetX) / _scale;
            double wy = (pivot.Y - _offsetY) / _scale;
            _scale = newScale;
            _offsetX = pivot.X - wx * _scale;
            _offsetY = pivot.Y - wy * _scale;

            RaiseViewChanged();
        }

        private void RaiseViewChanged()
        {
            RefreshStreamingWindow();
            ScheduleSmoothOverlay();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        /// <summary>
        /// Re-reads a window covering the visible area (plus a margin) for every visible
        /// streaming layer — the active layer directly in its own cell space (as before), every
        /// other one by projecting the same viewport through world coordinates into that layer's
        /// own georeference — decimated to match the current zoom, re-colourised, but only when a
        /// layer's current cache no longer comfortably covers what is on screen.
        /// </summary>
        private void RefreshStreamingWindow()
        {
            CancelStreamingRefresh();
            if (ActiveLayer == null || !_layers.Any(l => l.Source != null)) return;

            if (_streamRefreshTimer == null)
            {
                _streamRefreshTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(40),
                };
                _streamRefreshTimer.Tick += (_, _) =>
                {
                    _streamRefreshTimer.Stop();
                    BeginStreamingRefresh();
                };
            }
            _streamRefreshTimer.Stop();
            _streamRefreshTimer.Start();
        }

        private void CancelStreamingRefresh()
        {
            _streamGeneration++;
            _streamRefreshCts?.Cancel();
            _streamRefreshBusy = false;
        }

        private async void BeginStreamingRefresh()
        {
            var requests = BuildStreamingRequests();
            if (requests.Count == 0)
            {
                _streamRefreshBusy = false;
                return;
            }

            _streamRefreshCts?.Dispose();
            _streamRefreshCts = new CancellationTokenSource();
            CancellationToken token = _streamRefreshCts.Token;
            int generation = ++_streamGeneration;
            bool entered = false;
            _streamRefreshBusy = true;
            InvalidateVisual();
            try
            {
                await _streamReadGate.WaitAsync(token);
                entered = true;
                List<StreamingResult> results = await Task.Run(() => ReadStreamingRequests(requests, token), token);
                if (token.IsCancellationRequested || generation != _streamGeneration) return;

                foreach (var result in results)
                {
                    if (token.IsCancellationRequested || generation != _streamGeneration) return;
                    StreamingRequest request = result.Request;
                    RasterLayer layer = request.Layer;
                    if (!_layers.Contains(layer) || !ReferenceEquals(layer.Source, request.Source)) continue;

                    layer.WindowRaster = result.First;
                    layer.WindowRasterG = result.Green;
                    layer.WindowRasterB = result.Blue;
                    layer.BitmapOriginX = request.OriginX;
                    layer.BitmapOriginY = request.OriginY;
                    layer.BitmapStep = request.Step;
                    RebuildLayerBitmap(layer);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Streaming refresh failed: {ex}");
            }
            finally
            {
                if (entered) _streamReadGate.Release();
                if (generation == _streamGeneration)
                {
                    _streamRefreshBusy = false;
                    InvalidateVisual();
                }
            }
        }

        private List<StreamingRequest> BuildStreamingRequests()
        {
            var requests = new List<StreamingRequest>();
            var active = ActiveLayer;
            if (active == null) return requests;

            double vw = Bounds.Width, vh = Bounds.Height;
            if (vw <= 0 || vh <= 0) return requests;
            int bitmapW = (int)Math.Ceiling(vw) + StreamingMargin * 2;
            int bitmapH = (int)Math.Ceiling(vh) + StreamingMargin * 2;
            Point activeCentreCell = ScreenToCell(new Point(vw / 2.0, vh / 2.0));

            foreach (var layer in _layers)
            {
                bool comparisonLayer = _comparisonMode != RasterComparisonMode.None &&
                    (ReferenceEquals(layer, _comparisonFirst) || ReferenceEquals(layer, _comparisonSecond));
                if (layer.Source == null || (!layer.IsVisible && !comparisonLayer)) continue;
                StreamingRequest? request = CreateStreamingRequest(layer, active, activeCentreCell, bitmapW, bitmapH);
                if (request != null) requests.Add(request);
            }
            return requests;
        }

        private StreamingRequest? CreateStreamingRequest(RasterLayer layer, RasterLayer active,
            Point activeCentreCell, int bitmapW, int bitmapH)
        {
            int originX, originY, width, height, step;

            if (ReferenceEquals(layer, active))
            {
                step = _scale >= 1.0 ? 1 : Math.Max(1, (int)Math.Round(1.0 / _scale));
                double windowWidthCells = (double)bitmapW * step;
                double windowHeightCells = (double)bitmapH * step;
                originX = (int)Math.Floor(activeCentreCell.X - windowWidthCells / 2.0);
                originY = (int)Math.Floor(activeCentreCell.Y - windowHeightCells / 2.0);
                width = (int)windowWidthCells;
                height = (int)windowHeightCells;
            }
            else
            {
                var activeGeo = active.Document.GeoReference;
                var layerGeo = layer.Document.GeoReference;
                if (!layerGeo.IsInvertible) return null;

                int activeStep = _scale >= 1.0 ? 1 : Math.Max(1, (int)Math.Round(1.0 / _scale));
                double halfW = bitmapW * activeStep / 2.0, halfH = bitmapH * activeStep / 2.0;

                var corners = new[]
                {
                    activeGeo.PixelToWorld(activeCentreCell.X - halfW, activeCentreCell.Y - halfH),
                    activeGeo.PixelToWorld(activeCentreCell.X + halfW, activeCentreCell.Y - halfH),
                    activeGeo.PixelToWorld(activeCentreCell.X - halfW, activeCentreCell.Y + halfH),
                    activeGeo.PixelToWorld(activeCentreCell.X + halfW, activeCentreCell.Y + halfH),
                };

                double minCol = double.PositiveInfinity, minRow = double.PositiveInfinity;
                double maxCol = double.NegativeInfinity, maxRow = double.NegativeInfinity;
                foreach (var (wx, wy) in corners)
                {
                    var (col, row) = layerGeo.WorldToPixel(wx, wy);
                    if (col < minCol) minCol = col; if (col > maxCol) maxCol = col;
                    if (row < minRow) minRow = row; if (row > maxRow) maxRow = row;
                }

                double activeWorldPerCell = AverageWorldPerCell(activeGeo);
                double layerWorldPerCell = AverageWorldPerCell(layerGeo);
                double screenWorldPerPixel = activeWorldPerCell / _scale;
                step = layerWorldPerCell > 0 ? Math.Max(1, (int)Math.Round(screenWorldPerPixel / layerWorldPerCell)) : 1;

                originX = (int)Math.Floor(minCol);
                originY = (int)Math.Floor(minRow);
                width = Math.Max(1, (int)Math.Ceiling(maxCol) - originX);
                height = Math.Max(1, (int)Math.Ceiling(maxRow) - originY);
            }

            if (IsLayerStreamingCacheGood(layer, originX, originY, step, width, height)) return null;
            return new StreamingRequest(layer, layer.Source!, originX, originY, width, height, step,
                layer.ShowRgbComposite, layer.ActiveBand);
        }

        private static List<StreamingResult> ReadStreamingRequests(IReadOnlyList<StreamingRequest> requests,
            CancellationToken token)
        {
            var results = new List<StreamingResult>(requests.Count);
            foreach (var request in requests)
            {
                token.ThrowIfCancellationRequested();
                Raster first = request.Source.ReadWindow(request.OriginX, request.OriginY,
                    request.Width, request.Height, request.Step, request.Step,
                    request.Rgb ? 0 : request.Band);
                if (first.Width == 0 || first.Height == 0) continue;

                Raster? green = null, blue = null;
                if (request.Rgb)
                {
                    token.ThrowIfCancellationRequested();
                    green = request.Source.ReadWindow(request.OriginX, request.OriginY,
                        request.Width, request.Height, request.Step, request.Step, 1);
                    token.ThrowIfCancellationRequested();
                    blue = request.Source.ReadWindow(request.OriginX, request.OriginY,
                        request.Width, request.Height, request.Step, request.Step, 2);
                }
                results.Add(new StreamingResult(request, first, green, blue));
            }
            return results;
        }

        private sealed record StreamingRequest(RasterLayer Layer, IRasterSource Source,
            int OriginX, int OriginY, int Width, int Height, int Step, bool Rgb, int Band);

        private sealed record StreamingResult(StreamingRequest Request, Raster First, Raster? Green, Raster? Blue);

        private static bool IsLayerStreamingCacheGood(RasterLayer layer, int requestedX, int requestedY, int step, int width, int height)
        {
            if (layer.WindowRaster == null || layer.BitmapStep != step) return false;

            double cachedX0 = layer.BitmapOriginX, cachedY0 = layer.BitmapOriginY;
            double cachedX1 = layer.BitmapOriginX + layer.WindowRaster.Width * layer.BitmapStep;
            double cachedY1 = layer.BitmapOriginY + layer.WindowRaster.Height * layer.BitmapStep;

            double reqX0 = requestedX, reqY0 = requestedY;
            double reqX1 = requestedX + (double)width;
            double reqY1 = requestedY + (double)height;

            // Require at least half of the streaming margin to remain in hand on every side.
            double shrink = StreamingMargin * step * 0.5;
            return reqX0 + shrink >= cachedX0 && reqY0 + shrink >= cachedY0 &&
                   reqX1 - shrink <= cachedX1 && reqY1 - shrink <= cachedY1;
        }

        private void RebuildLayerBitmap(RasterLayer layer)
        {
            RasterImage image;
            layer.RenderVersion++;
            if (ReferenceEquals(layer, ActiveLayer)) ScheduleSmoothOverlay();

            if (layer.ShowRgbComposite)
            {
                var bands = layer.RgbBands;
                if (bands == null)
                {
                    layer.Bitmap?.Dispose();
                    layer.Bitmap = null;
                    InvalidateVisual();
                    return;
                }
                image = RgbCompositeRenderer.Render(bands.Value.R, bands.Value.G, bands.Value.B, layer.RgbNeedsAutoStretch);
            }
            else
            {
                Raster? r = layer.ActiveRaster;
                if (r == null || layer.Colorizer == null)
                {
                    layer.Bitmap?.Dispose();
                    layer.Bitmap = null;
                    InvalidateVisual();
                    return;
                }
                image = RasterImageRenderer.Render(r, layer.Colorizer);
            }

            if (layer.Bitmap == null || layer.Bitmap.PixelSize.Width != image.Width || layer.Bitmap.PixelSize.Height != image.Height)
            {
                layer.Bitmap?.Dispose();
                layer.Bitmap = new WriteableBitmap(
                    new PixelSize(image.Width, image.Height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Unpremul);
            }

            using (ILockedFramebuffer fb = layer.Bitmap.Lock())
            {
                int srcStride = image.Stride;
                int dstStride = fb.RowBytes;
                if (srcStride == dstStride)
                {
                    Marshal.Copy(image.Pixels, 0, fb.Address, image.Pixels.Length);
                }
                else
                {
                    for (int y = 0; y < image.Height; y++)
                        Marshal.Copy(image.Pixels, y * srcStride, fb.Address + y * dstStride, srcStride);
                }
            }

            InvalidateVisual();
        }

        // ---- rendering ----------------------------------------------------------

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Background, new Rect(Bounds.Size));

            var active = ActiveLayer;
            bool comparisonValid = _comparisonMode != RasterComparisonMode.None &&
                _comparisonFirst?.Bitmap != null && _comparisonSecond?.Bitmap != null &&
                _layers.Contains(_comparisonFirst) && _layers.Contains(_comparisonSecond);
            if (active == null || (!comparisonValid && !_layers.Any(l => l.IsVisible && l.Bitmap != null)))
            {
                var text = new FormattedText(L.T("Drop .ers / .tif / .tiff / .erv / .geojson / .csv / .rfproj files here, or File ▸ Open…"),
                    System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 13, AppTheme.TextSecondary);
                context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
                return;
            }

            bool activeInvertible = active.Document.GeoReference.IsInvertible;
            if (comparisonValid)
            {
                DrawComparison(context, active, activeInvertible);
                foreach (var vectorLayer in _drawOrder.OfType<VectorLayer>())
                {
                    if (vectorLayer.IsVisible && activeInvertible)
                        DrawVectorLayer(context, vectorLayer, active);
                }
            }
            else
            {
                foreach (var layer in _drawOrder)
                {
                    switch (layer)
                    {
                        case RasterLayer rasterLayer when rasterLayer.IsVisible && rasterLayer.Bitmap != null && rasterLayer.Opacity > 0:
                            DrawRasterWithStyle(context, rasterLayer, active, activeInvertible);
                            break;
                        case VectorLayer vectorLayer when vectorLayer.IsVisible && activeInvertible:
                            DrawVectorLayer(context, vectorLayer, active);
                            break;
                    }
                }
            }

            if (ShowGrid && _scale >= 8.0 && active.Bitmap != null)
                DrawGrid(context, active);

            if (_hasSelection)
                DrawSelection(context);

            if (_path.Count > 0 && activeInvertible)
                DrawPath(context, active);

            if (comparisonValid)
                DrawComparisonOverlay(context);

            string hintText = $"zoom {_scale:0.###}×  ·  {_layers.Count} layer{(_layers.Count == 1 ? "" : "s")}";
            if (_vectorLayers.Count > 0) hintText += $" + {_vectorLayers.Count} vector";
            if (active.IsStreaming) hintText += _streamRefreshBusy ? "  (streaming · loading…)" : "  (streaming)";
            double? gsd = GroundSampleDistance;
            if (gsd.HasValue)
            {
                string unit = active.Document.Header.CoordinateSpace.EffectiveUnits;
                hintText += $"   ·   {FormatDistance(gsd.Value)} {unit}/px";
                double? denom = MapScaleDenominator(EffectiveDpi());
                if (denom.HasValue) hintText += $"   ·   1 : {FormatScale(denom.Value)}";
            }

            var hint = new FormattedText(hintText,
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10.5, new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEA)));
            var hintBox = new Rect(10, Bounds.Height - hint.Height - 16, hint.Width + 16, hint.Height + 8);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(150, 0x15, 0x18, 0x1D)), hintBox, 6);
            context.DrawText(hint, new Point(hintBox.X + 8, hintBox.Y + 4));
        }

        private void DrawRasterWithStyle(DrawingContext context, RasterLayer layer, RasterLayer active, bool activeInvertible)
        {
            if (layer.Bitmap == null || layer.Opacity <= 0) return;
            using (context.PushOpacity(layer.Opacity))
            using (context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = ToAvalonia(layer.BlendMode) }))
                DrawLayer(context, layer, active, activeInvertible);
        }

        private void DrawComparison(DrawingContext context, RasterLayer active, bool activeInvertible)
        {
            var first = _comparisonFirst!;
            var second = _comparisonSecond!;
            if (_comparisonMode == RasterComparisonMode.Blink)
            {
                DrawRasterWithStyle(context, _blinkShowsSecond ? second : first, active, activeInvertible);
                return;
            }

            double divider = Bounds.Width * _swipePosition;
            using (context.PushClip(new Rect(0, 0, divider, Bounds.Height)))
                DrawRasterWithStyle(context, first, active, activeInvertible);
            using (context.PushClip(new Rect(divider, 0, Math.Max(0, Bounds.Width - divider), Bounds.Height)))
                DrawRasterWithStyle(context, second, active, activeInvertible);
        }

        private void DrawComparisonOverlay(DrawingContext context)
        {
            string left = _comparisonMode == RasterComparisonMode.Blink
                ? (_blinkShowsSecond ? _comparisonSecond!.Name : _comparisonFirst!.Name)
                : _comparisonFirst!.Name;
            string right = _comparisonMode == RasterComparisonMode.Blink ? L.T("Blink") : _comparisonSecond!.Name;
            DrawComparisonLabel(context, left, 12, HorizontalAlignment.Left);
            DrawComparisonLabel(context, right, Bounds.Width - 12, HorizontalAlignment.Right);

            if (_comparisonMode != RasterComparisonMode.Swipe) return;
            double x = Bounds.Width * _swipePosition;
            var shadow = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 5);
            var line = new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2);
            context.DrawLine(shadow, new Point(x, 0), new Point(x, Bounds.Height));
            context.DrawLine(line, new Point(x, 0), new Point(x, Bounds.Height));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 255, 255)), null,
                new Point(x, Bounds.Height / 2), 7, 18);
        }

        private static void DrawComparisonLabel(DrawingContext context, string text, double anchorX, HorizontalAlignment alignment)
        {
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
            double x = alignment == HorizontalAlignment.Right ? anchorX - formatted.Width - 16 : anchorX;
            var box = new Rect(x, 12, formatted.Width + 16, formatted.Height + 8);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(175, 0, 0, 0)), box, 5);
            context.DrawText(formatted, new Point(box.X + 8, box.Y + 4));
        }

        /// <summary>
        /// Draws one layer's bitmap onto the shared screen space. The active layer (or, as a
        /// degenerate fallback, any layer when the active one's georeference isn't invertible)
        /// draws directly via its own bitmap origin/step; every other layer is placed by
        /// computing the affine transform from its bitmap's own pixel space into the active
        /// layer's cell space (via world coordinates) and applying it as a draw-time transform —
        /// exact for a pure scale/translate difference between layers, and correct even when they
        /// are relatively rotated to each other.
        /// </summary>
        private void DrawLayer(DrawingContext context, RasterLayer layer, RasterLayer active, bool activeInvertible)
        {
            var bmp = layer.Bitmap!;
            double bw = bmp.PixelSize.Width, bh = bmp.PixelSize.Height;

            if (ReferenceEquals(layer, active) || !activeInvertible)
            {
                if (!ReferenceEquals(layer, active)) return; // can't place a non-active layer without an invertible active geo

                double destX = _offsetX + layer.BitmapOriginX * _scale;
                double destY = _offsetY + layer.BitmapOriginY * _scale;
                var dest = new Rect(destX, destY, bw * layer.BitmapStep * _scale, bh * layer.BitmapStep * _scale);
                context.DrawImage(bmp, new Rect(0, 0, bw, bh), dest);

                var smooth = layer.SmoothBitmap;
                if (smooth != null && _displayResampling == DisplayResampling.Bezier)
                {
                    double sw = smooth.PixelSize.Width, sh = smooth.PixelSize.Height;
                    var sdest = new Rect(_offsetX + layer.SmoothOriginX * _scale, _offsetY + layer.SmoothOriginY * _scale,
                        sw * layer.SmoothStep * _scale, sh * layer.SmoothStep * _scale);
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                        context.DrawImage(smooth, new Rect(0, 0, sw, sh), sdest);
                }
                return;
            }

            Point p0 = LayerBitmapPixelToScreen(layer, active, 0, 0);
            Point p1 = LayerBitmapPixelToScreen(layer, active, bw, 0);
            Point p2 = LayerBitmapPixelToScreen(layer, active, 0, bh);

            var matrix = new Matrix(
                (p1.X - p0.X) / bw, (p1.Y - p0.Y) / bw,
                (p2.X - p0.X) / bh, (p2.Y - p0.Y) / bh,
                p0.X, p0.Y);

            using (context.PushTransform(matrix))
                context.DrawImage(bmp, new Rect(0, 0, bw, bh), new Rect(0, 0, bw, bh));
        }

        private Point LayerBitmapPixelToScreen(RasterLayer layer, RasterLayer active, double bx, double by)
        {
            double col = layer.BitmapOriginX + bx * layer.BitmapStep;
            double row = layer.BitmapOriginY + by * layer.BitmapStep;
            var (wx, wy) = layer.Document.GeoReference.PixelToWorld(col, row);
            var (acol, arow) = active.Document.GeoReference.WorldToPixel(wx, wy);
            return new Point(_offsetX + acol * _scale, _offsetY + arow * _scale);
        }

        // ---- vector layer rendering -----------------------------------------------

        /// <summary>
        /// A vector object's (X, Y) is already a world coordinate in the same sense as a raster
        /// cell's <c>PixelToWorld</c> result (both interpreted per the shared <c>CoordinateSpace</c>
        /// convention) — so placing it only needs the active layer's <c>WorldToPixel</c>, not a
        /// second layer-to-layer projection the way one raster layer needs to place another.
        /// </summary>
        private Point WorldToScreen(RasterLayer active, double worldX, double worldY)
        {
            var (col, row) = active.Document.GeoReference.WorldToPixel(worldX, worldY);
            return new Point(_offsetX + col * _scale, _offsetY + row * _scale);
        }

        /// <summary>
        /// The world-space rectangle currently on screen (all four viewport corners, so it's
        /// still correct under rotation), expanded by <see cref="StreamingMargin"/> screen pixels'
        /// worth of world space — generous enough that a point marker, stroke width or short text
        /// run anchored just outside the strict viewport still gets drawn. Used to cull vector
        /// objects that are nowhere near the screen before doing any per-vertex work on them.
        /// </summary>
        private (double MinX, double MinY, double MaxX, double MaxY) VisibleWorldRect(RasterLayer active)
        {
            double vw = Bounds.Width, vh = Bounds.Height;
            var geo = active.Document.GeoReference;
            if (vw <= 0 || vh <= 0 || !geo.IsInvertible)
                return (double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity);

            Span<Point> screenCorners = stackalloc Point[] { new Point(0, 0), new Point(vw, 0), new Point(0, vh), new Point(vw, vh) };
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var sc in screenCorners)
            {
                Point cell = ScreenToCell(sc);
                var (wx, wy) = geo.PixelToWorld(cell.X, cell.Y);
                if (wx < minX) minX = wx;
                if (wx > maxX) maxX = wx;
                if (wy < minY) minY = wy;
                if (wy > maxY) maxY = wy;
            }

            double margin = StreamingMargin * AverageWorldPerCell(geo) / Math.Max(_scale, 1e-9);
            return (minX - margin, minY - margin, maxX + margin, maxY + margin);
        }

        /// <summary>
        /// Draws every object in a vector layer, in that layer's own <see cref="VectorLayer.Color"/>
        /// and <see cref="VectorLayer.LineWidth"/> (a per-layer override, rather than honouring
        /// each object's own embedded colour/width — simpler and more predictable). Page-relative
        /// objects (print-composition coordinates, not image/world ones) are skipped — this is an
        /// interactive data viewer, not a map-composition renderer.
        ///
        /// Two things that used to make a large <c>.erv</c> render sluggishly, fixed here: every
        /// object was allocating its own <see cref="SolidColorBrush"/>/<see cref="Pen"/> on every
        /// single repaint even though a layer's colour/width is shared by all its objects (now
        /// built once per layer per frame), and every object was walked and tessellated even when
        /// nowhere near the viewport (now culled up front against each object's precomputed
        /// <see cref="VectorLayer.ObjectBounds"/>, without touching its point list at all).
        /// </summary>
        private void DrawVectorLayer(DrawingContext context, VectorLayer layer, RasterLayer active)
        {
            var objects = layer.Document.Objects;
            var bounds = layer.ObjectBounds;
            var (minX, minY, maxX, maxY) = VisibleWorldRect(active);

            var strokeBrush = new SolidColorBrush(layer.Color);
            var pen = new Pen(strokeBrush, Math.Max(0.5, layer.LineWidth));
            var fillBrush = new SolidColorBrush(layer.Color, 0.35);
            var widthFactors = layer.WidthFactors;
            var labels = layer.Labels;
            Dictionary<double, Pen>? pens = widthFactors == null ? null : new Dictionary<double, Pen>();

            for (int i = 0; i < objects.Count; i++)
            {
                var (bMinX, bMinY, bMaxX, bMaxY) = bounds[i];
                if (bMaxX < minX || bMinX > maxX || bMaxY < minY || bMinY > maxY) continue;

                Pen objectPen = pen;
                if (pens != null && widthFactors![i] != 1.0)
                {
                    double factor = widthFactors[i];
                    if (!pens.TryGetValue(factor, out objectPen!))
                        pens[factor] = objectPen = new Pen(strokeBrush, Math.Max(0.5, layer.LineWidth * factor));
                }

                switch (objects[i])
                {
                    case VectorPoint p: DrawVectorPoint(context, active, strokeBrush, p); break;
                    case VectorPolyObject poly:
                        DrawVectorPoly(context, active, objectPen, fillBrush, poly);
                        if (labels?[i] is string label) DrawLineLabel(context, active, strokeBrush, poly, label);
                        break;
                    case VectorRectangleObject rect: DrawVectorRect(context, active, pen, fillBrush, rect); break;
                    case VectorTextObject text: DrawVectorText(context, active, strokeBrush, text); break;
                }
            }
        }

        private void DrawVectorPoint(DrawingContext context, RasterLayer active, IBrush brush, VectorPoint p)
        {
            if (p.Page) return;
            Point pt = WorldToScreen(active, p.X, p.Y);
            const double r = 4;
            context.DrawEllipse(brush, new Pen(Brushes.Black, 1), pt, r, r);
        }

        private void DrawVectorPoly(DrawingContext context, RasterLayer active, Pen pen, IBrush fillBrush, VectorPolyObject poly)
        {
            if (poly.Page || poly.Points.Count < 2) return;

            bool closed = poly is VectorPolygon or VectorMapPolygon;

            var geometry = new StreamGeometry();
            using (var gc = geometry.Open())
            {
                var pts = poly.Points;
                gc.BeginFigure(WorldToScreen(active, pts[0].X, pts[0].Y), closed && poly.Fill != 0);
                for (int i = 1; i < pts.Count; i++)
                    gc.LineTo(WorldToScreen(active, pts[i].X, pts[i].Y));
                gc.EndFigure(closed);
            }

            IBrush? fill = closed && poly.Fill != 0 ? fillBrush : null;
            context.DrawGeometry(fill, pen, geometry);
        }

        /// <summary>
        /// Draws a label on a polyline at its middle vertex, rotated to follow the line (kept upright),
        /// on a translucent halo — only when the line is long enough on screen to carry it.
        /// </summary>
        private void DrawLineLabel(DrawingContext context, RasterLayer active, IBrush brush, VectorPolyObject poly, string label)
        {
            var pts = poly.Points;
            if (poly.Page || pts.Count < 2) return;
            int mid = pts.Count / 2;
            Point a = WorldToScreen(active, pts[Math.Max(0, mid - 1)].X, pts[Math.Max(0, mid - 1)].Y);
            Point b = WorldToScreen(active, pts[mid].X, pts[mid].Y);
            Point first = WorldToScreen(active, pts[0].X, pts[0].Y), last = WorldToScreen(active, pts[pts.Count - 1].X, pts[pts.Count - 1].Y);
            var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold), 11, brush);
            if (Distance(first, last) < ft.Width * 1.5 && pts.Count < 8) return; // too small on screen

            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            if (angle > Math.PI / 2) angle -= Math.PI;
            if (angle < -Math.PI / 2) angle += Math.PI;
            var centre = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);

            var m = Matrix.CreateTranslation(-ft.Width / 2, -ft.Height / 2) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(centre.X, centre.Y);
            using (context.PushTransform(m))
            {
                // Map-style label: the line's own colour on a pale halo, readable over any palette.
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(215, 255, 255, 250)), new Rect(-3, 0, ft.Width + 6, ft.Height), 3);
                context.DrawText(ft, new Point(0, 0));
            }
        }

        private void DrawVectorRect(DrawingContext context, RasterLayer active, Pen pen, IBrush fillBrush, VectorRectangleObject rect)
        {
            if (rect.Page) return;

            Point p0 = WorldToScreen(active, rect.Ltx, rect.Lty);
            Point p1 = WorldToScreen(active, rect.Rbx, rect.Rby);
            var screenRect = new Rect(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Abs(p1.X - p0.X), Math.Abs(p1.Y - p0.Y));

            IBrush? fill = rect.Fill != 0 ? fillBrush : null;

            if (rect is VectorOval)
                context.DrawEllipse(fill, pen, screenRect.Center, screenRect.Width / 2, screenRect.Height / 2);
            else
                context.DrawRectangle(fill, pen, screenRect);
        }

        private void DrawVectorText(DrawingContext context, RasterLayer active, IBrush brush, VectorTextObject text)
        {
            if (text.Page || text.Lines.Count == 0) return;

            Point anchor = WorldToScreen(active, text.X, text.Y);
            string joined = string.Join("\n", text.Lines);
            var ft = new FormattedText(joined, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, Math.Max(6, text.Size), brush);

            // The format anchors text at the bottom-left of its first line.
            context.DrawText(ft, new Point(anchor.X, anchor.Y - ft.Height));
        }

        private Rect SelectionScreenRect()
        {
            double sx0 = _offsetX + _selX0 * _scale, sy0 = _offsetY + _selY0 * _scale;
            double sx1 = _offsetX + _selX1 * _scale, sy1 = _offsetY + _selY1 * _scale;
            return new Rect(Math.Min(sx0, sx1), Math.Min(sy0, sy1), Math.Abs(sx1 - sx0), Math.Abs(sy1 - sy0));
        }

        /// <summary>The 8 handle positions (screen space) for a selection rectangle, paired with what dragging each one does.</summary>
        private static (Point Point, SelDrag Drag)[] HandlePoints(Rect r) => new[]
        {
            (new Point(r.Left, r.Top), SelDrag.TL),
            (new Point(r.Right, r.Top), SelDrag.TR),
            (new Point(r.Left, r.Bottom), SelDrag.BL),
            (new Point(r.Right, r.Bottom), SelDrag.BR),
            (new Point((r.Left + r.Right) / 2, r.Top), SelDrag.T),
            (new Point((r.Left + r.Right) / 2, r.Bottom), SelDrag.B),
            (new Point(r.Left, (r.Top + r.Bottom) / 2), SelDrag.L),
            (new Point(r.Right, (r.Top + r.Bottom) / 2), SelDrag.R),
        };

        private void DrawSelection(DrawingContext context)
        {
            Rect rect = SelectionScreenRect();

            context.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 210, 0)), rect);
            var dash = new DashStyle(new double[] { 4, 2 }, 0);
            context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 210, 0)), 1.5, dash), rect);

            var handleFill = new SolidColorBrush(Color.FromArgb(255, 255, 210, 0));
            var handleOutline = new Pen(Brushes.Black, 1);
            const double hs = 5;
            foreach (var (pt, _) in HandlePoints(rect))
                context.DrawRectangle(handleFill, handleOutline, new Rect(pt.X - hs, pt.Y - hs, hs * 2, hs * 2));

            DrawSelectionLabel(context, rect);
        }

        private void DrawSelectionLabel(DrawingContext context, Rect rect)
        {
            int w = Math.Max(0, (int)Math.Round(rect.Width / _scale));
            int h = Math.Max(0, (int)Math.Round(rect.Height / _scale));
            string label = $"{w} × {h} px";

            if (Document != null)
            {
                var (_, b, c, _, e, f) = Document.GeoReference.GeoTransform;
                double worldW = Math.Sqrt(b * b + e * e) * w;
                double worldH = Math.Sqrt(c * c + f * f) * h;
                string unit = Document.Header.CoordinateSpace.EffectiveUnits;
                label += $"   ({FormatDistance(worldW)} × {FormatDistance(worldH)} {unit})";
            }

            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.Black);

            double lx = rect.Left, ly = rect.Top - text.Height - 6;
            if (ly < 0) ly = rect.Bottom + 6;

            context.FillRectangle(new SolidColorBrush(Color.FromArgb(235, 255, 210, 0)),
                new Rect(lx - 2, ly - 1, text.Width + 4, text.Height + 2));
            context.DrawText(text, new Point(lx, ly));
        }

        private void DrawPath(DrawingContext context, RasterLayer active)
        {
            Color color = _pathTool switch
            {
                PathTool.Measure => Color.FromRgb(255, 210, 0),
                PathTool.Zone => Color.FromRgb(255, 90, 200),
                _ => Color.FromRgb(80, 200, 255),
            };
            var brush = new SolidColorBrush(color);
            var pts = _path.Select(p => WorldToScreen(active, p.X, p.Y)).ToList();

            bool polygon = _pathTool == PathTool.Zone || (_pathTool == PathTool.Measure && _pathFinished);
            if (pts.Count >= 2)
            {
                var geometry = new StreamGeometry();
                using (var gc = geometry.Open())
                {
                    gc.BeginFigure(pts[0], polygon && pts.Count >= 3);
                    for (int i = 1; i < pts.Count; i++) gc.LineTo(pts[i]);
                    gc.EndFigure(polygon && pts.Count >= 3);
                }
                IBrush? fill = polygon && pts.Count >= 3 ? new SolidColorBrush(color, 0.18) : null;
                context.DrawGeometry(fill, new Pen(new SolidColorBrush(Colors.Black, 0.6), 4), geometry);
                context.DrawGeometry(fill, new Pen(brush, 2), geometry);
                if (_pathTool == PathTool.Measure && !_pathFinished && pts.Count >= 3)
                    context.DrawLine(new Pen(brush, 1, new DashStyle(new double[] { 4, 3 }, 0)), pts[pts.Count - 1], pts[0]);
            }

            // Midpoint handles (hollow, smaller): drag one to insert a new vertex on that segment.
            var midFill = new SolidColorBrush(Colors.White, 0.85);
            var midPen = new Pen(brush, 1.5);
            foreach (var (_, midpoint) in PathMidpoints(active)) context.DrawEllipse(midFill, midPen, midpoint, 3.5, 3.5);

            var outline = new Pen(Brushes.Black, 1);
            foreach (var pt in pts) context.DrawEllipse(brush, outline, pt, 4.5, 4.5);

            if (pts.Count >= 2)
            {
                string unit = active.Document.Header.CoordinateSpace.EffectiveUnits;
                string label = FormatDistance(Measurement.Length(_path)) + " " + unit;
                if (polygon && _path.Count >= 3)
                    label += "   ·   " + FormatDistance(Measurement.Area(_path)) + " " + unit + "²";
                var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.Black);
                Point at = new Point(pts[pts.Count - 1].X + 10, pts[pts.Count - 1].Y - text.Height - 6);
                context.FillRectangle(new SolidColorBrush(color, 0.92), new Rect(at.X - 3, at.Y - 1, text.Width + 6, text.Height + 2), 3);
                context.DrawText(text, at);
            }
        }

        private SelDrag HitTestHandle(Point screen)
        {
            if (!_hasSelection) return SelDrag.None;
            Rect rect = SelectionScreenRect();
            const double tol = 8;

            foreach (var (pt, drag) in HandlePoints(rect))
            {
                double dx = pt.X - screen.X, dy = pt.Y - screen.Y;
                if (Math.Sqrt(dx * dx + dy * dy) <= tol) return drag;
            }
            return rect.Contains(screen) ? SelDrag.Move : SelDrag.None;
        }

        private void NormalizeAndClampSelection()
        {
            if (Document == null) { _hasSelection = false; return; }

            double x0 = Clamp(Math.Min(_selX0, _selX1), 0, DatasetWidth);
            double x1 = Clamp(Math.Max(_selX0, _selX1), 0, DatasetWidth);
            double y0 = Clamp(Math.Min(_selY0, _selY1), 0, DatasetHeight);
            double y1 = Clamp(Math.Max(_selY0, _selY1), 0, DatasetHeight);

            if (x1 - x0 < 1 || y1 - y0 < 1) { _hasSelection = false; return; }
            _selX0 = x0; _selX1 = x1; _selY0 = y0; _selY1 = y1;
        }

        /// <summary>Best available screen DPI (96 &#215; the platform's render scaling), falling back to 96.</summary>
        private double EffectiveDpi() => (this.GetVisualRoot() as TopLevel)?.RenderScaling is double s ? s * 96.0 : 96.0;

        private static string FormatDistance(double metersPerPixel) =>
            metersPerPixel >= 1
                ? metersPerPixel.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : metersPerPixel.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        private static string FormatScale(double denominator) =>
            denominator >= 1000
                ? $"{denominator / 1000.0:0.#}k"
                : denominator.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        private void DrawGrid(DrawingContext context, RasterLayer active)
        {
            if (active.Bitmap == null) return;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), 1);

            double datasetMinX = active.BitmapOriginX, datasetMaxX = active.BitmapOriginX + active.Bitmap.PixelSize.Width * active.BitmapStep;
            double datasetMinY = active.BitmapOriginY, datasetMaxY = active.BitmapOriginY + active.Bitmap.PixelSize.Height * active.BitmapStep;

            int firstCol = (int)Math.Max(datasetMinX, Math.Floor(ScreenToCell(new Point(0, 0)).X));
            int lastCol = (int)Math.Min(datasetMaxX, Math.Ceiling(ScreenToCell(new Point(Bounds.Width, 0)).X));
            for (int c = firstCol; c <= lastCol; c++)
            {
                double x = _offsetX + c * _scale;
                context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
            }

            int firstRow = (int)Math.Max(datasetMinY, Math.Floor(ScreenToCell(new Point(0, 0)).Y));
            int lastRow = (int)Math.Min(datasetMaxY, Math.Ceiling(ScreenToCell(new Point(0, Bounds.Height)).Y));
            for (int r = firstRow; r <= lastRow; r++)
            {
                double y = _offsetY + r * _scale;
                context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }

        // ---- input ------------------------------------------------------------

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var p = e.GetCurrentPoint(this);

            if (_comparisonMode == RasterComparisonMode.Swipe && p.Properties.IsLeftButtonPressed &&
                Math.Abs(p.Position.X - Bounds.Width * _swipePosition) <= 12)
            {
                _draggingSwipe = true;
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                e.Handled = true;
                return;
            }

            if (SelectionMode && p.Properties.IsLeftButtonPressed && Document != null)
            {
                SelDrag hit = _hasSelection ? HitTestHandle(p.Position) : SelDrag.None;

                if (hit == SelDrag.None)
                {
                    // clicked outside any existing selection (or there wasn't one): start a new one
                    Point c = ScreenToCell(p.Position);
                    _selX0 = _selX1 = Clamp(c.X, 0, DatasetWidth);
                    _selY0 = _selY1 = Clamp(c.Y, 0, DatasetHeight);
                    _hasSelection = true;
                    _selDrag = SelDrag.Create;
                }
                else
                {
                    _selDrag = hit;
                    _dragStartX0 = _selX0; _dragStartY0 = _selY0;
                    _dragStartX1 = _selX1; _dragStartY1 = _selY1;
                }

                _dragStartScreen = p.Position;
                e.Pointer.Capture(this);
                RaiseSelectionChanged();
                InvalidateVisual();
                return;
            }

            if (_pathTool != PathTool.None && Document != null)
            {
                if (p.Properties.IsRightButtonPressed) { FinishPath(); e.Handled = true; return; }
                if (p.Properties.IsLeftButtonPressed)
                {
                    if (e.ClickCount >= 2) { FinishPath(); e.Handled = true; return; }
                    int hit = HitTestPathVertex(p.Position);
                    if (hit >= 0)
                    {
                        _pathDragIndex = hit;
                        e.Pointer.Capture(this);
                        return;
                    }
                    int segment = HitTestPathMidpoint(p.Position);
                    if (segment >= 0)
                    {
                        _pathInsertSegment = segment;
                        _pathPressScreen = p.Position;
                        e.Pointer.Capture(this);
                        return;
                    }
                    // Might be a click (adds a vertex on release) or the start of a pan.
                    _pathClickPending = true;
                    _pathPressScreen = p.Position;
                    _panning = true;
                    _panLast = p.Position;
                    e.Pointer.Capture(this);
                    return;
                }
            }

            if (IdentifyMode && p.Properties.IsLeftButtonPressed && Document != null)
            {
                Point c = ScreenToCell(p.Position);
                var (wx, wy) = Document.GeoReference.PixelToWorld(c.X, c.Y);
                IdentifyRequested?.Invoke(this, new RasterReadoutEventArgs
                {
                    WorldX = wx, WorldY = wy,
                    Column = (int)Math.Floor(c.X), Row = (int)Math.Floor(c.Y),
                    InsideRaster = c.X >= 0 && c.Y >= 0 && c.X < DatasetWidth && c.Y < DatasetHeight,
                });
                return;
            }

            if (p.Properties.IsLeftButtonPressed || p.Properties.IsMiddleButtonPressed)
            {
                _panning = true;
                _panLast = p.Position;
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_draggingSwipe)
            {
                _draggingSwipe = false;
                e.Pointer.Capture(null);
                Cursor = Cursor.Default;
                e.Handled = true;
                return;
            }

            if (_selDrag != SelDrag.None)
            {
                _selDrag = SelDrag.None;
                e.Pointer.Capture(null);
                NormalizeAndClampSelection();
                RaiseSelectionChanged();
                InvalidateVisual();
                return;
            }

            if (_pathDragIndex >= 0)
            {
                _pathDragIndex = -1;
                e.Pointer.Capture(null);
                RaisePathChanged();
                return;
            }

            if (_pathInsertSegment >= 0)
            {
                // Released without dragging: a plain click on a midpoint handle changes nothing.
                _pathInsertSegment = -1;
                e.Pointer.Capture(null);
                return;
            }

            if (_pathClickPending)
            {
                _pathClickPending = false;
                _panning = false;
                e.Pointer.Capture(null);
                var world = ScreenToWorld(e.GetPosition(this));
                if (world != null)
                {
                    if (_pathFinished) { _path.Clear(); _pathFinished = false; }
                    _path.Add(world.Value);
                    RaisePathChanged();
                }
                return;
            }

            if (_panning)
            {
                _panning = false;
                e.Pointer.Capture(null);
                Cursor = SelectionMode || _pathTool != PathTool.None ? new Cursor(StandardCursorType.Cross)
                    : IdentifyMode ? new Cursor(StandardCursorType.Help) : Cursor.Default;
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Point pos = e.GetPosition(this);

            if (_draggingSwipe)
            {
                _swipePosition = Bounds.Width <= 0 ? 0.5 : Math.Clamp(pos.X / Bounds.Width, 0.02, 0.98);
                InvalidateVisual();
            }
            else if (_selDrag != SelDrag.None)
            {
                UpdateSelectionDrag(pos);
                RaiseSelectionChanged();
                InvalidateVisual();
            }
            else if (_pathInsertSegment >= 0)
            {
                // The drag has started: insert the vertex after the segment's start and keep dragging it.
                var world = ScreenToWorld(pos);
                if (world != null && Distance(pos, _pathPressScreen) > 3 && _pathInsertSegment < _path.Count)
                {
                    int index = _pathInsertSegment + 1;
                    _path.Insert(index, world.Value);
                    _pathInsertSegment = -1;
                    _pathDragIndex = index;
                    RaisePathChanged();
                }
            }
            else if (_pathDragIndex >= 0)
            {
                var world = ScreenToWorld(pos);
                if (world != null && _pathDragIndex < _path.Count) { _path[_pathDragIndex] = world.Value; RaisePathChanged(); }
            }
            else if (_panning)
            {
                if (_pathClickPending && Distance(pos, _pathPressScreen) > 4) _pathClickPending = false; // it's a pan after all
                _offsetX += pos.X - _panLast.X;
                _offsetY += pos.Y - _panLast.Y;
                _panLast = pos;
                RaiseViewChanged();
            }
            else if (SelectionMode)
            {
                var hover = HitTestHandle(pos);
                Cursor = hover != SelDrag.None ? new Cursor(StandardCursorType.SizeAll) : new Cursor(StandardCursorType.Cross);
            }
            else if (_pathTool != PathTool.None)
            {
                Cursor = HitTestPathVertex(pos) >= 0 || HitTestPathMidpoint(pos) >= 0
                    ? new Cursor(StandardCursorType.SizeAll)
                    : new Cursor(StandardCursorType.Cross);
            }
            else if (_comparisonMode == RasterComparisonMode.Swipe &&
                     Math.Abs(pos.X - Bounds.Width * _swipePosition) <= 12)
            {
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
            }

            RaiseReadout(pos);
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void UpdateSelectionDrag(Point screenPos)
        {
            double maxW = DatasetWidth, maxH = DatasetHeight;
            Point cur = ScreenToCell(screenPos);
            double cx = Clamp(cur.X, 0, maxW), cy = Clamp(cur.Y, 0, maxH);

            switch (_selDrag)
            {
                case SelDrag.Create: _selX1 = cx; _selY1 = cy; break;
                case SelDrag.TL: _selX0 = cx; _selY0 = cy; break;
                case SelDrag.TR: _selX1 = cx; _selY0 = cy; break;
                case SelDrag.BL: _selX0 = cx; _selY1 = cy; break;
                case SelDrag.BR: _selX1 = cx; _selY1 = cy; break;
                case SelDrag.T: _selY0 = cy; break;
                case SelDrag.B: _selY1 = cy; break;
                case SelDrag.L: _selX0 = cx; break;
                case SelDrag.R: _selX1 = cx; break;
                case SelDrag.Move:
                {
                    Point start = ScreenToCell(_dragStartScreen);
                    double dx = cur.X - start.X, dy = cur.Y - start.Y;
                    double nx0 = _dragStartX0 + dx, nx1 = _dragStartX1 + dx;
                    double ny0 = _dragStartY0 + dy, ny1 = _dragStartY1 + dy;
                    double w = nx1 - nx0, h = ny1 - ny0;

                    if (nx0 < 0) { nx0 = 0; nx1 = w; }
                    if (nx1 > maxW) { nx1 = maxW; nx0 = maxW - w; }
                    if (ny0 < 0) { ny0 = 0; ny1 = h; }
                    if (ny1 > maxH) { ny1 = maxH; ny0 = maxH - h; }

                    _selX0 = nx0; _selX1 = nx1; _selY0 = ny0; _selY1 = ny1;
                    break;
                }
            }
        }

        private Point ScreenToCell(Point screen) => new Point((screen.X - _offsetX) / _scale, (screen.Y - _offsetY) / _scale);

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            double factor = e.Delta.Y > 0 ? 1.2 : 1.0 / 1.2;
            ZoomAt(e.GetPosition(this), factor);
            RaiseReadout(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            const double step = 40;
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add: ZoomBy(1.2); break;
                case Key.OemMinus or Key.Subtract: ZoomBy(1.0 / 1.2); break;
                case Key.D0 or Key.F: ZoomToFit(); break;
                case Key.Left: _offsetX += step; RaiseViewChanged(); break;
                case Key.Right: _offsetX -= step; RaiseViewChanged(); break;
                case Key.Up: _offsetY += step; RaiseViewChanged(); break;
                case Key.Down: _offsetY -= step; RaiseViewChanged(); break;
                case Key.Escape:
                    if (_hasSelection) ClearSelection();
                    else if (_path.Count > 0) ClearPath();
                    else if (_comparisonMode != RasterComparisonMode.None) StopComparison();
                    else return;
                    break;
                case Key.Back when _pathTool != PathTool.None: RemoveLastPathVertex(); break;
                case Key.Enter when _pathTool != PathTool.None: FinishPath(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void RaiseReadout(Point screen)
        {
            if (PointerReadout == null) return;
            var layer = ActiveLayer;
            if (layer == null)
            {
                PointerReadout(this, new RasterReadoutEventArgs());
                return;
            }

            double cellX = (screen.X - _offsetX) / _scale;
            double cellY = (screen.Y - _offsetY) / _scale;
            var (wx, wy) = layer.Document.GeoReference.PixelToWorld(cellX, cellY);

            int col = (int)Math.Floor(cellX);
            int row = (int)Math.Floor(cellY);
            bool inside = col >= 0 && row >= 0 && col < layer.DatasetWidth && row < layer.DatasetHeight;

            float? value = null;
            if (inside)
            {
                if (layer.Raster != null)
                {
                    value = layer.Raster.GetValueOrNull(row, col);
                }
                else if (layer.Source != null)
                {
                    try
                    {
                        Raster single = layer.Source.ReadWindow(col, row, 1, 1, band: layer.ActiveBand);
                        if (single.Width == 1 && single.Height == 1) value = single.GetValueOrNull(0, 0);
                    }
                    catch (IOException) { /* best-effort hover sampling */ }
                }
            }

            PointerReadout(this, new RasterReadoutEventArgs
            {
                InsideRaster = inside,
                WorldX = wx,
                WorldY = wy,
                Column = inside ? col : -1,
                Row = inside ? row : -1,
                Value = value,
            });
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>The interactive path tools of <see cref="RasterView"/>.</summary>
    public enum PathTool
    {
        /// <summary>No path tool.</summary>
        None,

        /// <summary>A multi-point profile line.</summary>
        Profile,

        /// <summary>Distance (and, once finished, area) measurement.</summary>
        Measure,

        /// <summary>A polygon zone for zonal statistics.</summary>
        Zone,
    }

    /// <summary>How a raster layer is composited onto what is below it.</summary>
    public enum LayerBlendMode
    {
        Normal,
        Multiply,
        Screen,
        Overlay,
        Darken,
        Lighten,
        SoftLight,
    }

    /// <summary>How two raster layers are compared visually.</summary>
    public enum RasterComparisonMode
    {
        /// <summary>Normal layer-stack rendering.</summary>
        None,

        /// <summary>First layer left of a draggable divider, second layer right of it.</summary>
        Swipe,

        /// <summary>Alternates between the two layers at a fixed interval.</summary>
        Blink,
    }

    /// <summary>How <see cref="RasterView"/> draws cells when magnified.</summary>
    public enum DisplayResampling
    {
        /// <summary>Crisp cells (nearest neighbour).</summary>
        Nearest,

        /// <summary>Bilinear blending between cells.</summary>
        Bilinear,

        /// <summary>A smooth bicubic Bézier-patch surface of the visible window (display only).</summary>
        Bezier,
    }

    /// <summary>Display-range strategies offered by <see cref="RasterView.AutoRange"/>.</summary>
    public enum RangeMode
    {
        MinMax,
        TwoSigma,
        Percentile,
    }
}
