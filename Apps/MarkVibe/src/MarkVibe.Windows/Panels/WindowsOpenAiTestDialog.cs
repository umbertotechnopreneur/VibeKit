/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native OpenAI connection test transcript
 * Human guidance: show a real test reply in a simple Acrylic dialog
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Globalization;
using MarkVibe.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Shows one explicit connection check, its actual message and its plain-text reply.</summary>
internal static class WindowsOpenAiTestDialog
{
    /// <summary>Runs one bounded request after opening. Closing the dialog cancels the request and animation.</summary>
    public static async Task ShowAsync(XamlRoot root, string? candidate, CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var prompt = T(OpenAiKeyRules.DiagnosticPrompt);
        var transcript = new StackPanel { Spacing = 8 };
        transcript.Children.Add(Speaker("MarkVibe"));
        transcript.Children.Add(Message(prompt, 14));
        var assistant = Speaker("OpenAI");
        assistant.Margin = new Thickness(0, 12, 0, 0);
        assistant.Visibility = Visibility.Collapsed;
        transcript.Children.Add(assistant);
        var progress = new ProgressBar { IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        transcript.Children.Add(progress);
        var reply = Message("", 14);
        reply.IsTextSelectionEnabled = true;
        reply.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(reply, "OpenAiProbeReply");
        transcript.Children.Add(reply);
        var status = Message(T("Testing connection…"), 13);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        transcript.Children.Add(status);
        var scroll = WindowsDialogs.Scroll(transcript);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Content = scroll,
            CloseButtonText = T("Close"), DefaultButton = ContentDialogButton.Close
        };
        WindowsDialogs.SetTitle(dialog, T("Test OpenAI key"));
        // Preserve the system contrast theme and provide an opaque fallback when transparency is disabled.
        var host = (FrameworkElement)root.Content;
        var theme = host.ActualTheme;
        if (!new AccessibilitySettings().HighContrast)
        {
            var tint = theme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
            var material = new AcrylicBrush { TintColor = tint, TintOpacity = .72, FallbackColor = theme == ElementTheme.Dark
                ? global::Windows.UI.Color.FromArgb(255, 37, 39, 44) : global::Windows.UI.Color.FromArgb(255, 248, 249, 251) };
            dialog.Background = material;
            dialog.Resources["ContentDialogBackground"] = material;
        }
        Task? request = null;
        dialog.Opened += (_, _) => request = RunTestAsync();
        dialog.Closing += (_, _) => operation.Cancel();
        using var closing = lifetime.Register(() => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
        try
        {
            await WindowsDialogs.ShowScrollableAsync(dialog, scroll, 510);
        }
        finally
        {
            operation.Cancel();
            if (request != null) { await request; }
        }

        /// <summary>Presents only the reply returned by the service or its sanitized diagnostic.</summary>
        async Task RunTestAsync()
        {
            try
            {
                var text = await OpenAiKeySettings.ProbeAsync(candidate, prompt, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                progress.Visibility = Visibility.Collapsed;
                assistant.Visibility = Visibility.Visible;
                reply.Visibility = Visibility.Visible;
                status.Visibility = Visibility.Collapsed;
                status.Text = "";
                await RevealAsync(reply, text, operation.Token);
                status.Text = T("OpenAI returned a test reply.");
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            catch (WorkspaceException error)
            {
                status.Text = T(error.Message);
            }
            catch (Exception)
            {
                status.Text = T("The connection test could not be completed.");
            }
            finally
            {
                progress.IsIndeterminate = false;
                progress.Visibility = Visibility.Collapsed;
                status.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>Labels the two speakers without separate panels or chat bubbles.</summary>
    private static TextBlock Speaker(string name)
    {
        var label = Message(name, 16);
        label.FontWeight = FontWeights.Medium;
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        return label;
    }

    /// <summary>Wraps transcript copy using the current application font and native foreground.</summary>
    private static TextBlock Message(string text, double size) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Reveals whole graphemes in a short sequence and respects reduced-motion preferences.</summary>
    private static async Task RevealAsync(TextBlock reply, string text, CancellationToken token)
    {
        if (!new UISettings().AnimationsEnabled)
        {
            reply.Text = text;
            return;
        }
        var starts = StringInfo.ParseCombiningCharacters(text);
        var step = Math.Max(1, (starts.Length + 35) / 36);
        for (var index = step; index < starts.Length; index += step)
        {
            token.ThrowIfCancellationRequested();
            reply.Text = text[..starts[index]];
            await Task.Delay(22, token);
        }
        reply.Text = text;
    }
}
