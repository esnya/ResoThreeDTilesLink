namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed record TraversalPlanningState(
        PlanningTree Tree,
        HashSet<string> CandidateStableIds);

    internal sealed record WriterBacklogState(
        int PendingDiscovery,
        int PendingPrepared,
        int PendingSend,
        int PendingRemove,
        int InFlightSendCount,
        bool HasInFlightRemove,
        bool MetadataInFlight,
        int CandidateTiles,
        int ProcessedTiles)
    {
        public int PendingUnits =>
            PendingDiscovery +
            PendingPrepared +
            PendingSend +
            PendingRemove +
            InFlightSendCount +
            (HasInFlightRemove ? 1 : 0) +
            (MetadataInFlight ? 1 : 0);

        public int CandidateBacklog => System.Math.Max(0, CandidateTiles - ProcessedTiles);

        public int TotalUnits => ProcessedTiles + PendingUnits + CandidateBacklog;

        public bool IsQuiescent => PendingSend == 0 && PendingRemove == 0 && InFlightSendCount == 0;
    }

    internal sealed record DesiredMetadataState(
        string LicenseCredit,
        float ProgressValue,
        string ProgressText,
        bool UpdateLicense,
        bool UpdateProgressText,
        bool ProgressValueChanged,
        bool IsCompleted,
        bool CompletionStateChanged,
        bool CadenceElapsed,
        bool ProcessedDeltaReached,
        bool ProgressDeltaReached,
        bool IsQuiescent)
    {
        public bool HasChanges => UpdateLicense || UpdateProgressText || ProgressValueChanged;

        public bool ShouldSync =>
            HasChanges &&
            (UpdateLicense ||
             CompletionStateChanged ||
             IsCompleted ||
             IsQuiescent ||
             (CadenceElapsed && (ProcessedDeltaReached || ProgressDeltaReached)));
    }

    internal sealed record WriterReductionState(
        WriterState PlanningState,
        bool HasPendingRemovals,
        int SendConcurrencyLimit);
}
