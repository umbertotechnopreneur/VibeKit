using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeKit.StudioControls.Windows;

public static class Diagnostics
{
    private static readonly object Gate = new();
    private const long Limit = 1024 * 1024;
    public static string DirectoryPath => Path.Combine(LocalSettings.DirectoryPath, "Logs");
    private static string LogPath => Path.Combine(DirectoryPath, "studio.log");

    public static void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Record("process.unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => Record("task.unobserved", args.Exception);
        Record("startup");
    }

    // Exception messages and payloads are deliberately excluded: a server error
    // can contain credentials or local paths. Stack frames retain fault locations.
    public static void Record(string operation, Exception? error = null)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= Limit)
                {
                    for (int index = 2; index >= 0; index--)
                    {
                        string source = index == 0 ? LogPath : LogPath + "." + index;
                        if (File.Exists(source)) File.Move(source, LogPath + "." + (index + 1), true);
                    }
                }
                string entry = $"{DateTimeOffset.UtcNow:O} {operation} {error?.GetType().Name}\n";
                Exception? cause = error?.InnerException;
                for (int depth = 0; depth < 4 && cause is not null; depth++, cause = cause.InnerException)
                    entry += "caused-by " + cause.GetType().FullName + "\n";
                if (error?.StackTrace is { } stack) entry += stack[..Math.Min(stack.Length, 12000)] + "\n";
                File.AppendAllText(LogPath, entry, Encoding.UTF8);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Export(string destination)
    {
        lock (Gate)
        {
            using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            for (int index = 0; index <= 3; index++)
            {
                string source = index == 0 ? LogPath : LogPath + "." + index;
                if (File.Exists(source)) archive.CreateEntryFromFile(source, Path.GetFileName(source));
            }
            using var writer = new StreamWriter(archive.CreateEntry("environment.txt").Open());
            writer.WriteLine("Studio Controls / Windows");
            writer.WriteLine(RuntimeInformation.FrameworkDescription);
            writer.WriteLine(RuntimeInformation.OSDescription);
            writer.WriteLine("Settings, passwords, project text and OBS configuration are excluded.");
        }
    }
}
