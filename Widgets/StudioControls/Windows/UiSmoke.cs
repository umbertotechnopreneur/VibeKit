using Microsoft.UI.Xaml;

namespace VibeKit.StudioControls.Windows;

/// <summary>Opt-in CI startup test. It never invokes OBS or camera controls.</summary>
internal static class UiSmoke
{
    public static bool Enabled { get; private set; }
    public static bool Passed { get; private set; }
    private static string? temporary;
    private static string ReportPath => Path.Combine(AppContext.BaseDirectory, "ui-smoke.txt");

    public static void Prepare()
    {
        Enabled = true;
        temporary = Path.Combine(Path.GetTempPath(), "StudioControls-ui-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        LocalSettings.VerificationDirectory = temporary;
        WriteReport("RUNNING: isolated native WinUI startup and appearance check.");
    }

    public static void Complete()
    {
        WriteReport("PASS: native WinUI window opened; Light/Dark/System preferences changed; window closed. No OBS connection, camera controller, enumeration or capture was created.");
        Passed = true;
    }

    public static void Fail(Exception error)
    {
        Passed = false;
        WriteReport("FAIL: " + error);
        Diagnostics.Record("ui.smoke", error);
    }

    public static void Cleanup()
    {
        LocalSettings.VerificationDirectory = null;
        if (temporary is null) return;
        try { Directory.Delete(temporary, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void WriteReport(string message)
    {
        try { File.WriteAllText(ReportPath, message + "\n"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Passed = false;
            throw new IOException("The UI smoke report could not be written.", error);
        }
    }
}
