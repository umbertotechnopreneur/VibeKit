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

    private UIElement BuildCameraAndScenes()
    {
        var columns = Columns(2);
        columns.MinWidth = 900;
        var left = Column();
        left.Children.Add(Heading("CAMERA · native Windows controls"));
        left.Children.Add(cameras);
        var cameraActions = Row();
        cameraActions.Children.Add(ActionButton("Refresh cameras", RefreshCamerasAsync));
        cameraActions.Children.Add(ActionButton("Open controls", OpenCameraAsync));
        left.Children.Add(cameraActions);
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
            left.Children.Add(slider);
        }
        left.Children.Add(ActionButton("Reset camera framing", () => CameraFrameAsync(null)));
        var presets = Columns(3);
        foreach (var (name, index) in new[] { ("Wide", 0), ("Medium", 1), ("Close-up", 2) })
        {
            var group = Column(6);
            group.Children.Add(ActionButton(name, async () =>
            {
                if (!settings.CameraPresets.TryGetValue(name, out var frame))
                    throw new InvalidOperationException("Adjust the camera and save this preset first.");
                await CameraFrameAsync(frame);
            }));
            group.Children.Add(ActionButton("Save preset", () =>
            {
                if (cameraValues.Count == 0) throw new InvalidOperationException("Open camera controls first.");
                settings.CameraPresets[name] = new((int)cameraSliders[CameraProperty.Zoom].Value,
                    (int)cameraSliders[CameraProperty.Pan].Value, (int)cameraSliders[CameraProperty.Tilt].Value);
                SavePreferences(); cameraNotice.Text = name + " camera preset saved.";
                return Task.CompletedTask;
            }));
            Grid.SetColumn(group, index); presets.Children.Add(group);
        }
        left.Children.Add(presets);
        left.Children.Add(cameraNotice);
        left.Children.Add(blur);
        left.Children.Add(blurIntensity);
        blur.Toggled += (_, _) => QueueEffect("blur", async token => SetState(await service!.SetBlurEnabledAsync(blur.IsOn, token)));
        blurIntensity.ValueChanged += (_, _) => QueueEffect("blur-intensity", async token => SetState(await service!.SetBlurIntensityAsync((int)blurIntensity.Value, token)));
        columns.Children.Add(left);

        var right = Column();
        right.Children.Add(Heading("SCENES · choose what to record"));
        var scenes = new Grid { RowSpacing = 10, ColumnSpacing = 10, MinWidth = 480 };
        scenes.ColumnDefinitions.Add(new()); scenes.ColumnDefinitions.Add(new());
        for (int row = 0; row < 6; row++) scenes.RowDefinitions.Add(new() { Height = GridLength.Auto });
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
            ToolTipService.SetToolTip(button, definition.Title + "\n" + definition.Description);
            button.Click += async (_, _) =>
            {
                bool takeover = LiveCheckActive;
                InterruptLivePlayback("Manual scene selection.");
                await CommandAsync("scene", token => service!.SetSceneAsync(style.Name, token), takeover);
            };
            Grid.SetRow(button, index / 2); Grid.SetColumn(button, index % 2); scenes.Children.Add(button);
            sceneButtons[style.Name] = button; sceneIcons[style.Name] = icon;
        }
        right.Children.Add(scenes);
        right.Children.Add(Text("Scene changes use OBS’s configured transition. Privacy hides video; audio keeps its current mute state."));
        Grid.SetColumn(right, 1); columns.Children.Add(right);
        return columns;
    }

    private UIElement BuildEffects()
    {
        var columns = Columns(2);
        columns.MinWidth = 850;
        var screen = Column();
        screen.Children.Add(Heading("SCREEN · zoom & position"));
        screen.Children.Add(screenZoom); screen.Children.Add(screenX); screen.Children.Add(screenY);
        foreach (var slider in new[] { screenZoom, screenX, screenY }) slider.ValueChanged += (_, _) => QueueScreen();
        screen.Children.Add(ActionButton("Reset screen · 1×, centred", () => { SetScreenValues(new()); QueueScreen(); return Task.CompletedTask; }));
        var presets = Columns(3);
        foreach (var (name, index) in new[] { ("Overview", 0), ("Detail", 1), ("Close-up", 2) })
        {
            var group = Column(6);
            group.Children.Add(ActionButton(name, () => { SetScreenValues(settings.ScreenPresets[name]); QueueScreen(); return Task.CompletedTask; }));
            group.Children.Add(ActionButton("Save position", () =>
            {
                if (state?.Screen is null) throw new InvalidOperationException("Choose a scene with a captured screen first.");
                settings.ScreenPresets[name] = new(screenZoom.Value, screenX.Value, screenY.Value);
                SavePreferences(); notice.Text = name + " screen position saved.";
                return Task.CompletedTask;
            }));
            Grid.SetColumn(group, index); presets.Children.Add(group);
        }
        screen.Children.Add(presets);
        screen.Children.Add(Text("Zoom stays between 1× and 4×. Horizontal and vertical positioning become available above 1×; presets keep the framing inside the captured screen."));
        columns.Children.Add(screen);
        var effects = Column();
        effects.Children.Add(Heading("VIGNETTES · independent controls"));
        foreach (var definition in StudioCatalog.Default.Vignettes)
        {
            var toggle = new ToggleSwitch { Header = definition.Label, IsEnabled = false };
            var intensity = Slider(0, 100, 25, definition.Label + " intensity");
            vignettes[definition.Key] = (toggle, intensity);
            void Changed() => QueueEffect("vignette-" + definition.Key, async token =>
                SetState(await service!.SetVignetteAsync(definition.Key, toggle.IsOn, intensity.Value, token)));
            toggle.Toggled += (_, _) => Changed();
            intensity.ValueChanged += (_, _) => Changed();
            effects.Children.Add(toggle); effects.Children.Add(intensity);
        }
        effects.Children.Add(Text("Each vignette is confined to its own screen, webcam or hardware box. Switching it off keeps the chosen intensity."));
        Grid.SetColumn(effects, 1); columns.Children.Add(effects);
        return columns;
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
