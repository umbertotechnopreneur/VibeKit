/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native acrylic reader paper gallery
 * Human guidance: show generously sized samples before applying a paper texture
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Previews the reader's bundled paper tiles at their printed scale using native WinUI controls.</summary>
internal static class WindowsPaperDialog
{
    /// <summary>Returns the accepted paper ID, or null if dismissed. Highlighting a sample does not persist it.</summary>
    public static async Task<string?> ChooseAsync(XamlRoot root, string current, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var gallery = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsItemClickEnabled = false
        };
        AutomationProperties.SetName(gallery, T("Paper"));
        gallery.Items.Add(Sample(ReaderPaper.ThemeBackground, T("Theme background")));
        foreach (var paper in ReaderPaper.Options)
        {
            gallery.Items.Add(Sample(paper.Id, T(paper.Label)));
        }
        gallery.SelectedItem = gallery.Items.Cast<GridViewItem>().First(item => (string)item.Tag == current);
        var host = (FrameworkElement)root.Content;
        var accessibility = new AccessibilitySettings();
        (bool Contrast, bool Dark)? displayedMode = null;
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 12),
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
        };
        gallery.Header = note;
        var dialog = new ContentDialog
        {
            XamlRoot = root, Content = gallery, PrimaryButtonText = T("Apply paper"), CloseButtonText = T("Cancel"),
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
        };
        AutomationProperties.SetAutomationId(dialog, "ReaderPaperDialog");
        WindowsDialogs.SetTitle(dialog, T("Paper"));
        gallery.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            gallery.SelectedItem is GridViewItem selected && (string)selected.Tag != current;

        /// <summary>Reflows equal-sized samples while leaving the dialog actions outside the scroll area.</summary>
        void Arrange(XamlRoot sender, XamlRootChangedEventArgs? args)
        {
            var width = Math.Clamp(sender.Size.Width - 32, 64, 936);
            dialog.Resources["ContentDialogMinWidth"] = Math.Min(320, width);
            dialog.Resources["ContentDialogMaxWidth"] = width;
            gallery.Width = Math.Max(1, width - 48);
            gallery.MaxHeight = Math.Clamp(sender.Size.Height - 184, 64, 650);
            var available = Math.Max(1, gallery.Width - 24);
            var columns = Math.Clamp((int)(available / 178), 1, 4);
            foreach (GridViewItem item in gallery.Items)
            {
                item.Width = Math.Max(1, Math.Floor(available / columns) - 8);
            }
            Paint();
        }

        /// <summary>Refreshes the actual samples and popup Acrylic when the owner's contrast mode or theme changes.</summary>
        void Paint()
        {
            var contrast = accessibility.HighContrast;
            var dark = host.ActualTheme == ElementTheme.Dark;
            if (displayedMode == (contrast, dark)) { return; }
            displayedMode = (contrast, dark);
            note.Text = T(contrast ? "Windows contrast themes use a solid reading surface." : "These reading choices are saved for this folder.");
            if (contrast)
            {
                dialog.ClearValue(Control.BackgroundProperty);
                if (dialog.Resources.ContainsKey("ContentDialogBackground"))
                {
                    dialog.Resources.Remove("ContentDialogBackground");
                }
            }
            else
            {
                var tint = dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
                var material = new AcrylicBrush { TintColor = tint, TintOpacity = .74, FallbackColor = dark
                    ? global::Windows.UI.Color.FromArgb(255, 37, 39, 44) : global::Windows.UI.Color.FromArgb(255, 248, 249, 251) };
                dialog.Background = material;
                dialog.Resources["ContentDialogBackground"] = material;
            }
            var systemBackground = new UISettings().GetColorValue(UIColorType.Background);
            foreach (GridViewItem item in gallery.Items)
            {
                var swatch = (Border)((StackPanel)item.Content).Children[0];
                var id = (string)item.Tag;
                if (contrast || id == ReaderPaper.ThemeBackground)
                {
                    swatch.Background = new SolidColorBrush(contrast ? systemBackground :
                        dark ? global::Windows.UI.Color.FromArgb(255, 25, 29, 35) : Microsoft.UI.Colors.White);
                }
                else
                {
                    var mode = dark ? "dark" : "light";
                    var tile = new Uri($"ms-appx:///app/images/paper-{id}-{mode}.png");
                    swatch.Background = new ImageBrush { Stretch = Stretch.None, ImageSource = new BitmapImage(tile) };
                }
            }
        }

        /// <summary>Applies a changed system contrast preference on the owning UI thread.</summary>
        void ContrastChanged(AccessibilitySettings sender, object args) => dialog.DispatcherQueue.TryEnqueue(Paint);

        /// <summary>Keeps the native gallery in the same light or dark mode as the reader.</summary>
        void ThemeChanged(FrameworkElement sender, object args) => Paint();

        Arrange(root, null);
        root.Changed += Arrange;
        host.ActualThemeChanged += ThemeChanged;
        var tracksContrast = false;
        try
        {
            accessibility.HighContrastChanged += ContrastChanged;
            tracksContrast = true;
        }
        catch (System.Runtime.InteropServices.COMException error) when (error.HResult == unchecked((int)0x80070490))
        {
            // Some desktops expose only the current contrast setting. Arrange reads it again on every root change.
        }
        using var closing = token.Register(() => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
        try
        {
            var result = await WindowsDialogs.ShowAsync(dialog);
            return result == ContentDialogResult.Primary && gallery.SelectedItem is GridViewItem selected ? (string)selected.Tag : null;
        }
        finally
        {
            root.Changed -= Arrange;
            host.ActualThemeChanged -= ThemeChanged;
            if (tracksContrast) { accessibility.HighContrastChanged -= ContrastChanged; }
        }
    }

    /// <summary>Builds one keyboard-selectable item with the material's sample, accessible name and label.</summary>
    private static GridViewItem Sample(string id, string label)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new Border { Height = 77, CornerRadius = new CornerRadius(2) });
        content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var item = new GridViewItem
        {
            Tag = id, Content = content, Padding = new Thickness(8), Margin = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetAutomationId(item, $"PaperChoice-{id}");
        ToolTipService.SetToolTip(item, label);
        return item;
    }
}
