using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using VibeKit.StudioControls.Core;
using Size = global::Windows.Foundation.Size;
using Rect = global::Windows.Foundation.Rect;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private static readonly Dictionary<string, string> ActionDescriptions = new(StringComparer.Ordinal)
    {
        ["+10 s"] = "Add 10 seconds to the current scene without pausing.",
        ["+20 s"] = "Add 20 seconds to the current scene without pausing.",
        ["About"] = "Open information about Studio Controls and its author.",
        ["Add scene"] = "Click to add this scene at the end, or drag it into the sequence.",
        ["Apply preset"] = "Recall the camera framing saved in this slot.",
        ["Apply screen preset"] = "Apply this saved zoom and position to the active OBS scene’s screen capture.",
        ["Cancel"] = "Go back without applying these changes.",
        ["Cancel recording countdown"] = "Cancel the pending start before OBS begins recording.",
        ["Choose logo…"] = "Choose a local image for the project cards.",
        ["Close"] = "Close this window.",
        ["Connect"] = "Connect to the running OBS WebSocket server using the settings above.",
        ["Dark"] = "Use the dark theme for the control panel.",
        ["Desktop Audio"] = "Mute or unmute the desktop audio input in OBS.",
        ["Discard"] = "Leave without saving the unsaved card changes.",
        ["Disconnect"] = "Disconnect the panel from OBS. Recording continues in OBS.",
        ["Duplicate"] = "Make a copy of the selected workflow with its durations, notes and transitions.",
        ["Download UlanziDeck"] = "Open the UlanziDeck download page for the D200H.",
        ["Learn more"] = "Open an illustrated guide to the D200H keys during recording.",
        ["Use it as a secondary monitor"] = "Show the scene countdown on the dock.",
        ["Edit project"] = "Rename the selected project.",
        ["Edit step"] = "Edit this step’s duration, transition or note. Drag to change its position.",
        ["Edit workflow"] = "Rename the selected workflow.",
        ["Export diagnostics…"] = "Export diagnostic logs without passwords, settings or project content.",
        ["Intro sound"] = "Enable or disable the intro sound in OBS when applying the project.",
        ["Intro sound volume"] = "Set the volume of the project’s intro sound.",
        ["Library"] = "Choose, create or organize projects and their workflows.",
        ["Light"] = "Use the light theme for the control panel.",
        ["Mic/Aux"] = "Mute or unmute the microphone input in OBS.",
        ["Move left"] = "Place this step earlier in the sequence.",
        ["Move right"] = "Place this step later in the sequence.",
        ["New project"] = "Create a project with its own workflows and cards.",
        ["New workflow"] = "Add an empty workflow to the selected project.",
        ["Next"] = "Advance the workflow immediately to its next step.",
        ["Open"] = "Open the selected project and workflow.",
        ["Open controls"] = "Open the selected camera’s Windows controls for zoom, pan and tilt.",
        ["Open timer"] = "Open the separate presenter timer so you can place it outside the capture.",
        ["Pause recording"] = "Pause the OBS recording. A running OBS workflow stops.",
        ["Pause workflow"] = "Pause the countdown and hold the current scene. OBS keeps recording if it was recording.",
        ["Play in OBS"] = "Run the workflow and switch OBS scenes on time. Start the recording separately.",
        ["Prepare OBS scene collection…"] = "Create a new JSON scene collection to import into OBS.",
        ["Refresh cameras"] = "Find the cameras available to Windows and refresh the list.",
        ["Rehearse"] = "Run the timer and cues locally, without switching scenes in OBS.",
        ["Remove"] = "Confirm removal of the selected item.",
        ["Remove logo"] = "Clear the logo from the project form. Apply to update the OBS cards.",
        ["Remove project"] = "Remove this project and its workflows from the library?",
        ["Remove workflow"] = "Remove this workflow from the project?",
        ["Reset camera framing"] = "Restore the camera driver’s default zoom, pan and tilt. Saved presets are kept.",
        ["Reset screen · 1×, centred"] = "Adjustments apply to the recorded video. Reset screen returns to the full view.",
        ["Resume recording"] = "Continue the paused OBS recording in the same file.",
        ["Resume workflow"] = "Continue the countdown from where it paused.",
        ["Save"] = "Accept and save these changes.",
        ["Save position"] = "Replace this preset with the current screen zoom and position.",
        ["Save preset"] = "Replace this preset with the camera’s current zoom, pan and tilt.",
        ["Save project & apply to OBS"] = "Open OBS, then Apply. The local logo is copied beside the panel for portability. Scene changes remain manual.",
        ["Select scene"] = "Switch immediately to this scene in OBS. Manual changes stop a running workflow.",
        ["Start recording"] = "Start recording the current OBS scene. Uses the countdown if enabled.",
        ["Start timer"] = "Start a standalone countdown with no scene changes.",
        ["Stop workflow"] = "End the workflow. A running OBS recording continues.",
        ["System"] = "Follow the Windows appearance setting.",
        ["Time target"] = "Set a reference duration for the workflow. Scene durations stay as set.",
        ["Timer settings"] = "Choose what the timer shows and configure its countdown signals.",
        ["■ Stop and save"] = "Stop the current recording and let OBS save the file.",
    };

    private sealed record ControlHelp(string Text);
    private static readonly ConditionalWeakTable<FrameworkElement, ControlHelp> HelpSources = new();

    private static void Help(FrameworkElement control, string description)
    {
        if (HelpSources.TryGetValue(control, out var previous) && previous.Text == description) return;
        HelpSources.Remove(control);
        HelpSources.Add(control, new ControlHelp(description));
        if (!Equals(ToolTipService.GetToolTip(control), description))
            ToolTipService.SetToolTip(control, description);
        if (AutomationProperties.GetHelpText(control) != description)
            AutomationProperties.SetHelpText(control, description);
    }

    private static void ActionHelp(FrameworkElement control, string action) => Help(control, ActionDescriptions[action]);

    // Keep help outside the input so an unavailable control can still be explained.
    private static Grid FieldLabel(Control input, string label, string description)
    {
        var caption = Text(label);
        caption.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLabeledBy(input, caption);
        AutomationProperties.SetName(input, label);
        AutomationProperties.SetHelpText(input, description);
        var details = Text(description);
        details.MaxWidth = 320;
        var flyout = new Flyout { Content = details };
        var info = new Button
        {
            Content = new FontIcon { Glyph = "\uE946", FontSize = 14 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Padding = new(4), MinWidth = 26, MinHeight = 26, Flyout = flyout
        };
        AutomationProperties.SetName(info, "Info · " + label);
        AutomationProperties.SetHelpText(info, description);
        var tooltipText = Text(description);
        tooltipText.MaxWidth = 300;
        ToolTipService.SetToolTip(info, new ToolTip { Content = tooltipText });
        info.RegisterPropertyChangedCallback(AutomationProperties.HelpTextProperty, (_, _) =>
            tooltipText.Text = AutomationProperties.GetHelpText(info));
        flyout.Opening += (_, _) => details.Text = AutomationProperties.GetHelpText(info);
        var heading = WithTrailing(caption, info);
        heading.ColumnSpacing = 3;
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        return heading;
    }

    private static StackPanel Field(Control input, string label, string description)
    {
        var field = Column(2);
        field.Children.Add(FieldLabel(input, label, description));
        field.Children.Add(input);
        return field;
    }

    private static StackPanel SliderField(Slider slider, string label, string description, string unit = "")
    {
        var value = Text("");
        value.VerticalAlignment = VerticalAlignment.Center;
        value.Tag = NativeLocalization.UserText;
        void ShowValue() => value.Text = slider.IsEnabled ? slider.Value.ToString("0.##") + unit : "—";
        slider.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => ShowValue());
        slider.ValueChanged += (_, _) => ShowValue();
        ShowValue();
        var field = Column(0);
        field.Children.Add(WithTrailing(FieldLabel(slider, label, description), value));
        field.Children.Add(slider);
        return field;
    }

    private static Border Card(params FrameworkElement[] children)
    {
        var content = Column(8);
        foreach (var child in children) content.Children.Add(child);
        return new Border
        {
            Child = content, Style = (Style)Application.Current.Resources["PanelCardStyle"]
        };
    }

    private void AddSceneHelp(Button button, SceneStyle style, SceneDefinition scene, string action)
    {
        var labels = Column(4);
        var heading = Heading(scene.Title);
        heading.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(heading);
        labels.Children.Add(Text(style.Subtitle));
        labels.Children.Add(Text(scene.Description));
        labels.Children.Add(Text(ActionDescriptions[action]));
        var content = new Grid { MaxWidth = 340, ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new());
        var icon = SceneIcon(style, 26);
        icon.VerticalAlignment = VerticalAlignment.Top;
        content.Children.Add(icon);
        Grid.SetColumn(labels, 1);
        content.Children.Add(labels);
        localization.Attach(content);
        AutomationProperties.SetHelpText(button, scene.Description + "\n" + ActionDescriptions[action]);
        ToolTipService.SetToolTip(button, new ToolTip { Content = content });
    }

    private static Expander DetailSection(string title, string description, FrameworkElement body, bool open = false)
    {
        var header = Column(3);
        var heading = Heading(title);
        heading.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(heading);
        var subtitle = Text(description);
        subtitle.FontSize = 12;
        subtitle.Opacity = .78;
        header.Children.Add(subtitle);
        var section = Section(title, body, open);
        section.Header = header;
        AutomationProperties.SetName(section, title);
        return section;
    }

    private static Grid ResponsiveColumns(double breakpoint, params FrameworkElement[] items) =>
        ResponsiveColumns(breakpoint, items.Length, items);

    private static Grid ResponsiveColumns(double breakpoint, int maximum, params FrameworkElement[] items)
    {
        var layout = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        foreach (var item in items) layout.Children.Add(item);

        void Arrange(int columns)
        {
            if (layout.ColumnDefinitions.Count == columns) return;
            layout.ColumnDefinitions.Clear();
            layout.RowDefinitions.Clear();
            for (int index = 0; index < columns; index++) layout.ColumnDefinitions.Add(new());
            for (int index = 0; index < items.Length; index++)
            {
                if (index % columns == 0) layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Grid.SetColumn(items[index], index % columns);
                Grid.SetRow(items[index], index / columns);
            }
        }

        Arrange(1);
        layout.SizeChanged += (_, e) =>
        {
            // The breakpoint is the width needed for a pair, including the gutter.
            int count = (int)((e.NewSize.Width + layout.ColumnSpacing) / ((breakpoint + layout.ColumnSpacing) / 2));
            Arrange(Math.Clamp(count, 1, maximum));
        };
        return layout;
    }

    private void SetDialogHelp(ContentDialog dialog, string primary, string close, string secondary = "Remove")
    {
        dialog.Opened += (_, _) =>
        {
            void Visit(DependencyObject node)
            {
                if (node is Button button)
                {
                    string key = button.Name switch
                    {
                        "PrimaryButton" => primary, "CloseButton" => close, "SecondaryButton" => secondary, _ => ""
                    };
                    if (ActionDescriptions.TryGetValue(key, out string? help))
                        Help(button, localization.Text(help));
                }
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                    Visit(VisualTreeHelper.GetChild(node, index));
            }
            Visit(dialog);
        };
    }

    private sealed class ActionStrip : Panel
    {
        private const double Gap = 8;

        protected override Size MeasureOverride(Size availableSize) => Layout(availableSize, arrange: false);
        protected override Size ArrangeOverride(Size finalSize)
        {
            Layout(finalSize, arrange: true);
            return finalSize;
        }

        private Size Layout(Size available, bool arrange)
        {
            double x = 0, y = 0, rowHeight = 0, widest = 0;
            foreach (UIElement child in Children)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                if (!arrange) child.Measure(new Size(available.Width, double.PositiveInfinity));
                Size size = child.DesiredSize;
                if (x > 0 && x + size.Width > available.Width)
                {
                    x = 0;
                    y += rowHeight + Gap;
                    rowHeight = 0;
                }
                if (arrange) child.Arrange(new Rect(x, y, size.Width, size.Height));
                widest = Math.Max(widest, x + size.Width);
                x += size.Width + Gap;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            return new Size(widest, y + rowHeight);
        }
    }
}
