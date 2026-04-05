namespace ThreeDTilesLink.Core.App
{
    internal sealed record InteractiveWatchPath(
        string BasePath,
        string LatitudePath,
        string LongitudePath,
        string RangePath,
        string SearchPath)
    {
        internal static InteractiveWatchPath Parse(string input)
        {
            string normalizedBasePath = NormalizeBasePath(input);
            return new InteractiveWatchPath(
                normalizedBasePath,
                $"{normalizedBasePath}.Latitude",
                $"{normalizedBasePath}.Longitude",
                $"{normalizedBasePath}.Range",
                $"{normalizedBasePath}.Search");
        }

        private static string NormalizeBasePath(string input)
        {
            string trimmed = input.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidOperationException("Watch path cannot be empty.");
            }

            const string worldPrefix = "World/";
            if (!trimmed.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Watch path must start with 'World/'.");
            }

            string tail = trimmed[worldPrefix.Length..].Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(tail))
            {
                throw new InvalidOperationException("Watch path must contain a name after 'World/'.");
            }

            tail = tail.Replace('/', '.');
            string[] segments = tail.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                throw new InvalidOperationException("Watch path must contain at least one valid segment.");
            }

            var normalizedSegments = new List<string>(segments.Length);
            foreach (string segment in segments)
            {
                var chars = new List<char>(segment.Length);
                foreach (char ch in segment)
                {
                    if (char.IsLetterOrDigit(ch))
                    {
                        chars.Add(ch);
                    }
                }

                string cleaned = new(chars.ToArray());
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    cleaned = "ThreeWatch";
                }

                if (!char.IsLetter(cleaned[0]))
                {
                    cleaned = $"Three{cleaned}";
                }

                normalizedSegments.Add(cleaned);
            }

            return $"{worldPrefix}{string.Join('.', normalizedSegments)}";
        }
    }
}
