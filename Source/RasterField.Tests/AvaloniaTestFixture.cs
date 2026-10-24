using System;
using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace RasterField.Tests
{
    public class HeadlessTestApp
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    // Avalonia services are process-wide. Keep all consumers on one dispatcher and prevent
    // independent application setup from racing with layer rendering or PDF asset loading.
    [CollectionDefinition("Avalonia")]
    public class AvaloniaTestGroup : ICollectionFixture<AvaloniaTestFixture> { }

    public sealed class AvaloniaTestFixture : IDisposable
    {
        public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

        public void Dispose() => Session.Dispose();
    }
}
