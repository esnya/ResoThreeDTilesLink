namespace ThreeDTilesLink.Core.CommandLine
{
    internal static class CommonCommandOptions
    {
        internal static CommandOptionDefinition HeightOffset() =>
            new("--height-offset", CommandOptionValueKind.DecimalNumber, "Sea-level height offset applied to streamed geometry.", DefaultValue: 0d, Unit: "m", RenamedFrom: ["--height-offset-m"]);

        internal static CommandOptionDefinition EndpointHost() =>
            new(
                "--endpoint-host",
                CommandOptionValueKind.Text,
                "Target endpoint host name or IP address.",
                DefaultValue: "localhost",
                ValueName: "host");

        internal static CommandOptionDefinition EndpointPort(bool required = true) =>
            new(
                "--endpoint-port",
                CommandOptionValueKind.WholeNumber,
                "Target endpoint port (1-65535).",
                Required: required,
                ValueName: "port");

        internal static CommandOptionDefinition DetailTarget() =>
            new("--detail", CommandOptionValueKind.DecimalNumber, "Target tile detail before traversal stops descending renderable GLB tiles.", DefaultValue: 30d, Unit: "m", RenamedFrom: ["--detail-target-m"]);

        internal static CommandOptionDefinition ContentWorkers(string description) =>
            new("--content-workers", CommandOptionValueKind.WholeNumber, description, DefaultValue: 10);

        internal static CommandOptionDefinition ResoniteSendWorkers(string description) =>
            new("--resonite-send-workers", CommandOptionValueKind.WholeNumber, description, DefaultValue: 8);

        internal static CommandOptionDefinition MeasurePerformance() =>
            new("--measure-performance", CommandOptionValueKind.Switch, "Collect stage timing metrics and progress snapshots.", DefaultValue: false);

        internal static CommandOptionDefinition Timeout() =>
            new("--timeout", CommandOptionValueKind.WholeNumber, "Request timeout.", DefaultValue: 120, Unit: "sec", RenamedFrom: ["--timeout-sec"]);

        internal static CommandOptionDefinition LogLevelOption() =>
            new("--log-level", CommandOptionValueKind.Text, "Logging level.", DefaultValue: Microsoft.Extensions.Logging.LogLevel.Information.ToString(), ValueName: "level");

    }
}
