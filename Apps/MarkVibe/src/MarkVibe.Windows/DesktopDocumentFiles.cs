/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native Windows file pickers and explicit document exports
 * Human guidance: reuse the local file safeguards in the WinUI workspace
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MarkVibe.Core;
using MarkVibe.Native;
using Microsoft.Win32.SafeHandles;
using Windows.Storage.Pickers;
using WinRT.Interop;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Owns native picker and export boundaries outside the reader and shell controls.</summary>
internal sealed class DesktopDocumentFiles(DesktopOptions options, NativeFiles files, IntPtr owner)
{
    private readonly NativeFiles _files = files;
    private readonly SemaphoreSlim _exports = new(1, 1);

    /// <summary>Gets one user-granted Markdown file through the Windows picker.</summary>
    public async Task<string?> PickDocumentAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var extension in new[] { ".md", ".markdown", ".mdown" })
        {
            picker.FileTypeFilter.Add(extension);
        }
        InitializeWithWindow.Initialize(picker, owner);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>Gets one user-granted folder through the Windows picker.</summary>
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, owner);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    /// <summary>Uses a per-format picker and confirms the actual destination extension.</summary>
    private async Task<string?> PickSaveAsync(string filename, string extension, string label, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(filename) };
        picker.FileTypeChoices.Add(T(label), new[] { extension });
        InitializeWithWindow.Initialize(picker, owner);
        var result = await picker.PickSaveFileAsync();
        token.ThrowIfCancellationRequested();
        if (result == null)
        {
            return null;
        }
        if (!result.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceException("invalid-export", "Choose a filename ending in " + extension + ".");
        }
        return result.Path;
    }

    /// <summary>Saves the captured draft as a copy without allowing replacement of its original.</summary>
    public async Task<bool> SaveCopyAsync(string workspaceId, MarkdownModel document, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(document.Source) > Limits.DocumentBytes)
        {
            throw new WorkspaceException("invalid-export", "The Markdown copy exceeds its size limit.");
        }
        await _exports.WaitAsync(token);
        try
        {
            var filename = Path.GetFileNameWithoutExtension(document.Path) + " - edited.md";
            var destination = await PickSaveAsync(filename, ".md", "Markdown documents", token);
            if (destination == null)
            {
                return false;
            }
            using var original = ProtectMarkdownOriginal(workspaceId, document.Path, destination);
            await AtomicFile.WriteTextAsync(destination, document.Source, token);
            return true;
        }
        finally
        {
            _exports.Release();
        }
    }

    /// <summary>Writes the renderer's source-backed export to an explicitly selected destination.</summary>
    public async Task<object> SaveExportAsync(string format, string filename, string html,
        Func<string, string, CancellationToken, Task> printPdf, CancellationToken token)
    {
        if (format is not ("pdf" or "html") || Path.GetFileName(filename) != filename ||
            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !filename.EndsWith("." + format, StringComparison.Ordinal))
        {
            throw new WorkspaceException("invalid-export", "The export filename is invalid.");
        }
        ValidateHtml(html);
        await _exports.WaitAsync(token);
        try
        {
            string? destination;
            if (options.SmokeTest)
            {
                Directory.CreateDirectory(options.ExportDirectory!);
                destination = Path.Combine(options.ExportDirectory!, filename);
            }
            else
            {
                destination = await PickSaveAsync(filename, "." + format, format == "pdf" ? "PDF document" : "HTML document", token);
            }
            if (destination == null)
            {
                return new { filename, saved = false, cancelled = true };
            }
            if (format == "pdf")
            {
                await printPdf(html, destination, token);
            }
            else
            {
                await AtomicFile.WriteTextAsync(destination, html, token);
            }
            return new { filename, saved = true, cancelled = false };
        }
        finally
        {
            _exports.Release();
        }
    }

    /// <summary>Opens the self-contained document in the user's default external browser.</summary>
    public async Task<object> OpenPreviewAsync(string html, CancellationToken token)
    {
        ValidateHtml(html);
        var cache = Path.Combine(options.DataDirectory, "Previews");
        Directory.CreateDirectory(cache);
        var destination = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))) + ".html");
        await AtomicFile.WriteTextAsync(destination, html, token);
        token.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        return new { opened = true };
    }

    /// <summary>Bounds the trusted renderer's standalone HTML before disk or print work.</summary>
    private static void ValidateHtml(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > 32 * 1048576 || !html.StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceException("invalid-export", "The browser preview exceeds its document limit.");
        }
    }

    /// <summary>Resolves the local Markdown source before applying the shared original-file guard.</summary>
    private SafeFileHandle? ProtectMarkdownOriginal(string workspaceId, string path, string destination)
    {
        if (!workspaceId.StartsWith("native:", StringComparison.Ordinal))
        {
            return null;
        }

        var relativePath = WorkspacePath.Normalize(path, imported: true).Replace('/', Path.DirectorySeparatorChar);
        var originalPath = Path.GetFullPath(Path.Combine(_files.Root(workspaceId), relativePath));
        return ProtectOriginal(originalPath, destination, "markdown-copy",
            "Choose a new filename for the edited copy, outside the opened Markdown files.");
    }

    /// <summary>Rejects original-path aliases and keeps an existing original guarded until the independent copy has committed.</summary>
    internal static SafeFileHandle? ProtectOriginal(string originalPath, string destination, string errorCode, string errorMessage)
    {
        destination = Path.GetFullPath(destination);
        if (string.Equals(originalPath, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceException(errorCode, errorMessage);
        }

        // Metadata-only access follows junctions without requiring content access. Excluding delete sharing
        // also prevents a destination alias from replacing the original between validation and atomic rename.
        var original = OpenFileMetadata(originalPath, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (original.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            original.Dispose();
            if (error is 2 or 3)
            {
                // A missing original still must not be recreated via an alias of its parent directory.
                if (SameDestinationDirectory(originalPath, destination))
                {
                    throw new WorkspaceException(errorCode, errorMessage);
                }
                return null;
            }
            throw new Win32Exception(error);
        }

        try
        {
            if (File.Exists(destination))
            {
                using var target = OpenFileMetadata(destination, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
                if (target.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (string.Equals(FinalFilePath(original), FinalFilePath(target), StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceException(errorCode, errorMessage);
                }
            }
            return original;
        }
        catch
        {
            original.Dispose();
            throw;
        }
    }

    /// <summary>Detects the original filename through a directory alias when the original file does not yet exist.</summary>
    private static bool SameDestinationDirectory(string original, string destination)
    {
        if (!Path.GetFileName(original).Equals(Path.GetFileName(destination), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const uint openDirectory = 0x02000000;
        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        using var source = OpenFileMetadata(Path.GetDirectoryName(original)!, 0, share, IntPtr.Zero, FileMode.Open, openDirectory, IntPtr.Zero);
        using var target = OpenFileMetadata(Path.GetDirectoryName(destination)!, 0, share, IntPtr.Zero, FileMode.Open, openDirectory, IntPtr.Zero);
        return !source.IsInvalid && !target.IsInvalid &&
            string.Equals(FinalFilePath(source), FinalFilePath(target), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Obtains the normalized Windows path behind an opened file, resolving directory junctions and short-name aliases.</summary>
    private static string FinalFilePath(SafeFileHandle handle)
    {
        var path = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity)
        {
            path.Capacity = checked((int)length + 1);
            length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        }
        if (length == 0 || length >= path.Capacity)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return path.ToString();
    }

    /// <summary>Opens a metadata handle with explicit sharing while leaving file content unchanged.</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenFileMetadata(string filename, uint access, FileShare share, IntPtr security,
        FileMode creation, uint flags, IntPtr template);

    /// <summary>Reads the normalized final path for a metadata handle using the Windows file API.</summary>
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
