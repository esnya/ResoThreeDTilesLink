namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveRunRequest(
        string EndpointHost,
        int EndpointPort,
        double HeightOffset,
        TraversalOptions Traversal,
        TileSourceOptions TileSource,
        SearchOptions Search,
        bool RemoveOutOfRange,
        WatchOptions Watch);
}
