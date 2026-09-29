using System;
using Avalonia.Media.Imaging;
using RasterField.Rasters;
using RasterField.Rendering;

namespace RasterField
{
    /// <summary>
    /// One loaded dataset within a <see cref="RasterView"/>'s layer stack: its own document,
    /// streaming/loaded raster state, and its own independent display settings (palette, stretch,
    /// gamma, render mode, active band, RGB-composite toggle) — exactly the per-document state
    /// <see cref="RasterView"/> used to hold flatly, before it could show more than one dataset
    /// at once. Visibility and display order are the layer's own responsibility too; order is
    /// simply this layer's position in <see cref="RasterView.Layers"/> (later = drawn on top).
    /// </summary>
    public sealed class RasterLayer : IDisposable
    {
        internal RasterLayer(ErsDocument document, string name)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Name = name;
        }

        /// <summary>The dataset this layer shows (replaced in place when a derived layer is recomputed).</summary>
        public ErsDocument Document { get; internal set; }

        /// <summary>How this layer is composited onto the layers below it.</summary>
        public LayerBlendMode BlendMode { get; internal set; }

        /// <summary>How a derived layer can be recomputed (operation, source layer, parameters); null for a file layer.</summary>
        public LayerRecipe? Recipe { get; internal set; }

        /// <summary>
        /// An invisible stand-in that only provides a coordinate frame when the view holds vector
        /// layers but no raster; never listed in the layer panel, removed as soon as a real raster arrives.
        /// </summary>
        public bool IsFrame { get; internal set; }

        /// <summary>Display name in the layer list (defaults to the file name).</summary>
        public string Name { get; set; }

        /// <summary>Whether this layer is drawn at all. Hidden layers are skipped entirely, including in streaming refreshes.</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>Draw opacity in [0, 1] — lets a hillshade or a derived surface be blended over another layer.</summary>
        public double Opacity { get; internal set; } = 1.0;

        /// <summary>
        /// <see langword="true"/> for a derived layer (Bézier subdivision, terrain product, …) that so
        /// far exists only in memory; cleared once it has been saved to disk.
        /// </summary>
        public bool IsUnsaved { get; internal set; }

        /// <summary>How a derived layer was made ("Bézier ×4 of DTM (τ 1.00)"), or <see langword="null"/> for a layer opened from a file.</summary>
        public string? Lineage { get; internal set; }

        /// <summary>Bumped on every re-colourisation, so cached renderings (the Bézier display overlay) know they're stale.</summary>
        internal int RenderVersion { get; set; }

        /// <summary>Bézier display-smoothing overlay of the visible window (active layer only), in this layer's cell space.</summary>
        internal WriteableBitmap? SmoothBitmap { get; set; }
        internal double SmoothOriginX { get; set; }
        internal double SmoothOriginY { get; set; }
        internal double SmoothStep { get; set; } = 1.0;
        internal string? SmoothKey { get; set; }

        /// <summary>Fully loaded active-band raster (small/medium dataset only).</summary>
        public Raster? Raster { get; internal set; }

        /// <summary>Random-access reader (large dataset only).</summary>
        public RasterSource? Source { get; internal set; }

        /// <summary>Last streamed window for the active band (or the Red band, in RGB composite mode).</summary>
        public Raster? WindowRaster { get; internal set; }

        /// <summary>RGB composite streaming mode only: the Green and Blue bands of the same window.</summary>
        public Raster? WindowRasterG { get; internal set; }
        public Raster? WindowRasterB { get; internal set; }

        /// <summary>This layer's own colourisation — independent of every other layer's.</summary>
        public RasterColorizer? Colorizer { get; internal set; }

        /// <summary>Which band is displayed (0-based), when not showing an RGB composite.</summary>
        public int ActiveBand { get; internal set; }

        /// <summary>Whether this layer shows bands 1-3 as true colour instead of one band through its palette.</summary>
        public bool ShowRgbComposite { get; internal set; }

        /// <summary>The rendered bitmap currently on screen for this layer.</summary>
        public WriteableBitmap? Bitmap { get; internal set; }

        /// <summary>Maps this layer's bitmap's own pixel (0,0) to its cell (pixel) space: cell = origin + bitmapPixel * step.</summary>
        public double BitmapOriginX { get; internal set; }
        public double BitmapOriginY { get; internal set; }
        public double BitmapStep { get; internal set; } = 1.0;

        /// <summary><see langword="true"/> when this layer is shown via windowed reads rather than fully loaded.</summary>
        public bool IsStreaming => Source != null;

        /// <summary>Number of bands in this layer's dataset.</summary>
        public int BandCount => Document.Header.RasterInfo.NrOfBands;

        /// <summary><see langword="true"/> when this layer has (at least) the three bands a true-colour composite needs.</summary>
        public bool CanShowRgbComposite => BandCount >= 3;

        /// <summary>The fully loaded band if there is one, else the most recently fetched streaming window.</summary>
        public Raster? ActiveRaster => Raster ?? WindowRaster;

        /// <summary>Statistics of whatever is currently backing this layer's display (an estimate, in streaming mode).</summary>
        public RasterStatistics? CurrentStatistics => ActiveRaster?.Statistics;

        /// <summary>Cell columns in this layer's dataset.</summary>
        public int DatasetWidth => Document.Header.RasterInfo.NrOfCellsPerLine;

        /// <summary>Cell rows in this layer's dataset.</summary>
        public int DatasetHeight => Document.Header.RasterInfo.NrOfLines;

        /// <summary>The three bands backing this layer's RGB composite, or <see langword="null"/> when unavailable.</summary>
        internal (Raster R, Raster G, Raster B)? RgbBands
        {
            get
            {
                if (!ShowRgbComposite) return null;
                if (Source != null)
                    return WindowRaster != null && WindowRasterG != null && WindowRasterB != null
                        ? (WindowRaster, WindowRasterG, WindowRasterB) : ((Raster, Raster, Raster)?)null;
                return Document.Bands.Count >= 3 ? (Document.Bands[0], Document.Bands[1], Document.Bands[2]) : ((Raster, Raster, Raster)?)null;
            }
        }

        /// <summary><see langword="true"/> unless the dataset is already display-ready 0-255 (e.g. 8-bit true-colour).</summary>
        internal bool RgbNeedsAutoStretch => Document.Header.RasterInfo.CellType != ErMapper.ErsCellType.Unsigned8BitInteger;

        internal void DisposeSource()
        {
            Source?.Dispose();
            Source = null;
            WindowRaster = null;
            WindowRasterG = null;
            WindowRasterB = null;
        }

        /// <summary>Releases the streaming reader and rendered bitmap. Safe to call more than once.</summary>
        public void Dispose()
        {
            DisposeSource();
            Bitmap?.Dispose();
            Bitmap = null;
            DisposeSmooth();
        }

        internal void DisposeSmooth()
        {
            SmoothBitmap?.Dispose();
            SmoothBitmap = null;
            SmoothKey = null;
        }
    }
}
