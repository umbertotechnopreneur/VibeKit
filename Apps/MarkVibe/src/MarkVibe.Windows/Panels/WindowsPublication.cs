/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native About and keyboard-accessible publication footer
 * Human guidance: preserve publication links, identity and idle visibility in WinUI 3
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Reflection;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.ViewManagement;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Keeps project, authorship and shared legal destinations independent of navigation state.</summary>
internal static class WindowsPublication
{
    public const string Repository = "https://github.com/umbertotechnopreneur/MarkVibe";
    public const string Initiative = "https://github.com/umbertotechnopreneur/VibeWare";
    public const string Author = "https://umbertogiacobbi.biz";
    public const string Manifesto = Author + "/vibeware/manifesto";
    public const string Terms = Author + "/terms/";
    public const string Privacy = Author + "/privacy/";
    public const string License = Repository + "/blob/main/LICENSE";

    /// <summary>Creates a native hyperlink whose destination can open only on activation.</summary>
    public static HyperlinkButton Link(string label, string url)
    {
        var text = T(label);
        var link = new HyperlinkButton
        {
            Content = text, NavigateUri = new Uri(url), Padding = new Thickness(8, 4, 8, 4)
        };
        ToolTipService.SetToolTip(link, text);
        return link;
    }

    /// <summary>Displays the running packaged version, or the executable's version during development.</summary>
    public static string Version()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            var assembly = typeof(WindowsPublication).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? T("Development");
        }
    }
}

/// <summary>Provides persistent footer space with links revealed on pointer or keyboard use.</summary>
internal sealed class WindowsFooter : UserControl
{
    private const int LinkCount = 5;
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Grid _links = new() { ColumnSpacing = 4, RowSpacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
    private Storyboard? _fade;
    private bool _pointerInside;
    private int _linksPerRow;

    /// <summary>Lets the application shell coordinate About with its other dialogs.</summary>
    public event Action? AboutRequested;

    /// <summary>Creates one native link set and schedules idle fading only while loaded.</summary>
    public WindowsFooter()
    {
        var surface = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        surface.Children.Add(_links);
        Content = surface;
        FitWidth(double.PositiveInfinity);
        Populate();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        _idle.Tick += FadeAfterIdle;
    }

    /// <summary>Reserves native auto-sized rows from the shell's available footer width.</summary>
    public void FitWidth(double width)
    {
        var columns = width switch
        {
            >= 520 => 5,
            >= 360 => 3,
            >= 265 => 2,
            _ => 1
        };
        if (_linksPerRow == columns)
        {
            return;
        }

        _linksPerRow = columns;
        _links.ColumnDefinitions.Clear();
        for (var index = 0; index < columns; index++)
        {
            _links.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        _links.RowDefinitions.Clear();
        var rows = (LinkCount + columns - 1) / columns;
        for (var index = 0; index < rows; index++)
        {
            _links.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        PlaceLinks();
    }

    /// <summary>Displays translated captions and leaves every link independently keyboard-accessible.</summary>
    private void Populate()
    {
        _links.Children.Clear();
        var about = new HyperlinkButton { Content = T("About MarkVibe"), Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(about, T("About MarkVibe"));
        about.Click += (_, _) => AboutRequested?.Invoke();
        _links.Children.Add(about);
        _links.Children.Add(WindowsPublication.Link("Terms", WindowsPublication.Terms));
        _links.Children.Add(WindowsPublication.Link("Privacy", WindowsPublication.Privacy));
        _links.Children.Add(WindowsPublication.Link("VibeWare", WindowsPublication.Initiative));
        _links.Children.Add(WindowsPublication.Link("Manifesto", WindowsPublication.Manifesto));
        PlaceLinks();
        WindowsUiFont.ApplyTo(_links);
    }

    /// <summary>Places links in reading and tab order without rebuilding controls during resize.</summary>
    private void PlaceLinks()
    {
        for (var index = 0; index < _links.Children.Count; index++)
        {
            var link = (HyperlinkButton)_links.Children[index];
            Grid.SetRow(link, index / _linksPerRow);
            Grid.SetColumn(link, index % _linksPerRow);
        }
    }

    /// <summary>Subscribes for localization only as long as the footer is on screen.</summary>
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        UiStrings.Changed += Populate;
        Populate();
        Reveal();
        ScheduleFade();
    }

    /// <summary>Releases animation and localization hooks when detached from the window.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UiStrings.Changed -= Populate;
        _pointerInside = false;
        Reveal();
    }

    /// <summary>Reveals links while the pointer is over their reserved region.</summary>
    private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = true;
        Reveal();
    }

    /// <summary>Restarts the idle interval after pointer interaction.</summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = false;
        ScheduleFade();
    }

