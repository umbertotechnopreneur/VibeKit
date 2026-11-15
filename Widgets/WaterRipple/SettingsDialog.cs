using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace WaterRipple;

// Presentation only: preferences and OS-managed startup state live in the shared models.
internal sealed class SettingsDialog : ContentDialog
{
    internal SettingsDialog(StudioModel model, XamlRoot root)
    {
        Title = "Settings";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        RequestedTheme = ElementTheme.Dark;
        XamlRoot = root;
        var content = new StackPanel { Spacing = 14, MinWidth = 280, MaxWidth = 460 };
        var startup = Ui.Toggle(content, "Start with Windows", model.State, nameof(AppState.StartWithWindows));
        Ui.Bind(startup, ToggleSwitch.IsEnabledProperty, model.State, nameof(AppState.CanManageStartup));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Ui.Bind(status, TextBlock.TextProperty, model.State, nameof(AppState.StartupStatus));
        content.Children.Add(status);
        Ui.Toggle(content, "Acrylic background", model.Settings, nameof(AppSettings.UseAcrylic));
        Ui.Slider(content, "Animation limit", model.Settings, nameof(AppSettings.AnimationFps), 15, 120, 1, "fps");
        Ui.Slider(content, "Capture limit", model.Settings, nameof(AppSettings.CaptureFps), 5, 60, 1, "fps");
        content.Children.Add(new TextBlock { Text = "Changes are saved immediately. Windows manages the startup preference.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = root.Size.Height * .60 };
        Content = scroll;
        Ui.HoverScrollBar(scroll, scroll, this);
        content.Children.Add(new TextBlock { Text = "Windows may ask permission to hide its colored screen-capture border. You can decline and keep using the app with the border.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
    }
}
