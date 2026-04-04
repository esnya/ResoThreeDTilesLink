using System.Globalization;

namespace ThreeDTilesLink.Core.CommandLine
{
    public enum CommandOptionValueKind
    {
        Text,
        WholeNumber,
        DecimalNumber,
        Switch
    }

    public enum CommandParseStatus
    {
        Success,
        Help,
        Error
    }

    public sealed record CommandOptionDefinition(
        string Name,
        CommandOptionValueKind ValueKind,
        string Description,
        bool Required = false,
        object? DefaultValue = null,
        string? Unit = null,
        string ValueName = "value",
        IReadOnlyList<string>? RenamedFrom = null);

    public sealed record CommandSpecification(
        string Usage,
        string Summary,
        IReadOnlyList<CommandOptionDefinition> Options,
        IReadOnlyDictionary<string, string>? UnsupportedOptions = null);

    public sealed record ParsedCommand(
        CommandParseStatus Status,
        IReadOnlyDictionary<string, object?> Values,
        string Output,
        bool WriteToError);

    public sealed record CommandInvocation<TOptions>(
        bool ShouldRun,
        TOptions? Options,
        int ExitCode,
        string Output,
        bool WriteToError);

    public static class CommandLineParser
    {
        public static ParsedCommand Parse(CommandSpecification specification, IReadOnlyList<string> args)
        {
            ArgumentNullException.ThrowIfNull(specification);
            ArgumentNullException.ThrowIfNull(args);

            string helpText = RenderHelp(specification);
            var definitions = specification.Options.ToDictionary(option => option.Name, StringComparer.OrdinalIgnoreCase);
            var renamed = specification.Options
                .SelectMany(option => (option.RenamedFrom ?? []).Select(oldName => (OldName: oldName, option.Name)))
                .ToDictionary(entry => entry.OldName, entry => entry.Name, StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, string> unsupported = specification.UnsupportedOptions ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < args.Count; i++)
            {
                string current = args[i];
                if (string.Equals(current, "--help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current, "-h", StringComparison.OrdinalIgnoreCase))
                {
                    return new ParsedCommand(CommandParseStatus.Help, values, helpText, false);
                }

                if (!current.StartsWith("--", StringComparison.Ordinal))
                {
                    return Error($"Unexpected positional argument: {current}", helpText);
                }

                string key = current;
                string? rawValue = null;
                int equalsIndex = current.IndexOf('=', StringComparison.Ordinal);
                if (equalsIndex > 2)
                {
                    key = current[..equalsIndex];
                    rawValue = current[(equalsIndex + 1)..];
                }

                if (renamed.TryGetValue(key, out string? renamedTo))
                {
                    return Error($"{key} was renamed to {renamedTo}.", helpText);
                }

                if (unsupported.TryGetValue(key, out string? unsupportedMessage))
                {
                    return Error(unsupportedMessage, helpText);
                }

                if (!definitions.TryGetValue(key, out CommandOptionDefinition? definition))
                {
                    return Error($"Unknown argument: {key}", helpText);
                }

                if (definition.ValueKind == CommandOptionValueKind.Switch)
                {
                    if (rawValue is not null)
                    {
                        return Error($"{key} does not take a value.", helpText);
                    }

                    values[definition.Name] = true;
                    continue;
                }

                if (rawValue is null)
                {
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Error($"Missing value for {key}.", helpText);
                    }

                    rawValue = args[++i];
                }

                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    return Error($"Missing value for {key}.", helpText);
                }

                if (!TryConvertValue(definition, rawValue, out object? convertedValue, out string? conversionError))
                {
                    return Error(conversionError ?? $"Invalid value for {key}: {rawValue}", helpText);
                }

                values[definition.Name] = convertedValue;
            }

            foreach (CommandOptionDefinition definition in specification.Options)
            {
                if (values.ContainsKey(definition.Name))
                {
                    continue;
                }

                if (definition.Required)
                {
                    return Error($"Missing required argument: {definition.Name}", helpText);
                }

                values[definition.Name] = definition.DefaultValue ?? GetImplicitDefault(definition.ValueKind);
            }

            return new ParsedCommand(CommandParseStatus.Success, values, string.Empty, false);
        }

        public static string RenderHelp(CommandSpecification specification)
        {
            ArgumentNullException.ThrowIfNull(specification);

            const int labelWidth = 30;
            var lines = new List<string>
            {
                "Usage:",
                $"  {specification.Usage}",
                string.Empty,
                specification.Summary,
                string.Empty,
                "Options:",
                $"  {"--help",-labelWidth}Show this help."
            };

            foreach (CommandOptionDefinition option in specification.Options)
            {
                string label = option.ValueKind == CommandOptionValueKind.Switch
                    ? option.Name
                    : $"{option.Name} <{option.ValueName}>";
                string details = BuildOptionDetails(option);
                lines.Add($"  {label,-labelWidth}{details}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static bool TryConvertValue(
            CommandOptionDefinition definition,
            string rawValue,
            out object? convertedValue,
            out string? error)
        {
            switch (definition.ValueKind)
            {
                case CommandOptionValueKind.Text:
                    convertedValue = rawValue;
                    error = null;
                    return true;

                case CommandOptionValueKind.WholeNumber:
                    if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integerValue) ||
                        int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.CurrentCulture, out integerValue))
                    {
                        convertedValue = integerValue;
                        error = null;
                        return true;
                    }

                    convertedValue = null;
                    error = $"Invalid integer value for {definition.Name}: {rawValue}";
                    return false;

                case CommandOptionValueKind.DecimalNumber:
                    if (double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue) ||
                        double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out doubleValue))
                    {
                        convertedValue = doubleValue;
                        error = null;
                        return true;
                    }

                    convertedValue = null;
                    error = $"Invalid numeric value for {definition.Name}: {rawValue}";
                    return false;

                default:
                    convertedValue = null;
                    error = $"Unsupported option type for {definition.Name}.";
                    return false;
            }
        }

        private static ParsedCommand Error(string message, string helpText)
        {
            string output = $"{message}{Environment.NewLine}{Environment.NewLine}{helpText}";
            return new ParsedCommand(CommandParseStatus.Error, new Dictionary<string, object?>(), output, true);
        }

        private static string BuildOptionDetails(CommandOptionDefinition option)
        {
            var parts = new List<string>();
            if (option.Required)
            {
                parts.Add("Required.");
            }
            else if (option.DefaultValue is not null)
            {
                parts.Add($"Default: {FormatValue(option.DefaultValue)}.");
            }

            if (!string.IsNullOrWhiteSpace(option.Unit))
            {
                parts.Add($"Unit: {option.Unit}.");
            }

            parts.Add(option.Description);
            return string.Join(" ", parts);
        }

        private static object GetImplicitDefault(CommandOptionValueKind valueKind)
        {
            return valueKind switch
            {
                CommandOptionValueKind.Switch => false,
                _ => string.Empty
            };
        }

        private static string FormatValue(object value)
        {
            return value switch
            {
                double doubleValue => doubleValue.ToString("0.###", CultureInfo.InvariantCulture),
                float floatValue => floatValue.ToString("0.###", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
        }
    }
}
