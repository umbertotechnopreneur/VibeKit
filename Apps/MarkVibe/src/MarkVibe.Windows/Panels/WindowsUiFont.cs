/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; live native application typeface selection
 * Human guidance: keep shell, graph and Markdown fonts independent
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Runtime.CompilerServices;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkVibe.Windows;

/// <summary>Applies the UI font to native text while preserving icon fonts and explicit content typography.</summary>
internal static class WindowsUiFont
{
    private static readonly ConditionalWeakTable<DependencyObject, FontFamily> Applied = new();
    private static FontFamily? _family;
    public static event Action? Changed;

    /// <summary>Selects the typeface for application controls independently of the WinUI resource dictionaries.</summary>
    public static bool Select(string font)
    {
        var normalized = UiFontCatalog.Normalize(font);
        var selected = normalized == "system" ? null : normalized;
        if (selected == _family?.Source)
        {
            return false;
        }

        _family = selected == null ? null : new FontFamily(selected);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Applies the current font to native menu actions before their separate popup is created.</summary>
    public static void ApplyTo(MenuFlyout menu) => ApplyMenuItems(menu.Items);

    /// <summary>Applies the typeface to menu items and their submenus without requiring a realized template.</summary>
    private static void ApplyMenuItems(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            ApplyTo(item);
            if (item is MenuFlyoutSubItem submenu)
            {
                ApplyMenuItems(submenu.Items);
            }
        }
    }

    /// <summary>Updates realized native elements whose template or default font has already been resolved.</summary>
    public static void ApplyTo(DependencyObject root)
    {
        switch (root)
        {
            case TextBlock text:
                ApplyFont(text, TextBlock.FontFamilyProperty, text.FontFamily);
                break;
            case Control control:
                ApplyFont(control, Control.FontFamilyProperty, control.FontFamily);
                break;
        }

        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < children; index++)
        {
            ApplyTo(VisualTreeHelper.GetChild(root, index));
        }
    }

    /// <summary>Tracks only values owned by this preference so resetting restores the native styles.</summary>
    private static void ApplyFont(DependencyObject element, DependencyProperty property, FontFamily? current)
    {
        var family = _family;
        var local = element.ReadLocalValue(property);
        var ownsFont = false;
        if (Applied.TryGetValue(element, out var previous))
        {
            ownsFont = local is FontFamily font && font.Source == previous.Source;
            if (ownsFont && ReferenceEquals(family, previous))
            {
                return;
            }
            Applied.Remove(element);
        }
        if (family == null)
        {
            if (ownsFont)
            {
                element.ClearValue(property);
            }
            return;
        }
        if (!ownsFont && (local != DependencyProperty.UnsetValue ||
            current?.Source is not (null or "" or "XamlAutoFontFamily" or "Segoe UI" or "Segoe UI Variable")))
        {
            return;
        }

        element.SetValue(property, family);
        Applied.Add(element, family);
    }
}
