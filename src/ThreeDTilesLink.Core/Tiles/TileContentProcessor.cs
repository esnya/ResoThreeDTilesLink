using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    public sealed class TileContentProcessor(
        ITilesSource tilesSource,
        IGlbMeshExtractor glbMeshExtractor) : IContentProcessor
    {
        private readonly ITilesSource _tilesSource = tilesSource;
        private readonly IGlbMeshExtractor _glbMeshExtractor = glbMeshExtractor;

        public async Task<ContentProcessResult> ProcessAsync(
            TileSelectionResult tile,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tile);
            ArgumentNullException.ThrowIfNull(auth);

            FetchedNodeContent content = await _tilesSource
                .FetchNodeContentAsync(tile.ContentUri, auth, cancellationToken)
                .ConfigureAwait(false);

            return content switch
            {
                NestedTilesetFetchedContent nested => new NestedTilesetContentProcessResult(nested.Tileset),
                GlbFetchedContent glb => ToRenderableResult(glb.GlbBytes),
                UnsupportedFetchedContent unsupported => new SkippedContentProcessResult(unsupported.Reason),
                _ => throw new InvalidOperationException($"Unsupported fetched content type: {content.GetType().Name}")
            };
        }

        private ContentProcessResult ToRenderableResult(byte[] glbBytes)
        {
            GlbExtractResult extracted = _glbMeshExtractor.Extract(glbBytes);
            return new RenderableContentProcessResult(extracted.Meshes, extracted.AssetCopyright);
        }
    }
}
