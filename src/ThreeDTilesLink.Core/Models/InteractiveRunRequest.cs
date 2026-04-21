namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveRunRequest(
        string ResoniteHost,
        int ResonitePort,
        double HeightOffset,
        TraversalOptions Traversal,
        TileSourceOptions TileSource,
        SearchOptions Search,
        bool RemoveOutOfRange,
        WatchOptions Watch);
}
