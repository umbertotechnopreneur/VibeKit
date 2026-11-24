// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Tools.Widgets;

namespace VibeWare.Vignette;

internal sealed class DesktopOverlay : IDisposable
{
    private const int TextureSize = 512;
    private const long MaxDisplayPixels = 16_000_000, MaxTotalPixels = 24_000_000;
    private const string PostEffectProperty = "VibeWare.Vignette.PostEffect.v1";
    private const string CrtConsumerProperty = "VibeWare.CRT.FrameConsumer.v1";
    private const string RippleTargetProperty = "VibeWare.Ripple.FrameTarget.v1";
    private const uint ExcludeFromCapture = 0x11, PreviousWindow = 3;
    private static readonly nint Topmost = new(-1);
    private readonly Dictionary<string, Surface> surfaces = new(StringComparer.OrdinalIgnoreCase);
    private Appearance? previous;
    private uint[]? texture;

    internal bool HasSurfaces => surfaces.Count != 0;

    internal int Apply(IEnumerable<DisplayTarget> displays, Appearance appearance)
    {
        var selected = displays.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || appearance.Intensity == 0)
        {
            Dispose();
            return selected.Count;
        }

        long Pixels(DisplayTarget target) => (long)target.Bounds.Width * target.Bounds.Height;
        if (selected.Values.Any(target => Pixels(target) > MaxDisplayPixels || target.Bounds.Width <= 0 || target.Bounds.Height <= 0) ||
            selected.Values.Sum(Pixels) > MaxTotalPixels)
            throw new InvalidOperationException("The selected displays exceed the vignette surface budget.");
        bool changed = previous != appearance || texture is null;
        if (changed) texture = appearance.Texture(TextureSize);
        try
        {
            foreach (var id in surfaces.Keys.ToArray())
            {
                if (selected.TryGetValue(id, out var display) && display.Bounds == surfaces[id].Bounds) continue;
                surfaces[id].Dispose();
                surfaces.Remove(id);
            }
            var needsUpload = new List<Surface>();
            foreach (var (id, display) in selected)
            {
                if (!surfaces.TryGetValue(id, out var surface))
                {
                    surface = new Surface(display.Bounds);
                    surfaces.Add(id, surface);
                    needsUpload.Add(surface);
                }
                else if (changed) needsUpload.Add(surface);
            }
            // Windows retains the pixels of each layered window. Reuse one
            // temporary upload bitmap for all displays with these dimensions.
            foreach (var group in needsUpload.GroupBy(surface => (surface.Bounds.Width, surface.Bounds.Height)))
            {
                using var bitmap = new LayeredBitmap(group.Key.Width, group.Key.Height, texture!);
                foreach (var surface in group)
                    surface.Upload(bitmap.Dc);
            }
            previous = appearance;
            return surfaces.Count;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool IsPeerOnSelectedDisplay(nint window)
    {
        if (!GetWindowRect(window, out var rect)) return false;
        foreach (var surface in surfaces.Values)
        {
            if (Matches(surface, rect)) return IsCapturePeer(window);
        }
        return false;
    }

    internal void EnsureAbovePeers()
    {
        foreach (var surface in surfaces.Values)
        {
            nint above = GetWindow(surface.Handle, PreviousWindow);
            for (int index = 0; above != 0 && index < 1024; index++, above = GetWindow(above, PreviousWindow))
            {
                if (!GetWindowRect(above, out var rect) || !Matches(surface, rect) || !IsCapturePeer(above)) continue;
                surface.Raise();
                break;
            }
        }
    }

    private static bool Matches(Surface surface, Rect rect)
    {
        var (x, y, width, height) = surface.Bounds;
        return rect.X == x && rect.Y == y && rect.Right == x + width && rect.Bottom == y + height;
    }

    private static unsafe bool IsCapturePeer(nint window)
    {
        if (!IsWindowVisible(window) ||
            !GetWindowDisplayAffinity(window, out uint affinity) || affinity != ExcludeFromCapture)
            return false;
        if (GetProp(window, CrtConsumerProperty) == 1 || GetProp(window, RippleTargetProperty) != 0)
            return true;

        // Earlier builds and standalone Ripple may not have a frame-sharing
        // property. Keep recognizing their full-display capture overlays.
        Span<char> title = stackalloc char[64];
        fixed (char* buffer = title)
        {
            ReadOnlySpan<char> name = title[..GetWindowText(window, buffer, title.Length)];
            return name.SequenceEqual("Retro Terminal screen effect") || name.SequenceEqual("Water Ripple screen effect");
        }
    }

    public void Dispose()
    {
        foreach (var surface in surfaces.Values) surface.Dispose();
        surfaces.Clear();
        previous = null;
        texture = null;
    }

    private sealed class Surface : IDisposable
    {
        internal (int X, int Y, int Width, int Height) Bounds { get; }
        internal nint Handle { get; private set; }

        internal Surface((int X, int Y, int Width, int Height) bounds)
        {
            Bounds = bounds;
            // The overlay is click-through, capture-excluded and never becomes active.
            const uint style = 0x80000000, extended = 0x00080000 | 0x20 | 0x80 | 0x08000000 | 8;
            Handle = CreateWindowEx(extended, "Static", "VibeWare Vignette screen effect", style,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, 0, 0);
            if (Handle == 0) throw new Win32Exception();
            if (!SetWindowDisplayAffinity(Handle, ExcludeFromCapture))
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error, "The overlay could not be excluded from screen capture.");
            }
            SetProp(Handle, PostEffectProperty, 1);
        }

