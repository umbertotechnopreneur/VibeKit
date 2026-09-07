using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace MailMeUp.Cli;

internal static class CliLogging
{
    internal static Logger Create(CliOptions options, string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            // SDK and transport events may contain request arguments, results or provider exception messages.
            // Emit only our deliberately bounded diagnostics, even at verbose level.
            .Filter.ByIncludingOnly(logEvent =>
                logEvent.Properties.TryGetValue("SourceContext", out var source) &&
                source is ScalarValue { Value: string name } &&
                name.StartsWith("MailMeUp.", StringComparison.Ordinal))
            .WriteTo.Console(
                restrictedToMinimumLevel: options.LogLevel,
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}",
                theme: options.Command == CliCommand.Stdio || options.NoColor ||
                    Console.IsErrorRedirected || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) ||
                    Environment.GetEnvironmentVariable("TERM") == "dumb"
                        ? ConsoleTheme.None
                        : AnsiConsoleTheme.Code)
            .WriteTo.File(
                Path.Combine(logDirectory, "mailmeup-.log"),
                restrictedToMinimumLevel: LogEventLevel.Debug,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}")
            .CreateLogger();
    }
}
