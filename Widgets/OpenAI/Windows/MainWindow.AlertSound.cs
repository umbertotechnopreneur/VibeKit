using System.Runtime.InteropServices;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private void PlayAlertDing()
    {
        if (!settings.AlertSound) return;

        const uint async = 0x0001;
        const uint noDefault = 0x0002;
        const uint filename = 0x00020000;
        _ = PlaySound(Path.Combine(AppContext.BaseDirectory, "Branding", "alert-ding.wav"), 0,
            async | noDefault | filename);
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, nint module, uint flags);
}
