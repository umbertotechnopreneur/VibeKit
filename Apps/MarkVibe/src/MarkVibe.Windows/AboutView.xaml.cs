/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; localized product, author and diagnostic actions
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Displays MarkVibe's publication details inside the window's native About dialog.</summary>
public sealed partial class AboutView : UserControl
{
    private readonly Func<Task<string>>? _email;

    /// <summary>Resolves strings and destinations for this opening and shows diagnostics when available.</summary>
    internal AboutView(Func<Task<string>>? email)
    {
        InitializeComponent();
        _email = email;
        VersionLabel.Text = T("Version") + " " + WindowsPublication.Version();
        Purpose.Text = T("I built MarkVibe to read local Markdown, explore links between documents, and make the workspace my own.");
        Responsibility.Text = T("I created this software with AI assistance. I define its purpose, guide the implementation, and take responsibility for checking functionality, performance, and security.");
        ProjectLinks.Children.Add(WindowsPublication.Link("MarkVibe on GitHub", WindowsPublication.Repository));
        ProjectLinks.Children.Add(WindowsPublication.Link("VibeWare on GitHub", WindowsPublication.Initiative));
        ProjectLinks.Children.Add(WindowsPublication.Link("umbertogiacobbi.biz", WindowsPublication.Author));
        DiagnosticsHeading.Text = T("Diagnostics");
        EmailLabel.Text = T("Email diagnostics…");
        AutomationProperties.SetName(EmailButton, EmailLabel.Text);
        DiagnosticsPanel.Visibility = email == null ? Visibility.Collapsed : Visibility.Visible;

        var manifesto = T("Participates in VibeWare. Read the manifesto");
        ManifestoBadge.NavigateUri = new Uri(WindowsPublication.Manifesto);
        AutomationProperties.SetName(ManifestoBadge, manifesto);
        ToolTipService.SetToolTip(ManifestoBadge, manifesto);
        var links = new NativeWrapPanel();
        links.Children.Add(WindowsPublication.Link("Manifesto", WindowsPublication.Manifesto));
        links.Children.Add(WindowsPublication.Link("MIT license", WindowsPublication.License));
        links.Children.Add(WindowsPublication.Link("Terms", WindowsPublication.Terms));
        links.Children.Add(WindowsPublication.Link("Privacy", WindowsPublication.Privacy));
        LegalLinks.Content = links;
    }

    /// <summary>Keeps the reading order while moving the links below the credits on narrow viewports.</summary>
    private void ArrangeDetails(object sender, SizeChangedEventArgs args)
    {
        var wide = args.NewSize.Width >= 540;
        Details.ColumnDefinitions[1].Width = wide ? new GridLength(0.8, GridUnitType.Star) : new GridLength(0);
        Details.ColumnSpacing = wide ? 24 : 0;
        Details.RowSpacing = wide ? 0 : 12;
        Grid.SetColumn(ProjectPanel, wide ? 1 : 0);
        Grid.SetRow(ProjectPanel, wide ? 0 : 1);
    }

    /// <summary>Prepares diagnostics only on activation and announces progress and completion.</summary>
    private async void EmailDiagnostics(object sender, RoutedEventArgs args)
    {
        if (_email == null) { return; }
        EmailButton.IsEnabled = false;
        DiagnosticsStatus.Visibility = Visibility.Visible;
        DiagnosticsStatus.Text = T("Preparing the diagnostics ZIP…");
        try { DiagnosticsStatus.Text = await _email(); }
        catch (Exception) { DiagnosticsStatus.Text = T("Could not create a diagnostics ZIP or open email."); }
        finally { EmailButton.IsEnabled = true; }
    }
}
