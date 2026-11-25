using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Graphics;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private const int MinimumWidth = 300;
    private const int LargerWidgetPercent = 15;
    private const double TileSpacing = 8;
    private readonly Grid widgetBody = new() { Padding = new(8) };
    private readonly Grid layout = new();
    private readonly Viewbox layoutView = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer viewport = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private Module[] modules = [];
    private bool layoutQueued;
    private bool fitHeight = true;
    private SizeInt32? requestedSize;
    private SizeInt32? expandedSize;

    private sealed record Module(string Key, string Label, string Description, FrameworkElement Element, IWidgetTile Tile, bool Detail = false);

    private void BuildResponsiveLayout()
    {
        modules =
        [
            new("allowance", "Allowance remaining", "Percentage still available in the selected Codex quota.", usageLink, remaining),
            new("credits", "Remaining Credits", "Remaining credit balance. The bar compares it with the highest balance seen for this account during this session.", credits, credits),
            new(CreditPercentageTile.FieldKey, CreditPercentageTile.FieldLabel,
                "Credits left as a percentage of the highest balance seen for this account during this session.", creditPercentage, creditPercentage),
            new("estimate", "Time to run out", "Estimated time until the selected quota runs out at your observed usage rate.", forecastLink, forecast),
            new("vouchers", "Available vouchers", "Vouchers you can use now for a manual quota reset. Click the count for their expiration dates.", vouchersButton, vouchers),
            new("first-reset", "Next voucher expiry", "When the next available voucher with a reported date expires. A dash means no date is available.", voucherExpiry, voucherExpiry),
            new("clock", "Auto reset clock", "Alternates between a countdown and the date when the selected quota renews automatically.", countdownLink, resetClock),
            new("five-hour", "5-hour allowance", "Percentage remaining in the five-hour quota.", fiveHour, fiveHour, true),
            new("five-reset", "5-hour auto reset in", "Hours and minutes until the five-hour quota renews automatically.", fiveReset, fiveReset, true),
            new("weekly", "Weekly allowance", "Percentage remaining in the weekly quota.", weekly, weekly, true),
            new("weekly-reset", "Weekly auto reset", "Local date and time when the weekly quota renews automatically.", weeklyReset, weeklyReset, true),
            new("detail-estimate", "Run-out estimate", "Same usage estimate as Time to run out, shown in the expanded details.", detailsEstimate, detailsEstimate, true),
            new("time-to-reset", "Selected auto reset in", "Time left until the selected quota renews automatically. The Auto reset clock also shows this countdown.", timeToReset, timeToReset, true),
            new("forecast", "Will the quota last?", "Compares the estimated time to run out with the automatic renewal of the selected quota.", forecastMessage, forecastMessage, true)
        ];
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        layout.Children.Add(signalHousing);
        Grid.SetColumn(strip, 1);
        layout.Children.Add(strip);
        layoutView.Child = layout;
        viewport.Content = layoutView;
        viewport.ViewChanged += (_, _) => QueueLayout();
        widgetBody.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        widgetBody.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        widgetBody.Children.Add(viewport);
        Grid.SetColumn(menuButton, 1);
        widgetBody.Children.Add(menuButton);
    }

    private IEnumerable<Module> OrderedModules()
    {
        var byKey = modules.ToDictionary(module => module.Key);
        var order = settings.FieldOrder ?? [];
        bool hasCreditPercentagePosition = order.Contains(CreditPercentageTile.FieldKey);
        foreach (var key in order)
        {
            if (key is null || !byKey.Remove(key, out var module)) continue;
            yield return module;
            if (key == "credits" && !hasCreditPercentagePosition && byKey.Remove(CreditPercentageTile.FieldKey, out var percentageModule))
                yield return percentageModule;
        }
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
        var visible = OrderedModules().Where(module => ModuleEnabled(module.Key) && (!module.Detail || (detailsOpen && settings.EnableDetailsBar)))
            .Select(module => module.Element).ToArray();
        if (!strip.Children.SequenceEqual(visible))
        {
            strip.Children.Clear();
            foreach (var element in visible) strip.Children.Add(element);
            fitHeight = true;
        }
        foreach (var module in modules)
            module.Tile.SetLabelPosition(settings.LabelPosition);
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

    private void RequestWidgetFit(SizeInt32? restore = null)
    {
        fitHeight = true;
        requestedSize = restore;
        QueueLayout();
    }

    private void FitWindow()
    {
        double dpi = root.XamlRoot.RasterizationScale;
        var size = AppWindow.ClientSize;
        var outer = AppWindow.Size;
        int frameWidth = outer.Width - size.Width;
        int frameHeight = outer.Height - size.Height;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        int maxWidth = workArea is { } display ? Math.Max(1, display.Width - frameWidth) : int.MaxValue;
        int maxHeight = workArea is { } screen ? Math.Max(1, screen.Height - frameHeight) : int.MaxValue;
        int dockClientWidth = 0;
        if (IsDocked)
        {
            if (!desktopAppBar!.TryGetMonitorBounds(out var monitor))
            {
                DockFailed("Windows could not find a display for the docked widget.");
                return;
            }
            maxWidth = Math.Max(1, monitor.Width - frameWidth);
            maxHeight = Math.Max(1, monitor.Height - frameHeight);
            dockClientWidth = Math.Clamp((dockBounds?.Width ?? monitor.Width) - frameWidth, 1, maxWidth);
        }
        bool compact = IsCompact;
        var restore = requestedSize;
        bool resizeHeight = fitHeight;
        requestedSize = null;
        fitHeight = false;

        var tiles = modules.Where(module => strip.Children.Contains(module.Element))
            .Select(module => module.Tile).ToArray();
        double fixedWidth = widgetBody.Padding.Left + widgetBody.Padding.Right + TileSpacing + MenuButtonWidth;
        double gaps = Math.Max(0, tiles.Length - 1) * TileSpacing;
        double minimumTileWidth = tiles.Sum(tile => tile.MinimumTileWidth);
        double preferredWidth = tiles.Sum(tile => tile.PreferredWidth);
        int clientWidth = IsDocked ? dockClientWidth : restore?.Width ?? size.Width;
        double zoom = settings.BigSize && clientWidth >= 375 * dpi ? 1 + LargerWidgetPercent / 100d : 1;
        double minimumWidth = compact
            ? Math.Max(MinimumWidth, fixedWidth + (gaps + Math.Max(minimumTileWidth, preferredWidth * .6)) * zoom)
            : MinimumWidth;
        int minimum = IsDocked ? 1 : Math.Min(maxWidth, (int)Math.Ceiling(minimumWidth * dpi));
        if (!IsDocked)
        {
            clientWidth = Math.Max(minimum, clientWidth);
            if (resizeHeight || restore.HasValue)
                clientWidth = Math.Min(clientWidth, maxWidth);
        }
        double contentWidth = Math.Max(1, clientWidth / dpi - fixedWidth);
        if (compact && tiles.Length > 0)
            zoom = Math.Min(zoom, contentWidth / (gaps + minimumTileWidth));

        double width = contentWidth / zoom;
        bool showSignal = !compact && tiles.Length > 0;
        signalHousing.Visibility = showSignal ? Visibility.Visible : Visibility.Collapsed;
        signalHousing.Width = width + fixedWidth < 530 ? 30 : 44;
        double signalWidth = showSignal ? TileSpacing + signalHousing.Width : 0;
        double tileWidth = Math.Max(FlipTile.MinimumWidth, width - signalWidth);
        strip.SingleRow = compact;
        strip.Width = tileWidth;
        double halfRow = Math.Max(100, Math.Floor((tileWidth - TileSpacing) / 2));
        double[] tileWidths = compact
            ? FitCompactWidths(tiles, tileWidth - gaps)
            : tiles.Select(tile => Math.Min(tile.PreferredWidth, tiles.Length > 2 && tileWidth < 320 && tile.PreferredWidth <= 160
                ? Math.Min(tileWidth, halfRow) : tileWidth)).ToArray();
        double fontSize = 20;
        for (int i = 0; i < tiles.Length; i++)
            fontSize = Math.Min(fontSize, tiles[i].FontLimit(tileWidths[i]));
        for (int i = 0; i < tiles.Length; i++)
            tiles[i].FitWidth(tileWidths[i], fontSize);

        layout.Width = width;
        strip.Measure(new(tileWidth, double.PositiveInfinity));
        double insets = widgetBody.Padding.Top + widgetBody.Padding.Bottom;
        double stripHeight = strip.DesiredSize.Height;
        double menuHeight = menuButton.Height;
        signalHousing.Height = Math.Min(128, stripHeight);
        layout.Height = stripHeight;
        layoutView.Width = contentWidth;
        layoutView.Height = stripHeight * zoom;
        double widgetHeight = Math.Max(layoutView.Height, menuHeight) + insets;
        int minHeight = Math.Min(maxHeight, (int)Math.Ceiling((menuHeight + insets) * dpi));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.PreferredMinimumWidth != minimum + frameWidth)
                presenter.PreferredMinimumWidth = minimum + frameWidth;
            if (presenter.PreferredMinimumHeight != minHeight + frameHeight)
                presenter.PreferredMinimumHeight = minHeight + frameHeight;
        }
        if (IsDocked)
        {
            int height = Math.Clamp((int)Math.Ceiling(widgetHeight * dpi), minHeight, maxHeight);
            if (ApplyDockBounds(height + frameHeight))
            {
                root.UpdateLayout();
                UpdateDragRegions();
            }
            return;
        }
        int desiredHeight = resizeHeight
            ? Math.Min(maxHeight, Math.Max(restore?.Height ?? 0, (int)Math.Ceiling(widgetHeight * dpi)))
            : size.Height;
        desiredHeight = Math.Max(minHeight, desiredHeight);
        var position = AppWindow.Position;
        int x = position.X, y = position.Y;
        if (resizeHeight && workArea is { } area)
        {
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - desiredHeight - frameHeight));
            if (clientWidth != size.Width)
                x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - clientWidth - frameWidth));
        }
        if (size.Height != desiredHeight || size.Width != clientWidth || x != position.X || y != position.Y)
            AppWindow.MoveAndResize(new(x, y, clientWidth + frameWidth, desiredHeight + frameHeight));
        root.UpdateLayout();
        UpdateDragRegions();
    }

    private static double[] FitCompactWidths(IWidgetTile[] tiles, double available)
    {
        double low = 0, high = 20;
        foreach (var tile in tiles)
            high = Math.Min(high, tile.FontLimit(tile.PreferredWidth));
        for (int step = 0; step < 8; step++)
        {
            double size = (low + high) / 2;
            if (tiles.Sum(tile => Math.Min(tile.PreferredWidth, tile.WidthForFont(size))) <= available)
                low = size;
            else
                high = size;
        }
        var widths = tiles.Select(tile => Math.Min(tile.PreferredWidth, tile.WidthForFont(low))).ToArray();
        double used = widths.Sum();
        double flexible = tiles.Sum(tile => tile.PreferredWidth) - used;
        double fraction = flexible > 0 ? Math.Clamp((available - used) / flexible, 0, 1) : 0;
        for (int i = 0; i < widths.Length; i++)
            widths[i] += Math.Floor((tiles[i].PreferredWidth - widths[i]) * fraction);
        return widths;
    }

    private void UpdateDragRegions()
    {
        if (!root.IsLoaded || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        if (IsDocked)
        {
            nonClientInput?.SetRegionRects(NonClientRegionKind.Caption, []);
            return;
        }
        double dpi = root.XamlRoot.RasterizationScale;
        int width = (int)Math.Round(root.ActualWidth * dpi), height = (int)Math.Round(root.ActualHeight * dpi);
        var caption = new List<RectInt32> { new(0, 0, width, height) };
        var controls = strip.Children.OfType<FrameworkElement>().Append(menuButton).Append(signalHousing);
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
        if (!IsDocked && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = settings.AlwaysOnTop;
        RefreshMenuState();
    }

}
