// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tools.Widgets;
using static Tools.Widgets.EffectStudioLayout;

namespace VibeWare.EffectsStudio;

internal sealed partial class Studio
{
    internal const string Product = "CRT Degauss", Identifier = "CrtDegauss", InstanceName = @"Local\VibeWare.CrtDegauss";
    private const string Description = "Gradually magnetize the primary display and clear it with a degauss pulse.";
    private readonly NumberBox minutes = new() { Minimum = 1, Maximum = 120, SmallChange = 1, Width = 120,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly ComboBox patches = new() { ItemsSource = new[] {
        "One patch", "Two patches", "Similar hues", "Two · Soft", "Two · Strong", "Similar · Bold" } };
    private readonly ToggleSwitch sound = new();
    private readonly ComboBox profile = new() { ItemsSource = new[] {
        "Classic", "Dry relay", "Low coil", "Desktop monitor", "PAL television", "Long buzz" } };
    private readonly Button previewSound = new() { Content = "Preview" };
    private readonly Button previewPatches = new() { Content = "Preview" };
    private readonly Slider previewGain = new() { Minimum = -50, Maximum = 50, Value = 0, StepFrequency = 1 };
    private readonly TextBlock shortcut = new();
    private Button pulse = null!, show = null!;
    private Native.Magnetization current;

    private FrameworkElement BuildEffectPage()
    {
        var page = StudioSectionPage("Effect");
        minutes.ValueChanged += (_, args) =>
        {
            if (double.IsFinite(args.NewValue)) Configure(s => s with { Minutes = (uint)Math.Round(args.NewValue) });
        };
        patches.SelectionChanged += (_, _) =>
        {
            if (patches.SelectedIndex >= 0) Configure(s => s with { Patches = (uint)patches.SelectedIndex + 1 });
        };
        previewPatches.Click += (_, _) => Change(Native.CrtPreviewPatches);
        AutomationProperties.SetName(previewPatches, "Preview color patches");
        page.Children.Add(RowGroup("Magnetization",
            PreferenceRow("Time before degauss", "Minutes for color patches to reach full strength.", minutes),
            PreferenceRow("Color patches", "Choose a pattern, color pairing, and intensity.",
                PreviewControls(patches, previewPatches, "Color patch preset"))));
        pulse = Button("Degauss", () => Change(Native.CrtDegaussNow));
        show = Button("Show now", () => Change(Native.CrtShowNow));
        page.Children.Add(RowGroup("Manual controls",
            PreferenceRow("Degauss the screen", "Clear the color patches and restart the cycle.", pulse),
            PreferenceRow("Show magnetization", "Skip the buildup and show the patches at full strength.", show)));
        return page;
    }

    private FrameworkElement BuildProfilePage()
    {
        var page = StudioSectionPage("Presets");
        sound.Toggled += (_, _) => Configure(s => s with { Sound = sound.IsOn ? 1u : 0u });
        profile.SelectionChanged += (_, _) =>
        {
            if (profile.SelectedIndex >= 0) Configure(s => s with { Profile = (uint)profile.SelectedIndex });
        };
        previewSound.Click += (_, _) => Change(() => Native.CrtPreviewSound((int)previewGain.Value));
        AutomationProperties.SetName(previewSound, "Preview selected degauss sound");
        var gainValue = new TextBlock { Text = "0%" };
        previewGain.ValueChanged += (_, args) => gainValue.Text = args.NewValue.ToString("+0;-0;0") + "%";
        page.Children.Add(RowGroup("Degauss audio",
            PreferenceRow("Degauss sound", "Play a sound with the degauss pulse.", sound),
            PreferenceRow("Sound profile", "Choose the relay or coil sound for the pulse.",
                PreviewControls(profile, previewSound, "Sound profile")),
            SliderRow("Preview volume", "0% plays the original sample level.", previewGain, gainValue)));
        return page;
    }

    private static Grid PreviewControls(ComboBox selector, Button preview, string selectorName)
    {
        var controls = new Grid { ColumnSpacing = 8, Width = 200 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selector.HorizontalAlignment = HorizontalAlignment.Stretch;
        selector.MinWidth = 0;
        AutomationProperties.SetName(selector, selectorName);
        controls.Children.Add(selector);
        Grid.SetColumn(preview, 1);
        controls.Children.Add(preview);
        return controls;
    }

    private void Configure(Func<Native.Magnetization, Native.Magnetization> update) =>
        Change(() => Native.CrtConfigure(update(current)));

    private void AddSettings(StackPanel page)
    {
        page.Children.Add(RowGroup("Display", PreferenceRow("Effect target",
            "Magnetization and the degauss pulse run on this display.", new TextBlock { Text = "Primary display" })));
    }

    private FrameworkElement FooterContent() => Footnote("\uE765", shortcut);

    private void AddTrayCommands(WidgetTrayMenu.Items menu)
    {
        Native.WidgetRead(out var state);
        Native.CrtRead(out _, out int pulsing, out _);
        bool ready = state.Active != 0 && pulsing == 0;
        menu.Add("Degauss", () => Change(Native.CrtDegaussNow), ready);
        menu.Add("Show magnetization now", () => Change(Native.CrtShowNow), ready);
        menu.Add("Stop", () => Change(() => Native.WidgetEnable(0)), state.Active != 0);
    }

    private void RefreshProduct(bool active)
    {
        Native.CrtRead(out current, out int pulsing, out int hotkey);
        if (minutes.Value != current.Minutes) minutes.Value = current.Minutes;
        patches.SelectedIndex = (int)current.Patches - 1;
        sound.IsOn = current.Sound != 0;
        profile.SelectedIndex = (int)current.Profile;
        profile.IsEnabled = sound.IsOn;
        previewSound.IsEnabled = previewGain.IsEnabled = sound.IsOn;
        previewPatches.IsEnabled = pulsing == 0;
        pulse.IsEnabled = show.IsEnabled = active && pulsing == 0;
        shortcut.Text = hotkey != 0 ? "Emergency shortcut: Windows + Shift + F9" : "Emergency shortcut unavailable";
    }
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Magnetization { internal uint Minutes, Patches, Sound, Profile; }
    [DllImport(Engine)] internal static extern void CrtRead(out Magnetization settings, out int pulsing, out int hotkey);
    [DllImport(Engine)] internal static extern void CrtConfigure(Magnetization settings);
    [DllImport(Engine)] internal static extern void CrtDegaussNow();
    [DllImport(Engine)] internal static extern void CrtPreviewSound(int adjustment);
    [DllImport(Engine)] internal static extern void CrtPreviewPatches();
    [DllImport(Engine)] internal static extern void CrtShowNow();
}
