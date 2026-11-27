using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private static ComboBox? FindPresetSelector(DependencyObject node)
    {
        if (node is ComboBox combo && AutomationProperties.GetAutomationId(combo) == "PresetSelector") return combo;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindPresetSelector(VisualTreeHelper.GetChild(node, i)) is { } match) return match;
        return null;
    }

    private async Task VerifyDiscoverabilityUiAsync()
    {
        if (!UiSmoke.Enabled) throw new InvalidOperationException("Isolated verification required.");
        var pages = navigation.MenuItems.Cast<NavigationViewItem>().ToArray();
        var previousSize = AppWindow.ClientSize;
        var palette = (Grid)((Expander)paletteSurface.Content).Content;
        var comparison = (Button)palette.Children[6];
        var tooltip = (ToolTip)ToolTipService.GetToolTip(comparison);
        var tooltipLayout = (Grid)tooltip.Content;
        var labels = ((StackPanel)tooltipLayout.Children[1]).Children.OfType<TextBlock>().ToArray();
        var comparisonScene = StudioCatalog.Default.Scenes.Single(scene => scene.Name == "Comparison A-B");
        string previousLanguage = localization.Language;
        string originalTheme = settings.Theme;
        var translations = StudioTranslations.LoadDefault();
        foreach (UiLanguage language in StudioTranslations.Languages)
        {
            localization.Language = language.Code;
            if (tooltipLayout.Children[0] is not FontIcon ||
                !labels.Select(label => label.Text).SequenceEqual(
                    new[] { comparisonScene.Title, SceneStyles[6].Subtitle, comparisonScene.Description, ActionDescriptions["Add scene"] }
                        .Select(text => translations.Text(text, language.Code))) ||
                !Equals(ToolTipService.GetToolTip(connect), translations.Text(ActionDescriptions["Connect"], language.Code)))
                throw new InvalidOperationException("Scene and action tooltips must use the current language.");
        }

        int previousSelection = languageSelector.SelectedIndex;
        languageSelector.SelectedIndex = 1;
        SelectTheme("Light", persist: false);
        try
        {
            double scale = root.XamlRoot.RasterizationScale;
            foreach (int width in new[] { 450, 740, 1100 })
            {
                languageSelector.SelectedIndex = width == 450 ? 4 : width == 740 ? 0 : 1;
                SelectTheme(width == 450 ? "Dark" : "Light", persist: false);
                AppWindow.ResizeClient(new((int)(width * scale), (int)(780 * scale)));
                foreach (var page in pages)
                {
                    navigation.SelectedItem = page;
                    await Task.Delay(420);
                    root.UpdateLayout();
                    var scroll = (ScrollViewer)page.Tag;
                    if (!ReferenceEquals(navigation.Content, scroll) || scroll.ScrollableWidth > 1)
                        throw new InvalidOperationException($"Page navigation or width failed at {width} DIPs: {page.Content}.");
                    if (page == pages[0])
                    {
                        var bottom = languageSelector.TransformToVisual(root).TransformPoint(new(0, languageSelector.ActualHeight)).Y;
                        var navigationTop = navigation.TransformToVisual(root).TransformPoint(new(0, 0)).Y;
                        if (bottom > navigationTop + 1)
                            throw new InvalidOperationException("The language picker overlaps the page navigation.");
                        var hostPoint = host.TransformToVisual(root).TransformPoint(new(0, 0));
                        var portPoint = port.TransformToVisual(root).TransformPoint(new(0, 0));
                        if (hostPoint.X >= portPoint.X && Math.Abs(hostPoint.Y - portPoint.Y) < 2)
                            throw new InvalidOperationException("OBS endpoint fields overlap.");
                    }
                    if (page == pages[2] || page == pages[3])
                    {
                        var selector = FindPresetSelector(scroll) ?? throw new InvalidOperationException("Preset selector is missing.");
                        string expected = localization.Text(page == pages[2] ? "Wide" : "Overview");
                        if (!Equals(selector.SelectionBoxItem, expected))
                            throw new InvalidOperationException($"The displayed preset did not switch to {localization.Language}.");
                    }
                    if (page == pages[3])
                    {
                        var groups = vignettes.Values.Select(pair => pair.Slider.TransformToVisual(root).TransformPoint(new(0, 0))).ToArray();
                        if (width == 1100 && groups.Select(point => Math.Round(point.X)).Distinct().Count() != groups.Length)
                            throw new InvalidOperationException("Wide vignette controls failed to form independent columns.");
                    }
                    if (page == pages[4])
                    {
                        var field = projectFields["Name"];
                        var caption = AutomationProperties.GetLabeledBy(field) as TextBlock
                            ?? throw new InvalidOperationException("Project name is missing its label.");
                        var info = ((Grid)caption.Parent).Children.OfType<Button>().Single();
                        string help = AutomationProperties.GetHelpText(field);
                        if (string.IsNullOrWhiteSpace(help) || ToolTipService.GetToolTip(info) is not ToolTip { Content: TextBlock tip }
                            || tip.Text != help || tip.TextWrapping != TextWrapping.Wrap)
                            throw new InvalidOperationException("Field help is missing or in the wrong language.");
                        UiSmoke.Invoke(info);
                        var flyout = (Flyout)info.Flyout;
                        try { await UiSmoke.WaitForAsync(() => flyout.IsOpen && ((TextBlock)flyout.Content).Text == help); }
                        finally { flyout.Hide(); }
                    }
                    if (UiSmoke.PreviewDirectory is { } previews)
                        await SaveElementAsync(root, previews, $"page-{Array.IndexOf(pages, page)}-{width}.png");
                }
            }
            navigation.SelectedItem = workflowPage;
            if (UiSmoke.PreviewDirectory is { } directory)
            {
                ((Expander)paletteSurface.Content).IsExpanded = true;
                await Task.Delay(180);
                ((ScrollViewer)workflowPage.Tag).ScrollToVerticalOffset(paletteSurface.TransformToVisual(root).TransformPoint(new(0, 0)).Y);
                await Task.Delay(180);
                ToolTipService.SetPlacementTarget(comparison, comparison);
                tooltip.IsOpen = true;
                try
                {
                    await UiSmoke.WaitForAsync(() => tooltip.ActualWidth > 30);
                    if (tooltipLayout.ActualWidth < 24 || labels.Any(label => label.ActualHeight < 8))
                        throw new InvalidOperationException("The scene tooltip did not lay out all its fields.");
                }
                finally { tooltip.IsOpen = false; }
            }
        }
        finally
        {
            AppWindow.ResizeClient(previousSize);
            SelectTheme(originalTheme, persist: false);
            languageSelector.SelectedIndex = previousSelection;
            localization.Language = previousLanguage;
        }
    }

    private async Task VerifyDeckExchangeAsync()
    {
        if (!UiSmoke.Enabled || LocalSettings.VerificationDirectory is null)
            throw new InvalidOperationException("Isolated deck verification required.");
        Directory.CreateDirectory(DeckDirectory);
        var connected = Path.Combine(DeckDirectory, "dock-v1");
        File.WriteAllText(connected, "D200H");
        settings.UseUlanziDeck = true;
        try
        {
            await UpdateDeckAsync();
            var first = JsonSerializer.Deserialize<DeckState>(File.ReadAllBytes(DeckStatePath), SettingsStore.JsonOptions);
            if (first is not { Version: 1, Running: true, Recording: false } ||
                deckOption.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The rehearsal countdown was not shared with the connected deck.");
            var path = Path.Combine(DeckDirectory, "action-verify.json");
            void Send(long ticket) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(
                new DeckAction(deckInstance, deckRun, ticket, "add10"), SettingsStore.JsonOptions));
            Send(first.Ticket - 1);
            int before = player.RemainingSeconds;
            await UpdateDeckAsync();
            if (player.RemainingSeconds > before) throw new InvalidOperationException("A stale deck action extended the scene.");
            Send(first.Ticket);
            await UpdateDeckAsync();
            if (player.RemainingSeconds < before + 9 || File.Exists(path))
                throw new InvalidOperationException("The current deck action did not extend the scene exactly once.");
            File.Delete(connected);
            await UpdateDeckAsync();
            if (File.Exists(DeckStatePath) || deckOption.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("The deck stayed enabled after disconnecting.");
        }
        finally
        {
            settings.UseUlanziDeck = false;
            ClearDeckState();
        }
    }
}