    /// <summary>Restores faded links immediately when a keyboard user reaches them.</summary>
    private void OnGotFocus(object sender, RoutedEventArgs args) => Reveal();

    /// <summary>Waits for the next focus target so tabbing between footer links keeps them visible.</summary>
    private void OnLostFocus(object sender, RoutedEventArgs args) => DispatcherQueue.TryEnqueue(ScheduleFade);

    /// <summary>Starts a new idle interval only while neither pointer nor keyboard is using the footer.</summary>
    private void ScheduleFade()
    {
        _idle.Stop();
        if (IsLoaded && !_pointerInside && !HasFocus())
        {
            _idle.Start();
        }
    }

    /// <summary>Keeps the link container visible and cancels any running opacity transition.</summary>
    private void Reveal()
    {
        _idle.Stop();
        _fade?.Stop();
        _fade = null;
        _links.Opacity = 1;
    }

    /// <summary>Fades after 30 seconds without changing layout or tab order.</summary>
    private void FadeAfterIdle(object? sender, object args)
    {
        _idle.Stop();
        if (!IsLoaded || _pointerInside || HasFocus())
        {
            return;
        }

        if (!new UISettings().AnimationsEnabled)
        {
            _links.Opacity = 0;
            return;
        }

        var animation = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromSeconds(3)) };
        Storyboard.SetTarget(animation, _links);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _fade = new Storyboard();
        _fade.Children.Add(animation);
        _fade.Begin();
    }

    /// <summary>Checks the active native focus target for membership of the footer.</summary>
    private bool HasFocus()
    {
        var element = XamlRoot == null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (element != null)
        {
            if (ReferenceEquals(this, element))
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}

/// <summary>Shows the product version, usage, attribution and explicit publishing actions in native controls.</summary>
internal static class WindowsAboutDialog
{
    /// <summary>Opens the native About dialog owned by the supplied shell.</summary>
    public static async Task ShowAsync(XamlRoot root, Func<Task<string>>? emailDiagnostics = null)
    {
        var scroll = WindowsDialogs.Scroll(new AboutView(emailDiagnostics));
        scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = scroll,
            CloseButtonText = T("Close"),
            CloseButtonStyle = (Style)Application.Current.Resources["AboutCloseButtonStyle"]
        };
        AutomationProperties.SetName(dialog, T("About MarkVibe"));
        dialog.PreviewKeyDown += (_, args) => ScrollAboutOnPageKey(scroll, args);
        await WindowsDialogs.ShowScrollableAsync(dialog, scroll, 760);
    }

    /// <summary>Scrolls the About body from the keyboard while focus stays on its current control.</summary>
    private static void ScrollAboutOnPageKey(ScrollViewer scroll, KeyRoutedEventArgs args)
    {
        var current = scroll.VerticalOffset;
        var limit = scroll.ScrollableHeight;
        double? offset = args.Key switch
        {
            VirtualKey.PageUp => current - scroll.ViewportHeight,
            VirtualKey.PageDown => current + scroll.ViewportHeight,
            VirtualKey.Home => 0,
            VirtualKey.End => limit,
            _ => null
        };
        if (offset is double requested)
        {
            var target = Math.Clamp(requested, 0, limit);
            if (target != current)
            {
                args.Handled = scroll.ChangeView(null, target, null, disableAnimation: true);
            }
        }
    }
}
