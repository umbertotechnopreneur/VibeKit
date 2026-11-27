using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private static string MinuteClock(int seconds)
    {
        long value = Math.Abs((long)seconds);
        return $"{(seconds < 0 ? "+" : "")}{value / 60:00}:{value % 60:00}";
    }

    private sealed class PresenterFace
    {
        private readonly MainWindow owner;
        private readonly TextBlock mode = DynamicText(13), current = DynamicText(18), next = DynamicText(18);
        private readonly TextBlock clock = DynamicText(66), total = DynamicText(19), note = DynamicText(13), obs = DynamicText(12);
        private readonly TextBlock clockCaption = Caption("Scene remaining");
        private readonly Grid progression = Columns(2);
        private readonly StackPanel totalGroup = Column(0);
        private readonly Grid signal = new() { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly Dictionary<string, Grid> lights = new();
        private readonly Button hold = GlyphButton("\uE769", "Pause"), advance = GlyphButton("\uE893", "Next"), stop = GlyphButton("\uE71A", "Stop workflow");
        private readonly Button add10 = new() { Content = "+10 s", IsEnabled = false };
        private readonly Button add20 = new() { Content = "+20 s", IsEnabled = false };
        private readonly Button options = GlyphButton("\uE713", "Timer settings");
        private readonly Border board = new() { CornerRadius = new(12), BorderThickness = new(1) };
        private Storyboard? blend;
        private string? displayedSignal;
        private readonly Viewbox scalableDigits = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        internal readonly Grid Body = new();

        public PresenterFace(MainWindow owner)
        {
            this.owner = owner;
            Body.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            Body.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var stripe = WithTrailing(mode, options);
            Body.Children.Add(stripe);
            options.Click += async (_, _) => await owner.EditTimerSettingsAsync();

            var display = Column(8);
            board.Child = display;
            var scroll = new ScrollViewer { Content = board, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1);
            Body.Children.Add(scroll);
            Body.ActualThemeChanged += (_, _) => Paint();
            Paint();
            var clockRow = new Grid { ColumnSpacing = 12 };
            clockRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            clockRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            clockRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            display.Children.Add(clockRow);
            foreach (string key in new[] { "off", "green", "amber", "red" })
            {
                var image = new Image { Stretch = Stretch.Uniform,
                    Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "TimerSignals", key + ".png"))) { DecodePixelHeight = 320 } };
                var layer = new Grid { Opacity = 0 };
                layer.Children.Add(image);
                if (key != "off")
                {
                    // Match the reference widget's light spill, aligned with the active lens.
                    var glow = new Grid { IsHitTestVisible = false, Opacity = .48 };
                    for (int i = 0; i < 3; i++) glow.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
                    var color = ToColor(key == "green" ? 0x44E85Eu : key == "amber" ? 0xFFC336u : 0xFF4330u);
                    var lens = new Border { Margin = new(-4, -2, -4, -2), Background = new RadialGradientBrush
                    {
                        GradientStops =
                        {
                            new() { Color = color, Offset = 0 },
                            new() { Color = global::Windows.UI.Color.FromArgb(115, color.R, color.G, color.B), Offset = .46 },
                            new() { Color = global::Windows.UI.Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 }
                        }
                    }};
                    Grid.SetRow(lens, key == "green" ? 2 : key == "amber" ? 1 : 0);
                    glow.Children.Add(lens);
                    layer.Children.Add(glow);
                }
                lights.Add(key, layer);
                signal.Children.Add(layer);
            }
            clockRow.Children.Add(signal);
            var mainTime = Column(0);
            mainTime.VerticalAlignment = VerticalAlignment.Center;
            clock.FontFamily = total.FontFamily = new FontFamily("Consolas");
            clock.FontWeight = current.FontWeight = next.FontWeight = FontWeights.SemiBold;
            mainTime.Children.Add(clockCaption);
            scalableDigits.Child = clock;
            clock.TextWrapping = TextWrapping.NoWrap;
            mainTime.Children.Add(scalableDigits);
            Grid.SetColumn(mainTime, 1);
            clockRow.Children.Add(mainTime);
            totalGroup.VerticalAlignment = VerticalAlignment.Center;
            var totalCaption = Caption("Workflow remaining");
            totalCaption.MaxWidth = 104;
            totalGroup.Children.Add(totalCaption);
            totalGroup.Children.Add(total);
            Grid.SetColumn(totalGroup, 2);
            clockRow.Children.Add(totalGroup);

            var scene = Column(2);
            scene.Children.Add(Caption("Current scene"));
            scene.Children.Add(current);
            progression.ColumnSpacing = 12;
            progression.Children.Add(scene);
            var nextDetails = Column(2);
            nextDetails.Children.Add(Caption("Next scene"));
            nextDetails.Children.Add(next);
            var nextEntry = new Grid { ColumnSpacing = 9 };
            nextEntry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            nextEntry.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            nextEntry.Children.Add(new FontIcon { Glyph = "\uE72A", FontSize = 15, Opacity = .68, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(nextDetails, 1);
            nextEntry.Children.Add(nextDetails);
            // Keep the progression in two constrained columns at phone-monitor widths.
            Grid.SetColumn(nextEntry, 1);
            progression.Children.Add(nextEntry);
            display.Children.Add(progression);
            display.Children.Add(note);

            var actions = Row();
            actions.Spacing = 6;
            actions.Children.Add(hold);
            actions.Children.Add(add10);
            actions.Children.Add(add20);
            actions.Children.Add(advance);
            actions.Children.Add(stop);
            ActionHelp(add10, "+10 s");
            ActionHelp(add20, "+20 s");
            ActionHelp(hold, "Pause workflow");
            ActionHelp(advance, "Next");
            ActionHelp(stop, "Stop workflow");
            ActionHelp(options, "Timer settings");
            AutomationProperties.SetAutomationId(add10, "ExtendScene10");
            AutomationProperties.SetAutomationId(add20, "ExtendScene20");
            add10.Click += (_, _) => ExtendScene(10);
            add20.Click += (_, _) => ExtendScene(20);
            hold.Click += (_, _) =>
            {
                if (owner.recordingCue?.IsPending == true) return;
                if (owner.player.State == WorkflowState.Paused) owner.player.Resume();
                else owner.player.Pause();
                owner.RefreshPlayback();
            };
            advance.Click += async (_, _) =>
            {
                if (owner.recordingCue?.IsPending == true) return;
                owner.player.Next(); await owner.AdvanceWorkflowAsync();
            };
            stop.Click += (_, _) =>
            {
                if (owner.recordingCue is not null) owner.CancelRecordingCue();
                else owner.StopWorkflow("Workflow stopped.");
            };
            obs.Opacity = .78;
            var footer = WithTrailing(obs, actions);
            footer.Margin = new(2, 4, 2, 0);
            Grid.SetRow(footer, 2);
            Body.Children.Add(footer);
        }

        private void ExtendScene(int seconds)
        {
            if (owner.recordingCue is not null) return;
            owner.player.ExtendCurrent(seconds);
            owner.RefreshPlayback();
        }

        private void Paint()
        {
            bool dark = Body.ActualTheme == ElementTheme.Dark;
            Body.Background = Brush(dark ? 0x171A1Eu : 0xF5F2EEu);
            board.Background = Brush(dark ? 0x25292Eu : 0xFFFDF9u);
            board.BorderBrush = Brush(dark ? 0x485159u : 0xD7D0C8u);
        }

        private static TextBlock DynamicText(double size) => new()
        {
            FontSize = size, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2, Tag = NativeLocalization.UserText
        };

        private static TextBlock Caption(string key) => new() { Text = key, FontSize = 12,
            Opacity = .76, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };

        private static Button GlyphButton(string glyph, string label)
        {
            var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Padding = new(9, 5, 9, 5) };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            return button;
        }

        internal void ApplySize(Window window, bool large)
        {
            Body.Padding = new(large ? 22 : 12);
            Body.RowSpacing = large ? 12 : 8;
            scalableDigits.MaxHeight = large ? 124 : 78;
            board.Padding = new(large ? 24 : 12);
            clock.FontSize = large ? 104 : 66;
            current.FontSize = next.FontSize = large ? 27 : 20;
            signal.Height = large ? 124 : 77;
            signal.Width = large ? 48 : 29;
            double scale = Body.XamlRoot?.RasterizationScale ?? owner.root.XamlRoot?.RasterizationScale ?? 1;
            window.AppWindow.ResizeClient(new((int)Math.Ceiling((large ? 690 : 458) * scale),
                (int)Math.Ceiling((large ? 406 : 302) * scale)));
        }

        private static void Set(TextBlock field, string value)
        {
            if (field.Text != value) field.Text = value;
        }

        internal void ShowObsStatus(string text) => Set(obs, text);

        internal void UpdateTransport()
        {
            bool cue = owner.recordingCue is not null;
            options.IsEnabled = !cue && !owner.workflowDialogOpen;
            bool paused = owner.player.State == WorkflowState.Paused;
            var glyph = (FontIcon)hold.Content;
            string symbol = paused ? "\uE768" : "\uE769";
            if (glyph.Glyph != symbol) glyph.Glyph = symbol;
            string label = owner.localization.Text(paused ? "Resume" : "Pause");
            if (AutomationProperties.GetName(hold) != label)
            {
                AutomationProperties.SetName(hold, label);
                ActionHelp(hold, paused ? "Resume workflow" : "Pause workflow");
            }
            hold.IsEnabled = !cue && (owner.player.State == WorkflowState.Running || paused);
            advance.IsEnabled = !cue && owner.player.State == WorkflowState.Running && owner.player.Mode != WorkflowMode.Free;
            add10.IsEnabled = add20.IsEnabled = !cue && owner.player.CanExtendCurrent;
            stop.IsEnabled = cue || owner.PlaybackActive;
            string stopLabel = owner.localization.Text(cue ? "Cancel" : "Stop workflow");
            if (AutomationProperties.GetName(stop) != stopLabel)
            {
                AutomationProperties.SetName(stop, stopLabel);
                ActionHelp(stop, cue ? "Cancel recording countdown" : "Stop workflow");
            }
        }

        internal void Present(string activity)
        {
            var prefs = owner.settings.Timer;
            var player = owner.player;
            int count = owner.RecordingCueSeconds;
            bool activeStep = count == 0 && owner.PlaybackActive && player.Current != null;
            string T(string key) => owner.localization.Text(key);
            string caption = count > 0 ? "Recording in" : player.Mode == WorkflowMode.Free && owner.PlaybackActive ? "Free timer" : "Scene remaining";
            Set(clockCaption, T(caption));
            Set(mode, count > 0 ? "OBS" : activity);
            Set(clock, MinuteClock(count > 0 ? count : player.RemainingSeconds));
            Set(total, MinuteClock(player.TotalRemainingSeconds));
            Set(current, activeStep && player.Current is { } step ? owner.SceneTitle(step.Scene) : "");
            Set(next, activeStep && player.NextStep is { } upcoming ? owner.SceneTitle(upcoming.Scene) : "");
            Set(note, activeStep ? player.Current?.Note ?? "" : "");
            ShowObsStatus(owner.status.Text);
            progression.Visibility = activeStep ? Visibility.Visible : Visibility.Collapsed;
            progression.Children[1].Visibility = prefs.ShowNext && next.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan((FrameworkElement)progression.Children[0], progression.Children[1].Visibility == Visibility.Visible ? 1 : 2);
            totalGroup.Visibility = activeStep && prefs.ShowTotal ? Visibility.Visible : Visibility.Collapsed;
            note.Visibility = prefs.ShowNote && note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            obs.Visibility = prefs.ShowObs ? Visibility.Visible : Visibility.Collapsed;
            signal.Visibility = prefs.RecordCountdown || prefs.EndWarning ? Visibility.Visible : Visibility.Collapsed;
            bool nearEnd = prefs.EndWarning && player.State == WorkflowState.Running && player.RemainingSeconds > 0 && player.RemainingSeconds <= prefs.WarningSeconds;
            ShowSignal(count switch { 3 => "green", 2 => "amber", 1 => "red", _ => nearEnd ? "green" : "off" });
            UpdateTransport();
        }

        private void ShowSignal(string key)
        {
            if (key == displayedSignal) return;
            string? previous = displayedSignal;
            displayedSignal = key;
            blend?.Stop();
            blend = null;
            foreach (var (color, layer) in lights) layer.Opacity = color == key ? 1 : 0;
            if (previous is null || !Body.IsLoaded || !owner.systemSettings.AnimationsEnabled) return;
            var transition = new Storyboard();
            foreach (var (layer, from, to) in new[] { (lights[previous], 1d, 0d), (lights[key], 0d, 1d) })
            {
                var fade = new DoubleAnimation { From = from, To = to,
                    Duration = new(TimeSpan.FromMilliseconds(170)), FillBehavior = FillBehavior.Stop };
                Storyboard.SetTarget(fade, layer); Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
                transition.Children.Add(fade);
            }
            blend = transition;
            transition.Begin();
        }

        internal async Task VerifyTimeExtensionAsync()
        {
            var player = owner.player;
            long ticket = player.Ticket;
            foreach (var (button, seconds) in new[] { (add10, 10), (add20, 20) })
            {
                if (!button.IsEnabled) throw new InvalidOperationException("Scene extension is disabled during rehearsal.");
                int remaining = player.RemainingSeconds;
                UiSmoke.Invoke(button);
                await UiSmoke.WaitForAsync(() => player.RemainingSeconds >= remaining + seconds - 1);
                if (player.Ticket != ticket || player.State != WorkflowState.Running)
                    throw new InvalidOperationException("Scene extension changed or paused the current scene.");
            }
        }

        internal void VerifyLayout()
        {
            var corner = stop.TransformToVisual(Body).TransformPoint(new(stop.ActualWidth, stop.ActualHeight));
            var sceneCorner = current.TransformToVisual(Body).TransformPoint(new(current.ActualWidth, current.ActualHeight));
            var toolbarTop = hold.TransformToVisual(Body).TransformPoint(new(0, 0));
            if (stop.ActualHeight < 20 || corner.X > Body.ActualWidth + 1 || corner.Y > Body.ActualHeight + 1 ||
                sceneCorner.Y > toolbarTop.Y + 1)
                throw new InvalidOperationException("The presenter must fit the scene and all transport controls in its viewport.");
        }

        internal void Close() => blend?.Stop();
    }
}
