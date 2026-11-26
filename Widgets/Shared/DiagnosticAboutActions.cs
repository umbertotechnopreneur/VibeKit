using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Tools.Widgets;

public static class DiagnosticAboutActions
{
    public static StackPanel Create(string appId, string appName)
    {
        string? archive = null;
        var heading = new TextBlock { Text = "Support diagnostics", FontSize = 16, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        var status = new TextBlock
        {
            Text = "Create a ZIP with the past seven days of app logs.", TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var panel = new StackPanel { Spacing = 8 };
        var create = new Button { Content = "Create log ZIP" };
        var show = new Button { Content = "Show ZIP", IsEnabled = false };
        var email = new Button { Content = "Open email draft", IsEnabled = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(create);
        actions.Children.Add(show);
        actions.Children.Add(email);
        panel.Children.Add(heading);
        panel.Children.Add(status);
        panel.Children.Add(actions);

        panel.SizeChanged += (_, args) =>
        {
            var unconstrained = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            double required = actions.Spacing * (actions.Children.Count - 1);
            foreach (var button in actions.Children)
            {
                button.Measure(unconstrained);
                required += button.DesiredSize.Width;
            }
            actions.Orientation = args.NewSize.Width < required ? Orientation.Vertical : Orientation.Horizontal;
        };

        void SetBusy(bool busy)
        {
            create.IsEnabled = !busy;
            show.IsEnabled = email.IsEnabled = !busy && archive is not null;
        }

        create.Click += async (_, _) =>
        {
            SetBusy(true);
            try
            {
                archive = await Task.Run(() => VibeWareDiagnostics.CreateSupportArchive(appId));
                status.Text = "ZIP ready. Review its contents before sharing.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status.Text = error is InvalidOperationException ? error.Message : "The log ZIP could not be saved.";
            }
            finally { SetBusy(false); }
        };
        show.Click += (_, _) =>
        {
            if (archive is null) return;
            try { VibeWareDiagnostics.RevealArchive(archive); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            { status.Text = $"Could not open the ZIP location. File saved to {archive}"; }
        };
        email.Click += async (_, _) =>
        {
            if (archive is not { } path) return;
            SetBusy(true);
            try
            {
                var result = await Task.Run(() => VibeWareDiagnostics.OpenEmailDraft(appName, path));
                status.Text = result switch
                {
                    SupportEmailResult.ManualAttachment => "Attach the ZIP to the email draft using Show ZIP.",
                    SupportEmailResult.NoEmailApp => $"No email app opened. You can attach the ZIP and email it to {VibeWareDiagnostics.SupportAddress}.",
                    _ => "The ZIP remains available for review and sharing."
                };
            }
            catch (IOException) { status.Text = "Create a new ZIP before opening the email draft."; }
            finally { SetBusy(false); }
        };
        return panel;
    }
}
