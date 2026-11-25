using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tools.Widgets;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private async Task<ContentDialogResult> ShowDialogAsync(string title,UIElement content,
        string? closeButtonText="Close",string? primaryButtonText=null,string? secondaryButtonText=null,
        bool isPrimaryEnabled=true,Func<bool>? onPrimary=null,double width=500,double height=590,bool about=false,
        FrameworkElement? footer=null, bool showScrollbarOnHover=false)
    {
        if(!dialogGate.Wait(0))return ContentDialogResult.None;
        var host=new Window {Title=title};
        if(!about && WidgetAcrylicBackdrop.IsSupported)host.SystemBackdrop=new WidgetAcrylicBackdrop();
        var surface=new Grid {RequestedTheme=about?ElementTheme.Light:root.ActualTheme,Background=about?WidgetAbout.Paper():root.Background};
        surface.RowDefinitions.Add(new(){Height=new(about?0:44)});surface.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});surface.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var titleBar=new StackPanel {Orientation=Orientation.Horizontal,Spacing=10,Margin=new(20,0,60,0),VerticalAlignment=VerticalAlignment.Center};
        if(!about){titleBar.Children.Add(Logo(22));titleBar.Children.Add(Text(title,16,true));surface.Children.Add(titleBar);}
        var body = new ScrollViewer
        {
            Content = content, Margin = about ? new(16, 12, 16, 8) : new(24, 12, 24, 16),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = showScrollbarOnHover ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto
        };
        if (showScrollbarOnHover)
        {
            body.Background = new SolidColorBrush(Colors.Transparent);
            body.PointerEntered += (_, _) => body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            body.PointerExited += (_, _) => body.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        Grid.SetRow(body,1);surface.Children.Add(body);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(24,0,24,20)};
        var loaded=new TaskCompletionSource();var result=new TaskCompletionSource<ContentDialogResult>();
        surface.Loaded+=(_,_)=>loaded.TrySetResult();
        bool dismissing=false;
        void Complete(ContentDialogResult choice)=>result.TrySetResult(choice);
        if(about)surface.KeyDown+=(_,e)=>{if(e.Key==global::Windows.System.VirtualKey.Escape){e.Handled=true;Complete(ContentDialogResult.None);}};
        Button Command(string label,ContentDialogResult choice,bool enabled=true)
        {
            var button=new Button {Content=label,MinWidth=100,IsEnabled=enabled};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,label);
            button.Click+=(_,_)=>{if(choice!=ContentDialogResult.Primary || onPrimary is null || onPrimary())Complete(choice);};
            return button;
        }
        if(primaryButtonText is not null)buttons.Children.Add(Command(primaryButtonText,ContentDialogResult.Primary,isPrimaryEnabled));
        if(secondaryButtonText is not null)buttons.Children.Add(Command(secondaryButtonText,ContentDialogResult.Secondary));
        if(closeButtonText is not null)buttons.Children.Add(Command(closeButtonText,ContentDialogResult.None));
        var bottom=footer??(buttons.Children.Count>0?buttons:null);
        if(bottom is not null){Grid.SetRow(bottom,2);surface.Children.Add(bottom);}
        host.Content=surface;activeDialog=host;
        host.AppWindow.Closing+=(_,e)=>{if(!dismissing){e.Cancel=true;Complete(ContentDialogResult.None);loaded.TrySetResult();}};
        nint owner=WinRT.Interop.WindowNative.GetWindowHandle(this);
        var preferences = settingsWindow;
        nint preferencesHandle = preferences is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(preferences);
        try
        {
            host.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Branding","app-icon.ico"));
            if(!about)
            {
                host.ExtendsContentIntoTitleBar=true;host.SetTitleBar(titleBar);
                host.AppWindow.TitleBar.ButtonBackgroundColor=Colors.Transparent;
                host.AppWindow.TitleBar.ButtonInactiveBackgroundColor=Colors.Transparent;
                host.AppWindow.TitleBar.ButtonForegroundColor=PrimaryText.Color;
                host.AppWindow.TitleBar.ButtonInactiveForegroundColor=SecondaryText.Color;
            }
            // An owned utility window stays above its widget without creating another taskbar entry.
            host.AppWindow.IsShownInSwitchers=false;
            SetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(host),-8,owner);
            double dpi=root.XamlRoot.RasterizationScale;var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
            int w=(int)Math.Min(width*dpi,area.Width*.94),h=(int)Math.Min(height*dpi,area.Height*.94);
            host.AppWindow.MoveAndResize(new(area.X+(area.Width-w)/2,area.Y+(area.Height-h)/2,w,h));
            if(host.AppWindow.Presenter is OverlappedPresenter p){p.IsMinimizable=false;p.IsMaximizable=false;p.IsResizable=about;}
            host.Activate();await loaded.Task;
            if(result.Task.IsCompleted)return await result.Task;
            EnableWindow(owner,false);
            if (preferencesHandle != 0 && settingsWindow == preferences) EnableWindow(preferencesHandle, false);
            if(Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(content) is Control first)first.Focus(FocusState.Programmatic);
            if(!about)Diagnostics.Record("window.preferences","Flat acrylic utility window opened; switchers=False; owned=True.");
            return await result.Task;
        }
        finally
        {
            if (preferencesHandle != 0 && settingsWindow == preferences) EnableWindow(preferencesHandle, true);
            EnableWindow(owner,true);activeDialog=null;dismissing=true;host.Close();dialogGate.Release();
            if(!closed)Activate();
        }
    }
    private async Task ShowAboutAsync()
    {
        ShowWindow();
        var initiative = new Image
        {
            Width = 64, Height = 64, Stretch = Stretch.Uniform,
            Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Branding", "VibeWare.png")))
        };
        var details = new Expander
        {
            Header = "Technical details",
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new TextBlock
            {
                Text = "Separate watchdog · 1 GB process-tree limit",
                FontFamily = MetricFont, TextWrapping = TextWrapping.Wrap
            }
        };
        var support = new StackPanel { Spacing = 12 };
        support.Children.Add(details);
        support.Children.Add(DiagnosticAboutActions.Create(Program.AppId, "OpenAI Usage"));
        var content = WidgetAbout.Create(Logo(100), initiative, "OpenAI Usage",
            $"Version {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown"}",
            "I built OpenAI Usage to see my Codex allowance, tokens and credits at a glance.",
            ProjectUrl,
            "I provide this software independently and as-is, without warranty.", support);
        content.MaxWidth = 590;
        await ShowDialogAsync("About OpenAI Usage", content, width:640, height:620, about:true);
    }
}
