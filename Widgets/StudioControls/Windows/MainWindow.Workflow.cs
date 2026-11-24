using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VibeKit.StudioControls.Core;
using global::Windows.ApplicationModel.DataTransfer;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private readonly ComboBox workflowProjects = new() { Header = "Project", MinWidth = 180 };
    private readonly ComboBox workflowSelector = new() { Header = "Workflow", MinWidth = 180 };
    private readonly ContentControl editorSurface = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel workflowEditor = Column();
    private readonly StackPanel timeline = Row();
    private readonly TextBlock workflowSummary = Heading("");
    private readonly TextBlock workflowMessage = Text("");
    private readonly NumberBox targetSeconds = SecondsBox("Target (seconds)", 180, 0);
    private readonly NumberBox stepSeconds = SecondsBox("Duration (seconds)", 15);
    private readonly TextBox stepNote = new() { Header = "Note", MaxLength = 500 };
    private readonly StackPanel stepEditor = Column();
    private Expander? stepSection;
    private WorkflowLibrary? workflowLibrary;
    private string? workflowId, stepId, savedProjectForm;
    private bool editingLibrary, refreshingLibrary;
    private (string Kind, string Value)? internalDrag;

    private static string LibraryPath => Path.Combine(LocalSettings.DirectoryPath, "workflows.json");
    private WorkflowProject? CurrentProject => workflowLibrary?.Projects.FirstOrDefault(project => project.Id == workflowLibrary.ActiveProjectId);
    private Workflow? CurrentWorkflow => CurrentProject?.Workflows.FirstOrDefault(flow => flow.Id == workflowId);
    private string CurrentAssetsDirectory => CurrentProject is { } project && project.Id != WorkflowLibrary.DefaultProjectId
        ? Path.Combine(LocalSettings.DirectoryPath, "Projects", project.Id, "Cards") : LocalSettings.AssetsPath;

    private void LoadWorkflowLibrary()
    {
        string name = new ProjectSettings().Name;
        try
        {
            if (File.Exists(LocalSettings.ProjectPath)) name = SettingsStore.Load<ProjectSettings>(LocalSettings.ProjectPath).Name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { Diagnostics.Record("workflow.legacy", error); }
        try { workflowLibrary = WorkflowLibrary.Load(LibraryPath, name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        {
            Diagnostics.Record("workflow.load", error);
            workflowMessage.Text = "Workflow library could not be opened. The saved file was preserved.";
        }
    }

    private static NumberBox SecondsBox(string header, int value, int minimum = 1) => new()
    {
        Header = header, Value = value, Minimum = minimum, Maximum = 86400,
        SmallChange = 1, LargeChange = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 165
    };

    private static int ReadSeconds(NumberBox field, int minimum = 1)
    {
        if (!double.IsFinite(field.Value) || field.Value < minimum || field.Value > 86400 || field.Value != Math.Truncate(field.Value))
            throw new ArgumentException("Enter a whole number of seconds.");
        return (int)field.Value;
    }

    private UIElement BuildWorkflow()
    {
        var content = Column();
        var select = Row();
        select.Children.Add(workflowProjects);
        select.Children.Add(ActionButton("New project", AddWorkflowProjectAsync));
        workflowEditor.Children.Add(select);
        var flowActions = Row();
        flowActions.Children.Add(workflowSelector);
        flowActions.Children.Add(ActionButton("New workflow", () => NameWorkflowAsync("New workflow")));
        flowActions.Children.Add(ActionButton("Duplicate", () => NameWorkflowAsync("Duplicate")));
        flowActions.Children.Add(ActionButton("Rename workflow", () => NameWorkflowAsync("Rename workflow")));
        workflowEditor.Children.Add(flowActions);
        workflowEditor.Children.Add(workflowSummary);

        var track = new Border { Padding = new(10), MinHeight = 72, MinWidth = 450, CornerRadius = new(8),
            BorderThickness = new(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], AllowDrop = true };
        track.Child = new ScrollViewer { Content = timeline, MaxWidth = 920, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled };
        ConfigureDrop(track, null);
        workflowEditor.Children.Add(track);

        var palette = Columns(3);
        for (int i = 0; i < (SceneStyles.Length + 2) / 3; i++) palette.RowDefinitions.Add(new() { Height = GridLength.Auto });
        palette.RowSpacing = 6;
        for (int i = 0; i < SceneStyles.Length; i++)
        {
            var style = SceneStyles[i];
            string title = StudioCatalog.Default.Scenes.First(scene => scene.Name == style.Name).Title;
            var face = Row();
            face.Children.Add(new FontIcon { Glyph = style.Glyph, FontSize = 20 });
            face.Children.Add(Text(title));
            var card = new Button { Content = face, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(card, title);
            ToolTipService.SetToolTip(card, style.Subtitle);
            card.Click += async (_, _) => await InsertStepAsync(style.Name, CurrentWorkflow?.Steps.Length ?? 0);
            ConfigureDrag(card, "scene", style.Name);
            Grid.SetRow(card, i / 3); Grid.SetColumn(card, i % 3); palette.Children.Add(card);
        }
        workflowEditor.Children.Add(Section("Scene palette", palette, true));

        var detail = Row();
        detail.Children.Add(stepSeconds); stepNote.Width = 320; detail.Children.Add(stepNote);
        detail.Children.Add(ActionButton("Save changes", () => EditStepAsync("save")));
        stepEditor.Children.Add(detail);
        var arrange = Row();
        arrange.Children.Add(ActionButton("Move left", () => EditStepAsync("left")));
        arrange.Children.Add(ActionButton("Move right", () => EditStepAsync("right")));
        arrange.Children.Add(ActionButton("Remove", () => EditStepAsync("remove")));
        stepEditor.Children.Add(arrange);
        stepSection = Section("Selected step", stepEditor, true);
        workflowEditor.Children.Add(stepSection);

        var targetRow = Row();
        targetRow.Children.Add(targetSeconds);
        targetRow.Children.Add(ActionButton("Save target", async () =>
        {
            int value = ReadSeconds(targetSeconds, 0);
            if (CurrentWorkflow is { } flow) await SaveWorkflowAsync(flow with { TargetSeconds = value == 0 ? null : value });
        }));
        targetRow.Children.Add(Text("Use 0 for no target."));
        workflowEditor.Children.Add(Section("Time target", targetRow, false));
        workflowProjects.SelectionChanged += async (_, _) =>
        {
            if (refreshingLibrary || workflowLibrary is null || workflowProjects.SelectedIndex < 0) return;
            string id = workflowLibrary.Projects[workflowProjects.SelectedIndex].Id;
            if (id != workflowLibrary.ActiveProjectId) await SwitchWorkflowProjectAsync(id);
        };
        workflowSelector.SelectionChanged += (_, _) =>
        {
            if (refreshingLibrary || PlaybackActive || editingLibrary || CurrentProject is not { } project || workflowSelector.SelectedIndex < 0) return;
            workflowId = project.Workflows[workflowSelector.SelectedIndex].Id;
            stepId = null;
            RefreshWorkflowEditor();
        };
        editorSurface.Content = workflowEditor;
        content.Children.Add(editorSurface);
        content.Children.Add(BuildPlaybackPanel());
        content.Children.Add(workflowMessage);
        return content;
    }

    private static Expander Section(string title, FrameworkElement content, bool open) => new()
    { Header = title, Content = content, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch,
      HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private async Task<string?> PromptNameAsync(string title, string initial)
    {
        var field = new TextBox { MaxLength = 100, Text = initial };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = localization.Text(title), Content = field,
            PrimaryButtonText = localization.Text("Save"), CloseButtonText = localization.Text("Cancel"),
            DefaultButton = ContentDialogButton.Primary, RequestedTheme = root.RequestedTheme };
        dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initial);
        field.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(field.Text);
        field.SelectAll();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? field.Text.Trim() : null;
    }

    private async Task<bool> CanLeaveProjectAsync()
    {
        if (savedProjectForm == System.Text.Json.JsonSerializer.Serialize(ReadProject())) return true;
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = localization.Text("Discard unsaved card changes?"),
            PrimaryButtonText = localization.Text("Discard"), CloseButtonText = localization.Text("Cancel"),
            DefaultButton = ContentDialogButton.Close, RequestedTheme = root.RequestedTheme };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task AddWorkflowProjectAsync()
    {
        if (workflowLibrary is not { } library || !await CanLeaveProjectAsync()) return;
        if (await PromptNameAsync("New project", "") is not { } name) return;
        var flow = new Workflow(WorkflowLibrary.NewId(), "Workflow 1", 180, []);
        var project = new WorkflowProject(WorkflowLibrary.NewId(), name, [flow]);
        await CommitLibraryAsync(library with { Projects = [..library.Projects, project], ActiveProjectId = project.Id }, flow.Id);
    }

    private async Task SwitchWorkflowProjectAsync(string id)
    {
        if (workflowLibrary is not { } library || rendering || editingLibrary || PlaybackActive || !await CanLeaveProjectAsync())
        { RefreshWorkflowEditor(); return; }
        await CommitLibraryAsync(library with { ActiveProjectId = id });
    }

    private async Task NameWorkflowAsync(string action)
    {
        if (CurrentProject is not { } project || workflowLibrary is not { } library) return;
        var original = CurrentWorkflow;
        string initial = action == "Duplicate" ? original?.Name + " (2)" : action == "Rename workflow" ? original?.Name ?? "" : "";
        if (await PromptNameAsync(action, initial) is not { } name) return;
        if (action == "Rename workflow" && original is not null)
        { await SaveWorkflowAsync(original with { Name = name }); return; }
        var flow = new Workflow(WorkflowLibrary.NewId(), name, action == "Duplicate" ? original?.TargetSeconds : 180,
            action == "Duplicate" && original is not null ? original.Steps.Select(step => step with { Id = WorkflowLibrary.NewId() }).ToArray() : []);
        await CommitLibraryAsync(ReplaceProject(library, project with { Workflows = [..project.Workflows, flow] }), flow.Id);
    }

    private static WorkflowLibrary ReplaceProject(WorkflowLibrary library, WorkflowProject updated) =>
        library with { Projects = library.Projects.Select(project => project.Id == updated.Id ? updated : project).ToArray() };

    private Task SaveWorkflowAsync(Workflow updated, string? selected = null)
    {
        if (CurrentProject is not { } project || workflowLibrary is not { } library) return Task.CompletedTask;
        return CommitLibraryAsync(ReplaceProject(library, project with
        { Workflows = project.Workflows.Select(flow => flow.Id == updated.Id ? updated : flow).ToArray() }), updated.Id, selected);
    }

    private async Task CommitLibraryAsync(WorkflowLibrary proposed, string? selectedFlow = null, string? selectedStep = null)
    {
        if (editingLibrary || rendering || PlaybackActive || closed) return;
        editingLibrary = true;
        UpdateWorkflowControls();
        try
        {
            await proposed.SaveAsync(LibraryPath, lifetime.Token);
            if (closed) return;
            bool changed = workflowLibrary?.ActiveProjectId != proposed.ActiveProjectId;
            workflowLibrary = proposed;
            workflowId = selectedFlow ?? (changed ? null : workflowId);
            stepId = selectedStep ?? (changed ? null : stepId);
            workflowMessage.Text = "";
            if (changed)
            {
                projectNotice.Text = workflowMessage.Text = "Apply the project to update the cards in OBS.";
                LoadProject();
            }
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error)
        {
            Diagnostics.Record("workflow.save", error);
            workflowMessage.Text = localization.Text("Workflow could not be saved.") + " " + error.Message;
        }
        finally
        {
            editingLibrary = false;
            if (!closed) RefreshWorkflowEditor();
        }
    }

    private async Task InsertStepAsync(string scene, int insertion)
    {
        if (CurrentWorkflow is not { } flow) return;
        var steps = flow.Steps.ToList();
        var step = new WorkflowStep(WorkflowLibrary.NewId(), scene, 15);
        steps.Insert(Math.Clamp(insertion, 0, steps.Count), step);
        await SaveWorkflowAsync(flow with { Steps = steps.ToArray() }, step.Id);
    }

    private async Task EditStepAsync(string action)
    {
        if (CurrentWorkflow is not { } flow) return;
        var steps = flow.Steps.ToList();
        int index = steps.FindIndex(step => step.Id == stepId);
        if (index < 0) return;
        if (action == "save") steps[index] = steps[index] with { Seconds = ReadSeconds(stepSeconds), Note = stepNote.Text.Trim() };
        else if (action == "remove") steps.RemoveAt(index);
        else
        {
            int next = Math.Clamp(index + (action == "left" ? -1 : 1), 0, steps.Count - 1);
            if (index == next) return;
            (steps[index], steps[next]) = (steps[next], steps[index]);
        }
        await SaveWorkflowAsync(flow with { Steps = steps.ToArray() });
    }

    private void ConfigureDrag(Button button, string kind, string value)
    {
        button.CanDrag = true;
        button.DragStarting += (_, args) =>
        {
            internalDrag = (kind, value);
            args.Data.RequestedOperation = kind == "scene" ? DataPackageOperation.Copy : DataPackageOperation.Move;
            args.Data.SetData("VibeKit.StudioControls.Workflow", value);
        };
        button.DropCompleted += (_, _) => internalDrag = null;
    }

    private void ConfigureDrop(FrameworkElement target, string? targetId)
    {
        target.AllowDrop = true;
        target.DragOver += (_, args) =>
        {
            if (internalDrag is not { } drag || !args.DataView.Contains("VibeKit.StudioControls.Workflow")) return;
            args.AcceptedOperation = drag.Kind == "scene" ? DataPackageOperation.Copy : DataPackageOperation.Move;
            args.Handled = true;
        };
        target.Drop += async (_, args) =>
        {
            if (internalDrag is not { } drag || CurrentWorkflow is not { } flow || !args.DataView.Contains("VibeKit.StudioControls.Workflow")) return;
            args.Handled = true;
            int to = targetId is null ? flow.Steps.Length : Array.FindIndex(flow.Steps, step => step.Id == targetId);
            if (to < 0) return;
            if (targetId is not null && args.GetPosition(target).X >= target.ActualWidth / 2) to++;
            if (drag.Kind == "scene") { await InsertStepAsync(drag.Value, to); return; }
            var steps = flow.Steps.ToList();
            int from = steps.FindIndex(step => step.Id == drag.Value);
            if (from < 0) return;
            if (from < to) to--;
            if (from == to) return;
            var moved = steps[from]; steps.RemoveAt(from); steps.Insert(to, moved);
            await SaveWorkflowAsync(flow with { Steps = steps.ToArray() }, moved.Id);
        };
    }

    private void RefreshWorkflowEditor()
    {
        refreshingLibrary = true;
        try
        {
            var project = CurrentProject;
            workflowProjects.ItemsSource = workflowLibrary?.Projects.Select(x => x.Name).ToArray();
            workflowProjects.SelectedIndex = project is null ? -1 : Array.IndexOf(workflowLibrary!.Projects, project);
            workflowSelector.ItemsSource = project?.Workflows.Select(x => x.Name).ToArray();
            var flow = CurrentWorkflow ?? project?.Workflows.FirstOrDefault();
            workflowId = flow?.Id;
            workflowSelector.SelectedIndex = flow is null ? -1 : Array.IndexOf(project!.Workflows, flow);
            timeline.Children.Clear();
            if (flow is null) return;
            var selected = flow.Steps.FirstOrDefault(step => step.Id == stepId) ?? flow.Steps.FirstOrDefault();
            stepId = selected?.Id;
            foreach (var step in flow.Steps)
            {
                string title = SceneTitle(step.Scene);
                var button = new Button { Content = title + "  ·  " + ClockText(step.Seconds),
                    BorderThickness = new(step == selected ? 2 : 1), Padding = new(12, 10, 12, 10) };
                button.Click += (_, _) => { stepId = step.Id; RefreshStepSelection(flow, step); };
                ConfigureDrag(button, "step", step.Id); ConfigureDrop(button, step.Id);
                timeline.Children.Add(button);
            }
            if (flow.Steps.Length == 0) timeline.Children.Add(Text(localization.Text("Drag a scene here or choose one below.")));
            if (selected is not null) RefreshStepSelection(flow, selected);
            if (stepSection is not null) stepSection.IsEnabled = selected is not null;
            targetSeconds.Value = flow.TargetSeconds ?? 0;
            int total = flow.Steps.Sum(step => step.Seconds);
            workflowSummary.Text = localization.Text("Total") + " " + ClockText(total)
                + (flow.TargetSeconds is int target ? "  ·  " + localization.Text("Target") + " " + ClockText(target)
                    + (total > target ? "  (+" + ClockText(total - target) + ")" : "") : "");
        }
        finally { refreshingLibrary = false; UpdateWorkflowControls(); }
    }

    private void RefreshStepSelection(Workflow flow, WorkflowStep selected)
    {
        for (int i = 0; i < timeline.Children.Count && i < flow.Steps.Length; i++)
            if (timeline.Children[i] is Button tile) tile.BorderThickness = new(flow.Steps[i].Id == selected.Id ? 2 : 1);
        stepSeconds.Value = selected.Seconds; stepNote.Text = selected.Note;
    }

    private string SceneTitle(string scene) => localization.Text(StudioCatalog.Default.Scenes.FirstOrDefault(item => item.Name == scene)?.Title ?? scene);
    private static string ClockText(int seconds)
    {
        long value = Math.Abs((long)seconds);
        return $"{(seconds < 0 ? "+" : "")}{value / 60}:{value % 60:00}";
    }
}
