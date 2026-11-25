/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; shared theme and viewport for native dialogs
 * Human guidance: keep native dialogs usable in the app's light, dark and compact layouts
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MarkVibe.Windows;

/// <summary>Applies the owning window's presentation to native dialog popups.</summary>
internal static class WindowsDialogs
{
    /// <summary>Sets a native light-weight title with the same accessible name as the dialog.</summary>
    public static void SetTitle(ContentDialog dialog, string title)
    {
        dialog.Title = new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeights.Light,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(dialog, title);
    }

    /// <summary>Adds a localized action description for hover and assistive technology.</summary>
    public static void SetButtonDescription(ButtonBase button, string description)
    {
        ToolTipService.SetToolTip(button, description);
        AutomationProperties.SetHelpText(button, description);
    }

    /// <summary>Creates a vertically scrolling content area within the native action bar.</summary>
    public static ScrollViewer Scroll(FrameworkElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        ZoomMode = ZoomMode.Disabled
    };

    /// <summary>Limits a long dialog's content to the available window space while its actions remain visible.</summary>
    public static async Task ShowScrollableAsync(ContentDialog dialog, ScrollViewer scroll, double maximumWidth)
    {
        const double windowGutter = 32;
        const double dialogPadding = 48;
        const double titleAndActions = 184;
        const double maximumHeight = 580;
        var root = dialog.XamlRoot;
        dialog.Resources["ContentDialogMaxWidth"] = maximumWidth;

        /// <summary>Adapts the existing scroll viewport to resize and display-scale changes.</summary>
        void Arrange(XamlRoot sender, XamlRootChangedEventArgs? args)
        {
            var dialogWidth = Math.Clamp(sender.Size.Width - windowGutter, dialogPadding, maximumWidth);
            dialog.Resources["ContentDialogMinWidth"] = Math.Min(320, dialogWidth);
            scroll.Width = dialogWidth - dialogPadding;
            scroll.MaxHeight = Math.Clamp(sender.Size.Height - titleAndActions, 0, maximumHeight);
        }

        Arrange(root, null);
        root.Changed += Arrange;
        try
        {
            await ShowAsync(dialog);
        }
        finally
        {
            root.Changed -= Arrange;
        }
    }

    /// <summary>Mirrors the host theme for as long as the native popup is displayed.</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var host = dialog.XamlRoot.Content as FrameworkElement;
        using var paper = new WindowsPaperSurface.DialogLayer(dialog);

        /// <summary>Applies the current host theme to the popup's separate visual tree.</summary>
        void ApplyTheme(FrameworkElement sender, object args) => dialog.RequestedTheme = sender.ActualTheme;

        /// <summary>Refreshes the popup's resolved font when the UI preference changes.</summary>
        void ApplyFont() => WindowsUiFont.ApplyTo(dialog);

        /// <summary>Applies typography and fallback tooltips after WinUI creates the native action buttons.</summary>
        void ApplyOpenedPresentation(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            ApplyFont();
            ApplyButtonToolTips(sender);
        }

        if (host != null)
        {
            dialog.RequestedTheme = host.ActualTheme;
            host.ActualThemeChanged += ApplyTheme;
        }
        dialog.Opened += ApplyOpenedPresentation;
        WindowsUiFont.Changed += ApplyFont;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (host != null) { host.ActualThemeChanged -= ApplyTheme; }
            dialog.Opened -= ApplyOpenedPresentation;
            WindowsUiFont.Changed -= ApplyFont;
        }
    }

    /// <summary>Gives labeled dialog buttons a hover hint without inspecting credential controls.</summary>
    private static void ApplyButtonToolTips(DependencyObject root)
    {
        if (root is PasswordBox)
        {
            return;
        }

        if (root is ButtonBase button && button is not RepeatButton && ToolTipService.GetToolTip(button) == null)
        {
            var label = button.Content as string ?? AutomationProperties.GetName(button);
            if (!string.IsNullOrWhiteSpace(label))
            {
                ToolTipService.SetToolTip(button, label);
            }
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            ApplyButtonToolTips(VisualTreeHelper.GetChild(root, index));
        }
    }
}
