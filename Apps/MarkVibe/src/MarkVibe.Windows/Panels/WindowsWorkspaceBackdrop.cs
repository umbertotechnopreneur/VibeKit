/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native desktop glass for the navigation and status surfaces
 * Human guidance: show frosted glass under the reader's single rounded corner
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MarkVibe.Windows;

/// <summary>Provides one backdrop per workspace window, exposed by its transparent navigation and status surfaces.</summary>
internal sealed class WindowsWorkspaceBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;

    /// <summary>Uses thin desktop acrylic where supported and the standard Windows backdrop elsewhere.</summary>
    public static SystemBackdrop Create() =>
        DesktopAcrylicController.IsSupported() ? new WindowsWorkspaceBackdrop() : new MicaBackdrop();

    /// <summary>Lets XAML supply the current window theme, activation and contrast policy to the compositor.</summary>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
        _controller.AddSystemBackdropTarget(target);
    }

    /// <summary>Releases the composition target and controller when the owning window disconnects.</summary>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(target);
    }
}
