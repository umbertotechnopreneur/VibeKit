using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VibeKit.StudioControls.Core;
using global::Windows.ApplicationModel.DataTransfer;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private readonly Button libraryButton = new() { Content = "Library" };
    private readonly Button targetButton = new() { Content = "Time target" };
    private int highlightedIndex = -1;
    private readonly StackPanel timeline = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock workflowName = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis, Tag = NativeLocalization.UserText };
    private readonly TextBlock projectName = new() { Opacity = .72, TextTrimming = TextTrimming.CharacterEllipsis, Tag = NativeLocalization.UserText };
    private readonly TextBlock workflowSummary = Text("");
    private readonly TextBlock workflowMessage = Text("");
    private readonly Grid emptySequence = new() { MinHeight = 58, ColumnSpacing = 10 };
    private readonly ContentControl paletteSurface = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private WorkflowLibrary? workflowLibrary;
    private string? workflowId, savedProjectForm;
    private bool editingLibrary, workflowDialogOpen;
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

    private static NumberBox SecondsBox(int value, int minimum = 1) => new()
    {
        Value = value, Minimum = minimum, Maximum = 86400,
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
        var content = Column(10);
        var editor = Column(8);
        var heading = Column(2);
        heading.Children.Add(projectName);
        heading.Children.Add(workflowName);
        ActionHelp(libraryButton, "Library");
        libraryButton.Click += async (_, _) => await OpenWorkflowDialogAsync(ManageLibraryAsync);
        content.Children.Add(WithTrailing(heading, libraryButton));

        var caption = Column(2);
        caption.Children.Add(Heading("Sequence"));
        caption.Children.Add(workflowSummary);
        ActionHelp(targetButton, "Time target");
        targetButton.Click += async (_, _) => await OpenWorkflowDialogAsync(EditTargetAsync);
        var strip = new ScrollViewer { Content = timeline,
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        emptySequence.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        emptySequence.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        emptySequence.Children.Add(new FontIcon { Glyph = "\uE8B9", FontSize = 20, Opacity = .72 });
        var hint = Text("Choose a scene from the palette or drag it here.");
        hint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(hint, 1);
        emptySequence.Children.Add(hint);
        var dropArea = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        dropArea.Children.Add(strip);
        dropArea.Children.Add(emptySequence);
        ConfigureDrop(dropArea, null);
        editor.Children.Add(Card(WithTrailing(caption, targetButton), dropArea));
        editor.Children.Add(BuildPlaybackPanel());
        workflowMessage.Visibility = string.IsNullOrEmpty(workflowMessage.Text) ? Visibility.Collapsed : Visibility.Visible;
        workflowMessage.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            workflowMessage.Visibility = string.IsNullOrEmpty(workflowMessage.Text) ? Visibility.Collapsed : Visibility.Visible);
        editor.Children.Add(workflowMessage);

        var palette = new Grid { RowSpacing = 6, ColumnSpacing = 8 };
        var tiles = SceneStyles.Select(style =>
        {
            var definition = StudioCatalog.Default.Scenes.First(scene => scene.Name == style.Name);
            string title = definition.Title;
            var face = new Grid { ColumnSpacing = 7 };
            face.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            face.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            face.Children.Add(SceneIcon(style, 18));
            var label = Text(title);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 1);
            face.Children.Add(label);
            var tile = new Button { Content = face, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(8, 5, 8, 5) };
            AutomationProperties.SetName(tile, title);
            AddSceneHelp(tile, style, definition, "Add scene");
            tile.Click += async (_, _) => await InsertStepAsync(style.Name, CurrentWorkflow?.Steps.Length ?? 0);
            ConfigureDrag(tile, "scene", style.Name);
            palette.Children.Add(tile);
            return tile;
        }).ToArray();
        void Arrange(int columns)
        {
            if (columns == palette.ColumnDefinitions.Count) return;
            palette.ColumnDefinitions.Clear();
            palette.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) palette.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            for (int i = 0; i < tiles.Length; i++)
            {
                if (i % columns == 0) palette.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Grid.SetColumn(tiles[i], i % columns);
                Grid.SetRow(tiles[i], i / columns);
            }
        }
        Arrange(1);
        palette.SizeChanged += (_, e) => Arrange(e.NewSize.Width >= 700 ? 4 : e.NewSize.Width >= 470 ? 3 : e.NewSize.Width >= 295 ? 2 : 1);
        paletteSurface.Content = Section("Scene palette", palette, true);
        content.Children.Add(ResponsiveColumns(820, editor, paletteSurface));
        content.Children.Add(BuildDeck());
        return content;
    }

    private static Grid WithTrailing(FrameworkElement leading, FrameworkElement trailing)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(trailing, 1);
        trailing.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(leading);
        grid.Children.Add(trailing);
        return grid;
    }

    private static FontIcon SceneIcon(SceneStyle style, double size)
    {
        var icon = new FontIcon { Glyph = style.Glyph, FontSize = size };
        void Paint() => icon.Foreground = Brush(icon.ActualTheme == ElementTheme.Dark ? style.Dark : style.Light);
        icon.Loaded += (_, _) => Paint();
        icon.ActualThemeChanged += (_, _) => Paint();
        Paint();
        return icon;
    }

    private ContentDialog Dialog(string title, FrameworkElement body, string primary = "Save", string close = "Cancel")
    {
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme,
            Title = localization.Text(title), Content = body, PrimaryButtonText = localization.Text(primary),
            CloseButtonText = localization.Text(close), DefaultButton = ContentDialogButton.Primary };
        SetDialogHelp(dialog, primary, close);
        return dialog;
    }

    private async Task OpenWorkflowDialogAsync(Func<Task> show)
    {
        if (workflowDialogOpen || PlaybackActive || sendingScene || editingLibrary || rendering || closed) return;
        workflowDialogOpen = true;
        UpdateWorkflowControls();
        try { await show(); }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) { Fail("workflow.dialog", error); }
        finally
        {
            workflowDialogOpen = false;
            if (!closed) UpdateWorkflowControls();
        }
    }

    private async Task EditTargetAsync()
    {
        if (CurrentWorkflow is not { } flow) return;
        var target = SecondsBox(flow.TargetSeconds ?? 0, 0);
        var body = Column(8);
        body.Children.Add(Field(target, localization.Text("Target (seconds)"), localization.Text("Compare the planned workflow length with this target. Enter 0 to turn the target off.")));
        var dialog = Dialog("Time target", body);
        void Validate() => dialog.IsPrimaryButtonEnabled = ValidSeconds(target, 0);
        target.ValueChanged += (_, _) => Validate();
        var error = Text("");
        body.Children.Add(error);
        while (!closed && await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (await SaveWorkflowAsync(flow with { TargetSeconds = target.Value > 0 ? ReadSeconds(target, 0) : null })) break;
            error.Text = workflowMessage.Text;
        }
    }

    private static bool ValidSeconds(NumberBox field, int minimum) =>
        double.IsFinite(field.Value) && field.Value >= minimum && field.Value <= 86400 && field.Value == Math.Truncate(field.Value);

    private static Expander Section(string title, FrameworkElement content, bool open) => new()
    { Header = title, Content = content, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch,
      HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private async Task<bool> CanLeaveProjectAsync()
    {
        if (savedProjectForm == System.Text.Json.JsonSerializer.Serialize(ReadProject())) return true;
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = localization.Text("Discard unsaved card changes?"),
            PrimaryButtonText = localization.Text("Discard"), CloseButtonText = localization.Text("Cancel"),
            DefaultButton = ContentDialogButton.Close, RequestedTheme = root.RequestedTheme };
        SetDialogHelp(dialog, "Discard", "Cancel");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static WorkflowLibrary ReplaceProject(WorkflowLibrary library, WorkflowProject updated) =>
        library with { Projects = library.Projects.Select(project => project.Id == updated.Id ? updated : project).ToArray() };

    private Task<bool> SaveWorkflowAsync(Workflow updated)
    {
        if (CurrentProject is not { } project || workflowLibrary is not { } library) return Task.FromResult(false);
        return CommitLibraryAsync(ReplaceProject(library, project with
        { Workflows = project.Workflows.Select(flow => flow.Id == updated.Id ? updated : flow).ToArray() }), updated.Id);
    }

    private async Task<bool> CommitLibraryAsync(WorkflowLibrary proposed, string? selectedFlow = null)
    {
        if (editingLibrary || rendering || PlaybackActive || closed) return false;
        editingLibrary = true;
        UpdateWorkflowControls();
        try
        {
            await proposed.SaveAsync(LibraryPath, lifetime.Token);
            if (closed) return false;
            bool changed = workflowLibrary?.ActiveProjectId != proposed.ActiveProjectId;
            workflowLibrary = proposed;
            workflowId = selectedFlow ?? (changed ? null : workflowId);
            workflowMessage.Text = "";
            if (changed)
            {
                projectNotice.Text = workflowMessage.Text = "Apply the project to update the cards in OBS.";
                LoadProject();
            }
            return true;
        }
        catch (OperationCanceledException) when (closed) { return false; }
        catch (Exception error)
        {
            Diagnostics.Record("workflow.save", error);
            workflowMessage.Text = localization.Text("Workflow could not be saved.") + " " + error.Message;
            return false;
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
        await SaveWorkflowAsync(flow with { Steps = steps.ToArray() });
    }

    private static string TransitionLabel(string transition) => transition switch { "cut" => "Cut", "fade" => "Fade", _ => "OBS default" };

    private async Task EditStepAsync(Workflow flow, int index)
    {
        if (CurrentWorkflow != flow || PlaybackActive || editingLibrary) return;
        var step = flow.Steps[index];
        var body = Column(12);
        body.Children.Add(Heading(SceneTitle(step.Scene)));
        var seconds = SecondsBox(step.Seconds);
        string[] transitions = ["default", "cut", "fade"];
        var transition = new ComboBox {
            ItemsSource = transitions.Select(key => localization.Text(TransitionLabel(key))).ToArray(),
            SelectedIndex = Array.IndexOf(transitions, step.Transition), HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(ResponsiveColumns(350,
            Field(seconds, localization.Text("Duration (seconds)"), localization.Text("How long this step runs before the workflow moves to the next scene. Enter whole seconds.")),
            Field(transition, localization.Text("Transition"), localization.Text("Choose how OBS enters this scene during playback. OBS default uses the configured transition."))));
        var note = new TextBox { Text = step.Note, MaxLength = 500,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 70 };
        body.Children.Add(Field(note, localization.Text("Note"), localization.Text("A cue for the presenter during this step. Enable Show scene note in Timer settings to read it on the timer.")));
        int position = index;
        var order = Row();
        var left = new Button { Content = localization.Text("Move left") };
        var counter = Text("");
        counter.VerticalAlignment = VerticalAlignment.Center;
        var right = new Button { Content = localization.Text("Move right") };
        Help(left, localization.Text(ActionDescriptions["Move left"]));
        Help(right, localization.Text(ActionDescriptions["Move right"]));
        void UpdatePosition()
        {
            left.IsEnabled = position > 0;
            right.IsEnabled = position + 1 < flow.Steps.Length;
            counter.Text = $"{position + 1} / {flow.Steps.Length}";
        }
        left.Click += (_, _) => { position--; UpdatePosition(); };
        right.Click += (_, _) => { position++; UpdatePosition(); };
        UpdatePosition();
        order.Children.Add(left); order.Children.Add(counter); order.Children.Add(right);
        body.Children.Add(order);
        var dialog = Dialog("Edit step", body);
        dialog.SecondaryButtonText = localization.Text("Remove");
        seconds.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = ValidSeconds(seconds, 1);
        var error = Text("");
        body.Children.Add(error);
        while (!closed)
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None || CurrentWorkflow != flow) return;
            var steps = flow.Steps.ToList();
            steps.RemoveAt(index);
            if (result == ContentDialogResult.Primary)
                steps.Insert(position, step with { Seconds = ReadSeconds(seconds), Note = note.Text, Transition = transitions[transition.SelectedIndex] });
            if (await SaveWorkflowAsync(flow with { Steps = steps.ToArray() })) break;
            error.Text = workflowMessage.Text;
        }
    }

    private void ConfigureDrag(Button button, string kind, string value)
    {
        button.CanDrag = true;
        button.DragStarting += (_, args) =>
        {
            if (!libraryButton.IsEnabled) { args.Cancel = true; return; }
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
            if (!libraryButton.IsEnabled || internalDrag is not { } drag || !args.DataView.Contains("VibeKit.StudioControls.Workflow")) return;
            args.AcceptedOperation = drag.Kind == "scene" ? DataPackageOperation.Copy : DataPackageOperation.Move;
            args.Handled = true;
        };
        target.Drop += async (_, args) =>
        {
            if (!libraryButton.IsEnabled || internalDrag is not { } drag || CurrentWorkflow is not { } flow || !args.DataView.Contains("VibeKit.StudioControls.Workflow")) return;
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
            await SaveWorkflowAsync(flow with { Steps = steps.ToArray() });
        };
    }

    private void RefreshWorkflowEditor()
    {
        var project = CurrentProject;
        var flow = CurrentWorkflow ?? project?.Workflows.FirstOrDefault();
        workflowId = flow?.Id;
        projectName.Text = project?.Name ?? "";
        workflowName.Text = flow?.Name ?? "";
        timeline.Children.Clear();
        emptySequence.Visibility = flow is { Steps.Length: 0 } ? Visibility.Visible : Visibility.Collapsed;
        highlightedIndex = -1;
        if (flow is null) { UpdateWorkflowControls(); return; }
        int start = 0;
        for (int index = 0; index < flow.Steps.Length; index++)
        {
            var step = flow.Steps[index];
            if (index > 0)
            {
                timeline.Children.Add(new FontIcon { Glyph = "\uE72A", FontSize = 14, Opacity = .62,
                    Width = 22, VerticalAlignment = VerticalAlignment.Center });
            }
            timeline.Children.Add(SceneCard(flow, index, start));
            start += step.Seconds;
        }
        workflowSummary.Text = localization.Text("Total") + " " + ClockText(start)
            + (flow.TargetSeconds is int target ? "  ·  " + localization.Text("Target") + " " + ClockText(target)
                + (start > target ? "  (+" + ClockText(start - target) + ")" : "") : "");
        UpdateWorkflowControls();
        HighlightWorkflowStep();
    }

    private void HighlightWorkflowStep()
    {
        int index = PlaybackActive && player.Current is not null ? player.Index : -1;
        if (index == highlightedIndex) return;
        if (highlightedIndex >= 0 && timeline.Children[highlightedIndex * 2] is Button previous)
        {
            previous.ClearValue(Control.BorderBrushProperty);
            previous.ClearValue(Control.BorderThicknessProperty);
            AutomationProperties.SetItemStatus(previous, "");
        }
        highlightedIndex = index;
        if (index < 0 || index * 2 >= timeline.Children.Count || timeline.Children[index * 2] is not Button active) return;
        active.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        active.BorderThickness = new(3);
        AutomationProperties.SetItemStatus(active, localization.Text("Running"));
        active.StartBringIntoView();
    }

    private Button SceneCard(Workflow flow, int index, int start)
    {
        var step = flow.Steps[index];
        var style = SceneStyles.First(scene => scene.Name == step.Scene);
        string title = SceneTitle(step.Scene);
        string startLabel = localization.Text("Starts at") + " " + ClockText(start);
        var content = Column(5);
        var number = Text((index + 1).ToString("00"));
        number.Opacity = .7; number.FontSize = 12;
        var icon = SceneIcon(style, 21);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(WithTrailing(icon, number));
        var sceneName = Heading(title);
        sceneName.MaxLines = 2; sceneName.TextWrapping = TextWrapping.Wrap; sceneName.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(sceneName);
        var time = Heading(ClockText(step.Seconds));
        time.FontSize = 22;
        content.Children.Add(time);
        var details = Text(startLabel + (step.Transition == "default" ? "" : "  ·  " + localization.Text(TransitionLabel(step.Transition))));
        details.FontSize = 12; details.Opacity = .78;
        content.Children.Add(details);
        var card = new Button { Width = 154, MinHeight = 144, Content = content, Padding = new(12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(card, $"{index + 1}. {title}, {ClockText(step.Seconds)}, {startLabel}");
        Help(card, localization.Text(ActionDescriptions["Edit step"]));
        card.Click += async (_, _) => await OpenWorkflowDialogAsync(() => EditStepAsync(flow, index));
        ConfigureDrag(card, "step", step.Id);
        ConfigureDrop(card, step.Id);
        return card;
    }

    private string SceneTitle(string scene) => localization.Text(StudioCatalog.Default.Scenes.FirstOrDefault(item => item.Name == scene)?.Title ?? scene);
    private static string ClockText(int seconds)
    {
        long value = Math.Abs((long)seconds);
        return $"{(seconds < 0 ? "+" : "")}{value / 60}:{value % 60:00}";
    }
}
