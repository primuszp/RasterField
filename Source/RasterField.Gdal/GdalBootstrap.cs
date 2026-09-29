using System;
using MaxRev.Gdal.Core;

namespace RasterField.Gdal
{
    /// <summary>Initialises the bundled GDAL and PROJ runtime once per process.</summary>
    public static class GdalBootstrap
    {
        private static readonly object Gate = new object();
        private static bool _configured;

        public static void EnsureConfigured()
        {
            if (_configured) return;
            lock (Gate)
            {
                if (_configured) return;
                GdalBase.ConfigureAll();
                _configured = true;
            }
        }
    }
}
