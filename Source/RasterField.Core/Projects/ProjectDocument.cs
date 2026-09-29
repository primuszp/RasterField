using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RasterField.Projects
{
    /// <summary>
    /// A RasterField project (<c>.rfproj</c>, JSON): the layer stack with each layer's source file,
    /// display settings and — for a derived layer — the recipe it can be recomputed from, plus the
    /// view and bookmarks. Data is never embedded; file paths are stored relative to the project
    /// file so a project folder can be moved or shared as a whole.
    /// </summary>
    public sealed class ProjectDocument
    {
        /// <summary>The file-format version this code writes.</summary>
        public const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>File-format version.</summary>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>Every layer, in draw order (first = bottom).</summary>
        public List<ProjectLayer> Layers { get; set; } = new List<ProjectLayer>();

        /// <summary><see cref="ProjectLayer.Id"/> of the active raster layer.</summary>
        public string? ActiveLayerId { get; set; }

        /// <summary>The visible map area.</summary>
        public ProjectView? View { get; set; }

        /// <summary>Saved views.</summary>
        public List<ProjectBookmark> Bookmarks { get; set; } = new List<ProjectBookmark>();

        /// <summary>Serialises the project; every layer path is written relative to <paramref name="projectPath"/>'s folder when possible.</summary>
        public void Save(string projectPath)
        {
            if (projectPath == null) throw new ArgumentNullException(nameof(projectPath));
            string dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
            var copy = (ProjectDocument)MemberwiseClone();
            copy.Layers = new List<ProjectLayer>();
            foreach (var layer in Layers)
            {
                var l = layer.Clone();
                if (l.Path != null) l.Path = MakeRelative(dir, l.Path);
                copy.Layers.Add(l);
            }
            File.WriteAllText(projectPath, JsonSerializer.Serialize(copy, Options), new UTF8Encoding(false));
        }

        /// <summary>Loads a project and resolves every relative layer path against the project's folder.</summary>
        public static ProjectDocument Load(string projectPath)
        {
            if (projectPath == null) throw new ArgumentNullException(nameof(projectPath));
            var project = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(projectPath), Options)
                ?? throw new FormatException("The project file is empty.");
            if (project.Version > CurrentVersion)
                throw new FormatException($"This project was written by a newer RasterField (format version {project.Version}).");
            string dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
            project.Layers ??= new List<ProjectLayer>();
            project.Bookmarks ??= new List<ProjectBookmark>();
            foreach (var layer in project.Layers)
                if (layer.Path != null && !Path.IsPathRooted(layer.Path))
                    layer.Path = Path.GetFullPath(Path.Combine(dir, layer.Path));
            return project;
        }

        /// <summary>Converts to JSON (paths as they are).</summary>
        public string ToJson() => JsonSerializer.Serialize(this, Options);

        /// <summary>Parses JSON (paths as they are).</summary>
        public static ProjectDocument FromJson(string json) =>
            JsonSerializer.Deserialize<ProjectDocument>(json, Options) ?? throw new FormatException("The project JSON is empty.");

        private static string MakeRelative(string baseDir, string path)
        {
            string full = Path.GetFullPath(path);
            var baseUri = new Uri(AppendSeparator(baseDir));
            var target = new Uri(full);
            if (baseUri.Scheme != target.Scheme) return full;
            string rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(target).ToString()).Replace('/', System.IO.Path.DirectorySeparatorChar);
            // Different drive/root: MakeRelativeUri returns an absolute URI.
            return System.IO.Path.IsPathRooted(rel) || rel.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? full : rel;
        }

        private static string AppendSeparator(string dir) =>
            dir.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? dir : dir + System.IO.Path.DirectorySeparatorChar;
    }

    /// <summary>One layer of a <see cref="ProjectDocument"/>.</summary>
    public sealed class ProjectLayer
    {
        /// <summary>Stable identifier (recipes refer to their source layer by it).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary><c>raster</c> or <c>vector</c>.</summary>
        public string Kind { get; set; } = "raster";

        /// <summary>Display name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Source file (<c>.ers</c> / <c>.erv</c>); <see langword="null"/> for a derived layer that was never saved.</summary>
        public string? Path { get; set; }

        /// <summary>Visibility.</summary>
        public bool Visible { get; set; } = true;

        /// <summary>Opacity 0–1.</summary>
        public double Opacity { get; set; } = 1;

        /// <summary>Blend mode name (<c>Normal</c>, <c>Multiply</c>, <c>Screen</c>, …).</summary>
        public string? BlendMode { get; set; }

        /// <summary>Raster: palette name.</summary>
        public string? Palette { get; set; }

        /// <summary>Raster: palette reversed.</summary>
        public bool PaletteReversed { get; set; }

        /// <summary>Raster: stretch minimum.</summary>
        public double? Minimum { get; set; }

        /// <summary>Raster: stretch maximum.</summary>
        public double? Maximum { get; set; }

        /// <summary>Raster: gamma.</summary>
        public double? Gamma { get; set; }

        /// <summary>Raster: palette render mode name.</summary>
        public string? RenderMode { get; set; }

        /// <summary>Raster: class count for discrete mode.</summary>
        public int? ClassCount { get; set; }

        /// <summary>Raster: displayed band (0-based).</summary>
        public int? ActiveBand { get; set; }

        /// <summary>Raster: show bands 1–3 as true colour.</summary>
        public bool? RgbComposite { get; set; }

        /// <summary>Vector: colour as <c>#RRGGBB</c>.</summary>
        public string? Color { get; set; }

        /// <summary>Vector: line width.</summary>
        public double? LineWidth { get; set; }

        /// <summary>How a derived layer is recomputed from another layer.</summary>
        public ProjectRecipe? Recipe { get; set; }

        /// <summary>Human-readable description of how a derived layer was made.</summary>
        public string? Lineage { get; set; }

        internal ProjectLayer Clone()
        {
            var c = (ProjectLayer)MemberwiseClone();
            if (Recipe != null) c.Recipe = new ProjectRecipe { Operation = Recipe.Operation, SourceLayerId = Recipe.SourceLayerId, Parameters = new Dictionary<string, string>(Recipe.Parameters) };
            return c;
        }
    }

    /// <summary>A derived layer's recipe: an operation applied to a source layer with named parameters.</summary>
    public sealed class ProjectRecipe
    {
        /// <summary>Operation key (<c>bezier</c>, <c>contours</c>, <c>streams</c>, <c>slope</c>, …).</summary>
        public string Operation { get; set; } = "";

        /// <summary><see cref="ProjectLayer.Id"/> of the source layer.</summary>
        public string? SourceLayerId { get; set; }

        /// <summary>Operation parameters, invariant-culture strings.</summary>
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>The visible map area: its centre and scale.</summary>
    public sealed class ProjectView
    {
        /// <summary>World X of the view centre.</summary>
        public double CenterX { get; set; }

        /// <summary>World Y of the view centre.</summary>
        public double CenterY { get; set; }

        /// <summary>World units per screen pixel.</summary>
        public double WorldPerPixel { get; set; }
    }

    /// <summary>A named saved view.</summary>
    public sealed class ProjectBookmark
    {
        /// <summary>Bookmark name.</summary>
        public string Name { get; set; } = "";

        /// <summary>The saved view.</summary>
        public ProjectView View { get; set; } = new ProjectView();
    }
}
