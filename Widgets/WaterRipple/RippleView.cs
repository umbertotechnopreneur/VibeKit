using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Storage.Pickers;
using Tools.Widgets;
using static Tools.Widgets.EffectStudioLayout;

namespace WaterRipple;

// Native presentation of the shared catalog and observable pipeline/settings state.
internal sealed class RippleView : Grid
{
    private readonly PipelineModel pipelines;
    private readonly StackPanel effects = new() { Spacing = 10 };
    private readonly StackPanel library = new() { Spacing = 12 };
    private readonly StackPanel rows = new() { Spacing = 0 };
    private readonly Dictionary<Pipeline, (Border Card, TextBlock Title, TextBlock Summary, Button More)> pipelineRows = new();
    private readonly TextBox name = new() { PlaceholderText = "Pipeline name", MaxLength = 80 };
    private readonly StackPanel editor = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ComboBox choice = new() { Header = "Pipeline", PlaceholderText = "Custom", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
    private Pipeline? renaming;
    private bool refreshing;

    internal RippleView(StudioModel model, StudioController controller, Window window, Grid root, ToggleSwitch effectSwitch)
    {
        pipelines = model.Pipelines;
        var displayPicker = new DisplayTargetPicker(WinRT.Interop.WindowNative.GetWindowHandle(window),
            () => controller.DisplaySelection, ids => model.Settings.TargetDisplays = ids,
            () => model.Settings.TargetDisplay);
        var settings = BuildSettings(model, displayPicker, effectSwitch);
        var diagnostics = new StackPanel { Spacing = 12 };
        var pages = new StudioPages(
            new StudioPage("Pipeline", "\uE9D9", effects),
            new StudioPage("Presets", "\uE81E", library),
            new StudioPage("Settings", "\uE713", settings, () =>
            {
                displayPicker.Refresh();
                controller.OpenSettingsCommand.Execute(null);
            }),
            new StudioPage("Diagnostics", "\uE9D9", diagnostics));
        Children.Add(pages);
        foreach (var scroll in pages.Scrollers) Ui.HoverScrollBar(scroll, scroll, root);
        controller.SettingsRequested += () => pages.Select("Settings");
        controller.DisplaysChanged += displayPicker.Refresh;
        pages.Select("Settings");
        var target = ApplyToPicker();
        Ui.Bind(target, ComboBox.SelectedIndexProperty, model.Settings, nameof(AppSettings.TargetIndex), BindingMode.TwoWay);
        effects.Children.Add(target);
        choice.ItemsSource = pipelines.Items;
        choice.SelectionChanged += (_, _) => { if (!refreshing && choice.SelectedItem is Pipeline p) pipelines.Select(p); };
        effects.Children.Add(choice);
        var pipelineHint = Ui.Text("Edits to saved pipelines are saved automatically. Duplicate a built-in to customize it.", 12);
        pipelineHint.Style = Ui.SecondaryTextStyle;
        effects.Children.Add(pipelineHint);
        foreach (var group in pipelines.Catalog.Groups)
        {
            var controls = new StackPanel { Spacing = 6 };
            if (group.Presets.Length > 0)
            {
                var filterChoice = new ComboBox { Header = "Preset", ItemsSource = new[] { "Custom" }.Concat(group.Presets.Select(p => p.Name)).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
                var description = Ui.Text("", 12);
                bool syncingFilter = false;
                void SyncFilter()
                {
                    syncingFilter = true;
                    filterChoice.SelectedIndex = pipelines.FilterPresetIndex(group);
                    description.Text = filterChoice.SelectedIndex == 0 ? "Adjust the values below." : group.Presets[filterChoice.SelectedIndex - 1].Description;
                    syncingFilter = false;
                }
                filterChoice.SelectionChanged += (_, _) =>
                {
                    if (!syncingFilter && filterChoice.SelectedIndex > 0) pipelines.ApplyFilterPreset(group.Presets[filterChoice.SelectedIndex - 1]);
                };
                void FilterChanged(object? sender, PropertyChangedEventArgs args) => SyncFilter();
                Loaded += (_, _) => model.Settings.PropertyChanged += FilterChanged;
                Unloaded += (_, _) => model.Settings.PropertyChanged -= FilterChanged;
                Ui.Bind(filterChoice, Control.IsEnabledProperty, model.Settings, pipelines.Catalog.Parameters.First(p => p.Key == group.Enabled).WindowsProperty!);
                SyncFilter(); controls.Children.Add(filterChoice); controls.Children.Add(description);
            }
            foreach (var key in group.Keys)
            {
                var p = pipelines.Catalog.Parameters.First(p => p.Key == key);
                if (p.WindowsProperty is null) continue;
                if (p.IsBoolean) Ui.Toggle(controls, p.Title, model.Settings, p.WindowsProperty);
                else
                {
                    var slider = Ui.Slider(controls, p.WindowsTitle ?? p.Title, model.Settings, p.WindowsProperty,
                        (p.WindowsMin ?? p.Min) * p.WindowsScale, (p.WindowsMax ?? p.Max) * p.WindowsScale,
                        p.Step * p.WindowsScale, "");
                    if (group.Enabled is not null)
                        Ui.Bind(slider, Control.IsEnabledProperty, model.Settings, pipelines.Catalog.Parameters.First(p => p.Key == group.Enabled).WindowsProperty!);
                }
            }
            var expander = new Expander { Header = group.Title, Content = controls, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = group.Enabled is null };
            if (group.Enabled is not null)
            {
                var enabled = pipelines.Catalog.Parameters.First(p => p.Key == group.Enabled);
                var header = new Grid { ColumnSpacing = 12 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock { Text = group.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var toggle = new ToggleSwitch
                {
                    OnContent = null, OffContent = null, MinWidth = 0, Width = 40,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Enable " + group.Title);
                Ui.Bind(toggle, ToggleSwitch.IsOnProperty, model.Settings, enabled.WindowsProperty!, BindingMode.TwoWay);
                SetColumn(toggle, 1);
                header.Children.Add(toggle);
                expander.Header = header;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, group.Title);
            }
            effects.Children.Add(expander);
        }
        var persistenceError = Ui.Text("", 12);
        Ui.Bind(persistenceError, TextBlock.TextProperty, model.State, nameof(AppState.PersistenceError));
        effects.Children.Add(persistenceError);
        var capability = Ui.Text("", 12); Ui.Bind(capability, TextBlock.TextProperty, model.State, nameof(AppState.CapabilityText)); diagnostics.Children.Add(capability);
        diagnostics.Children.Add(Ui.DiagnosticRows(model.State, nameof(AppState.MetricsText)));
        diagnostics.Children.Add(Ui.DiagnosticRows(model.State, nameof(AppState.ResourcesText)));
        diagnostics.Children.Add(new Button { Content = "Recheck GPU", Command = controller.RestartCommand });
        diagnostics.Children.Add(JokeSection(window, model.Settings.ShowGpuJokes,
            enabled => model.Settings.ShowGpuJokes = enabled, GpuJokes.Caption));
        diagnostics.Children.Add(SettingsHint("If Windows asks to hide the colored capture border, you can keep the border visible."));


        var libraryIntro = Ui.Text("Pipelines are saved sets of ripple and filter settings.", 13); libraryIntro.Style = Ui.SecondaryTextStyle; library.Children.Add(libraryIntro);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(MakeActionButton("New pipeline", "\uE710", () => { renaming = null; name.Text = ""; editor.Visibility = Visibility.Visible; name.Focus(FocusState.Programmatic); }));
        var import = MakeActionButton("Import…", "\uE896", async () =>
        {
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                var file = await picker.PickSingleFileAsync(); if (file is not null) pipelines.Import(file.Path);
            }
            catch (Exception ex) { pipelines.ReportError(ex.Message); }
        });
        var export = MakeActionButton("Export…", "\uE898", async () =>
        {
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = "ripple-pipelines" };
                picker.FileTypeChoices.Add("Pipeline library", new[] { ".json" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                var file = await picker.PickSaveFileAsync(); if (file is not null) pipelines.Export(file.Path);
            }
            catch (Exception ex) { pipelines.ReportError(ex.Message); }
        });
        actions.Children.Add(import); actions.Children.Add(export); library.Children.Add(actions);
        editor.Children.Add(name);
        var editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        editActions.Children.Add(MakeButton("Save", () =>
        {
            if (renaming is null) pipelines.Create(name.Text); else pipelines.Rename(renaming, name.Text);
            if (pipelines.Error.Length == 0) editor.Visibility = Visibility.Collapsed;
        }));
        editActions.Children.Add(MakeButton("Cancel", () => editor.Visibility = Visibility.Collapsed));
        editor.Children.Add(editActions); library.Children.Add(editor); library.Children.Add(rows);
        library.Children.Add(Ui.Text("Library: " + pipelines.FilePath, 12));
        foreach (var panel in new[] { effects, library })
        {
            var error = Ui.Text("", 13); Ui.Bind(error, TextBlock.TextProperty, pipelines, nameof(PipelineModel.Error)); panel.Children.Add(error);
        }
        void Refresh()
        {
            refreshing = true;
            choice.SelectedItem = pipelines.Selected;
            choice.PlaceholderText = pipelines.CurrentName;
            foreach (var removed in pipelineRows.Keys.Except(pipelines.Items).ToArray())
            {
                rows.Children.Remove(pipelineRows[removed].Card);
                pipelineRows.Remove(removed);
            }
            for (int index = 0; index < pipelines.Items.Count; index++)
            {
                var pipeline = pipelines.Items[index];
                if (!pipelineRows.TryGetValue(pipeline, out var cached))
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = new StackPanel { Spacing = 4 };
                    var title = Ui.Text("", 18);
                    var summary = Ui.Text("", 12);
                    label.Children.Add(title);
                    label.Children.Add(summary);
                    var open = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Padding = new Thickness(4, 9, 4, 9) };
                    // Applying a pipeline keeps its library row, focus and viewport in place.
                    open.Click += (_, _) => pipelines.Select(pipeline);
                    row.Children.Add(open);
                    var menu = new MenuFlyout();
                    MenuFlyoutItem Action(string actionTitle, Action action)
                    { var item = new MenuFlyoutItem { Text = actionTitle }; item.Click += (_, _) => action(); return item; }
                    menu.Items.Add(Action("Duplicate", () => pipelines.Duplicate(pipeline)));
                    if (!pipeline.IsBuiltIn)
                    {
                        menu.Items.Add(Action("Rename", () => { renaming = pipeline; name.Text = pipeline.Name; editor.Visibility = Visibility.Visible; }));
                        menu.Items.Add(Action("Delete", () => pipelines.Delete(pipeline)));
                    }
                    var more = new Button { Content = "⋮", Flyout = menu, VerticalAlignment = VerticalAlignment.Center,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                    SetColumn(more, 1); row.Children.Add(more);
                    cached = (new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 191, 187, 178)) }, title, summary, more);
                    pipelineRows.Add(pipeline, cached);
                }
                cached.Title.Text = (pipeline == pipelines.Selected ? "✓ " : "") + pipeline.Name;
                cached.Summary.Text = pipelines.Summary(pipeline);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cached.More, "Actions for " + pipeline.Name);
                // Parameter/selection changes retain the controls, keyboard focus and flyouts.
                int previousIndex = rows.Children.IndexOf(cached.Card);
                if (previousIndex == index) continue;
                if (previousIndex >= 0) rows.Children.RemoveAt(previousIndex);
                rows.Children.Insert(index, cached.Card);
            }
            refreshing = false;
        }
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(PipelineModel.Items) or nameof(PipelineModel.Selected)) Refresh();
        }
        Loaded += (_, _) => { pipelines.PropertyChanged += Changed; Refresh(); };
        Unloaded += (_, _) => pipelines.PropertyChanged -= Changed;
        Refresh();
    }
    private static FrameworkElement BuildSettings(StudioModel model, DisplayTargetPicker displayPicker, ToggleSwitch effectSwitch)
    {
        var theme = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" } };
        Ui.Bind(theme, ComboBox.SelectedIndexProperty, model.Settings, nameof(AppSettings.ThemeIndex), BindingMode.TwoWay);
        var acrylic = new ToggleSwitch();
        Ui.Bind(acrylic, ToggleSwitch.IsOnProperty, model.Settings, nameof(AppSettings.UseAcrylic), BindingMode.TwoWay);

        var startup = new ToggleSwitch();
        Ui.Bind(startup, ToggleSwitch.IsOnProperty, model.State, nameof(AppState.StartWithWindows), BindingMode.TwoWay);
        Ui.Bind(startup, Control.IsEnabledProperty, model.State, nameof(AppState.CanManageStartup));
        var startupStatus = SettingsHint("");
        Ui.Bind(startupStatus, TextBlock.TextProperty, model.State, nameof(AppState.StartupStatus), converter: new StartupDescriptionConverter());

        var recovery = new ToggleSwitch();
        Ui.Bind(recovery, ToggleSwitch.IsOnProperty, model.Settings, nameof(AppSettings.AutoRecover), BindingMode.TwoWay);
        var recoveryTiming = SettingsHint("At least 1 min delay · 30 s stable");
        recoveryTiming.TextAlignment = TextAlignment.Right;

        var settings = SettingsPage();
        settings.Children.Add(RowGroup("Appearance",
            PreferenceRow("Effect", "Enable or disable the water ripple effect.", effectSwitch),
            PreferenceRow("Theme", "App theme.", theme),
            PreferenceRow("Acrylic background", "Use a translucent background for this window.", acrylic)));
        settings.Children.Add(displayPicker);
        settings.Children.Add(RowGroup("Startup",
            PreferenceRow("Start with Windows", startupStatus, startup),
            PreferenceRow("Resume automatically", "Resume after a temporary performance pause.", recovery)));
        settings.Children.Add(RowGroup("Performance protection",
            Ui.PreferenceSlider("Animation limit", "Maximum animation frame rate for the effect.",
                model.Settings, nameof(AppSettings.AnimationFps), 15, 120, 1, "fps"),
            Ui.PreferenceSlider("Capture limit", "Maximum screen capture frame rate.",
                model.Settings, nameof(AppSettings.CaptureFps), 5, 60, 1, "fps"),
            PreferenceRow("Recovery", "Resume only after the system becomes stable again.", recoveryTiming)));
        return settings;
    }

    private sealed class StartupDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => value switch
        {
            "Water Ripple will start in the notification area and apply click ripples when you sign in." => "Apply click ripples at sign-in.",
            "Water Ripple will open only when you launch it." => "Open only when you launch it.",
            _ => value
        };

        public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
    }

    private static Button MakeButton(string title, Action action)
    { var button = new Button { Content = title }; button.Click += (_, _) => action(); return button; }
    private static Button MakeActionButton(string title, string glyph, Action action)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        label.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        label.Children.Add(new TextBlock { Text = title });
        var button = new Button { Content = label };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        button.Click += (_, _) => action(); return button;
    }
}
