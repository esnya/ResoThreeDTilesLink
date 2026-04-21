using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class TileContentProcessor(
        IGlbMeshExtractor glbMeshExtractor,
        RunPerformanceSummary? performanceSummary = null) : IContentProcessor
    {
        private readonly IGlbMeshExtractor _glbMeshExtractor = glbMeshExtractor;
        private readonly RunPerformanceSummary? _performanceSummary = performanceSummary;

        public async Task<ContentProcessResult> ProcessAsync(
            FetchedNodeContent content,
            CancellationToken cancellationToken)
        {
            return content switch
            {
                NestedTilesetFetchedContent nested => new NestedTilesetContentProcessResult(nested.Tileset),
                GlbFetchedContent glb => ToRenderableResult(glb.GlbBytes),
                UnsupportedFetchedContent unsupported => new SkippedContentProcessResult(unsupported.Reason),
                _ => throw new InvalidOperationException($"Unsupported fetched content type: {content.GetType().Name}")
            };
        }

        private RenderableContentProcessResult ToRenderableResult(byte[] glbBytes)
        {
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            DateTimeOffset startedAt = performanceSummary is null ? default : DateTimeOffset.UtcNow;
            GlbExtractResult extracted = _glbMeshExtractor.Extract(glbBytes);
            if (performanceSummary is not null)
            {
                performanceSummary.AddExtract(DateTimeOffset.UtcNow - startedAt);
            }
            return new RenderableContentProcessResult(extracted.Meshes, extracted.AssetCopyright);
        }
    }
}
