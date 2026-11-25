using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenAIUsage.Windows;

// Each window owns its controller. System policy still controls fallback/high contrast.
public sealed class WidgetAcrylicBackdrop(ElementTheme theme) : SystemBackdrop
{
    public static bool IsSupported => DesktopAcrylicController.IsSupported();
    private DesktopAcrylicController? controller;
    private ElementTheme requestedTheme = theme;

    internal void SetTheme(ElementTheme theme)
    {
        if (requestedTheme == theme) return;
        requestedTheme = theme;
        ApplyAppearance();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (!IsSupported) return;
        var configuration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        controller = new DesktopAcrylicController();
        controller.SetSystemBackdropConfiguration(configuration);
        ApplyAppearance();
        controller.AddSystemBackdropTarget(target);
        Diagnostics.Record("acrylic.ready", "Native desktop acrylic connected; transparent XAML surface.");
    }

    private void ApplyAppearance()
    {
        if (controller is null) return;
        bool dark = requestedTheme == ElementTheme.Dark;
        controller.TintColor = dark ? ColorHelper.FromArgb(255, 26, 34, 43) : ColorHelper.FromArgb(255, 239, 247, 252);
        controller.FallbackColor = dark ? ColorHelper.FromArgb(255, 26, 34, 43) : ColorHelper.FromArgb(255, 243, 248, 252);
        controller.TintOpacity = dark ? .55f : .6f;
        controller.LuminosityOpacity = .65f;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose();
        controller = null;
        base.OnTargetDisconnected(target);
    }
}
