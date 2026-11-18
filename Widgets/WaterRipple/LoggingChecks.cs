#if DEBUG
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Tools.Widgets;

namespace WaterRipple;

internal static class LoggingChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WaterRipple-logging-checks-" + Guid.NewGuid().ToString("N"));
        string logs = Path.Combine(root, "logs");
        try
        {
            using (var logger = VibeWareDiagnostics.CreateLogger(LoggingService.AppId, out _, logs))
            {
                logger.Information("Logging check queued");
                logger.Warning(new InvalidOperationException("private exception detail"), "Startup failed");
                logger.Information("Private value {Credential}", "sk-example-private");
            }
            var files = Directory.GetFiles(logs, "VibeWare-*.log");
            Check(files.Length == 1, "daily writer flushes to one file");
            string current = File.ReadAllText(files[0]);
            Check(current.Contains("[INF] Logging check queued", StringComparison.Ordinal), "information level and message format");
            Check(current.Contains("InvalidOperationException", StringComparison.Ordinal) &&
                !current.Contains("private exception detail", StringComparison.Ordinal), "exception type without private exception text");
            Check(!current.Contains("sk-example-private", StringComparison.Ordinal), "token redacted");

            var now = DateTimeOffset.UtcNow;
            var historical = now.AddDays(-8);
            string old = historical.ToString(VibeWareDiagnostics.TimestampFormat, CultureInfo.InvariantCulture) + " [INF] obsolete";
            File.AppendAllText(files[0], old + Environment.NewLine);
            File.WriteAllText(Path.Combine(logs, "WaterRipple-2026-W25.log"), "legacy diagnostics");
            File.WriteAllText(Path.Combine(logs, "settings.json"), "private configuration");
            var archive = VibeWareDiagnostics.CreateSupportArchive(LoggingService.AppId, logs, Path.Combine(root, "exports"), now);
            using var zip = ZipFile.OpenRead(archive);
            var names = zip.Entries.Select(entry => entry.FullName).ToArray();
            Check(names.Length == 2 && names.Contains("manifest.json") &&
                names.Contains("logs/" + Path.GetFileName(files[0])), "archive includes only the common log and manifest");
            using var reader = new StreamReader(zip.GetEntry("logs/" + Path.GetFileName(files[0]))!.Open());
            string exported = reader.ReadToEnd();
            Check(exported.Contains("Logging check queued", StringComparison.Ordinal) &&
                !exported.Contains("obsolete", StringComparison.Ordinal), "archive uses exactly the last seven days");
            Console.WriteLine("PASS bounded diagnostics flush, redaction and seven-day ZIP");
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

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
