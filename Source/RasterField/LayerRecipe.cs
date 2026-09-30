using System;
using System.Collections.Generic;
using System.Globalization;

namespace RasterField
{
    /// <summary>
    /// How a derived layer was made, precisely enough to redo it: an operation key, the source
    /// layer (by reference) and invariant-culture parameters. Used to recompute a layer with new
    /// parameters and to store unsaved derived layers in a project.
    /// </summary>
    public sealed class LayerRecipe
    {
        public LayerRecipe(string operation, object source, IDictionary<string, string>? parameters = null)
        {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Parameters = parameters != null ? new Dictionary<string, string>(parameters) : new Dictionary<string, string>();
        }

        /// <summary>Operation key: bezier, contours, streams, slope, aspect, hillshade, curvature, flowdir, flowacc, swissrelief, filter, bandmath.</summary>
        public string Operation { get; }

        /// <summary>The source layer (a <see cref="RasterLayer"/>).</summary>
        public object Source { get; }

        /// <summary>Parameters as invariant strings.</summary>
        public Dictionary<string, string> Parameters { get; }

        public double Get(string key, double fallback) =>
            Parameters.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;

        public int Get(string key, int fallback) =>
            Parameters.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;

        public bool Get(string key, bool fallback) =>
            Parameters.TryGetValue(key, out var v) && bool.TryParse(v, out bool b) ? b : fallback;

        public string Get(string key, string fallback) => Parameters.TryGetValue(key, out var v) ? v : fallback;

        public static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
