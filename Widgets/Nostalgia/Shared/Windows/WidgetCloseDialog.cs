// SPDX-License-Identifier: MIT
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Tools.Widgets;

public enum WidgetCloseChoice { Cancel, Minimize, Quit }

/// <summary>Owns one close prompt per studio window. The host verifies its tray and performs the chosen action.</summary>
public sealed class WidgetCloseDialog
{
    private ContentDialog? active;
    private bool explicitQuit;

    /// <summary>Brings an existing prompt back to the foreground.</summary>
    public bool FocusIfOpen(Window owner)
    {
        if (active is null) return false;
        owner.Activate();
        active.Focus(FocusState.Programmatic);
        return true;
    }

    // A tray Quit or memory shutdown goes straight to the host's usual quit path after the modal is dismissed.
    public bool RequestQuitIfOpen()
    {
        if (active is null) return false;
        explicitQuit = true;
        active.Hide();
        return true;
    }

    /// <summary>Prompts once for the close action on the studio window.</summary>
    public async Task<WidgetCloseChoice> ChooseAsync(Window owner, string product, bool canMinimize,
        string? additionalInformation = null)
    {
        if (active is not null) throw new InvalidOperationException("A close dialog is already open for this studio.");
        if (owner.Content is not FrameworkElement { XamlRoot: { } xamlRoot } root)
            throw new InvalidOperationException("The studio must have a loaded XAML root to show close options.");

        const string explanation = "Minimize to the notification area to keep the app running. Quitting stops the effect and closes the app.";
        string message = additionalInformation is null ? explanation : explanation + "\n\n" + additionalInformation;
        if (!canMinimize) message += "\n\nThe notification area is unavailable.";
        active = new ContentDialog
        {
            Title = $"Close {product}?", Content = message,
            PrimaryButtonText = "Minimize to tray", IsPrimaryButtonEnabled = canMinimize,
            SecondaryButtonText = "Quit", CloseButtonText = "Cancel",
            DefaultButton = canMinimize ? ContentDialogButton.Primary : ContentDialogButton.Close,
            XamlRoot = xamlRoot, RequestedTheme = root.ActualTheme
        };
        try
        {
            var result = await active.ShowAsync();
            if (explicitQuit || result == ContentDialogResult.Secondary) return WidgetCloseChoice.Quit;
            return result == ContentDialogResult.Primary ? WidgetCloseChoice.Minimize : WidgetCloseChoice.Cancel;
        }
        finally
        {
            active = null;
            explicitQuit = false;
        }
    }
}
