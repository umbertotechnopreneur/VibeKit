using System;
using System.Runtime.InteropServices;
using System.Numerics;
namespace WaterRipple;
[StructLayout(LayoutKind.Sequential)] internal struct NativeWave
{
    public float X, Y, Age, Active;
    public NativeWave(float x, float y, float age, float active) { X = x; Y = y; Age = age; Active = active; }
}
[StructLayout(LayoutKind.Sequential)] internal struct NativeStats
{
    public int CaptureWidth, CaptureHeight, CaptureCount, PresentCount;
    public float LastCaptureMs, LastRenderCpuMs;
    public int Status, Reserved;
}
[StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
internal static partial class Native
{
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int RippleCreate(IntPtr panel, string shaderDirectory, out IntPtr renderer);
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int RippleRender(IntPtr renderer, int x, int y, int width, int height, float widthDip, float heightDip, float strength, float band, float speed, float duration, [In] NativeWave[] waves, int count, int captureEnabled, int captureDue);
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
    [DllImport("RippleRenderer.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int RippleGetGpuMemory(IntPtr renderer, out GpuMemory memory);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    internal static void SetTopmost(IntPtr hwnd, bool enabled) => SetWindowPos(hwnd, new IntPtr(enabled ? -1 : -2), 0, 0, 0, 0, 0x0013);
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
