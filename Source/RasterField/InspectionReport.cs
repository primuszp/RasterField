using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using RasterField.Rasters;

namespace RasterField
{
    /// <summary>Immutable data captured on the UI thread before an inspection PDF is written.</summary>
    internal sealed class InspectionReportData
    {
        public string Title { get; init; } = "RasterField";
        public DateTimeOffset CreatedAt { get; init; }
        public string LayerName { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public string Dimensions { get; init; } = "";
        public string Band { get; init; } = "";
        public string CellType { get; init; } = "";
        public string CellSize { get; init; } = "";
        public string CoordinateReferenceSystem { get; init; } = "";
        public string NoDataValue { get; init; } = "";
        public string? Lineage { get; init; }
        public bool StatisticsAreApproximate { get; init; }
        public long ValidCount { get; init; }
        public long NoDataCount { get; init; }
        public double Minimum { get; init; }
        public double Maximum { get; init; }
        public double Mean { get; init; }
        public double StandardDeviation { get; init; }
        public double DisplayMinimum { get; init; }
        public double DisplayMaximum { get; init; }
        public string ValueUnit { get; init; } = "";
        public string DistanceUnit { get; init; } = "";
        public byte[] MapPng { get; init; } = Array.Empty<byte>();
        public IReadOnlyList<ReportColor> Palette { get; init; } = Array.Empty<ReportColor>();
        public IReadOnlyList<ReportProfileSeries> ProfileSeries { get; init; } = Array.Empty<ReportProfileSeries>();
    }

    internal readonly record struct ReportColor(byte Red, byte Green, byte Blue);

    internal sealed record ReportProfileSeries(
        string Name,
        IReadOnlyList<ProfileSample> Samples,
        ReportColor Color,
        bool Dashed);

    /// <summary>Creates a compact, hand-off-ready A4 inspection report with embedded fonts.</summary>
    internal static class InspectionReportWriter
    {
        private const string FontFamily = "RasterField Inter";
        private static readonly object FontGate = new object();
        private static readonly char[] WordSeparators = { ' ', '\r', '\n' };
        private static bool _fontResolverConfigured;

        /// <summary>
        /// Loads Inter from Avalonia's existing embedded font assembly. Call this on the UI thread
        /// before dispatching PDF creation to a worker.
        /// </summary>
        public static void EnsureFontResolver()
        {
            lock (FontGate)
            {
                if (_fontResolverConfigured) return;
                byte[] regular = ReadAsset("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf");
                byte[] semibold = ReadAsset("avares://Avalonia.Fonts.Inter/Assets/Inter-SemiBold.ttf");
                GlobalFontSettings.FontResolver = new InterFontResolver(regular, semibold);
                _fontResolverConfigured = true;
            }
        }

        public static void Write(string path, InspectionReportData data)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            ArgumentNullException.ThrowIfNull(data);
            EnsureFontResolver();

            using var document = new PdfDocument();
            document.Info.Title = data.Title;
            document.Info.Author = "RasterField";
            document.Info.Subject = L.T("Raster inspection report");
            document.Info.CreationDate = data.CreatedAt.LocalDateTime;

            AddSummaryPage(document, data);
            if (data.ProfileSeries.Any(s => s.Samples.Any(p => p.Value.HasValue)))
                AddProfilePage(document, data);

            document.Save(path);
        }

        private static void AddSummaryPage(PdfDocument document, InspectionReportData data)
        {
            PdfPage page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using XGraphics gfx = XGraphics.FromPdfPage(page);
            var regular = new XFont(FontFamily, 9, XFontStyleEx.Regular);
            var small = new XFont(FontFamily, 7.5, XFontStyleEx.Regular);
            var heading = new XFont(FontFamily, 20, XFontStyleEx.Bold);
            var section = new XFont(FontFamily, 12, XFontStyleEx.Bold);
            var label = new XFont(FontFamily, 8.5, XFontStyleEx.Bold);
            double pageWidth = page.Width.Point;
            const double left = 42, right = 42;
            double contentWidth = pageWidth - left - right;
            double y = 38;

            gfx.DrawString(Ellipsize(gfx, data.Title, heading, contentWidth - 155), heading,
                Brush(28, 50, 66), new XPoint(left, y + 18));
            gfx.DrawString(data.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.CurrentCulture),
                small, XBrushes.Gray, new XRect(left, y + 4, contentWidth, 14), XStringFormats.TopRight);
            y += 34;
            gfx.DrawLine(new XPen(Color(20, 151, 147), 2), left, y, pageWidth - right, y);
            y += 18;

