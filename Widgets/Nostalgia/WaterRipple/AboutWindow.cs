using Microsoft.UI.Xaml;
using Tools.Widgets;

namespace WaterRipple;

internal static class AboutWindow
{
    internal static void Show(Window owner, AppSettings settings) => WidgetAboutWindow.Show(owner, new WidgetIdentity(
        "WaterRipple", "Water Ripple", typeof(AboutWindow).Assembly.GetName().Version!, "A little ripple for every click. Water Ripple brings a touch of movement to your desktop.",
        "Assets/WaterRipple.ico", "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/Nostalgia/WaterRipple",
        "Proprietary"), (window, root) => Ui.AttachAppearance(window, root, settings));
}
