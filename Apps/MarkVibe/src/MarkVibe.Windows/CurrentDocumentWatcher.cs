/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; current-file change notifications; 2026-10-23
 * Human guidance: Umberto Giacobbi; ask before reloading a document changed on disk
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MarkVibe.Core;
using MarkVibe.Native;
using Microsoft.UI.Dispatching;

namespace MarkVibe.Windows;

/// <summary>Reports one watched document's content changes without replacing its in-memory source.</summary>
internal sealed class CurrentDocumentWatcher : IDisposable
{
    private readonly NativeFiles _files;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<DocumentDiskChange> _notify;
    private readonly DispatcherQueueTimer _debounce;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource _lifetime = new();
    private string _workspaceId = "";
    private string _path = "";
    private string _watchId = "";
    private string _fingerprint = "";
    private int _generation;
    private bool _reading;
    private bool _disposed;
    private int _retryCount;

    /// <summary>Creates a UI-dispatched debounce boundary for background filesystem events.</summary>
    internal CurrentDocumentWatcher(NativeFiles files, DispatcherQueue dispatcher, Action<DocumentDiskChange> notify)
    {
        _files = files;
        _dispatcher = dispatcher;
        _notify = notify;
        _debounce = dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(600);
        _debounce.IsRepeating = false;
        _debounce.Tick += ReadChangedFile;
    }

    /// <summary>Watches only the contained current file, including atomic replacement and later recreation.</summary>
    internal void Start(string workspaceId, string path, string watchId, string fingerprint)
    {
        Stop();
        if (!WorkspacePath.IsMarkdown(path) || watchId.Length is < 1 or > 80 ||
            fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new WorkspaceException("invalid-watch", "The document watch request is invalid.");
        }

        var location = _files.Resolve(workspaceId, path, allowMissingLeaf: true);
        _workspaceId = workspaceId;
        _path = path;
        _watchId = watchId;
        _fingerprint = fingerprint;
        _retryCount = 0;
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(location)!, Path.GetFileName(location))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false
        };
        _watcher.Changed += FileChanged;
        _watcher.Created += FileChanged;
        _watcher.Deleted += FileChanged;
        _watcher.Renamed += FileChanged;
        _watcher.Error += WatcherError;
        _watcher.EnableRaisingEvents = true;
        // Close the read-to-registration window: a save may precede watcher setup.
        _debounce.Start();
    }

    /// <summary>Rejects queued callbacks and releases the previous file and any pending read.</summary>
    internal void Stop()
    {
        _generation++;
        _debounce.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        _watchId = "";
    }

    /// <summary>Returns filesystem callbacks to the owning UI thread without reading on the event thread.</summary>
    private void FileChanged(object sender, FileSystemEventArgs arguments) => QueueCheck(sender);

    /// <summary>Rechecks content after event-buffer overflow instead of trusting that every event arrived.</summary>
    private void WatcherError(object sender, ErrorEventArgs arguments) => QueueCheck(sender);

    /// <summary>Coalesces duplicate save events and rejects events belonging to a disposed watcher.</summary>
    private void QueueCheck(object sender)
    {
        if (_disposed)
        {
            return;
        }
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
            {
                return;
            }
            _retryCount = 0;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    /// <summary>Compares bounded UTF-8 content, retrying briefly while an editor is replacing or locking the file.</summary>
    private async void ReadChangedFile(DispatcherQueueTimer sender, object arguments)
    {
        _debounce.Stop();
        if (_disposed || _watcher == null)
        {
            return;
        }
        if (_reading)
        {
            _debounce.Start();
            return;
        }

        _reading = true;
        var generation = _generation;
        var cancellation = _lifetime.Token;
        byte[]? observedBytes = null;
        try
        {
            // Observation must not lock an editor out of its next save. The
            // accepted reload separately uses the provider's stable read path.
            observedBytes = await _files.ReadAsync(
                _workspaceId, _path, cancellation, FileShare.ReadWrite | FileShare.Delete);
            var source = new UTF8Encoding(false, true).GetString(observedBytes);
            if (source.StartsWith('\uFEFF'))
            {
                source = source[1..];
            }
            Publish(generation, MarkdownParser.Fingerprint(source), missing: false);
        }
        catch (OperationCanceledException)
        {
            // A navigation or window close owns cancellation, not an external edit.
        }
        catch (DecoderFallbackException)
        {
            // Invalid replacement text still merits a prompt; reload validates it independently.
            var fingerprint = Convert.ToHexString(SHA256.HashData(observedBytes!)).ToLowerInvariant();
            Publish(generation, fingerprint, missing: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkspaceException)
        {
            if (generation == _generation && _retryCount++ < 5)
            {
                _debounce.Start();
            }
            else
            {
                Publish(generation, "unavailable", missing: true);
            }
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>Notifies once per observed revision and never emits a late result for another document.</summary>
    private void Publish(int generation, string fingerprint, bool missing)
    {
        if (_disposed || generation != _generation || fingerprint == _fingerprint)
        {
            return;
        }
        _fingerprint = fingerprint;
        _notify(new DocumentDiskChange(_workspaceId, _path, _watchId, fingerprint, missing));
    }

    /// <summary>Closes the watcher and debounce timer with the desktop window.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        _lifetime.Dispose();
        _debounce.Tick -= ReadChangedFile;
    }
}

/// <summary>Identifies an observed file revision without disclosing its contents to the UI.</summary>
internal sealed record DocumentDiskChange(string WorkspaceId, string Path, string WatchId, string Fingerprint, bool Missing);
