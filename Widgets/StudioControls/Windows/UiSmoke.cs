using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

/// <summary>Opt-in CI startup test. It never invokes OBS or camera controls.</summary>
internal static class UiSmoke
{
    public static bool Enabled { get; private set; }
    public static bool Passed { get; private set; }
    private static string? temporary;
    private static string ReportPath => Path.Combine(AppContext.BaseDirectory, "ui-smoke.txt");

    internal static string? PreviewDirectory { get; private set; }

    public static void Prepare(bool exportPreviews)
    {
        Enabled = true;
        if (exportPreviews)
        {
            string? destination = Environment.GetEnvironmentVariable("STUDIO_TIMER_PREVIEW_DIR");
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
                throw new ArgumentException("An absolute STUDIO_TIMER_PREVIEW_DIR is required.");
            PreviewDirectory = Directory.CreateDirectory(destination).FullName;
        }
        temporary = Path.Combine(Path.GetTempPath(), "StudioControls-ui-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        LocalSettings.VerificationDirectory = temporary;
        WriteReport("RUNNING: isolated native WinUI startup and appearance check.");
    }

    public static void Complete()
    {
        WriteReport("PASS: native WinUI window, themes, localization, connected storyboard, transition persistence, modal library edits, active step and detached rehearsal timer verified. No OBS connection, camera controller, enumeration or capture was created.");
        Passed = true;
    }

    public static async Task CheckLibraryDialogAsync(ContentDialog dialog, NativeLocalization strings, Func<WorkflowLibrary> draft)
    {
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opening.TrySetResult();
        var closing = dialog.ShowAsync();
        try
        {
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var body = (StackPanel)((ScrollViewer)dialog.Content).Content;
            var sections = body.Children.OfType<Expander>().ToArray();
            var commands = (StackPanel)((StackPanel)sections[1].Content).Children.OfType<StackPanel>().Single();
            Button Action(string key) => commands.Children.OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == strings.Text(key));
            var form = body.Children.OfType<StackPanel>().Single();
            if (sections.Length != 2 || sections.Any(section => !section.IsExpanded) || Action("Remove workflow").IsEnabled)
                throw new InvalidOperationException("Library sections or last-workflow protection failed.");

            Invoke(Action("Duplicate"));
            await WaitForAsync(() => form.Children.OfType<TextBox>().Any());
            form.Children.OfType<TextBox>().Single().Text = "Duplicate check";
            Invoke(form.Children.OfType<StackPanel>().Single().Children.OfType<Button>().First());
            await WaitForAsync(() => draft().Projects.Single().Workflows.Length == 2);
            var copies = draft().Projects.Single().Workflows;
            if (copies[1].Name != "Duplicate check" || copies[0].Steps[1].Id == copies[1].Steps[1].Id ||
                copies[1].Steps[1].Transition != "fade")
                throw new InvalidOperationException("The duplicate lost step settings or reused step identities.");
            Invoke(Action("Remove workflow"));
            await WaitForAsync(() => form.Children.OfType<StackPanel>().Any());
            if (draft().Projects.Single().Workflows.Length != 2)
                throw new InvalidOperationException("The library removed a workflow before confirmation.");
            Invoke(form.Children.OfType<StackPanel>().Single().Children.OfType<Button>().First());
            await WaitForAsync(() => draft().Projects.Single().Workflows.Length == 1);
        }
        finally
        {
            dialog.Hide();
            await closing;
        }
    }

    private static void Invoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(30, timeout.Token);
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