        internal void Upload(nint dc)
        {
            var (x, y, width, height) = Bounds;
            var position = new Point(x, y);
            var size = new Point(width, height);
            var origin = new Point();
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(Handle, 0, ref position, ref size, dc, ref origin, 0, ref blend, 2))
                throw new Win32Exception();
            if (!IsWindowVisible(Handle)) Raise();
        }

        internal void Raise()
        {
            if (!SetWindowPos(Handle, Topmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x0200))
                throw new Win32Exception();
        }

        public void Dispose()
        {
            if (Handle == 0) return;
            RemoveProp(Handle, PostEffectProperty);
            DestroyWindow(Handle);
            Handle = 0;
        }
    }

    private sealed class LayeredBitmap : IDisposable
    {
        internal nint Dc { get; }
        private readonly nint bitmap, previousObject;

        internal unsafe LayeredBitmap(int width, int height, uint[] source)
        {
            Dc = CreateCompatibleDC(0);
            if (Dc == 0) throw new Win32Exception();
            try
            {
                var header = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                bitmap = CreateDIBSection(Dc, ref header, 0, out nint pixels, 0, 0);
                if (bitmap == 0 || pixels == 0) throw new Win32Exception();
                previousObject = SelectObject(Dc, bitmap);
                if (previousObject == 0 || previousObject == -1) throw new Win32Exception();
                Rasterize(source, new Span<uint>((void*)pixels, checked(width * height)), width, height);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (previousObject != 0 && previousObject != -1) SelectObject(Dc, previousObject);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(Dc);
        }
    }

    internal static void Rasterize(ReadOnlySpan<uint> source, Span<uint> destination, int width, int height)
    {
        if (width == TextureSize && height == TextureSize)
        {
            source.CopyTo(destination);
            return;
        }

        var columns = new int[width];
        int lastColumn = Math.Max(1, width - 1), lastRow = Math.Max(1, height - 1);
        for (int col = 0; col < width; col++)
            columns[col] = (int)((long)col * (TextureSize - 1) / lastColumn);
        int previousOffset = -1;
        for (int row = 0; row < height; row++)
        {
            int offset = (int)((long)row * (TextureSize - 1) / lastRow) * TextureSize;
            var output = destination.Slice(row * width, width);
            if (offset == previousOffset)
                destination.Slice((row - 1) * width, width).CopyTo(output);
            else
            {
                var input = source.Slice(offset, TextureSize);
                for (int col = 0; col < width; col++) output[col] = input[columns[col]];
            }
            previousOffset = offset;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int X, Y, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        internal int Size, Width, Height;
        internal ushort Planes, BitCount;
        internal int Compression, ImageSize, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend
    {
        internal byte Operation, Flags, Alpha, Format;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint ex, string classname, string title, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetPropW")]
    private static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPropW")]
    private static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RemovePropW")]
    private static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static extern unsafe int GetWindowText(nint hwnd, char* title, int length);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint dc,
        ref Point position, ref Point size, nint source, ref Point sourceOrigin, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info,
        uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
