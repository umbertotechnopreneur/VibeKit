// SPDX-License-Identifier: MIT
namespace Tools.Widgets;

/// <summary>Identifies the installed product and its assets independently of the UI library.</summary>
public sealed record WidgetIdentity(string DiagnosticsId, string DisplayName, Version Version,
    string Description, string IconPath, string ProductUrl, string License)
{
    /// <summary>Reject missing product metadata before building the window.</summary>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DiagnosticsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentNullException.ThrowIfNull(Version);
        if (Version.Build < 0) throw new ArgumentException("The product version must include the build number.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(IconPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(License);
        if (!Uri.TryCreate(ProductUrl, UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("An absolute HTTPS product URL is required.");
    }
}
