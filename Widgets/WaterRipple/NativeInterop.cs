using System;
using System.Runtime.InteropServices;
namespace WaterRipple;

// Keep numeric values aligned with RippleRenderer.h; the native ABI uses a 32-bit mask.
[Flags]
internal enum CaptureStatus : int
{
    None = 0,
    Clipped = 1,
    Waiting = 2,
    AccessLost = 4,
    PresentBusy = 8,
    Occluded = 16,
    ValidCapture = 32,
    PresentedCapture = 64,
    CrtComposition = 128,
    VisibleCapture = ValidCapture | PresentedCapture,
}
[StructLayout(LayoutKind.Sequential)] internal struct NativeWave
{
    public float X, Y, Age, Active;
    public NativeWave(float x, float y, float age, float active) { X = x; Y = y; Age = age; Active = active; }
}
[StructLayout(LayoutKind.Sequential)] internal struct NativeStats
{
    public int CaptureWidth, CaptureHeight, CaptureCount, PresentCount;
    public float LastCaptureMs, LastRenderCpuMs;
    public CaptureStatus Status;
    public int Reserved;
    public readonly bool HasPresentedCapture => (Status & CaptureStatus.VisibleCapture) == CaptureStatus.VisibleCapture;
}
[StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
internal static partial class Native
{
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int RippleCreate(IntPtr panel, string shaderDirectory, out IntPtr renderer);
    // Preserve the shipped C ABI; callers use named, unit-normalized parameters below.
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int RippleRender(IntPtr renderer, int x, int y, int width, int height, float widthDip, float heightDip, float strength, float band, float speed, float duration, float chromaticSeparation, float chromaticBlend, float crest, float ripples, float liquid, [In] NativeWave[] waves, int count, int captureEnabled, int captureDue);
    internal static int Render(IntPtr renderer, in RenderParameters parameters, NativeWave[] waves, int count) =>
        RippleRender(renderer, parameters.ScreenX, parameters.ScreenY, parameters.WidthPixels, parameters.HeightPixels,
            parameters.WidthDips, parameters.HeightDips, parameters.StrengthDips, parameters.BandDips,
            parameters.SpeedDipsPerSecond, parameters.LifetimeSeconds, parameters.ChromaticSeparationRatio,
            parameters.ChromaticBlendRatio, parameters.CrestRatio, parameters.RipplesRatio, parameters.LiquidRatio,
            waves, count, parameters.CaptureEnabled ? 1 : 0, parameters.CaptureDue ? 1 : 0);
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int RippleGetStats(IntPtr renderer, out NativeStats stats);
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void RippleDestroy(IntPtr renderer);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct GpuCapabilities
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string AdapterName;
    public ulong DedicatedVideoMemory, SharedSystemMemory;
    public uint FeatureLevel;
    public int Supported, Reason, Reserved;
    public int Left, Top, Width, Height;
}
[StructLayout(LayoutKind.Sequential)] internal struct GpuMemory
{
    public ulong DedicatedCapacity, LocalUsage, LocalBudget, SharedUsage;
}

internal static partial class Native
{
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int RippleCheckCapabilities(int x, int y, int width, int height, out GpuCapabilities capabilities);
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int RippleSetScreenMode(IntPtr renderer, int enabled);
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void RippleSetInteropWindow(IntPtr renderer, IntPtr window);
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void RippleSetTarget(IntPtr renderer, int target);
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int RippleGetGpuMemory(IntPtr renderer, out GpuMemory memory);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    internal static void SetOverlayTopmost(IntPtr hwnd) => SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
    internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    internal delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public NativePoint Point; public uint MouseDataValue, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")] internal static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
