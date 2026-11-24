using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text.Json;
using VibeKit.StudioControls.Core;
using global::Windows.Storage.Pickers;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private UIElement BuildProject()
    {
        var content = Column();
        content.MinWidth = 850;
        content.Children.Add(Heading("PROJECT · intro, chapters & thank you"));
        var columns = Columns(2);
        var left = Column(); var right = Column();
        AddField(left, "Name", "Project name", 100);
        AddField(left, "Eyebrow", "Eyebrow", 70);
        AddField(left, "Subtitle", "Subtitle", 180);
        var description = AddField(left, "Description", "Description", 600);
        description.AcceptsReturn = true; description.TextWrapping = TextWrapping.Wrap; description.Height = 140;
        for (int index = 0; index < 3; index++) AddField(left, "Link" + index, "Link " + (index + 1), 150);
        AddField(right, "Chapter", "Chapter title", 100);
        AddField(right, "ChapterDescription", "Chapter description", 180);
        AddField(right, "CompareA", "Comparison · left", 60);
        AddField(right, "CompareB", "Comparison · right", 60);
        right.Children.Add(logo);
        var logoActions = Row();
        logoActions.Children.Add(ActionButton("Choose logo…", ChooseLogoAsync));
        logoActions.Children.Add(ActionButton("Remove logo", () => { logo.Text = ""; return Task.CompletedTask; }));
        right.Children.Add(logoActions);
        right.Children.Add(vibe); right.Children.Add(sound);
        soundVolume.IsEnabled = true; right.Children.Add(soundVolume);
        columns.Children.Add(left); Grid.SetColumn(right, 1); columns.Children.Add(right);
        content.Children.Add(columns);
        apply.Click += async (_, _) => await ApplyProjectAsync();
        content.Children.Add(apply); content.Children.Add(projectNotice);
        content.Children.Add(Text("Cards render locally when you Apply. Stop recording stays available while artwork is prepared. VibeWare branding follows the project flag. The final card includes OBS credit and © 2026 Umberto Giacobbi."));
        return content;
    }

    private TextBox AddField(StackPanel parent, string key, string label, int limit)
    {
        var field = new TextBox { Header = label, MaxLength = limit };
        projectFields[key] = field; parent.Children.Add(field);
        return field;
    }

    private void LoadProject()
    {
        ProjectSettings values = new();
        try
        {
            string path = Path.Combine(CurrentAssetsDirectory, "project.json");
            values = File.Exists(path) ? SettingsStore.Load<ProjectSettings>(path) : new ProjectSettings { Name = CurrentProject?.Name ?? values.Name };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        {
            Diagnostics.Record("project.load", error);
            values = new();
            projectNotice.Text = "The saved project could not be read. Defaults are shown; Apply explicitly replaces the saved project.";
        }
        projectFields["Name"].Text = values.Name; projectFields["Eyebrow"].Text = values.Eyebrow;
        projectFields["Subtitle"].Text = values.Subtitle; projectFields["Description"].Text = values.Description;
        projectFields["Chapter"].Text = values.Chapter; projectFields["ChapterDescription"].Text = values.ChapterDescription;
        projectFields["CompareA"].Text = values.CompareA; projectFields["CompareB"].Text = values.CompareB;
        for (int index = 0; index < 3; index++) projectFields["Link" + index].Text = values.Links.ElementAtOrDefault(index) ?? "";
        logo.Text = values.Logo; vibe.IsOn = values.VibeWare; sound.IsOn = values.SoundEnabled; soundVolume.Value = values.SoundVolume;
        savedProjectForm = JsonSerializer.Serialize(ReadProject());
    }

    private ProjectSettings ReadProject() => new()
    {
        Name = projectFields["Name"].Text.Trim(), Eyebrow = projectFields["Eyebrow"].Text.Trim(),
        Subtitle = projectFields["Subtitle"].Text.Trim(), Description = projectFields["Description"].Text.Trim(),
        Chapter = projectFields["Chapter"].Text.Trim(), ChapterDescription = projectFields["ChapterDescription"].Text.Trim(),
        CompareA = projectFields["CompareA"].Text.Trim(), CompareB = projectFields["CompareB"].Text.Trim(),
        Links = Enumerable.Range(0, 3).Select(index => projectFields["Link" + index].Text.Trim()).ToArray(),
        Logo = logo.Text, VibeWare = vibe.IsOn, SoundEnabled = sound.IsOn, SoundVolume = soundVolume.Value
    };

    private async Task ApplyProjectAsync()
    {
        if (workflowLibrary is null || rendering || closed || editingLibrary || PlaybackActive || sendingScene) return;
        rendering = true; apply.IsEnabled = false;
        UpdateWorkflowControls();
        string assetsDirectory = CurrentAssetsDirectory;
        bool saved = false;
        try
        {
            ProjectSettings values = ReadProject();
            values.Validate();
            projectNotice.Text = "Preparing project cards… recording controls remain available.";
            values = await Task.Run(() => CardRenderer.Prepare(values, lifetime.Token, assetsDirectory), lifetime.Token);
            saved = true;
            if (closed) return;
            logo.Text = values.Logo;
            savedProjectForm = JsonSerializer.Serialize(values);
            if (service is null || state?.ConnectionAvailable != true)
            {
                projectNotice.Text = "Project saved locally. Connect to OBS, then Apply again to update its sources.";
                return;
            }
            projectNotice.Text = "Project saved locally · applying artwork to OBS…";
            await obsGate.WaitAsync(lifetime.Token);
            commandBusy = true; UpdateEnabled(true);
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (service is null) throw new InvalidOperationException("OBS disconnected before the project could be applied.");
                SetState(await service.ApplyProjectSettingsAsync(values, assetsDirectory, lifetime.Token));
                projectNotice.Text = state?.Warnings.Count > 0 ? "Project applied. Check the OBS capability warnings." : "Project saved and applied to OBS.";
            }
            finally { commandBusy = false; obsGate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Diagnostics.Record("project.apply", error);
            if (!closed) projectNotice.Text = (saved ? "Project saved locally; OBS update incomplete. Retry Apply. " : "Project was not saved. ") + error.Message;
            if (HasConnectionFailure(error)) Offline(error);
        }
        finally
        {
            rendering = false;
            if (!closed) { apply.IsEnabled = true; UpdateEnabled(service is not null && state?.ConnectionAvailable == true); }
        }
    }

    private static bool HasConnectionFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ObsConnectionException or OperationCanceledException) return true;
        return false;
    }

    private async Task ChooseLogoAsync()
    {
        var picker = new FileOpenPicker();
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null && !closed) logo.Text = file.Path;
    }

    private async Task ExportAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "Studio-Controls-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        picker.FileTypeChoices.Add("Diagnostic ZIP", new[] { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null || closed) return;
        await Task.Run(() => Diagnostics.Export(file.Path), lifetime.Token);
        if (!closed) notice.Text = "Diagnostic logs exported. Passwords, settings and project content are excluded.";
    }

    private async Task PrepareCollectionAsync()
    {
        string output = Path.Combine(LocalSettings.DirectoryPath, "Studio-Controls-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        string assetsDirectory = CurrentAssetsDirectory;
        await Task.Run(() =>
        {
            CardRenderer.EnsureAssets(lifetime.Token, assetsDirectory);
            string template = Path.Combine(AppContext.BaseDirectory, "Scenes", "studio-windows.json");
            lifetime.Token.ThrowIfCancellationRequested();
            ScenePackage.Prepare(template, assetsDirectory, output);
        }, lifetime.Token);
        if (closed) return;
        notice.Text = "Scene collection prepared: " + output + ". In OBS use Scene Collection → Import, then choose your screen and cameras in source Properties. Existing collections are unchanged.";
        Process.Start(new ProcessStartInfo(LocalSettings.DirectoryPath) { UseShellExecute = true });
    }
}
