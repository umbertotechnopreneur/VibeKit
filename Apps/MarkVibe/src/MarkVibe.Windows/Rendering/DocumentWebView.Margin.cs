/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native page margin exposing the window backdrop
 * Human guidance: a single lower-left paper corner in the reading pane
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace MarkVibe.Windows;

internal sealed partial class DocumentWebView
{
    private const int MarginHeight = 16;
    private const int PaperTileSize = 256;
    private readonly Grid _margin = new() { Height = MarginHeight, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _sourceEdge = new();
    private readonly Border _paperEdge = new() { CornerRadius = new CornerRadius(0, 0, 0, MarginHeight) };
    private readonly ColumnDefinition _sourceEdgeColumn = new();
    private readonly ColumnDefinition _paperEdgeColumn = new();
    private readonly ImageBrush _paperImage = new() { Stretch = Stretch.Fill };
    private string? _tileName;
    private byte[]? _tilePixels;
    private (int Width, double Phase)? _paintedMargin;

    /// <summary>Reserves a native margin below the actual WebView, including below the reader half of Split.</summary>
    private void InitializePaperMargin()
    {
        _margin.ColumnDefinitions.Add(_sourceEdgeColumn);
        _margin.ColumnDefinitions.Add(_paperEdgeColumn);
        _margin.Children.Add(_sourceEdge);
        _margin.Children.Add(_paperEdge);
        Grid.SetColumn(_paperEdge, 1);
        Grid.SetRow(_margin, 1);
        View.Children.Add(_margin);
        _paperEdge.SizeChanged += PaperEdgeSizeChanged;
        Control.SizeChanged += PaperEdgeSizeChanged;
    }

    /// <summary>Uses the native splitter ratio and gives Source-only mode its entire viewport back.</summary>
    public void SetPaperMargin(string view, double sourceRatio)
    {
        _margin.Visibility = view is "read" or "split" ? Visibility.Visible : Visibility.Collapsed;
        var fraction = view == "split" ? sourceRatio : 0;
        _sourceEdgeColumn.Width = new GridLength(fraction, GridUnitType.Star);
        _paperEdgeColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
    }

    /// <summary>Reads the active renderer's computed colors and the same validated local tile it paints.</summary>
    private async Task ApplyPaperMaterialAsync(JsonElement material)
    {
        if (!material.TryGetProperty("readerColor", out var background) || background.ValueKind != JsonValueKind.Number) { return; }
        if (SurfaceBrush(background) is not { } paperBrush) { return; }
        if (material.TryGetProperty("sourceColor", out var source) && SurfaceBrush(source) is { } sourceBrush)
        {
            _sourceEdge.Background = sourceBrush;
        }
        var paper = Text(material, "paper");
        var dark = material.TryGetProperty("dark", out var value) && value.ValueKind == JsonValueKind.True;
        var name = ReaderPaper.Options.Any(option => option.Id == paper) ? $"paper-{paper}-{(dark ? "dark" : "light")}.png" : null;
        if (_tileName == name && _tilePixels != null) { return; }
        _paperEdge.Background = paperBrush;
        if (_tileName == name) { return; }
        _tileName = name;
        _tilePixels = null;
        _paintedMargin = null;
        if (name == null) { return; }

        using var file = File.OpenRead(Path.Combine(_options.AssetsDirectory, "images", name));
        using var stream = file.AsRandomAccessStream();
        var tile = new WriteableBitmap(PaperTileSize, PaperTileSize);
        await tile.SetSourceAsync(stream);
        if (_disposed || _tileName != name || tile.PixelWidth != PaperTileSize || tile.PixelHeight != PaperTileSize) { return; }
        var pixels = new byte[PaperTileSize * PaperTileSize * 4];
        using var buffer = tile.PixelBuffer.AsStream();
        buffer.ReadExactly(pixels);
        _tilePixels = pixels;
        PaintPaperMargin();
    }

    /// <summary>Validates a renderer RGB triplet packed as a 24-bit integer, including derived system colors.</summary>
    private static SolidColorBrush? SurfaceBrush(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var rgb) || rgb is < 0 or > 0xFFFFFF) { return null; }
        return new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    /// <summary>Keeps the native texture aligned to the origin of the paper on viewport or split changes.</summary>
    private void PaperEdgeSizeChanged(object sender, SizeChangedEventArgs args) => PaintPaperMargin();

    /// <summary>Continues the 256-DIP tile through the margin, interpolating partial rows at fractional DPI.</summary>
    private void PaintPaperMargin()
    {
        if (_tilePixels is not { } tile || _margin.Visibility != Visibility.Visible || _paperEdge.ActualWidth <= 0) { return; }
        var width = (int)Math.Ceiling(_paperEdge.ActualWidth);
        var phase = Control.ActualHeight % PaperTileSize;
        if (_paintedMargin == (width, phase)) { return; }
        var margin = new WriteableBitmap(width, MarginHeight);
        using var buffer = margin.PixelBuffer.AsStream();
        var row = new byte[PaperTileSize * 4];
        var first = (int)phase;
        var blend = phase - first;
        for (var y = 0; y < MarginHeight; y++)
        {
            var line = ((first + y) % PaperTileSize) * row.Length;
            var next = ((first + y + 1) % PaperTileSize) * row.Length;
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = (byte)Math.Round(tile[line + i] + (tile[next + i] - tile[line + i]) * blend);
            }
            for (var x = 0; x < width; x += PaperTileSize)
            {
                buffer.Write(row, 0, Math.Min(width - x, PaperTileSize) * 4);
            }
        }
        margin.Invalidate();
        _paperImage.ImageSource = margin;
        _paperEdge.Background = _paperImage;
        _paintedMargin = (width, phase);
    }
}
