using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;
using Serilog;

namespace WaterRipple;

internal sealed class CaptureConsentService(AppState state)
{
    private bool checkedThisSession;
    internal async Task EnsureAsync(XamlRoot root)
    {
        // Older supported Windows builds retain the normal capture border without a consent prompt.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return;
        if (checkedThisSession || state.IsCaptureConsentOpen) return;
        checkedThisSession = true;
        state.IsCaptureConsentOpen = true;
        try
        {
            // Debug/unpackaged runs keep the normal OS policy without prompting.
            _ = Windows.ApplicationModel.Package.Current.Id;
            if (AppCapability.Create("graphicsCaptureWithoutBorder").CheckAccess() == AppCapabilityAccessStatus.Allowed) return;
            var explanation = new ContentDialog
            {
                XamlRoot = root, RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = "Hide the Windows capture border?",
                Content = "Windows marks screen capture with a colored border. To hide it, Windows may ask for permission next. You can decline and keep using Water Ripple with the border.",
                PrimaryButtonText = "Ask Windows", CloseButtonText = "Keep border",
                DefaultButton = ContentDialogButton.Close
            };
            if (await explanation.ShowAsync() != ContentDialogResult.Primary) return;
            var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Log.Information("Borderless capture consent Result={Result}", access);
            if (access != AppCapabilityAccessStatus.Allowed)
                state.StatusMessage = "Windows kept the capture border. The ripple effect can still run.";
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Borderless capture unavailable; Windows capture border policy remains active");
        }
        finally { state.IsCaptureConsentOpen = false; }
    }
}
