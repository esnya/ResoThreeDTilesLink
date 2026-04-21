using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IContentProcessor
    {
        Task<ContentProcessResult> ProcessAsync(
            FetchedNodeContent content,
            CancellationToken cancellationToken);
    }
}
