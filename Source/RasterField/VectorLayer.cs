using System;
using Avalonia.Media;
using RasterField.Vectors;

namespace RasterField
{
    /// <summary>
    /// One loaded <c>.erv</c> vector dataset within a <see cref="RasterView"/>'s layer stack.
    /// Vector layers always draw above every raster layer (a vector overlay — contours, parcels,
    /// annotations, … — sitting on top of a raster basemap is by far the overwhelmingly common
    /// case), but keep their own order and visibility among themselves. A vector layer's
    /// coordinates are drawn as-is in the active raster layer's world space (the same convention
    /// the <c>.erv</c>/<c>.ers</c> formats already share — see <c>CoordinateSpace</c>), so at
    /// least one raster layer must be loaded to give the view a coordinate frame to place it in.
    /// </summary>
    public sealed class VectorLayer
    {
        internal VectorLayer(ErvDocument document, string name)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Name = name;
            ObjectBounds = BuildObjectBounds(document);
        }

        /// <summary>
        /// Each object's world-space bounding box (min/max X/Y), in the same order as
        /// <see cref="ErvDocument.Objects"/> — computed once at load time so the renderer can cull
        /// off-screen objects on every repaint without re-walking a polygon's (possibly large)
        /// point list every single frame just to find out it's nowhere near the viewport.
        /// </summary>
        internal (double MinX, double MinY, double MaxX, double MaxY)[] ObjectBounds { get; }

        private static (double, double, double, double)[] BuildObjectBounds(ErvDocument document)
        {
            var objects = document.Objects;
            var bounds = new (double, double, double, double)[objects.Count];
            for (int i = 0; i < objects.Count; i++)
                bounds[i] = ComputeBounds(objects[i]);
            return bounds;
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) ComputeBounds(VectorObject obj)
        {
            switch (obj)
            {
                case VectorPoint p: return (p.X, p.Y, p.X, p.Y);
                case VectorRectangleObject r:
                    return (Math.Min(r.Ltx, r.Rbx), Math.Min(r.Lty, r.Rby), Math.Max(r.Ltx, r.Rbx), Math.Max(r.Lty, r.Rby));
                case VectorPolyObject poly:
                {
                    var pts = poly.Points;
                    if (pts.Count == 0) return (0, 0, 0, 0);
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var (x, y) = pts[i];
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                    return (minX, minY, maxX, maxY);
                }
                case VectorTextObject t: return (t.X, t.Y, t.X, t.Y);
                default: return (0, 0, 0, 0);
            }
        }

        /// <summary>The vector dataset this layer shows.</summary>
        public ErvDocument Document { get; }

        /// <summary>Display name in the layer list (defaults to the file name).</summary>
        public string Name { get; set; }

        /// <summary>Whether this layer is drawn at all.</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>
        /// The colour every object in this layer is drawn with — a per-layer override rather than
        /// each object's own embedded R/G/B (simpler and more predictable than honouring each
        /// object's individual colour, and matches how most users think of "this layer's colour").
        /// </summary>
        public Color Color { get; set; } = Color.FromRgb(255, 105, 180);

        /// <summary>Line width (points) every object in this layer is drawn with.</summary>
        public double LineWidth { get; set; } = 2.0;
    }
}
