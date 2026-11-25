/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native command menu iconography
 * Human guidance: align colored icons consistently in document and workspace menus
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    private static FontIcon MenuIcon(string action)
    {
        var glyph = action switch
        {
            "recent" => '\uE823',
            "Export" => '\uE72D',
            "assistant" => '\uE8BD',
            "about" => '\uE946',
            "up" => '\uE74A',
            "refresh" => '\uE72C',
            "copySelection" => '\uE8C8',
            "copyFile" => '\uE8A5',
            "find" => '\uE721',
            "column" => '\uE8A4',
            "expandedColumn" => '\uE9A6',
            "sourceHere" or "exportHtml" => '\uE943',
            "copyPath" or "copySection" => '\uE71B',
            "browser" => '\uE774',
            "reveal" or "sample" => '\uE8B7',
            "terminal" or "copyCode" => '\uE756',
            "delete" => '\uE74D',
            "appearance" => '\uE790',
            "paper" => '\uE7C3',
            "exportPdf" => '\uEA90',
            _ => '\uE8C8'
        };
        return new FontIcon
        {
            Glyph = glyph.ToString(), FontSize = 16, Style = (Style)Application.Current.Resources["CommandMenuIconStyle"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }
}
