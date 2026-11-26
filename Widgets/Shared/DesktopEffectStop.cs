// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace Tools.Widgets;

internal static class DesktopEffectStop
{
    // The native CRT tray registers the same stop-only desktop message.
    private static readonly uint Message = RegisterWindowMessageW("VibeWare.DesktopEffects.DisableAll.v1");
    internal static bool Matches(uint message) => Message != 0 && message == Message;
    internal static void Broadcast()
    {
        if (Message != 0) PostMessageW(0xffff, Message, 0, 0); // HWND_BROADCAST includes hidden top-level windows.
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(nint window, uint message, nint wp, nint lp);
}
