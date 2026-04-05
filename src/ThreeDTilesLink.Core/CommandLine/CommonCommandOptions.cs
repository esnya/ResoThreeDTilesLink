namespace ThreeDTilesLink.Core.CommandLine
{
    public static class CommonCommandOptions
    {
        public static CommandOptionDefinition HeightOffset() =>
            new("--height-offset", CommandOptionValueKind.DecimalNumber, "Height offset applied to streamed geometry.", DefaultValue: 0d, Unit: "m", RenamedFrom: ["--height-offset-m"]);

        public static CommandOptionDefinition ResoniteHost() =>
            new("--resonite-host", CommandOptionValueKind.Text, "Resonite Link host name or IP address.", DefaultValue: "localhost", ValueName: "host", RenamedFrom: ["--link-host"]);

        public static CommandOptionDefinition ResonitePort(bool required = true) =>
            new("--resonite-port", CommandOptionValueKind.WholeNumber, "Resonite Link port.", Required: required, ValueName: "port", RenamedFrom: ["--link-port"]);

        public static CommandOptionDefinition TileLimit(string description) =>
            new("--tile-limit", CommandOptionValueKind.WholeNumber, description, DefaultValue: 2048, RenamedFrom: ["--max-tiles"]);

        public static CommandOptionDefinition DepthLimit(string description) =>
            new("--depth-limit", CommandOptionValueKind.WholeNumber, description, DefaultValue: 32, RenamedFrom: ["--max-depth"]);

        public static CommandOptionDefinition DetailTarget() =>
            new("--detail", CommandOptionValueKind.DecimalNumber, "Target tile detail before traversal stops descending renderable GLB tiles.", DefaultValue: 30d, Unit: "m", RenamedFrom: ["--detail-target-m"]);

        public static CommandOptionDefinition ContentWorkers(string description) =>
            new("--content-workers", CommandOptionValueKind.WholeNumber, description, DefaultValue: 8);

        public static CommandOptionDefinition Timeout() =>
            new("--timeout", CommandOptionValueKind.WholeNumber, "Request timeout.", DefaultValue: 120, Unit: "sec", RenamedFrom: ["--timeout-sec"]);

        public static CommandOptionDefinition DryRun() =>
            new("--dry-run", CommandOptionValueKind.Switch, "Fetch and convert tiles without sending anything to Resonite.", DefaultValue: false);

        public static CommandOptionDefinition LogLevelOption() =>
            new("--log-level", CommandOptionValueKind.Text, "Logging level.", DefaultValue: Microsoft.Extensions.Logging.LogLevel.Information.ToString(), ValueName: "level");
    }
}
