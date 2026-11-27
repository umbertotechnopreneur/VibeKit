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
        var left = Column();
        left.Children.Add(Card(Heading("Intro & thank you"),
            ProjectField("Name", "Project name", 100, "The main title on the intro and closing cards. It also identifies the project on the chapter card."),
            ProjectField("Eyebrow", "Label above title", 70, "A short line above the project title on the intro and closing cards. Use the name of your series or studio."),
            ProjectField("Subtitle", "Subtitle", 180, "A line below the main title on both cards. Use it to tell viewers what this episode covers."),
            ProjectField("Description", "Description", 600, "The paragraph below the subtitle on both cards. Keep it short enough to read on video.", multiline: true)));

        var links = Column(6);
        for (int index = 0; index < 3; index++)
            links.Children.Add(ProjectField("Link" + index, "Link " + (index + 1), 150,
                "An address shown as text on the intro and closing cards, in the order entered. Leave unused links empty."));
        left.Children.Add(Card(Heading("Project links"), links));

        var right = Column();
        right.Children.Add(Card(Heading("Chapter"),
            ProjectField("Chapter", "Chapter title", 100, "The main heading on the Chapter card. Use the name of the next section."),
            ProjectField("ChapterDescription", "Chapter description", 180, "A line below the chapter title. Describe what the viewer will see in this section.")));
        right.Children.Add(Card(Heading("Comparison A/B"), ResponsiveColumns(400,
            ProjectField("CompareA", "Comparison · left", 60, "Caption above the left image in the comparison scene. For example, enter Before."),
            ProjectField("CompareB", "Comparison · right", 60, "Caption above the right image in the comparison scene. For example, enter After."))));

        var logoActions = new ActionStrip();
        logoActions.Children.Add(ActionButton("Choose logo…", ChooseLogoAsync));
        logoActions.Children.Add(ActionButton("Remove logo", () => { logo.Text = ""; return Task.CompletedTask; }));
        right.Children.Add(Card(Heading("Branding"),
            Field(logo, "Project logo", "Choose a local PNG, JPG or BMP for the intro and closing cards. Apply copies the image into the project."),
            logoActions, WithTrailing(FieldLabel(vibe, "This project is VibeWare",
                "Show VibeWare branding on the intro, a logo and link on Thank you, and a small logo in the other scenes."), vibe)));
        soundVolume.IsEnabled = true;
        right.Children.Add(Card(Heading("Audio"),
            WithTrailing(FieldLabel(sound, "Intro sound", ActionDescriptions["Intro sound"]), sound),
            SliderField(soundVolume, "Intro sound volume", "Set the intro volume from 0% to 100%. Apply sends the chosen level to OBS.", "%")));

        ActionHelp(apply, "Save project & apply to OBS");
        apply.Click += async (_, _) => await ApplyProjectAsync();
        var content = Column(8);
        content.Children.Add(apply);
        projectNotice.Visibility = Visibility.Collapsed;
        projectNotice.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            projectNotice.Visibility = string.IsNullOrEmpty(projectNotice.Text) ? Visibility.Collapsed : Visibility.Visible);
        content.Children.Add(projectNotice);
        content.Children.Add(ResponsiveColumns(700, left, right));
        return content;
    }

    private FrameworkElement ProjectField(string key, string label, int limit, string help, bool multiline = false)
    {
        var field = new TextBox { MaxLength = limit };
        if (multiline)
        {
            field.AcceptsReturn = true;
            field.TextWrapping = TextWrapping.Wrap;
            field.MinHeight = 80;
            field.MaxHeight = 144;
        }
        projectFields.Add(key, field);
        return Field(field, label, help);
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
