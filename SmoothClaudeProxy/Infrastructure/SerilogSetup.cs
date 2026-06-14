using Serilog;

namespace SmoothClaudeProxy.Infrastructure;

/// <summary>
/// Builds the Serilog logger: console + a rolling main log, plus two filtered side logs —
/// an LLM/route log (7-day rolling) and a non-rolling tools log that keeps tool warnings.
/// </summary>
public static class SerilogSetup
{
    public static Serilog.ILogger Build(string logPath, string llmLogPath, string toolsLogPath) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Async(a => a.Console())
            .WriteTo.Async(a => a.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7))
            .WriteTo.Logger(lc => lc
                .MinimumLevel.Information()
                .Filter.ByIncludingOnly(le =>
                    le.MessageTemplate?.Text?.Contains("LLM", StringComparison.OrdinalIgnoreCase) == true ||
                    le.MessageTemplate?.Text?.Contains("/api/v1/chat", StringComparison.OrdinalIgnoreCase) == true ||
                    le.Properties.ContainsKey("Route") && le.Properties["Route"]?.ToString() == "\"OpenCode\"")
                .WriteTo.Async(a => a.File(
                    llmLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)))
            .WriteTo.Logger(lc => lc
                .MinimumLevel.Information()
                .Filter.ByIncludingOnly(le =>
                    le.MessageTemplate?.Text?.Contains("tool", StringComparison.OrdinalIgnoreCase) == true &&
                    (le.Level >= Serilog.Events.LogEventLevel.Warning ||
                     le.MessageTemplate?.Text?.Contains("unsupported", StringComparison.OrdinalIgnoreCase) == true ||
                     le.MessageTemplate?.Text?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ||
                     le.MessageTemplate?.Text?.Contains("Failed", StringComparison.OrdinalIgnoreCase) == true))
                .WriteTo.File(
                    toolsLogPath,
                    rollingInterval: RollingInterval.Infinite, // No rolling
                    retainedFileCountLimit: null)) // Keep all
            .CreateLogger();
}
