/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native command menu iconography
 * Human guidance: align colored icons consistently in document and workspace menus
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkVibe.Windows;

public sealed partial class DesktopWindow
{
    /// <summary>Gives persistent shell and folder menus the same icon sizing and theme treatment.</summary>
    private void ApplyShellMenuIcons()
    {
        RecentButton.Icon = MenuIcon("recent");
        ExportButton.Icon = MenuIcon("Export");
        AiButton.Icon = MenuIcon("assistant");
        AboutButton.Icon = MenuIcon("about");
        GoUpFolderMenuItem.Icon = MenuIcon("up");
        RefreshFolderMenuItem.Icon = MenuIcon("refresh");
    }

    /// <summary>Creates a Fluent glyph in the native menu icon slot, inheriting system ink in contrast themes.</summary>
    private FontIcon MenuIcon(string action)
    {
        var (glyph, ink) = action switch
        {
            "recent" => ('\uE823', 0x805929),
            "Export" => ('\uE72D', 0x276D74),
            "assistant" => ('\uE8BD', 0x77509D),
            "about" => ('\uE946', 0x446A9A),
            "up" => ('\uE74A', 0x92703B),
            "refresh" => ('\uE72C', 0x257566),
            "copySelection" => ('\uE8C8', 0x596791),
            "copyFile" => ('\uE8A5', 0x486C88),
            "find" => ('\uE721', 0x277575),
            "column" => ('\uE8A4', 0x685F87),
            "expandedColumn" => ('\uE9A6', 0x795A78),
            "sourceHere" or "exportHtml" => ('\uE943', 0x4B7060),
            "copyPath" or "copySection" => ('\uE71B', 0x73609D),
            "browser" => ('\uE774', 0x356F92),
            "reveal" or "sample" => ('\uE8B7', 0x946923),
            "terminal" or "copyCode" => ('\uE756', 0x596781),
            "delete" => ('\uE74D', 0xAA434A),
            "appearance" => ('\uE790', 0x886438),
            "paper" => ('\uE7C3', 0x996D45),
            "exportPdf" => ('\uEA90', 0xA94D42),
            _ => ('\uE8C8', 0x567379)
        };
        var icon = new FontIcon { Glyph = glyph.ToString(), FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (!_accessibility.HighContrast)
        {
            var dark = IsDark;
            /// <summary>Softens the glyph ink against dark menu surfaces.</summary>
            byte Channel(int shift)
            {
                var value = (ink >> shift) & 255;
                return (byte)(dark ? value + (255 - value) * 0.56 : value);
            }
            icon.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, Channel(16), Channel(8), Channel(0)));
        }
        return icon;
    }
}
