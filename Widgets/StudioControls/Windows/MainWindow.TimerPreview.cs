using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using VibeKit.StudioControls.Core;
using global::Windows.Graphics.Imaging;
using global::Windows.Storage;
using global::Windows.Storage.Streams;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private async Task CheckPresenterAsync()
    {
        if (!UiSmoke.Enabled || timerFace == null || timerWindow == null)
            throw new InvalidOperationException("An isolated presenter window is required.");
        var prior = settings.Timer;
        settings.Timer = prior with { Large = true, ShowNote = true, WarningSeconds = 12 };
        LocalSettings.Save(settings);
        if (LocalSettings.Load(out _).Timer != settings.Timer || MinuteClock(7) != "00:07" || MinuteClock(-4) != "+00:04")
            throw new InvalidOperationException("Timer preferences or the minute display did not round-trip.");
        settings.Timer = prior;
        if (UiSmoke.PreviewDirectory is not { } directory) return;

        // Exercise the same presenter and playback controls as the shipped window, offline.
        languageSelector.SelectedIndex = StudioTranslations.Languages.ToList().FindIndex(item => item.Code == "it");
        SelectTheme("Light", persist: false);
        var scene = new WorkflowStep(WorkflowLibrary.NewId(), "On the Table", 25);
        var detail = new WorkflowStep(WorkflowLibrary.NewId(), "Detail", 60);
        var flow = new Workflow(WorkflowLibrary.NewId(), "Demo", 180,
            [scene, detail, new WorkflowStep(WorkflowLibrary.NewId(), "Thank you", 10)]);
        foreach (bool large in new[] { false, true })
        {
            settings.Timer = prior with { Large = large, WarningSeconds = 25 };
            player.Start(flow, live: false);
            timerFace.ApplySize(timerWindow, large);
            RefreshPlayback();
            await Task.Delay(330);
            await SaveElementAsync((FrameworkElement)timerWindow.Content, directory, large ? "timer-large.png" : "timer-compact.png");
        }
        Activate();
        var editor = new TimerSettingsEditor(this);
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Dialog.Opened += (_, _) => opening.TrySetResult();
        var closing = editor.Dialog.ShowAsync();
        try
        {
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(330);
            // Capture the dialog's layout surface, excluding the rest of the desktop.
            await SaveElementAsync(editor.Dialog, directory, "timer-settings.png");
        }
        finally
        {
            editor.Dialog.Hide();
            await closing;
            settings.Timer = prior;
        }
    }

    private static async Task SaveElementAsync(FrameworkElement element, string directory, string name)
    {
        element.UpdateLayout();
        var rendered = new RenderTargetBitmap();
        await rendered.RenderAsync(element);
        if (rendered.PixelWidth < 16 || rendered.PixelHeight < 16)
            throw new InvalidOperationException("The presenter did not produce a visible layout.");
        var buffer = await rendered.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        if (pixels.All(b => b == 0)) throw new InvalidOperationException("Empty presenter render.");
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rendered.PixelWidth, (uint)rendered.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
