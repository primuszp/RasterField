using System;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class ProfileChartTests
    {
        private static readonly ProfileSample[] Samples =
        {
            new ProfileSample(0, 100, 200, 1),
            new ProfileSample(10, 110, 200, 2),
            new ProfileSample(20, 120, 200, 3),
            new ProfileSample(35, 135, 200, 4),
        };

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(6, 1)]
        [InlineData(15, 1)]
        [InlineData(16, 2)]
        [InlineData(100, 3)]
        public void Hover_selects_the_nearest_profile_sample(double distance, int expectedIndex)
        {
            Assert.Equal(expectedIndex, ProfileChartControl.NearestSampleIndex(Samples, distance));
        }

        [Fact]
        public void Hover_handles_an_empty_profile()
        {
            Assert.Equal(-1, ProfileChartControl.NearestSampleIndex(Array.Empty<ProfileSample>(), 10));
        }
    }
}
