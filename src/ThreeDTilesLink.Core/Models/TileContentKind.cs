namespace ThreeDTilesLink.Core.Models
{
    internal enum TileContentKind
    {
        Json = 0,
        Glb = 1,
        B3dm = 2,
        Other = 3
    }

    internal static class TileContentKindExtensions
    {
        public static bool IsRenderable(this TileContentKind kind) =>
            kind is TileContentKind.Glb or TileContentKind.B3dm;
    }
}
