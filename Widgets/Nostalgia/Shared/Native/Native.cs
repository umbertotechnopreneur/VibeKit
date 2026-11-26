// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace VibeWare.EffectsStudio;

internal static partial class Native
{
    internal const string Engine = "WidgetEngine.dll";
    internal delegate void Notification(nint message);
    [StructLayout(LayoutKind.Sequential)]
    internal struct State { internal int Active, AllEnabled, Mixed; }

    [DllImport(Engine)] internal static extern int WidgetStart(nint window, int startup, Notification notify);
    [DllImport(Engine)] internal static extern void WidgetStop();
    [DllImport(Engine)] internal static extern void WidgetRead(out State state);
    [DllImport(Engine)] internal static extern void WidgetEnable(int enabled);
    [DllImport(Engine)] private static extern nint WidgetStatus();
    internal static string Status => Marshal.PtrToStringUni(WidgetStatus()) ?? "";
}
