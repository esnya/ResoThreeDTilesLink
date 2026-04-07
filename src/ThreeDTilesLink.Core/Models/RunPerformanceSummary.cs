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

        private readonly Meter _meter = new(MeterName);
        private readonly Histogram<double> _fetchDurationMs;
        private readonly Histogram<double> _extractDurationMs;
        private readonly Histogram<double> _placementDurationMs;
        private readonly Histogram<double> _sendDurationMs;
        private readonly Histogram<double> _removeDurationMs;
        private readonly MeterListener _listener;

        private long _fetchMilliseconds;
        private long _extractMilliseconds;
        private long _placementMilliseconds;
        private long _sendMilliseconds;
        private long _removeMilliseconds;
        private bool _disposed;

        public RunPerformanceSummary()
        {
            _fetchDurationMs = _meter.CreateHistogram<double>(FetchDurationInstrument, unit: "ms");
            _extractDurationMs = _meter.CreateHistogram<double>(ExtractDurationInstrument, unit: "ms");
            _placementDurationMs = _meter.CreateHistogram<double>(PlacementDurationInstrument, unit: "ms");
            _sendDurationMs = _meter.CreateHistogram<double>(SendDurationInstrument, unit: "ms");
            _removeDurationMs = _meter.CreateHistogram<double>(RemoveDurationInstrument, unit: "ms");

            _listener = new MeterListener();
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName)
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

        public void AddFetch(TimeSpan elapsed) => _fetchDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddExtract(TimeSpan elapsed) => _extractDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddPlacement(TimeSpan elapsed) => _placementDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddSend(TimeSpan elapsed) => _sendDurationMs.Record(elapsed.TotalMilliseconds);

        public void AddRemove(TimeSpan elapsed) => _removeDurationMs.Record(elapsed.TotalMilliseconds);

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
            }
        }
    }
}
