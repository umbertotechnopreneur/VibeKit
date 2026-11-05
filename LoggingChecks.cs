#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Serilog.Events;
using Serilog.Parsing;

namespace WaterRipple;

internal static class LoggingChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WaterRipple-logging-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Check(WeeklyLogSink.FileNameFor(new DateTimeOffset(2021, 1, 1, 12, 0, 0, TimeSpan.Zero)) == "WaterRipple-2020-W53.log", "ISO previous-year week");
            Check(WeeklyLogSink.FileNameFor(new DateTimeOffset(2019, 12, 30, 12, 0, 0, TimeSpan.FromHours(7))) == "WaterRipple-2020-W01.log", "ISO next-year week");
            Check(WeeklyLogSink.FileNameFor(new DateTimeOffset(2024, 12, 30, 12, 0, 0, TimeSpan.Zero)) == "WaterRipple-2025-W01.log", "ISO second next-year boundary");
            CheckRetention(Path.Combine(root, "retention"));
            CheckQueue(Path.Combine(root, "queue"));
            CheckFiles(Path.Combine(root, "files"));
            Console.WriteLine("PASS weekly ISO rotation, file flush and scoped retention");
            Console.WriteLine("PASS bounded logging queue and nonblocking overflow");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL logging checks: " + ex.Message);
            return 1;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("WaterRipple-logging-checks-", StringComparison.Ordinal) && Directory.Exists(resolved))
            {
                try { Directory.Delete(resolved, recursive: true); }
                catch (Exception ex) { Debug.WriteLine("Logging check cleanup: " + ex.Message); }
            }
        }
    }

    private static void CheckRetention(string directory)
    {
        Directory.CreateDirectory(directory);
        for (int week = 1; week <= 6; week++) File.WriteAllText(Path.Combine(directory, $"WaterRipple-2026-W{week:D2}.log"), "fixture");
        File.WriteAllText(Path.Combine(directory, "WaterRipple-2026-W99.log"), "unrelated");
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "unrelated");
        WeeklyLogSink.PruneWeeklyFiles(directory, "WaterRipple-2026-W06.log");
        Check(!File.Exists(Path.Combine(directory, "WaterRipple-2026-W01.log")) &&
              !File.Exists(Path.Combine(directory, "WaterRipple-2026-W02.log")), "oldest weeks removed");
        for (int week = 3; week <= 6; week++) Check(File.Exists(Path.Combine(directory, $"WaterRipple-2026-W{week:D2}.log")), "retained week");
        Check(File.ReadAllText(Path.Combine(directory, "notes.txt")) == "unrelated", "unrelated file preserved");
        Check(File.Exists(Path.Combine(directory, "WaterRipple-2026-W99.log")), "invalid week name preserved");
    }

    private static void CheckQueue(string directory)
    {
        Directory.CreateDirectory(directory);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var sink = new WeeklyLogSink(directory, _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("test consumer gate");
        });
        try
        {
            var entry = Entry(DateTimeOffset.Now);
            sink.Emit(entry);
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "logging worker started");
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < WeeklyLogSink.QueueCapacity + 8; i++) sink.Emit(entry);
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "queue overflow never waits for consumer");
            Check(sink.DroppedEvents == 8, "queue holds exactly the configured event capacity");
        }
        finally { release.Set(); sink.Dispose(); }
        Check(sink.WorkerStopped, "worker drains and stops on disposal");
    }

    private static void CheckFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        using var sink = new WeeklyLogSink(directory);
        sink.Emit(Entry(new DateTimeOffset(2019, 12, 30, 12, 0, 0, TimeSpan.Zero)));
        sink.Emit(Entry(new DateTimeOffset(2020, 1, 6, 12, 0, 0, TimeSpan.Zero)));
        sink.Dispose();
        Check(sink.WorkerStopped, "file worker flushes on disposal");
        Check(Directory.EnumerateFiles(directory, "*.log").Count() == 2, "two event weeks produce two files");
        Check(File.ReadAllText(Path.Combine(directory, "WaterRipple-2020-W01.log")).Contains("Logging check", StringComparison.Ordinal), "first week's file is flushed");
        Check(File.ReadAllText(Path.Combine(directory, "WaterRipple-2020-W02.log")).Contains("Logging check", StringComparison.Ordinal), "second week's file is flushed");
    }

    private static LogEvent Entry(DateTimeOffset timestamp) => new(timestamp, LogEventLevel.Information, null,
        new MessageTemplateParser().Parse("Logging check"), Array.Empty<LogEventProperty>());

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
