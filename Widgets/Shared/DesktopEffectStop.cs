// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace Tools.Widgets;

internal static class DesktopEffectStop
{
    // The native CRT tray registers the same stop-only desktop message.
    private static readonly uint Message = RegisterWindowMessageW("VibeWare.DesktopEffects.DisableAll.v1");
    private static readonly uint EnableMessage = RegisterWindowMessageW("VibeWare.DesktopEffects.EnableAll.v1");
    internal static bool Matches(uint message) => Message != 0 && message == Message;
    internal static bool MatchesEnable(uint message) => EnableMessage != 0 && message == EnableMessage;
    internal static void Broadcast()
    {
        if (Message != 0) PostMessageW(0xffff, Message, 0, 0); // HWND_BROADCAST includes hidden top-level windows.
    }
    internal static void BroadcastEnable()
    {
        if (EnableMessage != 0) PostMessageW(0xffff, EnableMessage, 0, 0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(nint window, uint message, nint wp, nint lp);
}

/// <summary>Manual commands for participating VibeWare desktop effects in this Windows session.</summary>
public static class DesktopEffectCommands
{
    public static bool IsEnableAll(uint message) => DesktopEffectStop.MatchesEnable(message);
    public static bool IsDisableAll(uint message) => DesktopEffectStop.Matches(message);
    public static void EnableAll() => DesktopEffectStop.BroadcastEnable();
    public static void DisableAll() => DesktopEffectStop.Broadcast();
}
