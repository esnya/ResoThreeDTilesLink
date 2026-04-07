using GeographicLib;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Geo
{
    internal sealed class SeaLevelGeoReferenceResolver : IGeoReferenceResolver, IDisposable
    {
        private const string BundledGeoidName = "egm96-5";
        private readonly Geoid _geoid;

        public SeaLevelGeoReferenceResolver(string? geoidName = null, string? geoidPath = null)
        {
            string resolvedName = string.IsNullOrWhiteSpace(geoidName)
                ? BundledGeoidName
                : geoidName;
            string resolvedPath = string.IsNullOrWhiteSpace(geoidPath)
                ? GetBundledGeoidPath()
                : geoidPath;

            try
            {
                _geoid = new Geoid(resolvedName, resolvedPath, cubic: true, threadsafe: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load bundled GeographicLib geoid data '{resolvedName}' from '{resolvedPath}'.",
                    ex);
            }
        }

        public GeoReference Resolve(double latitude, double longitude, double heightOffset)
        {
            double seaLevelEllipsoidHeight = _geoid.Evaluate(latitude, longitude);
            return new GeoReference(latitude, longitude, seaLevelEllipsoidHeight + heightOffset);
        }

        public void Dispose()
        {
            _geoid.Dispose();
        }

        private static string GetBundledGeoidPath()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "GeographicLib", "geoids");
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Bundled geoid directory was not found: {path}");
            }

            return path;
        }
    }
}
