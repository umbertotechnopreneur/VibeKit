using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace Tools.Widgets;

internal enum SupportEmailResult { Completed, ManualAttachment, NoEmailApp }

public static class VibeWareDiagnostics
{
    internal const string SupportAddress = "hello@umbertogiacobbi.biz";
    internal const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const int TimestampLength = 24;
    private const int DailyLimit = 2 * 1024 * 1024;
    private const int QueueCapacity = 256;
    private static readonly TimeSpan History = TimeSpan.FromDays(7);
    private static readonly Regex DailyName = new(@"^VibeWare-[0-9]{8}\.log$", RegexOptions.CultureInvariant);
    private static readonly Regex PrivateText = new(
        "(?:[a-z][a-z0-9+.-]*://[^\\s<>]+|[a-z]:\\\\[^\\r\\n<>\"]*|\\\\\\\\[^\\r\\n<>\"]*|[\\w.+-]+@[\\w.-]+\\.[a-z]{2,}|\\b(?:bearer\\s+|sk-)[a-z0-9_.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string GetLogDirectory(string appId) => Path.Combine(GetAppDirectory(appId), "logs");

    public static Logger CreateLogger(string appId) => CreateLogger(appId, out _);

    internal static Logger CreateLogger(string appId, out Func<long> droppedEvents, string? logDirectory = null)
    {
        var directory = logDirectory ?? GetLogDirectory(appId);
        droppedEvents = static () => 0;
        try
        {
            Directory.CreateDirectory(directory);
            var fileLogger = new LoggerConfiguration().MinimumLevel.Verbose()
                .WriteTo.File(new SupportFormatter(), Path.Combine(directory, "VibeWare-.log"),
                    rollingInterval: RollingInterval.Day, fileSizeLimitBytes: DailyLimit,
                    rollOnFileSizeLimit: false, shared: true,
                    retainedFileCountLimit: 7, retainedFileTimeLimit: History)
                .CreateLogger();
            var sink = new BufferedSink(fileLogger);
            droppedEvents = () => sink.DroppedEvents;
            return new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("Diagnostic log directory is unavailable: " + error.GetType().Name);
            return new LoggerConfiguration().CreateLogger();
        }
    }

    internal static string CreateSupportArchive(string appId) => CreateSupportArchive(
        appId, GetLogDirectory(appId), Path.Combine(GetAppDirectory(appId), "diagnostics"), DateTimeOffset.UtcNow);

    internal static string CreateSupportArchive(string appId, string logDirectory, string outputDirectory, DateTimeOffset now)
    {
        ValidateAppId(appId);
        now = now.ToUniversalTime();
        var since = now - History;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            int entries = 0;
            if (Directory.Exists(logDirectory))
            {
                foreach (var file in new DirectoryInfo(logDirectory).EnumerateFiles("VibeWare-*.log"))
                {
                    try
                    {
                        if (!DailyName.IsMatch(file.Name) || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                            file.Length > DailyLimit + (16 * 1024) ||
                            !DateOnly.TryParseExact(file.Name.Substring(9, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var date) || date < DateOnly.FromDateTime(since.UtcDateTime.AddDays(-1)) ||
                            date > DateOnly.FromDateTime(now.UtcDateTime.AddDays(1))) continue;
                        if (CopyRecentLog(zip, file, since, now)) entries++;
                    }
                    catch (FileNotFoundException) { /* Log rotation can retire a file during export. */ }
                }
            }
            if (entries == 0) throw new InvalidOperationException("No diagnostic logs are available from the past seven days.");
            using var manifest = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(manifest, new { format = "vibeware-diagnostics-v1", app = appId, fromUtc = since, createdUtc = now });
        }
        Directory.CreateDirectory(outputDirectory);
        var stamp = now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var archivePath = Path.Combine(outputDirectory, $"VibeWare-{appId}-diagnostics-{stamp}.zip");
        var temporaryPath = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                buffer.Position = 0;
                buffer.CopyTo(output);
            }
            File.Move(temporaryPath, archivePath);
            return archivePath;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Debug.WriteLine("Could not discard an incomplete diagnostics export: " + error.GetType().Name); }
        }
    }

    private static bool CopyRecentLog(ZipArchive zip, FileInfo file, DateTimeOffset since, DateTimeOffset now)
    {
        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input);
        StreamWriter? writer = null;
        try
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length <= TimestampLength || !DateTimeOffset.TryParseExact(line.AsSpan(0, TimestampLength), TimestampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) || time < since || time > now) continue;
                writer ??= new StreamWriter(zip.CreateEntry("logs/" + file.Name, CompressionLevel.Fastest).Open());
                writer.WriteLine(PrivateText.Replace(line, "[redacted]"));
            }
            return writer is not null;
        }
        finally { writer?.Dispose(); }
    }

    internal static SupportEmailResult OpenEmailDraft(string displayName, string archivePath)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Create a diagnostics ZIP first.");
        string subject = $"{displayName} diagnostics";
        string body = "Please describe the problem, what you expected, and how to reproduce it. Review the diagnostic ZIP before sending.";
        // MAPI_DIALOG requires the mail client to show a compose dialog. The user controls delivery.
        if (OperatingSystem.IsWindows() && TryOpenAttachedDraft(subject, body, archivePath)) return SupportEmailResult.Completed;
        try
        {
            body += $"\r\n\r\nAttach this file: {Path.GetFileName(archivePath)}";
            var uri = $"mailto:{SupportAddress}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return SupportEmailResult.ManualAttachment;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return SupportEmailResult.NoEmailApp;
        }
    }

    internal static void RevealArchive(string archivePath)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{archivePath}\"") { UseShellExecute = true })?.Dispose();
        else
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(archivePath)!) { UseShellExecute = true })?.Dispose();
    }

    private static string GetAppDirectory(string appId)
    {
        ValidateAppId(appId);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeWare", appId);
    }

    private static void ValidateAppId(string appId)
    {
        if (appId.Length is < 1 or > 64) throw new ArgumentException("Invalid app identifier.", nameof(appId));
        foreach (char character in appId)
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-')
                throw new ArgumentException("Invalid app identifier.", nameof(appId));
    }

    internal sealed class SupportFormatter : ITextFormatter
    {
        public void Format(LogEvent entry, TextWriter writer)
        {
            var level = entry.Level switch
            {
                LogEventLevel.Verbose => "VRB", LogEventLevel.Debug => "DBG", LogEventLevel.Information => "INF",
                LogEventLevel.Warning => "WRN", LogEventLevel.Error => "ERR", _ => "FTL"
            };
            var message = entry.RenderMessage(CultureInfo.InvariantCulture).Replace('\r', ' ').Replace('\n', ' ');
            message = PrivateText.Replace(message, "[redacted]");
            writer.Write(entry.Timestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.Write($" [{level}] ");
            writer.Write(message.Length > 4000 ? message[..4000] : message);
            // Framework exception messages and stack paths can contain private data.
            if (entry.Exception is { } error) writer.Write($" ({error.GetType().Name}; 0x{error.HResult:X8})");
            writer.WriteLine();
        }
    }

    private sealed class BufferedSink : ILogEventSink, IDisposable
    {
        private readonly Channel<LogEvent> queue = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        private readonly Thread worker;
        private long dropped;

        internal BufferedSink(Logger fileLogger)
        {
            worker = new Thread(() => Run(fileLogger)) { Name = "VibeWare diagnostics", IsBackground = true };
            worker.Start();
        }

        internal long DroppedEvents => Interlocked.Read(ref dropped);

        public void Emit(LogEvent entry)
        {
            if (!queue.Writer.TryWrite(entry)) Interlocked.Increment(ref dropped);
        }

        public void Dispose()
        {
            queue.Writer.TryComplete();
            if (Thread.CurrentThread != worker) worker.Join(TimeSpan.FromSeconds(2));
        }

        private void Run(Logger fileLogger)
        {
            using (fileLogger)
            {
                while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    while (queue.Reader.TryRead(out var entry)) fileLogger.Write(entry);
            }
        }
    }

    private static bool TryOpenAttachedDraft(string subject, string body, string path)
    {
        var recipient = new MapiRecipient { Class = 1, Name = SupportAddress, Address = "SMTP:" + SupportAddress };
        var attachment = new MapiFile { Position = uint.MaxValue, Path = path, Name = Path.GetFileName(path) };
        nint recipientPointer = 0, filePointer = 0;
        try
        {
            recipientPointer = Marshal.AllocHGlobal(Marshal.SizeOf<MapiRecipient>());
            Marshal.StructureToPtr(recipient, recipientPointer, false);
            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFile>());
            Marshal.StructureToPtr(attachment, filePointer, false);
            var message = new MapiMessage
            {
                Subject = subject, Body = body, RecipientCount = 1, Recipients = recipientPointer,
                FileCount = 1, Files = filePointer
            };
            return MAPISendMailW(0, 0, ref message, 0x8, 0) is 0 or 1;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally
        {
            if (filePointer != 0) { Marshal.DestroyStructure<MapiFile>(filePointer); Marshal.FreeHGlobal(filePointer); }
            if (recipientPointer != 0) { Marshal.DestroyStructure<MapiRecipient>(recipientPointer); Marshal.FreeHGlobal(recipientPointer); }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiMessage
    {
        public uint Reserved;
        public string? Subject, Body, MessageType, DateReceived, ConversationId;
        public uint Flags;
        public nint Originator;
        public uint RecipientCount;
        public nint Recipients;
        public uint FileCount;
        public nint Files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiRecipient
    {
        public uint Reserved, Class;
        public string? Name, Address;
        public uint EntryIdSize;
        public nint EntryId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiFile
    {
        public uint Reserved, Flags, Position;
        public string? Path, Name;
        public nint FileType;
    }

    [DllImport("mapi32.dll", ExactSpelling = true)]
    private static extern uint MAPISendMailW(nint session, nuint owner, ref MapiMessage message, uint flags, uint reserved);
}
