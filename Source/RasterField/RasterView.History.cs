using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using RasterField.Rendering;

namespace RasterField
{
    public sealed partial class RasterView
    {
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
                var previousView = GetViewState();
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
                if (previousView is { } pv && active != null)
                    SetViewState(pv.X, pv.Y, pv.WorldPerPixel);
                else { _needsFit = true; ZoomToFit(); }
                ClearSelection();
                RefreshStreamingWindow();
                ScheduleSmoothOverlay();
            }
            finally { _undoSuppress--; }

            _lastCoalesceKey = null;
            InvalidateVisual();
            RasterLoaded?.Invoke(this, EventArgs.Empty);
            LayersChanged?.Invoke(this, EventArgs.Empty);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

    }
}
