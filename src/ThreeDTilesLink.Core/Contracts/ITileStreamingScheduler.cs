using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface ITileStreamingScheduler
    {
        void Initialize(Tileset rootTileset, StreamerOptions options);
        bool TryDequeueWorkItem(out SchedulerWorkItem? workItem);
        void HandleResult(SchedulerWorkResult result);
        RunSummary GetSummary();
    }
}
