using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace WaterRipple;

internal static class LoggingService
{
    private static readonly object Sync = new();
    private static WeeklyLogSink? sink;
    private static long previousDropped;

    internal static string DirectoryPath { get; private set; } = "";
    internal static string? InitializationError { get; private set; }
    // Rejected queue writes, not a count of events suppressed by the weekly file cap.
    internal static long DroppedEvents => Interlocked.Read(ref previousDropped) +
        (Volatile.Read(ref sink)?.DroppedEvents ?? 0);

    internal static void Initialize(string? root = null)
    {
        lock (Sync)
        {
            if (sink is not null) return;
            InitializationError = null;
            DirectoryPath = Path.GetFullPath(root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeWare", "WaterRipple", "logs"));
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                // Fail early on an unwritable destination without retaining a probe file.
                using (new FileStream(Path.Combine(DirectoryPath, ".write-check-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }

                var candidate = new WeeklyLogSink(DirectoryPath);
                try
                {
                    Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                        .WriteTo.Sink(candidate).CreateLogger();
                    sink = candidate;
                }
                catch
                {
                    candidate.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                InitializationError = ex.Message;
                Log.Logger = new LoggerConfiguration().CreateLogger();
                Debug.WriteLine("Water Ripple logging is unavailable: " + ex.Message);
            }
        }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            var current = sink;
            sink = null;
            var logger = Log.Logger;
            Log.Logger = new LoggerConfiguration().CreateLogger();
            try { (logger as IDisposable)?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("Water Ripple logging shutdown: " + ex.Message); }
            if (current is not null) Interlocked.Add(ref previousDropped, current.DroppedEvents);
        }
    }
}

// One bounded queue, one owner of the file logger, and no disk work in Emit.
internal sealed class WeeklyLogSink : ILogEventSink, IDisposable
{
    internal const int QueueCapacity = 256;
    internal const long WeeklyFileLimitBytes = 8L * 1024 * 1024;
    internal const int RetainedWeeks = 4;
    private static readonly Regex WeeklyName = new(
        @"^WaterRipple-(?<year>\d{4})-W(?<week>\d{2})\.log$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string directory;
    private readonly Channel<LogEvent> queue = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        // TryWrite returns false when full; producers never wait for disk I/O.
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly Thread worker;
    private readonly Action<LogEvent>? consumeOverride;
    private long droppedEvents;
    private int disposed;

    internal WeeklyLogSink(string directory, Action<LogEvent>? consumeOverride = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.consumeOverride = consumeOverride;
        worker = new Thread(Run) { IsBackground = true, Name = "Water Ripple weekly logging" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    internal long DroppedEvents => Interlocked.Read(ref droppedEvents);
    internal bool WorkerStopped => !worker.IsAlive;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null) return;
        if (!queue.Writer.TryWrite(logEvent)) Interlocked.Increment(ref droppedEvents);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        // A blocked filesystem cannot hold up the application shutdown indefinitely.
        if (Thread.CurrentThread != worker && !worker.Join(TimeSpan.FromSeconds(2)))
            Debug.WriteLine("Water Ripple log flush exceeded the two-second shutdown budget.");
    }

    internal static string FileNameFor(DateTimeOffset timestamp)
    {
        // Use the calendar/offset recorded on the event, normally the machine's local time.
        DateTime date = timestamp.Date;
        return $"WaterRipple-{ISOWeek.GetYear(date):D4}-W{ISOWeek.GetWeekOfYear(date):D2}.log";
    }

    internal static void PruneWeeklyFiles(string root, string currentFileName)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var files = new List<(string Path, string Name, int Key)>();
        foreach (string path in Directory.EnumerateFiles(normalizedRoot, "WaterRipple-*-W*.log", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            Match match = WeeklyName.Match(name);
            if (!match.Success ||
                !int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
                !int.TryParse(match.Groups["week"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int week) ||
                year is < 1 or > 9999 || week < 1 || week > ISOWeek.GetWeeksInYear(year)) continue;
            string resolved = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(resolved), normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add((resolved, name, year * 100 + week));
        }

        // Keep the active week even after a clock change or a late event from an older week.
        var retained = files.Where(f => string.Equals(f.Name, currentFileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OrderByDescending(f => f.Key))
        {
            if (retained.Count < RetainedWeeks) retained.Add(file.Name);
            else if (!retained.Contains(file.Name))
            {
                try { File.Delete(file.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Debug.WriteLine("Water Ripple could not remove an expired weekly log: " + ex.Message); }
            }
        }
    }

    private void Run()
    {
        Logger? fileLogger = null;
        string? activeFile = null;
        try
        {
            while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (queue.Reader.TryRead(out var entry))
                {
                    try
                    {
                        if (consumeOverride is not null) { consumeOverride(entry); continue; }
                        string name = FileNameFor(entry.Timestamp);
                        if (!string.Equals(name, activeFile, StringComparison.Ordinal))
                        {
                            fileLogger?.Dispose();
                            fileLogger = null;
                            activeFile = null;
                            fileLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.File(
                                Path.Combine(directory, name),
                                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                                rollingInterval: RollingInterval.Infinite,
                                fileSizeLimitBytes: WeeklyFileLimitBytes,
                                rollOnFileSizeLimit: false,
                                retainedFileCountLimit: null,
                                buffered: true,
                                flushToDiskInterval: TimeSpan.FromSeconds(2)).CreateLogger();
                            activeFile = name;
                            PruneWeeklyFiles(directory, name);
                        }
                        fileLogger!.Write(entry);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref droppedEvents);
                        Debug.WriteLine("Water Ripple weekly log write failed: " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("Water Ripple logging worker stopped: " + ex.Message); }
        finally
        {
            try { fileLogger?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("Water Ripple log flush failed: " + ex.Message); }
        }
    }
}
