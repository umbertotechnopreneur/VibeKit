using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private sealed record SceneStyle(string Name, string Glyph, string Subtitle, uint Light, uint Dark);
    private static readonly SceneStyle[] SceneStyles =
    [
        new("Test card", "\uE7F4", "Retro standby", 0x7243A1, 0xC9A2F0),
        new("Intro", "\uE768", "Project introduction", 0x0D6B64, 0x5ED4C7),
        new("That's me", "\uE77B", "Full-screen webcam", 0x1A61A8, 0x86B9F4),
        new("Software Demo", "\uE7F8", "Screen + corner webcam", 0x4C4AA9, 0xABA7FF),
        new("Screen Only", "\uE7F4", "Screen, no webcam", 0x00677E, 0x69C7E4),
        new("Detail", "\uE8A3", "Screen zoom & presets", 0x964700, 0xF9B367),
        new("Comparison A-B", "\uE89F", "Two apps side by side", 0x8C3E83, 0xE3A1D9),
        new("Chapter", "\uE8FD", "Next section", 0x3F6A25, 0xA5CF7B),
        new("Hardware Demo", "\uE90F", "Screen, webcam + table", 0x775900, 0xE1C266),
        new("On the Table", "\uE714", "Table + corner webcam", 0x994332, 0xF3A18F),
        new("Thank you", "\uE735", "You, links + credits", 0x9E325D, 0xF59DBD),
        new("Privacy", "\uE72E", "Video hidden · audio as set", 0x49616D, 0xA3BECD)
    ];

    private Action? refreshCameraPresets, refreshScreenPresets;

    private UIElement BuildCameraAndScenes()
    {
        var left = Column();
        var device = Column();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cameras, "Camera device");
        cameras.PlaceholderText = "Choose a camera";
        cameras.MinWidth = 0;
        device.Children.Add(Field(cameras, "Camera device", "Refresh the list, choose a camera, then open its controls. Pick the video source separately in OBS."));
        var cameraActions = new ActionStrip();
        cameraActions.Children.Add(ActionButton("Refresh cameras", RefreshCamerasAsync));
        var openCamera = ActionButton("Open controls", OpenCameraAsync);
        openCamera.IsEnabled = false;
        cameras.SelectionChanged += (_, _) => openCamera.IsEnabled = cameras.SelectedItem is CameraDevice;
        cameraActions.Children.Add(openCamera);
        device.Children.Add(cameraActions);
        device.Children.Add(cameraNotice);
        left.Children.Add(Card(device));
        var framing = Column();
        foreach (var property in new[] { CameraProperty.Zoom, CameraProperty.Pan, CameraProperty.Tilt })
        {
            var slider = Slider(0, 1, 0, property + " · device units");
            slider.ValueChanged += (_, _) =>
            {
                if (syncing || cameraValues.Count == 0) return;
                pendingCamera[property] = (int)Math.Round(slider.Value);
                cameraTimer.Start();
            };
            cameraSliders[property] = slider;
            string help = property switch
            {
                CameraProperty.Zoom => "Change the camera’s magnification. The available range and step size depend on the selected camera.",
                CameraProperty.Pan => "Move the camera framing horizontally using the driver’s range. Some cameras require zoom before the framing can move.",
                _ => "Move the camera framing vertically using the driver’s range. Some cameras require zoom before the framing can move."
            };
            framing.Children.Add(SliderField(slider, property.ToString(), help));
        }
        framing.Children.Add(ActionButton("Reset camera framing", () => CameraFrameAsync(null)));
        left.Children.Add(DetailSection("Camera framing", "Zoom, pan and tilt on supported cameras.", framing));
        left.Children.Add(Card(BuildPresetPicker("Camera presets", ["Wide", "Medium", "Close-up"],
            settings.CameraPresets, "Apply preset", "Save preset",
            () => !cameraBusy && !cameraSuspended && cameraSliders.Values.Any(slider => slider.IsEnabled),
            () => new((int)cameraSliders[CameraProperty.Zoom].Value, (int)cameraSliders[CameraProperty.Pan].Value,
                (int)cameraSliders[CameraProperty.Tilt].Value),
            frame => CameraFrameAsync(frame),
            frame => $"Zoom {frame.Zoom} · Pan {frame.Pan} · Tilt {frame.Tilt}",
            out refreshCameraPresets)));
        var blurControls = Column(4);
        blurControls.Children.Add(WithTrailing(FieldLabel(blur, "Background blur",
            "Soften the webcam background in OBS. The blur filter must be available for this scene."), blur));
        blurControls.Children.Add(SliderField(blurIntensity, "Blur intensity",
            "1 = light, 20 = strong. Use Background blur ON/OFF to enable or disable the effect."));
        left.Children.Add(Card(blurControls));
        blur.Toggled += (_, _) => QueueEffect("blur", async token => SetState(await service!.SetBlurEnabledAsync(blur.IsOn, token)));
        blurIntensity.ValueChanged += (_, _) => QueueEffect("blur-intensity", async token => SetState(await service!.SetBlurIntensityAsync((int)blurIntensity.Value, token)));

        var right = Column();
        right.Children.Add(Heading("Scenes"));
        var sceneTiles = new List<FrameworkElement>();
        for (int index = 0; index < SceneStyles.Length; index++)
        {
            var style = SceneStyles[index];
            var definition = StudioCatalog.Default.Scenes.First(scene => scene.Name == style.Name);
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new(12), CornerRadius = new(8), IsEnabled = false };
            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new() { Width = new(30) });
            content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = style.Glyph, FontFamily = new("Segoe Fluent Icons"), FontSize = 30 };
            content.Children.Add(icon);
            var labels = Column(6);
            labels.Children.Add(new TextBlock { Text = definition.Title, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock { Text = style.Subtitle, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(labels, 1); content.Children.Add(labels);
            button.Content = content;
            AddSceneHelp(button, style, definition, "Select scene");
            button.Click += async (_, _) =>
            {
                bool takeover = LiveCheckActive;
                InterruptLivePlayback("Manual scene selection.");
                await CommandAsync("scene", token => service!.SetSceneAsync(style.Name, token), takeover);
            };
            sceneTiles.Add(button);
            sceneButtons[style.Name] = button; sceneIcons[style.Name] = icon;
        }
        right.Children.Add(ResponsiveColumns(450, 2, sceneTiles.ToArray()));
        right.Children.Add(Text("Scene changes use OBS’s configured transition. Privacy hides video; audio keeps its current mute state."));
        return ResponsiveColumns(750, left, right);
    }

    private UIElement BuildEffects()
    {
        var content = Column();
        var zoom = SliderField(screenZoom, "Screen zoom",
            "Zoom stays between 1× and 4×. Horizontal and vertical positioning become available above 1×; presets keep the framing inside the captured screen.", "×");
        var x = SliderField(screenX, "Horizontal position", "Move the crop left or right after zooming in. Set the position to 0 to centre it horizontally.", "%");
        var y = SliderField(screenY, "Vertical position", "Move the crop up or down after zooming in. Set the position to 0 to centre it vertically.", "%");
        foreach (var slider in new[] { screenZoom, screenX, screenY }) slider.ValueChanged += (_, _) => QueueScreen();
        var reset = ActionButton("Reset screen · 1×, centred", () => { SetScreenValues(new()); QueueScreen(); return Task.CompletedTask; });
        screenZoom.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => reset.IsEnabled = screenZoom.IsEnabled);
        reset.IsEnabled = screenZoom.IsEnabled;
        var screen = ResponsiveColumns(415, 3, zoom, x, y);
        var presets = BuildPresetPicker("Screen presets", ["Overview", "Detail", "Close-up"],
            settings.ScreenPresets, "Apply screen preset", "Save position", () => screenZoom.IsEnabled,
            () => new(screenZoom.Value, screenX.Value, screenY.Value),
            frame => { SetScreenValues(frame); QueueScreen(); return Task.CompletedTask; },
            frame => $"Zoom {frame.Zoom:0.##}× · X {frame.X:0}% · Y {frame.Y:0}%", out refreshScreenPresets);
        content.Children.Add(Card(Heading("Screen framing"), Text("Choose a scene with a captured screen."),
            screen, reset, presets));
        content.Children.Add(Heading("Vignettes"));
        var effects = new List<FrameworkElement>();
        foreach (var definition in StudioCatalog.Default.Vignettes)
        {
            var toggle = new ToggleSwitch { IsEnabled = false };
            var intensity = Slider(0, 100, 25, definition.Label + " intensity");
            vignettes[definition.Key] = (toggle, intensity);
            void Changed() => QueueEffect("vignette-" + definition.Key, async token =>
                SetState(await service!.SetVignetteAsync(definition.Key, toggle.IsOn, intensity.Value, token)));
            toggle.Toggled += (_, _) => Changed();
            intensity.ValueChanged += (_, _) => Changed();
            var switchRow = WithTrailing(FieldLabel(toggle, definition.Label,
                "Each vignette stays inside its own video box. OFF keeps the selected intensity."), toggle);
            var strength = SliderField(intensity, "Intensity",
                "Vignette darkens only the edges. 0% = clear, 100% = strongest.", "%");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(intensity, definition.Label + " intensity");
            effects.Add(Card(switchRow, strength));
        }
        content.Children.Add(ResponsiveColumns(490, 3, effects.ToArray()));
        return content;
    }

    private StackPanel BuildPresetPicker<T>(string label, string[] names, Dictionary<string, T> slots,
        string recallAction, string saveAction, Func<bool> ready, Func<T> current, Func<T, Task> recall,
        Func<T, string> describe, out Action refresh) where T : class
    {
        var select = new ComboBox { MinWidth = 136 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(select, "PresetSelector");
        void TranslateChoices()
        {
            int selection = Math.Max(0, select.SelectedIndex);
            select.ItemsSource = names.Select(localization.Text).ToArray();
            select.SelectedIndex = selection;
        }
        TranslateChoices();
        localization.LanguageChanged += TranslateChoices;
        var applyFrame = new Button { Content = "Recall" };
        var saveFrame = new Button();
        ActionHelp(applyFrame, recallAction);
        ActionHelp(saveFrame, saveAction);
        var summary = Text("");
        summary.VerticalAlignment = VerticalAlignment.Center;
        summary.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        bool applying = false;
        void Refresh()
        {
            if (select.SelectedIndex < 0) return;
            string name = names[select.SelectedIndex];
            bool saved = slots.TryGetValue(name, out var frame);
            summary.Text = saved ? describe(frame!) : "No framing saved yet.";
            saveFrame.Content = saved ? "Replace with current" : "Save current framing";
            applyFrame.IsEnabled = ready() && saved && !applying;
            saveFrame.IsEnabled = ready() && !applying;
        }
        applyFrame.Click += async (_, _) =>
        {
            if (!ready() || select.SelectedIndex < 0 || !slots.TryGetValue(names[select.SelectedIndex], out var frame)) return;
            applying = true;
            Refresh();
            try { await recall(frame); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Fail("preset.recall", error); }
            finally { applying = false; if (!closed) Refresh(); }
        };
        saveFrame.Click += (_, _) =>
        {
            if (!ready() || select.SelectedIndex < 0) return;
            string name = names[select.SelectedIndex];
            slots.TryGetValue(name, out var previous);
            slots[name] = current();
            try
            {
                LocalSettings.Save(settings);
                notice.Text = "Preset saved:" + " " + localization.Text(name);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (previous is null) slots.Remove(name);
                else slots[name] = previous;
                Fail("preset.save", error);
            }
            Refresh();
        };
        select.SelectionChanged += (_, _) => Refresh();
        refresh = Refresh;
        Refresh();
        var actions = new ActionStrip();
        actions.Children.Add(select);
        actions.Children.Add(applyFrame);
        actions.Children.Add(saveFrame);
        actions.Children.Add(summary);
        var content = Column(5);
        content.Children.Add(FieldLabel(select, label,
            "Choose a slot to see its saved framing. Recall applies it. Saving stores your current adjustments in the selected slot."));
        content.Children.Add(actions);
        return content;
    }

    private void SetScreenValues(Framing values)
    {
        syncing = true;
        try { screenZoom.Value = values.Zoom; screenX.Value = values.X; screenY.Value = values.Y; }
        finally { syncing = false; }
    }

    private void QueueScreen()
    {
        if (syncing || state?.Screen is not { } screen) return;
        // Capture identity with the gesture; a later scene switch must not reuse its item ID.
        string scene = screen.SceneName;
        long item = screen.ItemId;
        var framing = new Framing(screenZoom.Value, screenX.Value, screenY.Value);
        screenX.IsEnabled = screenY.IsEnabled = framing.Zoom > 1 && state.ConnectionAvailable && !commandBusy;
        QueueEffect("screen", async token =>
        {
            if (state?.Screen?.SceneName != scene || state.Screen.ItemId != item) return;
            if (await service!.SetScreenFramingAsync(scene, item, framing.Zoom, framing.X, framing.Y, token))
                SetState(await service.GetStatusAsync(token));
        });
    }
}
