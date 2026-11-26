// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tools.Widgets;
using Windows.Storage.Pickers;
using static Tools.Widgets.EffectStudioLayout;

namespace VibeWare.EffectsStudio;

internal sealed partial class Studio
{
    internal const string Product = "Retro Sound", Identifier = "RetroSound", InstanceName = @"Local\VibeWare.HardDriveSound";
    private const string Description = "Mechanical drive sounds follow your computer’s disk activity.";
    private readonly ToggleSwitch session = new(), spindle = new();
    private readonly Slider volume = Level(100), density = Level(150);
    private readonly TextBlock volumeValue = new(), densityValue = new();
    private readonly NumberBox quiet = Seconds(120), retry = Seconds(600);
    private readonly ComboBox profiles = new();
    private readonly TextBlock profilePath = SettingsHint("");
    private readonly ComboBoxItem customProfile = new() { Content = "Custom folder", IsEnabled = false };
    private Native.Playback current;
    private bool choosing;

    private static Slider Level(int maximum) => new() { Minimum = 0, Maximum = maximum, StepFrequency = 1, SmallChange = 1 };
    private static NumberBox Seconds(int maximum) => new() { Minimum = 0, Maximum = maximum, SmallChange = 1, Width = 120,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };

    private FrameworkElement BuildEffectPage()
    {
        var page = StudioSectionPage("Effect");
        volume.ValueChanged += (_, a) => Configure(s => s with { Volume = (int)Math.Round(a.NewValue) });
        density.ValueChanged += (_, a) => Configure(s => s with { Density = (int)Math.Round(a.NewValue) });
        spindle.Toggled += (_, _) => Configure(s => s with { Idle = spindle.IsOn ? 1 : 0 });
        page.Children.Add(RowGroup("Drive sound",
            SliderRow("Volume", "Playback level for the selected profile.", volume, volumeValue),
            SliderRow("Seek density", "How often disk activity triggers a head movement.", density, densityValue),
            PreferenceRow("Quiet spindle sound", "Keep a low motor sound underneath the seeks.", spindle)));
        quiet.ValueChanged += (_, a) =>
        {
            if (double.IsFinite(a.NewValue)) Configure(s => s with { Quiet = (int)Math.Round(a.NewValue) });
        };
        retry.ValueChanged += (_, a) =>
        {
            if (double.IsFinite(a.NewValue)) Configure(s => s with { Retry = (int)Math.Round(a.NewValue) });
        };
        page.Children.Add(RowGroup("Timing",
            PreferenceRow("Idle clicks", "Seconds between idle clicks. Set to 0 to turn them off.", quiet),
            PreferenceRow("Head retry", "Seconds between simulated retries. Set to 0 to turn them off.", retry)));
        return page;
    }

    private FrameworkElement BuildProfilePage()
    {
        var page = StudioSectionPage("Presets");
        foreach (string name in new[] { "Classic", "Warm", "Computer" })
            profiles.Items.Add(new ComboBoxItem { Content = name });
        profiles.Items.Add(customProfile);
        profiles.SelectionChanged += (_, _) =>
        {
            string? profile = profiles.SelectedIndex switch { 0 => "", 1 => "@warm", 2 => "@computer", _ => null };
            if (profile is not null) Change(() => Native.SoundProfile(profile));
        };
        page.Children.Add(RowGroup("Sample library",
            PreferenceRow("Sound profile", "Choose the recordings used for playback.", profiles),
            PreferenceRow("Custom profile", profilePath, Button("Choose folder…", ChooseProfile))));
        page.Children.Add(RowGroup("Preview",
            PreferenceRow("Spin-up", "Hear the selected drive start.", Button("Play spin-up", () => Preview(0))),
            PreferenceRow("Seek", "Hear a burst of head movements.", Button("Play seeks", () => Preview(2))),
            PreferenceRow("Spin-down", "Hear the drive come to rest.", Button("Play spin-down", () => Preview(3)))));
        return page;
    }

    private async void ChooseProfile()
    {
        if (choosing) return;
        choosing = true;
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            if (await picker.PickSingleFolderAsync() is { } folder && !quitting)
                Change(() => Native.SoundProfile(folder.Path));
        }
        catch (COMException error)
        {
            if (!quitting) await new ContentDialog { XamlRoot = root.XamlRoot, Title = "Could not open folder",
                Content = error.Message, CloseButtonText = "Close" }.ShowAsync();
        }
        finally { choosing = false; }
    }

    private void Configure(Func<Native.Playback, Native.Playback> update) =>
        Change(() => Native.SoundConfigure(update(current)));

    private void AddSettings(StackPanel page)
    {
        session.Toggled += (_, _) => Configure(s => s with { Session = session.IsOn ? 1 : 0 });
        page.Children.Add(RowGroup("Startup", PreferenceRow("Start with Windows",
            "Play spin-up at sign-in and spin-down at sign-out.", session)));
    }

    private FrameworkElement FooterContent() => Footnote("\uE767", new TextBlock { Text = "Output: System default" });
    private void Preview(int kind) => Change(() => Native.SoundPreview(kind));
    private void AddTrayCommands(WidgetTrayMenu.Items menu)
    {
        menu.Add("Play spin-up", () => Preview(0));
        menu.Add("Play seeks", () => Preview(2));
        menu.Add("Play spin-down", () => Preview(3));
        menu.Add("Stop", () => Change(() => Native.WidgetEnable(0)));
    }

    private void RefreshProduct(bool active)
    {
        Native.SoundRead(out current, out var path);
        session.IsOn = current.Session != 0;
        spindle.IsOn = current.Idle != 0;
        volume.Value = current.Volume;
        density.Value = current.Density;
        volumeValue.Text = $"{current.Volume}%";
        densityValue.Text = $"{current.Density}%";
        if (quiet.Value != current.Quiet) quiet.Value = current.Quiet;
        if (retry.Value != current.Retry) retry.Value = current.Retry;
        string profile = Marshal.PtrToStringUni(path) ?? "";
        profiles.SelectedIndex = profile switch { "" => 0, "@warm" => 1, "@computer" => 2, _ => 3 };
        profilePath.Text = profiles.SelectedIndex == 3 ? profile : "Use a folder containing your own drive recordings.";
    }
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Playback { internal int Session, Idle, Volume, Quiet, Density, Retry; }
    [DllImport(Engine)] internal static extern void SoundRead(out Playback settings, out nint profile);
    [DllImport(Engine)] internal static extern void SoundConfigure(Playback settings);
    [DllImport(Engine, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern void SoundProfile(string path);
    [DllImport(Engine)] internal static extern void SoundPreview(int kind);
}