            gfx.DrawString(L.T("Dataset"), section, Brush(28, 50, 66), new XPoint(left, y));
            y += 10;
            y = DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Layer"), data.LayerName, L.T("Dimensions"), data.Dimensions);
            y = DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Band"), data.Band, L.T("Cell type"), data.CellType);
            y = DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Cell size"), data.CellSize, L.T("No-data"), data.NoDataValue);
            y = DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Coordinate system"), data.CoordinateReferenceSystem, "", "");
            y = DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Source"), data.SourcePath, "", "");

            if (!string.IsNullOrWhiteSpace(data.Lineage))
            {
                y += 3;
                gfx.DrawString(L.T("Analysis provenance"), label, Brush(28, 50, 66), new XPoint(left, y + 9));
                y = DrawWrappedText(gfx, data.Lineage!, regular, XBrushes.Black,
                    new XRect(left + 104, y, contentWidth - 104, 34), 11) + 3;
            }

            y += 12;
            gfx.DrawString(L.T("Map view"), section, Brush(28, 50, 66), new XPoint(left, y));
            y += 10;
            const double mapHeight = 278;
            gfx.DrawRectangle(new XPen(Color(199, 207, 214), 0.8), XBrushes.White,
                new XRect(left, y, contentWidth, mapHeight));
            if (data.MapPng.Length > 0)
            {
                using var mapStream = new MemoryStream(data.MapPng, writable: false);
                using XImage image = XImage.FromStream(mapStream);
                double scale = Math.Min(contentWidth / image.PixelWidth, mapHeight / image.PixelHeight);
                double w = image.PixelWidth * scale, h = image.PixelHeight * scale;
                gfx.DrawImage(image, left + (contentWidth - w) / 2, y + (mapHeight - h) / 2, w, h);
            }
            y += mapHeight + 9;

            if (data.Palette.Count > 0)
            {
                double swatchWidth = contentWidth / data.Palette.Count;
                for (int i = 0; i < data.Palette.Count; i++)
                {
                    ReportColor c = data.Palette[i];
                    gfx.DrawRectangle(Brush(c.Red, c.Green, c.Blue), left + i * swatchWidth, y, swatchWidth + 0.3, 10);
                }
                string unit = string.IsNullOrWhiteSpace(data.ValueUnit) ? "" : " " + data.ValueUnit;
                gfx.DrawString(data.DisplayMinimum.ToString("g6", CultureInfo.CurrentCulture) + unit, small, XBrushes.Gray,
                    new XPoint(left, y + 20));
                gfx.DrawString(data.DisplayMaximum.ToString("g6", CultureInfo.CurrentCulture) + unit, small, XBrushes.Gray,
                    new XRect(left, y + 12, contentWidth, 12), XStringFormats.TopRight);
                y += 30;
            }

