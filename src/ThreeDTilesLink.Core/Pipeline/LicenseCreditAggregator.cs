using ThreeDTilesLink.Core.Google;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class LicenseCreditAggregator
    {
        private readonly List<string> _attributionOrder = [];
        private readonly HashSet<string> _knownAttributions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _activeAttributionCounts = new(StringComparer.Ordinal);

        public void Reset()
        {
            _attributionOrder.Clear();
            _knownAttributions.Clear();
            _activeAttributionCounts.Clear();
        }

        public static IReadOnlyList<string> ParseOwners(IEnumerable<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var owners = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string[] segments = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string segment in segments)
                {
                    string? normalized = NormalizeAttributionOwner(segment);
                    if (normalized is null || !seen.Add(normalized))
                    {
                        continue;
                    }

                    owners.Add(normalized);
                }
            }

            return owners;
        }

        public void RegisterOrder(IEnumerable<string> owners)
        {
            ArgumentNullException.ThrowIfNull(owners);
            foreach (string normalized in ParseOwners(owners))
            {
                if (_knownAttributions.Add(normalized))
                {
                    _attributionOrder.Add(normalized);
                }
            }
        }

        public bool Activate(IEnumerable<string> owners)
        {
            ArgumentNullException.ThrowIfNull(owners);
            bool changed = false;
            foreach (string normalized in owners)
            {
                _activeAttributionCounts[normalized] = _activeAttributionCounts.TryGetValue(normalized, out int count) ? count + 1 : 1;
                changed = true;
            }

            return changed;
        }

        public bool Deactivate(IEnumerable<string> owners)
        {
            ArgumentNullException.ThrowIfNull(owners);
            bool changed = false;
            foreach (string normalized in owners)
            {
                if (!_activeAttributionCounts.TryGetValue(normalized, out int count))
                {
                    continue;
                }

                if (count <= 1)
                {
                    _ = _activeAttributionCounts.Remove(normalized);
                }
                else
                {
                    _activeAttributionCounts[normalized] = count - 1;
                }

                changed = true;
            }

            return changed;
        }

        public string BuildCreditString()
        {
            if (_activeAttributionCounts.Count == 0)
            {
                return GoogleMapsCompliance.BasemapAttribution;
            }

            var orderIndex = new Dictionary<string, int>(_attributionOrder.Count, StringComparer.Ordinal);
            for (int i = 0; i < _attributionOrder.Count; i++)
            {
                orderIndex[_attributionOrder[i]] = i;
            }

            IEnumerable<string> ordered = _attributionOrder
                .Where(value => _activeAttributionCounts.TryGetValue(value, out int count) && count > 0)
                .OrderByDescending(value => _activeAttributionCounts[value])
                .ThenBy(value => orderIndex[value]);

            var credits = new List<string> { GoogleMapsCompliance.BasemapAttribution };
            foreach (string attribution in ordered)
            {
                if (string.Equals(attribution, GoogleMapsCompliance.BasemapAttribution, StringComparison.Ordinal))
                {
                    continue;
                }

                credits.Add(attribution);
            }

            return string.Join("; ", credits);
        }

        private static string? NormalizeAttributionOwner(string? value)
        {
            return GoogleMapsCompliance.NormalizeAttributionOwner(value);
        }
    }
}
