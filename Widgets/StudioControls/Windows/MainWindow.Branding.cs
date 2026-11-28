using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    internal static string IconPath => Path.Combine(AppContext.BaseDirectory, "StudioControls.ico");
    private Window? about;

    internal void ShowFromTray()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    internal void ShowAbout()
    {
        if (about is null)
        {
            var dialog = new Window { Title = localization.Text("About") + " Studio Controls" };
            dialog.AppWindow.SetIcon(IconPath);
            dialog.AppWindow.ResizeClient(new(380, 300));
            if (dialog.AppWindow.Presenter is OverlappedPresenter frame)
                frame.IsResizable = frame.IsMaximizable = false;
            var body = new StackPanel
            {
                Padding = new Thickness(20), Spacing = 8, RequestedTheme = root.RequestedTheme,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            body.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/icons/hicolor/256x256/apps/biz.vibeware.StudioControls.png")),
                Width = 96, Height = 96
            });
            body.Children.Add(new TextBlock
            {
                Text = "Studio Controls", FontSize = 23, HorizontalAlignment = HorizontalAlignment.Center
            });
            body.Children.Add(new HyperlinkButton
            {
                Content = "Umberto Giacobbi", NavigateUri = new Uri("https://umbertogiacobbi.biz"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var close = new Button { Content = localization.Text("Close"), HorizontalAlignment = HorizontalAlignment.Center };
            Help(close, localization.Text(ActionDescriptions["Close"]));
            close.Click += (_, _) => dialog.Close();
            body.Children.Add(close);
            dialog.Content = body;
            Closed += CloseAbout;
            dialog.Closed += (_, _) => { Closed -= CloseAbout; about = null; };
            about = dialog;

            void CloseAbout(object sender, WindowEventArgs args) => dialog.Close();
        }
        if (about.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        about.Activate();
    }
}
