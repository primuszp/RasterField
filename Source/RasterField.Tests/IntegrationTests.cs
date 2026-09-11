using System.IO;
using RasterField;
using RasterField.ErMapper;
using RasterField.Rendering;
using Xunit;

namespace RasterField.Tests
{
    /// <summary>
    /// End-to-end checks against the <c>P_00_01.ers</c> / <c>P_00_01.dat</c> pair in the
    /// repository root. Skipped automatically when the data file is not present.
    /// </summary>
    public class IntegrationTests
    {
        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "P_00_01.ers")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        [Fact]
        public void Loads_the_sample_dataset_samples_and_renders()
        {
            string? root = FindRepoRoot();
            if (root == null || !File.Exists(Path.Combine(root, "P_00_01.dat")))
                return; // sample data file not present in this checkout

            var doc = ErsDocument.Load(Path.Combine(root, "P_00_01.ers"));

            Assert.Single(doc.Bands);
            Assert.Equal(640, doc.Band!.Width);
            Assert.Equal(450, doc.Band!.Height);
            Assert.True(doc.Band!.Statistics.ValidCount > 0);

            var centreValue = doc.Sample(360000 + 320 * 1000, 430000 - 225 * 1000);
            Assert.NotNull(centreValue);

            var colorizer = RasterColorizer.Percentile(doc.Band!, BuiltInPalettes.Elevation);
            var image = RasterImageRenderer.Render(doc.Band!, colorizer);
            Assert.Equal(640, image.Width);
            Assert.Equal(450, image.Height);
            Assert.Equal(640 * 450 * 4, image.Pixels.Length);
        }

        [Fact]
        public void Sample_dataset_round_trips_through_save()
        {
            string? root = FindRepoRoot();
            if (root == null || !File.Exists(Path.Combine(root, "P_00_01.dat")))
                return;

            var src = ErsDocument.Load(Path.Combine(root, "P_00_01.ers"));

            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_rt_" + System.Guid.NewGuid().ToString("N")));
            try
            {
                string ers = Path.Combine(dir.FullName, "copy.ers");
                src.Save(ers, new ErsSaveOptions { ByteOrder = ErsByteOrder.MsbFirst }); // also flips byte order

                var back = ErsDocument.Load(ers);
                Assert.Equal(src.Band!.Width, back.Band!.Width);
                Assert.Equal(src.Band!.Height, back.Band!.Height);

                for (int r = 0; r < back.Band!.Height; r += 37)
                    for (int c = 0; c < back.Band!.Width; c += 41)
                        Assert.Equal(src.Band![r, c], back.Band![r, c], 2);
            }
            finally { dir.Delete(recursive: true); }
        }
    }
}
