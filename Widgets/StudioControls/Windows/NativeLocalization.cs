using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

/// <summary>Translate native display properties without touching editable values or control models.</summary>
internal sealed class NativeLocalization : IDisposable
{
    internal static readonly object UserText = new();

    private sealed class Binding(DependencyObject target, DependencyProperty property, string? source)
    {
        public DependencyObject Target { get; } = target;
        public DependencyProperty Property { get; } = property;
        public string? Source { get; set; } = source;
        public long Token { get; set; }
    }
    private readonly StudioTranslations? catalog;
    private readonly Dictionary<(DependencyObject, DependencyProperty), Binding> bindings = new();
    private readonly HashSet<DependencyObject> attached = new();
    private bool updating;
    private string language = "en";

    public NativeLocalization()
    {
        try { catalog = StudioTranslations.LoadDefault(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { Diagnostics.Record("localization.load", error); }
    }

    public string Language
    {
        get => language;
        set
        {
            language = StudioTranslations.Languages.Any(item => item.Code == value) ? value : "en";
            foreach (Binding binding in bindings.Values) Update(binding);
        }
    }

    public string Text(string source) => catalog?.Text(source, language) ?? source;

    private void Bind(DependencyObject target, DependencyProperty property)
    {
        if (bindings.ContainsKey((target, property))) return;
        var binding = new Binding(target, property, target.GetValue(property) as string);
        bindings.Add((target, property), binding);
        binding.Token = target.RegisterPropertyChangedCallback(property, (_, _) =>
        {
            if (updating) return;
            binding.Source = target.GetValue(property) as string;
            Update(binding);
        });
        Update(binding);
    }

    private void Update(Binding binding)
    {
        if (binding.Source is null) return;
        string translated = Text(binding.Source);
        if (Equals(binding.Target.GetValue(binding.Property), translated)) return;
        updating = true;
        try { binding.Target.SetValue(binding.Property, translated); }
        finally { updating = false; }
    }

    public void Attach(DependencyObject target)
    {
        if (!attached.Add(target)) return;
        Bind(target, AutomationProperties.NameProperty);
        Bind(target, ToolTipService.ToolTipProperty);
        switch (target)
        {
            case TextBlock block when !ReferenceEquals(block.Tag, UserText): Bind(target, TextBlock.TextProperty); break;
            case TextBox _:
                Bind(target, TextBox.HeaderProperty); Bind(target, TextBox.PlaceholderTextProperty); break;
            case PasswordBox _: Bind(target, PasswordBox.HeaderProperty); break;
            case NumberBox _: Bind(target, NumberBox.HeaderProperty); break;
            case Slider _: Bind(target, Slider.HeaderProperty); break;
            case ToggleSwitch toggle:
                toggle.OnContent = "ON";
                toggle.OffContent = "OFF";
                Bind(target, ToggleSwitch.HeaderProperty);
                Bind(target, ToggleSwitch.OnContentProperty); Bind(target, ToggleSwitch.OffContentProperty); break;
            case ComboBox combo:
                Bind(target, ComboBox.HeaderProperty);
                foreach (object item in combo.Items) if (item is DependencyObject child) Attach(child);
                break;
            case TabView tabs:
                foreach (object item in tabs.TabItems) if (item is DependencyObject child) Attach(child);
                break;
            case TabViewItem tab:
                Bind(target, TabViewItem.HeaderProperty);
                if (tab.Content is DependencyObject tabContent) Attach(tabContent);
                break;
            case Expander expander:
                Bind(target, Expander.HeaderProperty);
                if (expander.Content is DependencyObject expandedContent) Attach(expandedContent);
                break;
            case ScrollViewer scroll:
                if (scroll.Content is DependencyObject scrollContent) Attach(scrollContent);
                break;
        }
        if (target is Panel panel) foreach (UIElement child in panel.Children) Attach(child);
        if (target is Border border && border.Child is { } borderChild) Attach(borderChild);
        if (target is ContentControl control)
        {
            if (control.Content is DependencyObject child) Attach(child);
            else if (control.Content is string) Bind(control, ContentControl.ContentProperty);
        }
    }

    public void Dispose()
    {
        foreach (Binding binding in bindings.Values)
            binding.Target.UnregisterPropertyChangedCallback(binding.Property, binding.Token);
        bindings.Clear();
        attached.Clear();
    }
}
