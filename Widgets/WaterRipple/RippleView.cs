using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Windows.Storage.Pickers;

namespace WaterRipple;

// Native presentation of the shared catalog and observable pipeline/settings state.
internal sealed class RippleView : Grid
{
    private readonly PipelineModel pipelines;
    private readonly StackPanel effects = new() { Spacing = 16 };
    private readonly StackPanel library = new() { Spacing = 12 };
    private readonly StackPanel rows = new() { Spacing = 2 };
    private readonly TextBox name = new() { PlaceholderText = "Pipeline name", MaxLength = 80 };
    private readonly StackPanel editor = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ComboBox choice = new() { Header = "Pipeline", PlaceholderText = "Custom", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
    private readonly ToggleButton effectTab = new() { Content = "Effect", IsChecked = true };
    private readonly ToggleButton pipelineTab = new() { Content = "Pipelines" };
    private Pipeline? renaming;
    private bool refreshing;

    internal RippleView(StudioModel model, StudioController controller, Window window)
    {
        pipelines = model.Pipelines;
        Margin = new Thickness(0, 20, 0, 8);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 16) };
        tabs.Children.Add(effectTab); tabs.Children.Add(pipelineTab);
        tabs.Children.Add(new Button { Content = "Settings", Command = controller.OpenSettingsCommand });
        tabs.Children.Add(new Button { Content = "Minimize to tray", Command = controller.MinimizeToTrayCommand });
        Children.Add(tabs);
        var pages = new Grid(); SetRow(pages, 1); Children.Add(pages);
        foreach (var panel in new[] { effects, library })
        {
            var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            pages.Children.Add(scroll);
            if (panel == library) scroll.Visibility = Visibility.Collapsed;
        }
        void Show(bool showLibrary)
        {
            effectTab.IsChecked = !showLibrary; pipelineTab.IsChecked = showLibrary;
            ((UIElement)pages.Children[0]).Visibility = showLibrary ? Visibility.Collapsed : Visibility.Visible;
            ((UIElement)pages.Children[1]).Visibility = showLibrary ? Visibility.Visible : Visibility.Collapsed;
        }
        effectTab.Click += (_, _) => Show(false); pipelineTab.Click += (_, _) => Show(true);
        var enable = new Button { Command = controller.ToggleScreenCommand, HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Ui.Bind(enable, Button.ContentProperty, model.State, nameof(AppState.ApplyButtonText));
        Ui.Bind(enable, Button.IsEnabledProperty, model.State, nameof(AppState.CanApply));
        effects.Children.Add(enable);
        choice.ItemsSource = pipelines.Items;
        choice.SelectionChanged += (_, _) => { if (!refreshing && choice.SelectedItem is Pipeline p) pipelines.Select(p); };
        effects.Children.Add(Card(choice));
        effects.Children.Add(Ui.Text("Saved pipelines update automatically. Duplicate a built-in pipeline to keep your changes.", 13));
        foreach (var group in pipelines.Catalog.Groups)
        {
            var controls = new StackPanel { Spacing = 12 };
            if (group.Enabled is not null)
            {
                var enabled = pipelines.Catalog.Parameters.First(p => p.Key == group.Enabled);
                Ui.Toggle(controls, "Enable", model.Settings, enabled.WindowsProperty!);
            }
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
            effects.Children.Add(new Expander { Header = group.Title, Content = controls, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = group.Enabled is null });
        }
        Ui.Toggle(effects, "Emergency exit: Ctrl + Alt + Shift + W", model.Settings, nameof(AppSettings.EmergencyShortcutEnabled));
        foreach (string property in new[] { nameof(AppState.EmergencyShortcutStatus), nameof(AppState.StatusMessage), nameof(AppState.PersistenceError) })
        {
            var text = Ui.Text("", 12); Ui.Bind(text, TextBlock.TextProperty, model.State, property); effects.Children.Add(text);
        }
        var diagnostics = new StackPanel { Spacing = 12 };
        var capability = Ui.Text("", 12); Ui.Bind(capability, TextBlock.TextProperty, model.State, nameof(AppState.CapabilityText)); diagnostics.Children.Add(capability);
        diagnostics.Children.Add(Ui.DiagnosticRows(model.State, nameof(AppState.MetricsText)));
        diagnostics.Children.Add(Ui.DiagnosticRows(model.State, nameof(AppState.ResourcesText)));
        diagnostics.Children.Add(new Button { Content = "Recheck GPU", Command = controller.RestartCommand });
        effects.Children.Add(new Expander { Header = "Diagnostics", Content = diagnostics, HorizontalAlignment = HorizontalAlignment.Stretch });

        library.Children.Add(Ui.Text("Pipelines are saved sets of ripple and filter settings.", 14));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(MakeButton("+ New pipeline", () => { renaming = null; name.Text = ""; editor.Visibility = Visibility.Visible; name.Focus(FocusState.Programmatic); }));
        var import = MakeButton("Import…", async () =>
        {
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                var file = await picker.PickSingleFileAsync(); if (file is not null) pipelines.Import(file.Path);
            }
            catch (Exception ex) { pipelines.ReportError(ex.Message); }
        });
        var export = MakeButton("Export…", async () =>
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
            rows.Children.Clear();
            foreach (var pipeline in pipelines.Items)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new StackPanel { Spacing = 4 };
                label.Children.Add(Ui.Text((pipeline == pipelines.Selected ? "✓ " : "") + pipeline.Name, 18));
                label.Children.Add(Ui.Text(pipelines.Summary(pipeline), 12));
                var open = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                open.Click += (_, _) => { pipelines.Select(pipeline); Show(false); };
                row.Children.Add(open);
                var menu = new MenuFlyout();
                MenuFlyoutItem Action(string title, Action action)
                { var item = new MenuFlyoutItem { Text = title }; item.Click += (_, _) => action(); return item; }
                menu.Items.Add(Action("Duplicate", () => pipelines.Duplicate(pipeline)));
                if (!pipeline.IsBuiltIn)
                {
                    menu.Items.Add(Action("Rename", () => { renaming = pipeline; name.Text = pipeline.Name; editor.Visibility = Visibility.Visible; }));
                    menu.Items.Add(Action("Delete", () => pipelines.Delete(pipeline)));
                }
                var more = new Button { Content = "⋮", Flyout = menu, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "Actions for " + pipeline.Name);
                SetColumn(more, 1); row.Children.Add(more); rows.Children.Add(Card(row));
            }
            refreshing = false;
        }
        void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(PipelineModel.Items)) Refresh(); }
        Loaded += (_, _) => { pipelines.PropertyChanged += Changed; Refresh(); };
        Unloaded += (_, _) => pipelines.PropertyChanged -= Changed;
        Refresh();
    }
    private static Button MakeButton(string title, Action action)
    { var button = new Button { Content = title }; button.Click += (_, _) => action(); return button; }
    private static Border Card(UIElement child) => new() { Child = child, Padding = new Thickness(14), CornerRadius = new CornerRadius(12), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(100, 70, 70, 70)) };
}