            y += 8;
            gfx.DrawString(L.T("Statistics"), section, Brush(28, 50, 66), new XPoint(left, y));
            if (data.StatisticsAreApproximate)
                gfx.DrawString(L.T("Visible streamed window - approximate"), small, XBrushes.Gray,
                    new XRect(left, y - 2, contentWidth, 12), XStringFormats.TopRight);
            y += 11;
            string suffix = string.IsNullOrWhiteSpace(data.ValueUnit) ? "" : " " + data.ValueUnit;
            y = DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Minimum"), data.Minimum.ToString("g7", CultureInfo.CurrentCulture) + suffix,
                L.T("Maximum"), data.Maximum.ToString("g7", CultureInfo.CurrentCulture) + suffix);
            y = DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Mean"), data.Mean.ToString("g7", CultureInfo.CurrentCulture) + suffix,
                "σ", data.StandardDeviation.ToString("g7", CultureInfo.CurrentCulture) + suffix);
            DrawMetadataRow(gfx, label, regular, left, y, contentWidth,
                L.T("Valid cells"), data.ValidCount.ToString("N0", CultureInfo.CurrentCulture),
                L.T("No-data cells"), data.NoDataCount.ToString("N0", CultureInfo.CurrentCulture));

            DrawFooter(gfx, page, data.CreatedAt, 1);
        }

        private static void AddProfilePage(PdfDocument document, InspectionReportData data)
        {
            PdfPage page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using XGraphics gfx = XGraphics.FromPdfPage(page);
            var heading = new XFont(FontFamily, 18, XFontStyleEx.Bold);
            var regular = new XFont(FontFamily, 8, XFontStyleEx.Regular);
            var small = new XFont(FontFamily, 7, XFontStyleEx.Regular);
            const double left = 48, top = 52, right = 42, bottom = 120;
            double pageWidth = page.Width.Point, pageHeight = page.Height.Point;
            double plotX = left + 42, plotY = top + 32;
            double plotW = pageWidth - plotX - right;
            double plotH = pageHeight - plotY - bottom;

            gfx.DrawString(L.T("Profile"), heading, Brush(28, 50, 66), new XPoint(left, top));
            gfx.DrawString(data.LayerName, regular, XBrushes.Gray, new XPoint(left, top + 17));

            var usable = data.ProfileSeries
                .Where(s => s.Samples.Any(p => p.Value.HasValue))
                .Take(6)
                .ToList();
            var valid = usable.SelectMany(s => s.Samples).Where(p => p.Value.HasValue).ToList();
            if (usable.Count == 0 || valid.Count < 2) return;

            double minDistance = usable.Min(s => s.Samples.Count == 0 ? 0 : s.Samples[0].Distance);
            double maxDistance = usable.Max(s => s.Samples.Count == 0 ? 0 : s.Samples[^1].Distance);
            double minValue = valid.Min(p => p.Value!.Value), maxValue = valid.Max(p => p.Value!.Value);
            if (maxDistance - minDistance < 1e-12) maxDistance = minDistance + 1;
            if (maxValue - minValue < 1e-12) { minValue -= 0.5; maxValue += 0.5; }

            double Sx(double value) => plotX + (value - minDistance) / (maxDistance - minDistance) * plotW;
            double Sy(double value) => plotY + plotH - (value - minValue) / (maxValue - minValue) * plotH;
            var gridPen = new XPen(Color(222, 227, 231), 0.6);
            var axisPen = new XPen(Color(80, 90, 98), 0.8);

            for (int i = 0; i <= 5; i++)
            {
                double value = minValue + (maxValue - minValue) * i / 5.0;
                double yy = Sy(value);
                gfx.DrawLine(gridPen, plotX, yy, plotX + plotW, yy);
                gfx.DrawString(value.ToString("g5", CultureInfo.CurrentCulture), small, XBrushes.Gray,
                    new XRect(left, yy - 5, 36, 10), XStringFormats.CenterRight);
            }
            foreach (double distance in new[] { minDistance, (minDistance + maxDistance) / 2, maxDistance })
            {
                double xx = Sx(distance);
                gfx.DrawString(distance.ToString("g5", CultureInfo.CurrentCulture), small, XBrushes.Gray,
                    new XRect(xx - 28, plotY + plotH + 5, 56, 10), XStringFormats.TopCenter);
            }
            gfx.DrawLine(axisPen, plotX, plotY, plotX, plotY + plotH);
            gfx.DrawLine(axisPen, plotX, plotY + plotH, plotX + plotW, plotY + plotH);

            foreach (ReportProfileSeries series in usable)
            {
                var pen = new XPen(Color(series.Color.Red, series.Color.Green, series.Color.Blue), 1.4);
                if (series.Dashed) pen.DashStyle = XDashStyle.Dash;
                ProfileSample? previous = null;
                foreach (ProfileSample sample in series.Samples)
                {
                    if (!sample.Value.HasValue) { previous = null; continue; }
                    if (previous.HasValue)
                        gfx.DrawLine(pen, Sx(previous.Value.Distance), Sy(previous.Value.Value!.Value),
                            Sx(sample.Distance), Sy(sample.Value.Value));
                    previous = sample;
                }
            }

            string axis = L.F("distance ({0})", data.DistanceUnit);
            if (!string.IsNullOrWhiteSpace(data.ValueUnit)) axis += " · " + L.F("value ({0})", data.ValueUnit);
            gfx.DrawString(axis, regular, XBrushes.Gray,
                new XRect(plotX, plotY + plotH + 20, plotW, 12), XStringFormats.TopCenter);

            double legendX = plotX, legendY = plotY + plotH + 42;
            foreach (ReportProfileSeries series in usable)
            {
                var pen = new XPen(Color(series.Color.Red, series.Color.Green, series.Color.Blue), 2);
                gfx.DrawLine(pen, legendX, legendY + 3, legendX + 18, legendY + 3);
                gfx.DrawString(series.Name, small, XBrushes.Black, new XPoint(legendX + 23, legendY + 6));
                legendX += Math.Min(160, 36 + gfx.MeasureString(series.Name, small).Width);
                if (legendX > pageWidth - right - 100) { legendX = plotX; legendY += 13; }
            }

            DrawFooter(gfx, page, data.CreatedAt, 2);
        }

        private static double DrawMetadataRow(XGraphics gfx, XFont labelFont, XFont valueFont,
            double x, double y, double width, string label1, string value1, string label2, string value2)
        {
            const double rowHeight = 23;
            double half = width / 2;
            double labelWidth = string.IsNullOrEmpty(label2) ? 118 : 72;
            gfx.DrawRectangle(new XPen(Color(226, 230, 234), 0.5), new XRect(x, y, width, rowHeight));
            DrawCell(gfx, labelFont, valueFont, x + 7, y, labelWidth, half - 10, label1, value1);
            if (!string.IsNullOrEmpty(label2))
                DrawCell(gfx, labelFont, valueFont, x + half + 7, y, labelWidth, half - 10, label2, value2);
            return y + rowHeight;
        }

        private static void DrawCell(XGraphics gfx, XFont labelFont, XFont valueFont,
            double x, double y, double labelWidth, double cellWidth, string label, string value)
        {
            gfx.DrawString(label, labelFont, Brush(65, 82, 92), new XPoint(x, y + 15));
            string clipped = Ellipsize(gfx, value ?? "", valueFont, Math.Max(10, cellWidth - labelWidth));
            gfx.DrawString(clipped, valueFont, XBrushes.Black, new XPoint(x + labelWidth, y + 15));
        }

        private static string Ellipsize(XGraphics gfx, string text, XFont font, double maxWidth)
        {
            if (gfx.MeasureString(text, font).Width <= maxWidth) return text;
            const string ellipsis = "…";
            int low = 0, high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (gfx.MeasureString(string.Concat(text.AsSpan(0, mid), ellipsis), font).Width <= maxWidth) low = mid;
                else high = mid - 1;
            }
            return string.Concat(text.AsSpan(0, low), ellipsis);
        }

        private static double DrawWrappedText(XGraphics gfx, string text, XFont font, XBrush brush,
            XRect bounds, double lineHeight)
        {
            double y = bounds.Y;
            string line = "";
            foreach (string word in text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (gfx.MeasureString(candidate, font).Width <= bounds.Width)
                {
                    line = candidate;
                    continue;
                }
                if (line.Length > 0) gfx.DrawString(line, font, brush, new XPoint(bounds.X, y + lineHeight - 2));
                y += lineHeight;
                line = word;
                if (y + lineHeight > bounds.Bottom) break;
            }
            if (line.Length > 0 && y + lineHeight <= bounds.Bottom)
                gfx.DrawString(line, font, brush, new XPoint(bounds.X, y + lineHeight - 2));
            return Math.Min(bounds.Bottom, y + lineHeight);
        }

        private static void DrawFooter(XGraphics gfx, PdfPage page, DateTimeOffset createdAt, int pageNumber)
        {
            var footer = new XFont(FontFamily, 7, XFontStyleEx.Regular);
            double y = page.Height.Point - 28;
            gfx.DrawLine(new XPen(Color(220, 225, 229), 0.5), 42, y - 8, page.Width.Point - 42, y - 8);
            gfx.DrawString("RasterField · " + createdAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture),
                footer, XBrushes.Gray, new XPoint(42, y));
            gfx.DrawString(pageNumber.ToString(CultureInfo.CurrentCulture), footer, XBrushes.Gray,
                new XRect(42, y - 7, page.Width.Point - 84, 12), XStringFormats.TopRight);
        }

        private static XColor Color(byte red, byte green, byte blue) => XColor.FromArgb(red, green, blue);
        private static XSolidBrush Brush(byte red, byte green, byte blue) => new XSolidBrush(Color(red, green, blue));

        private static byte[] ReadAsset(string uri)
        {
            using Stream input = AssetLoader.Open(new Uri(uri));
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        private sealed class InterFontResolver : IFontResolver
        {
            private const string RegularFace = "RasterField.Inter.Regular";
            private const string SemiboldFace = "RasterField.Inter.Semibold";
            private readonly byte[] _regular;
            private readonly byte[] _semibold;

            public InterFontResolver(byte[] regular, byte[] semibold)
            {
                _regular = regular;
                _semibold = semibold;
            }

            public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
                new FontResolverInfo(bold ? SemiboldFace : RegularFace, mustSimulateBold: false, mustSimulateItalic: italic);

            public byte[]? GetFont(string faceName) => faceName switch
            {
                RegularFace => _regular,
                SemiboldFace => _semibold,
                _ => null,
            };
        }
    }
}
