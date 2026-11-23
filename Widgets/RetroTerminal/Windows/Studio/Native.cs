using System.Runtime.InteropServices;
using System.Text;
namespace RetroStudio;
internal static class Native
{
    private const string Library = "RetroEngine.dll";
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern nint RetroCreate(nint owner);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroDestroy(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroStart(nint engine, nint monitor);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroSetDisplays(nint engine, [In] nint[] monitors, int count);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroActiveDisplayCount(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroIsArmed(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern uint RetroDisplayTopologyGeneration(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroRunning(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroStop(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern EmergencyShortcutState RetroSetEmergencyShortcut(nint engine, int enabled);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern SystemClose RetroSystemClose(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroRecoveryPending(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetRecovery(nint engine,int enabled,int seconds);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetFrameLimits(nint engine, int animationFps, int captureFps);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetTarget(nint engine, int target);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] private static extern nint RetroGpuJoke(uint index);
    internal static string GpuJoke(uint index) => Marshal.PtrToStringUni(RetroGpuJoke(index)) ?? string.Empty;
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroSetGpuJokes(nint engine, int enabled);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroKeepInTray(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroLeaveTray(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern TrayAction RetroTakeTrayAction(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroSave(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern float RetroGetSetting(nint engine, int index);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetSetting(nint engine, int index, float value);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroGetPalette(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetPalette(nint engine,int value);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroGetMask(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetMask(nint engine,int value);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroGetPersistence(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetPersistence(nint engine,int value);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroGetRandom(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetRandom(nint engine,int value);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroGetGlitchTypes(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetGlitchTypes(nint engine,int value);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern float RetroGetColor(nint engine,int channel);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern void RetroSetColor(nint engine,int channel,float value);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroPreset(nint engine,int index);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroBuiltInPresetCount();
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] private static extern nint RetroBuiltInPresetName(int index);
    internal static string BuiltInPresetName(int index) => Marshal.PtrToStringUni(RetroBuiltInPresetName(index)) ?? string.Empty;
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroApplyBuiltInPreset(nint engine, int index);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroSavePreset(nint engine,int index);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] internal static extern int RetroTryGlitch(nint engine);
    [DllImport(Library, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] internal static extern void RetroStatus(nint engine,StringBuilder buffer,int capacity);
}

internal enum TrayAction { None, Open, Quit, Preferences, About }
internal enum SystemClose { None, Saved, SaveFailed }
internal enum EmergencyShortcutState { Disabled = 0, Registered = 1, Unavailable = 2 }
