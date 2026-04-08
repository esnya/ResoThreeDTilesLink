using System.Diagnostics.Metrics;

namespace ThreeDTilesLink.Core.Models
{
    internal sealed class RunPerformanceSummary : IDisposable
    {
        private const string MeterName = "ThreeDTilesLink.Streaming";
        private const string FetchDurationInstrument = "threedtileslink.fetch.duration";
        private const string ExtractDurationInstrument = "threedtileslink.extract.duration";
        private const string PlacementDurationInstrument = "threedtileslink.placement.duration";
        private const string SendDurationInstrument = "threedtileslink.send.duration";
        private const string RemoveDurationInstrument = "threedtileslink.remove.duration";
        private const string MetadataSyncDurationInstrument = "threedtileslink.metadata.sync.duration";
        private const string MetadataLicenseDurationInstrument = "threedtileslink.metadata.license.duration";
        private const string MetadataProgressDurationInstrument = "threedtileslink.metadata.progress.duration";

        private readonly Meter _meter = new(MeterName);
        private readonly Histogram<double> _fetchDurationMs;
        private readonly Histogram<double> _extractDurationMs;
        private readonly Histogram<double> _placementDurationMs;
        private readonly Histogram<double> _sendDurationMs;
        private readonly Histogram<double> _removeDurationMs;
        private readonly Histogram<double> _metadataSyncDurationMs;
        private readonly Histogram<double> _metadataLicenseDurationMs;
        private readonly Histogram<double> _metadataProgressDurationMs;
        private readonly MeterListener _listener;

        private long _fetchMilliseconds;
        private long _extractMilliseconds;
        private long _placementMilliseconds;
        private long _sendMilliseconds;
        private long _removeMilliseconds;
        private long _metadataSyncMilliseconds;
        private long _metadataLicenseMilliseconds;
        private long _metadataProgressMilliseconds;
        private long _metadataSyncCount;
        private long _metadataLicenseCount;
        private long _metadataProgressCount;
        private long _metadataSyncMaxMilliseconds;
        private long _metadataLicenseMaxMilliseconds;
        private long _metadataProgressMaxMilliseconds;
        private bool _disposed;

        public RunPerformanceSummary()
        {
            _fetchDurationMs = _meter.CreateHistogram<double>(FetchDurationInstrument, unit: "ms", description: "Tileset and tile fetch duration");
            _extractDurationMs = _meter.CreateHistogram<double>(ExtractDurationInstrument, unit: "ms", description: "Tile content extraction duration");
            _placementDurationMs = _meter.CreateHistogram<double>(PlacementDurationInstrument, unit: "ms", description: "Mesh placement duration");
            _sendDurationMs = _meter.CreateHistogram<double>(SendDurationInstrument, unit: "ms", description: "Mesh send duration");
            _removeDurationMs = _meter.CreateHistogram<double>(RemoveDurationInstrument, unit: "ms", description: "Slot removal duration");
            _metadataSyncDurationMs = _meter.CreateHistogram<double>(MetadataSyncDurationInstrument, unit: "ms", description: "Session metadata sync duration");
            _metadataLicenseDurationMs = _meter.CreateHistogram<double>(MetadataLicenseDurationInstrument, unit: "ms", description: "Session license credit update duration");
            _metadataProgressDurationMs = _meter.CreateHistogram<double>(MetadataProgressDurationInstrument, unit: "ms", description: "Session progress update duration");

            _listener = new MeterListener();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, _meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);
            _listener.Start();
        }

        public long FetchMilliseconds => Interlocked.Read(ref _fetchMilliseconds);

        public long ExtractMilliseconds => Interlocked.Read(ref _extractMilliseconds);

        public long PlacementMilliseconds => Interlocked.Read(ref _placementMilliseconds);

        public long SendMilliseconds => Interlocked.Read(ref _sendMilliseconds);

        public long RemoveMilliseconds => Interlocked.Read(ref _removeMilliseconds);

        public long MetadataSyncMilliseconds => Interlocked.Read(ref _metadataSyncMilliseconds);

        public long MetadataLicenseMilliseconds => Interlocked.Read(ref _metadataLicenseMilliseconds);

        public long MetadataProgressMilliseconds => Interlocked.Read(ref _metadataProgressMilliseconds);

        public long MetadataSyncCount => Interlocked.Read(ref _metadataSyncCount);

        public long MetadataLicenseCount => Interlocked.Read(ref _metadataLicenseCount);

        public long MetadataProgressCount => Interlocked.Read(ref _metadataProgressCount);

        public long MetadataSyncMaxMilliseconds => Interlocked.Read(ref _metadataSyncMaxMilliseconds);

        public long MetadataLicenseMaxMilliseconds => Interlocked.Read(ref _metadataLicenseMaxMilliseconds);

        public long MetadataProgressMaxMilliseconds => Interlocked.Read(ref _metadataProgressMaxMilliseconds);

        public void AddFetch(TimeSpan elapsed) => _fetchDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddExtract(TimeSpan elapsed) => _extractDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddPlacement(TimeSpan elapsed) => _placementDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddSend(TimeSpan elapsed) => _sendDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddRemove(TimeSpan elapsed) => _removeDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddMetadataSync(TimeSpan elapsed) => _metadataSyncDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddMetadataLicense(TimeSpan elapsed) => _metadataLicenseDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddMetadataProgress(TimeSpan elapsed) => _metadataProgressDurationMs.Record(elapsed.TotalMilliseconds);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _listener.Dispose();
            _meter.Dispose();
        }

        private void OnMeasurementRecorded(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            if (!ReferenceEquals(instrument.Meter, _meter))
            {
                return;
            }

            long elapsedMilliseconds = (long)measurement;
            switch (instrument.Name)
            {
                case FetchDurationInstrument:
                    _ = Interlocked.Add(ref _fetchMilliseconds, elapsedMilliseconds);
                    break;
                case ExtractDurationInstrument:
                    _ = Interlocked.Add(ref _extractMilliseconds, elapsedMilliseconds);
                    break;
                case PlacementDurationInstrument:
                    _ = Interlocked.Add(ref _placementMilliseconds, elapsedMilliseconds);
                    break;
                case SendDurationInstrument:
                    _ = Interlocked.Add(ref _sendMilliseconds, elapsedMilliseconds);
                    break;
                case RemoveDurationInstrument:
                    _ = Interlocked.Add(ref _removeMilliseconds, elapsedMilliseconds);
                    break;
                case MetadataSyncDurationInstrument:
                    _ = Interlocked.Add(ref _metadataSyncMilliseconds, elapsedMilliseconds);
                    _ = Interlocked.Increment(ref _metadataSyncCount);
                    UpdateMax(ref _metadataSyncMaxMilliseconds, elapsedMilliseconds);
                    break;
                case MetadataLicenseDurationInstrument:
                    _ = Interlocked.Add(ref _metadataLicenseMilliseconds, elapsedMilliseconds);
                    _ = Interlocked.Increment(ref _metadataLicenseCount);
                    UpdateMax(ref _metadataLicenseMaxMilliseconds, elapsedMilliseconds);
                    break;
                case MetadataProgressDurationInstrument:
                    _ = Interlocked.Add(ref _metadataProgressMilliseconds, elapsedMilliseconds);
                    _ = Interlocked.Increment(ref _metadataProgressCount);
                    UpdateMax(ref _metadataProgressMaxMilliseconds, elapsedMilliseconds);
                    break;
            }
        }

        private static void UpdateMax(ref long target, long value)
        {
            while (true)
            {
                long current = Interlocked.Read(ref target);
                if (value <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
