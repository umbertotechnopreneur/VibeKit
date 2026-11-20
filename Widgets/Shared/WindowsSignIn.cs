using System;
using System.IO;
using Microsoft.Win32;

namespace Tools.Widgets;

internal sealed class WindowsSignIn(string valueName)
{
    internal const string StartArgument = "--start-with-windows";
    internal const string RemoveArgument = "--remove-windows-startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    internal (bool Enabled, bool Blocked) Read()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey);
        var approvalBytes = approval?.GetValue(valueName) as byte[];
        bool blocked = approvalBytes is { Length: > 0 } && (approvalBytes[0] & 1) != 0;
        return (Owns(run) && !blocked, blocked);
    }

    internal void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            Remove();
            return;
        }
        if (Read().Blocked) return;
        string command = Command();
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (run.GetValue(valueName) is not null && !Owns(run))
            throw new InvalidOperationException("A different installation holds this startup entry.");
        run.SetValue(valueName, command, RegistryValueKind.String);
    }

    internal void Remove()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run is not null && Owns(run)) run.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private bool Owns(RegistryKey? run) =>
        run?.GetValue(valueName) is string value &&
        string.Equals(value, Command(), StringComparison.OrdinalIgnoreCase);

    private static string Command()
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Windows application executable is required.");
        string command = $"\"{executable}\" {StartArgument}";
        if (command.Length > 260)
            throw new InvalidOperationException("The installation path is too long to register with Windows Startup Apps.");
        return command;
    }
}
