using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using global::Windows.Graphics;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private const int MinimumWidth = 300;
    private const int LargerWidgetPercent = 15;
    private const double TileSpacing = 8;
    private readonly Grid layout = new() { Padding = new(8) };
    private readonly WrappingPanel actions = new(3, centerRows: true);
    private readonly StackPanel brands = new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly StackPanel rail = new() { Spacing = 6, Margin = new(TileSpacing, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly Viewbox layoutView = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer viewport = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Button pinButton = Button("\uE718", "Keep on top");
    private Module[] modules = [];
    private bool layoutQueued;
    private int lastMinimumWidth;
    private double? expandedWidth;
    private static readonly WindowSubclass ResizeProcedure = HorizontalResize;

    private sealed record Module(string Key, string Label, string Description, FrameworkElement Element, FlipTile? Tile = null, bool Detail = false);

    private void BuildResponsiveLayout()
    {
        modules =
        [
            new("allowance", "Allowance remaining", "Percentage left in the allowance currently selected by Codex.", usageLink, remaining),
            new("credits", "Remaining credits", "Available credits. The bar compares your balance with the highest balance seen since launch.", credits, credits),
            new("estimate", "Estimated time remaining", "Estimated time before the current allowance runs out at the observed usage rate.", forecastLink, forecast),
            new("vouchers", "Resets available", "Number of reset vouchers. Select the tile to see their expiration dates.", vouchersButton, vouchers),
            new("first-reset", "First reset", "Date and time of the first upcoming allowance reset.", resetLink, compactReset),
            new("clock", "Next reset countdown and date", "Switches between the countdown and the calendar date of the next reset.", countdownLink, resetClock),
            new("five-hour", "5-hour allowance", "Percentage left in the rolling five-hour allowance.", fiveHour, fiveHour, true),
            new("five-reset", "5-hour reset in", "Time left before the five-hour allowance resets.", fiveReset, fiveReset, true),
            new("weekly", "Weekly allowance", "Percentage left in the weekly allowance.", weekly, weekly, true),
            new("weekly-reset", "Weekly reset at", "Date and time when the weekly allowance resets.", weeklyReset, weeklyReset, true),
            new("detail-estimate", "Detailed time estimate", "Time the current allowance is expected to last at the observed rate.", detailsEstimate, detailsEstimate, true),
            new("time-to-reset", "Time to reset", "Time until the current allowance resets.", timeToReset, timeToReset, true),
            new("forecast", "At the current rate", "Whether the allowance will last to the reset and how early it could run out.", forecastMessage, forecastMessage, true)
        ];
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.Children.Add(signalHousing);
        Grid.SetColumn(strip, 1);
        layout.Children.Add(strip);
        brands.Children.Add(appWebsiteButton);
        brands.Children.Add(manifestoButton);
        rail.Children.Add(actions);
        rail.Children.Add(brands);
        Grid.SetColumn(rail, 2);
        layout.Children.Add(rail);
        layoutView.Child = layout;
        viewport.Content = layoutView;
        viewport.ViewChanged += (_, _) => QueueLayout();
    }

    private IEnumerable<Module> OrderedModules()
    {
        var byKey = modules.ToDictionary(module => module.Key);
        foreach (var key in settings.FieldOrder ?? [])
            if (key is not null && byKey.Remove(key, out var module)) yield return module;
        foreach (var module in modules)
            if (byKey.ContainsKey(module.Key)) yield return module;
    }

    private bool ModuleEnabled(string key) => key switch
    {
        "credits" => settings.ShowCredits,
        "estimate" => settings.ShowEstimatedTime,
        "vouchers" => settings.ShowAvailableResets,
        _ => !(settings.HiddenFields ?? []).Contains(key)
    };

    private void ApplySize()
    {
        if (!settings.EnableDetailsBar && detailsOpen)
        {
            SetDetailsOpen(false);
            return;
        }
        detailsButton.Visibility = settings.EnableDetailsBar ? Visibility.Visible : Visibility.Collapsed;
        var visible = OrderedModules().Where(module => ModuleEnabled(module.Key) && (!module.Detail || detailsOpen))
            .Select(module => module.Element).ToArray();
        if (!strip.Children.SequenceEqual(visible))
        {
            strip.Children.Clear();
            foreach (var element in visible) strip.Children.Add(element);
        }
        foreach (var tile in modules.Select(module => module.Tile).OfType<FlipTile>())
            tile.SetLabelPosition(settings.LabelPosition);
        UpdateSnapshotMetrics();
        UpdateDetails(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        QueueLayout();
    }

    private void QueueLayout()
    {
        if (closed || !root.IsLoaded || layoutQueued) return;
        layoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            layoutQueued = false;
            if (!closed) FitWindow();
        })) layoutQueued = false;
    }

    private void FitWindow()
    {
        double dpi = root.XamlRoot.RasterizationScale;
        double zoom = settings.BigSize && root.ActualWidth >= 375 ? 1 + LargerWidgetPercent / 100d : 1;
        var size = AppWindow.ClientSize;
        var outer = AppWindow.Size;
        int frameWidth = outer.Width - size.Width;
        int frameHeight = outer.Height - size.Height;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        bool compact = IsCompact;
        var visibleTiles = modules.Where(module => strip.Children.Contains(module.Element))
            .Select(module => module.Tile).OfType<FlipTile>().ToArray();
        int commandCount = actions.Children.Count(child => child.Visibility == Visibility.Visible);
        double commandWidth = commandCount * 28 + Math.Max(0, commandCount - 1) * 3;
        double gutters = layout.Padding.Left + layout.Padding.Right + (compact ? TileSpacing : 2 * TileSpacing);
        double gaps = Math.Max(0, visibleTiles.Length - 1) * TileSpacing;
        double preferredTiles = visibleTiles.Sum(tile => tile.PreferredWidth);
        double minimumTiles = visibleTiles.Length * FlipTile.MinimumWidth;
        double compactChrome = gutters + commandWidth;
        double minimumWidth = MinimumWidth;
        if (compact)
        {
            double comfortable = compactChrome + gaps + Math.Max(minimumTiles, preferredTiles * .6);
            minimumWidth = Math.Max(MinimumWidth, comfortable * zoom);
        }
        int minimum = (int)Math.Ceiling(minimumWidth * dpi);
        if (workArea is { } display) minimum = Math.Min(minimum, Math.Max(1, display.Width - frameWidth));

        int clientWidth = size.Width;
        if (compact && clientWidth < minimum)
            expandedWidth ??= clientWidth / dpi;
        else if (!compact && expandedWidth is { } normalWidth)
        {
            clientWidth = (int)Math.Round(normalWidth * dpi);
            expandedWidth = null;
        }
        clientWidth = Math.Max(minimum, clientWidth);
        int minimumWindowWidth = minimum + frameWidth;
        if (lastMinimumWidth != minimumWindowWidth && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minimumWindowWidth;
            lastMinimumWidth = minimumWindowWidth;
        }

        if (compact)
        {
            // Keep one row even on a display narrower than the minimum tile widths.
            double hardWidth = compactChrome + gaps + minimumTiles;
            zoom = Math.Min(zoom, clientWidth / (dpi * hardWidth));
        }
        double width = clientWidth / (dpi * zoom);
        bool narrow = !compact && width < 530;
        brands.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        brands.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        rail.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top;
        double brandWidth = narrow ? Math.Max(appWebsiteButton.Width, manifestoButton.Width)
            : appWebsiteButton.Width + brands.Spacing + manifestoButton.Width;
        actions.Width = compact ? commandWidth : narrow ? brandWidth : Math.Max(brandWidth, commandWidth);
        signalHousing.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        signalHousing.Width = narrow ? 30 : 44;
        double tileWidth = width - gutters - (compact ? 0 : signalHousing.Width) - actions.Width;
        strip.SingleRow = compact;
        strip.Width = tileWidth;
        double flexibleWidth = preferredTiles - minimumTiles;
        double fraction = compact && flexibleWidth > 0
            ? Math.Clamp((tileWidth - gaps - minimumTiles) / flexibleWidth, 0, 1) : 1;
        foreach (var tile in visibleTiles)
            tile.FitWidth(compact ? FlipTile.MinimumWidth + (tile.PreferredWidth - FlipTile.MinimumWidth) * fraction : tileWidth);

        layout.Width = width;
        strip.Measure(new(tileWidth, double.PositiveInfinity));
        rail.Measure(new(actions.Width + rail.Margin.Left, double.PositiveInfinity));
        double contentHeight = Math.Max(strip.DesiredSize.Height, rail.DesiredSize.Height);
        signalHousing.Height = Math.Min(narrow ? 92 : 128, contentHeight);
        layout.Height = contentHeight + layout.Padding.Top + layout.Padding.Bottom;
        layoutView.Width = width * zoom;
        layoutView.Height = layout.Height * zoom;
        int desiredHeight = (int)Math.Ceiling(layoutView.Height * dpi);
        var position = AppWindow.Position;
        int x = position.X, y = position.Y;
        if (workArea is { } area)
        {
            desiredHeight = Math.Min(desiredHeight, Math.Max(100, area.Height - frameHeight));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - desiredHeight - frameHeight));
            if (clientWidth != size.Width)
                x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - clientWidth - frameWidth));
        }
        if (size.Height != desiredHeight || size.Width != clientWidth || x != position.X || y != position.Y)
            AppWindow.MoveAndResize(new(x, y, clientWidth + frameWidth, desiredHeight + frameHeight));
        root.UpdateLayout();
        UpdateDragRegions();
    }

    private void UpdateDragRegions()
    {
        if (!root.IsLoaded || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        double dpi = root.XamlRoot.RasterizationScale;
        int width = (int)Math.Round(root.ActualWidth * dpi), height = (int)Math.Round(root.ActualHeight * dpi);
        var caption = new List<RectInt32> { new(0, 0, width, height) };
        var controls = actions.Children.OfType<FrameworkElement>()
            .Concat(strip.Children.OfType<FrameworkElement>()).Append(signalHousing);
        if (brands.Visibility == Visibility.Visible)
            controls = controls.Concat([appWebsiteButton, manifestoButton]);
        foreach (var control in controls)
        {
            if (control.Visibility != Visibility.Visible) continue;
            var rect = control.TransformToVisual(root).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
            Exclude((int)Math.Floor(rect.Left * dpi), (int)Math.Floor(rect.Top * dpi),
                (int)Math.Ceiling(rect.Right * dpi), (int)Math.Ceiling(rect.Bottom * dpi));
        }
        if (viewport.ScrollableHeight > 0) Exclude(width - (int)Math.Ceiling(14 * dpi), 0, width, height);
        nonClientInput ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        nonClientInput.SetRegionRects(NonClientRegionKind.Caption, caption.ToArray());

        void Exclude(int left, int top, int right, int bottom)
        {
            var remaining = new List<RectInt32>();
            foreach (var box in caption)
            {
                int x2 = box.X + box.Width, y2 = box.Y + box.Height;
                int l = Math.Max(left, box.X), t = Math.Max(top, box.Y);
                int r = Math.Min(right, x2), b = Math.Min(bottom, y2);
                if (l >= r || t >= b) { remaining.Add(box); continue; }
                Add(box.X, box.Y, x2, t);
                Add(box.X, b, x2, y2);
                Add(box.X, t, l, b);
                Add(r, t, x2, b);
            }
            caption = remaining;

            void Add(int x, int y, int endX, int endY)
            {
                if (x < endX && y < endY) remaining.Add(new(x, y, endX - x, endY - y));
            }
        }
    }

    private void ApplyTopmost()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = settings.AlwaysOnTop;
        if (pinButton.Content is FontIcon icon) icon.Glyph = settings.AlwaysOnTop ? "\uE77A" : "\uE718";
        string label = settings.AlwaysOnTop ? "Unpin from always on top" : "Pin on top";
        ToolTipService.SetToolTip(pinButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pinButton, label);
    }

    private static nint HorizontalResize(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0082) RemoveWindowSubclass(hwnd, ResizeProcedure, id); // WM_NCDESTROY
        nint result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message != 0x0084) return result; // WM_NCHITTEST
        return (int)result switch
        {
            12 or 15 => 18, // Top and bottom borders cannot resize.
            13 or 16 => 10,
            14 or 17 => 11,
            _ => result
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, WindowSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, WindowSubclass callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
