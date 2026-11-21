/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; managed Windows Markdown assistant session and snapshot projection
 * Human guidance: keep workspace data and workflow state outside the native controls
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Text;
using MarkVibe.Core;

namespace MarkVibe.Windows;

/// <summary>Captures the editable document and its owning workspace model.</summary>
internal sealed record WindowsAiTarget(WorkspaceInfo Workspace, MarkdownModel Document, DocumentCatalog Catalog,
    IWorkspaceProvider Provider, AiDocumentCollection References);

/// <summary>Describes a reference using the catalog's metadata without retaining another copy of its source.</summary>
internal sealed record WindowsAiReferenceRow(string Path, string Title, int? Tokens, bool IsTarget, bool Missing);

/// <summary>Contains managed assistant state rendered by the WinUI composer.</summary>
internal sealed record WindowsAiSnapshot(string Provider, string Target, bool HasTarget, bool HasSelection, bool Busy, bool Checking,
    bool CanSend, bool CanUndo, bool CanRedo, bool CanSave, string Status, string Prompt,
    string Model, string Effort, IReadOnlyList<MarkdownAiModel> Models, IReadOnlyList<WindowsAiReferenceRow> References,
    MarkdownAiResult? Result, MarkdownModel? Preview, MarkdownAiUsage Usage, MarkdownAiCapacity? Capacity,
    long TotalTokens, int RequestCount);

/// <summary>Owns AI request preparation, local revisions and workspace-scoped reference commands.</summary>
internal sealed class WindowsAiSession(AiConnectionService connections,
    Func<WindowsAiTarget, string, CancellationToken, Task> apply, CancellationToken lifetime, bool disabled = false)
{
    /// <summary>A selected Markdown interval anchored to one parsed document revision.</summary>
    private sealed record Selection(string Fingerprint, int Start, int End, bool Unsupported);

    private WindowsAiTarget? _target;
    private Selection? _selection;
    private MarkdownAiResult? _result;
    private MarkdownModel? _preview;
    private MarkdownAiUsage? _usage;
    private string? _applyingFingerprint;
    private MarkdownAiModel[] _models = [];
    private string _provider = AiProviders.ApiKey;
    private string _status = "Check the AI connection to get started.";
    private string _prompt = "";
    private string _model = "";
    private string _effort = "medium";
    private long _totalTokens;
    private int _requestCount;
    private CancellationTokenSource? _connectionCheck;
    private CancellationTokenSource? _operation;

    /// <summary>Signals that the projection has changed on the owning application thread.</summary>
    public event Action? Changed;

    /// <summary>Returns the current model settings, local context estimate and available commands.</summary>
    public WindowsAiSnapshot Snapshot()
    {
        var session = _target?.Catalog.TryGetSession(_target.Document.Path);
        var selection = _selection;
        var target = _target?.Document.Path ?? "Open a document to use Markdown AI.";
        if (selection != null)
        {
            target += selection.Unsupported ? " · Unsupported rendered selection" : " · Selected Markdown";
        }

        var references = ReferenceRows();
        var usage = _usage ?? Estimate(references);
        var capacity = _models.Any(model => model.Id == _model) ? MarkdownAi.Capacity(_model, usage) : null;
        var busy = _operation != null;
        return new(_provider, target, _target != null, selection != null, busy, _connectionCheck != null,
            !disabled && !busy && _target != null && _model.Length != 0 && !string.IsNullOrWhiteSpace(_prompt) && selection?.Unsupported != true &&
                !references.Any(reference => reference.Missing && !reference.IsTarget),
            !busy && session?.CanUndo == true, !busy && session?.CanRedo == true,
            !busy && session?.IsModified == true, _status, _prompt, _model, _effort, _models, references, _result, _preview,
            usage, capacity, _totalTokens, _requestCount);
    }

    /// <summary>Updates provider and target, invalidating any operation tied to a superseded document or connection.</summary>
    public void SetTarget(string provider, WindowsAiTarget? target)
    {
        provider = AiProviders.Normalize(provider);
        var providerChanged = provider != _provider;
        var targetChanged = !SameTarget(_target, target);
        var ownRevision = !providerChanged && targetChanged && _target != null && target != null &&
            ReferenceEquals(_target.Catalog, target.Catalog) && _target.Document.Path == target.Document.Path &&
            target.Document.Fingerprint == _applyingFingerprint;
        _target = target;
        if (ownRevision)
        {
            _selection = null;
        }
        else if (targetChanged || providerChanged)
        {
            _operation?.Cancel();
            _selection = null;
            _preview = null;
            _result = null;
            _usage = null;
            _status = "";
        }

        if (providerChanged)
        {
            _connectionCheck?.Cancel();
            _connectionCheck = null;
            _provider = provider;
            _models = [];
            _model = "";
            _status = "Check the selected AI connection to load its models.";
        }

        Notify();
    }

    /// <summary>Accepts only offsets that refer to the current source, preserving unsupported-selection feedback.</summary>
    public void SetSelection(string fingerprint, int start, int end, bool unsupported)
    {
        var document = _target?.Document;
        if (document == null || fingerprint != document.Fingerprint)
        {
            return;
        }

        _selection = unsupported ? new(fingerprint, 0, 0, true)
            : start >= 0 && end > start && end <= document.Source.Length ? new(fingerprint, start, end, false) : null;
        _usage = null;
        Notify();
    }

    /// <summary>Clears a selection explicitly so the next request targets the full document.</summary>
    public void ClearSelection()
    {
        _selection = null;
        _usage = null;
        Notify();
    }

    /// <summary>Retains bounded prompt input in the session and refreshes its local token projection.</summary>
    public void SetPrompt(string value)
    {
        _prompt = value[..Math.Min(value.Length, MarkdownAi.PromptCharacters)];
        _usage = null;
        Notify();
    }

    /// <summary>Selects a model only from the available provider-filtered catalog.</summary>
    public void SetModel(string id)
    {
        if (_models.Any(model => model.Id == id) && _model != id)
        {
            _model = id;
            _usage = null;
            Notify();
        }
    }

    /// <summary>Applies a supported reasoning effort to the next request.</summary>
    public void SetEffort(string value)
    {
        if (MarkdownAi.Efforts.Contains(value, StringComparer.Ordinal) && _effort != value)
        {
            _effort = value;
            Notify();
        }
    }

    /// <summary>Checks local configuration and loads available models when the AI panel is explicitly used.</summary>
    public async Task RefreshAvailabilityAsync()
    {
        if (_connectionCheck != null || _operation != null || lifetime.IsCancellationRequested)
        {
            return;
        }
        if (disabled)
        {
            _status = "AI connections are unavailable in this application mode.";
            Notify();
            return;
        }

        var provider = _provider;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _connectionCheck = cancellation;
        _status = "Checking AI connection…";
        Notify();
        try
        {
            var connection = await connections.StatusAsync(provider, cancellation.Token);
            var models = connection.Configured && connection.Available
                ? await connections.ModelsAsync(provider, cancellation.Token) : [];
            cancellation.Token.ThrowIfCancellationRequested();
            _models = MarkdownAi.Models.Where(model => models.Contains(model.Id, StringComparer.Ordinal))
                .OrderByDescending(model => model.Id == MarkdownAi.DefaultModel).ToArray();
            _model = _models.Any(model => model.Id == _model) ? _model : _models.FirstOrDefault()?.Id ?? "";
            _status = _models.Length > 0 ? "AI connection ready." : connection.Configured && connection.Available
                ? "This connection has no available models from the MarkVibe catalog." : connection.Message;
        }
        catch (OperationCanceledException)
        {
            // A newer provider or closing window superseded this lookup.
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                _models = [];
                _model = "";
                _status = SafeError(error, "Could not load AI models.");
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionCheck, cancellation))
            {
                _connectionCheck = null;
                Notify();
            }
        }
    }

    /// <summary>Validates a batch of reference paths and commits membership only when the entire batch is readable.</summary>
    public async Task AddReferencesAsync(IReadOnlyList<string> paths)
    {
        if (_target == null || _operation != null || lifetime.IsCancellationRequested)
        {
            return;
        }

        var target = _target;
        using var operation = BeginOperation("Checking reference documents…");
        try
        {
            var current = target.References.Paths.ToHashSet(StringComparer.Ordinal);
            var additions = paths.Select(path => WorkspacePath.Normalize(path, imported: true))
                .Distinct(StringComparer.Ordinal).Where(path => !current.Contains(path)).ToArray();
            if (current.Count + additions.Length > AiDocumentCollection.MaximumDocuments)
            {
                throw new WorkspaceException("ai-reference-limit", "Choose at most 16 reference documents.");
            }

            long bytes = 0;
            foreach (var path in current.Concat(additions))
            {
                var document = await target.Catalog.OpenAsync(target.Provider, path, operation.Token);
                bytes += Encoding.UTF8.GetByteCount(document.Source);
                if (bytes > MarkdownAi.ReferenceBytes)
                {
                    throw new WorkspaceException("ai-reference-size", "The reference collection exceeds its 4 MiB limit.");
                }
            }

            RequireCurrent(target, operation.Token);
            foreach (var path in additions)
            {
                target.References.Add(path);
            }
            _usage = null;
            _status = additions.Length > 0 ? "AI reference collection updated." : "The selected documents are already in the collection.";
        }
        catch (Exception error)
        {
            SetOperationError(target, operation, error, "Could not add these reference documents.");
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>Removes one reference, or clears the entire collection when no path is supplied.</summary>
    public void RemoveReference(string? path)
    {
        if (_target == null || _operation != null)
        {
            return;
        }

        if (path == null)
        {
            _target.References.Clear();
        }
        else
        {
            _target.References.Remove(path);
        }
        _usage = null;
        Notify();
    }

    /// <summary>Requests a bounded review or applies a complete local revision to the captured source interval.</summary>
    public async Task SendAsync()
    {
        if (_target == null || !Snapshot().CanSend || lifetime.IsCancellationRequested)
        {
            return;
        }

        var target = _target;
        var provider = _provider;
        var start = _selection?.Start ?? 0;
        var end = _selection?.End ?? target.Document.Source.Length;
        var request = new MarkdownAiRequest(_model, _effort, _prompt, target.Document.Source[start..end], _selection == null ? "document" : "selection");
        _preview = null;
        _result = null;
        using var operation = BeginOperation("Checking the request context…");
        operation.CancelAfter(TimeSpan.FromSeconds(MarkdownAi.RequestTimeoutSeconds));
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            target.Catalog.EnsureRevisionCapacity(target.Document.Path);
            var references = new List<MarkdownAiReference>();
            foreach (var path in target.References.Paths.Where(path => path != target.Document.Path))
            {
                var document = await target.Catalog.OpenAsync(target.Provider, path, operation.Token);
                references.Add(new(path, document.Source));
            }

            RequireCurrent(target, operation.Token);
            request = MarkdownAi.Validate(request with { References = references.ToArray() });
            var prefixes = await connections.PrepareAsync(provider, request, operation.Token);
            RequireCurrent(target, operation.Token);
            _usage = MarkdownAi.PreparedUsage(request, prefixes);
            MarkdownAi.EnsureFits(request, prefixes);
            _status = provider == AiProviders.CodexCli ? "Waiting for Codex CLI…" : "Waiting for OpenAI…";
            Notify();

            var exchange = await connections.EditAsync(provider, request, prefixes, operation.Token);
            RequireCurrent(target, operation.Token);
            var result = MarkdownAi.Read(exchange, request);
            _result = result;
            _usage = result.Usage;
            if (result.Usage.Reported)
            {
                _totalTokens += result.Usage.Total;
                _requestCount++;
            }

            if (result.Complete)
            {
                _preview = new MarkdownParser().Parse("response.md", result.Markdown);
                if (result.Kind == "edit")
                {
                    var source = target.Document.Source;
                    var revised = source[..start] + result.Markdown + source[end..];
                    _applyingFingerprint = MarkdownParser.Fingerprint(revised);
                    await apply(target, revised, operation.Token);
                    _selection = null;
                    _prompt = "";
                }
            }
            _status = result.Summary;
        }
        catch (Exception error)
        {
            var expired = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(MarkdownAi.RequestTimeoutSeconds);
            SetOperationError(target, operation, error, "The Markdown request could not be completed.", expired);
        }
        finally
        {
            _applyingFingerprint = null;
            EndOperation(operation);
        }
    }

    /// <summary>Cancels the active AI request or reference validation.</summary>
    public void Cancel() => _operation?.Cancel();

    /// <summary>Projects current references without rereading or duplicating their Markdown.</summary>
    private WindowsAiReferenceRow[] ReferenceRows()
    {
        if (_target == null || _target.References.Count == 0)
        {
            return [];
        }

        var target = _target;
        var cards = target.Catalog.Cards.ToDictionary(card => card.Path, StringComparer.Ordinal);
        return target.References.Paths.Select(path => new WindowsAiReferenceRow(path,
            cards.GetValueOrDefault(path)?.Title ?? path, cards.GetValueOrDefault(path)?.Statistics?.EstimatedTokens,
            path == target.Document.Path, !cards.ContainsKey(path))).ToArray();
    }

    /// <summary>Estimates the next request using the source range and the catalog's reference token statistics.</summary>
    private MarkdownAiUsage Estimate(IReadOnlyList<WindowsAiReferenceRow> references)
    {
        var source = _target?.Document.Source ?? "";
        if (_selection is { Unsupported: false } selection)
        {
            source = source[selection.Start..selection.End];
        }
        var usage = MarkdownAi.Estimate(source, _prompt);
        var cost = references.Where(reference => !reference.IsTarget && reference.Tokens.HasValue)
            .Sum(reference => MarkdownAi.EstimateReferenceTokens(reference.Path, reference.Tokens.GetValueOrDefault()));
        return usage with { Document = usage.Document + cost, Input = usage.Input + cost, Total = usage.Total + cost };
    }

    /// <summary>Starts an exclusive document-scoped operation with lifetime cancellation.</summary>
    private CancellationTokenSource BeginOperation(string status)
    {
        _operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _status = status;
        Notify();
        return _operation;
    }

    /// <summary>Clears only the operation that owns the in-progress state.</summary>
    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operation, operation))
        {
            _operation = null;
            Notify();
        }
    }

    /// <summary>Rejects pending work after navigation or a revision change.</summary>
    private void RequireCurrent(WindowsAiTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SameTarget(target, _target))
        {
            throw new OperationCanceledException(token);
        }
    }

    /// <summary>Reports a fixed error or cancellation only to the workspace that initiated the operation.</summary>
    private void SetOperationError(WindowsAiTarget target, CancellationTokenSource operation, Exception error, string fallback, bool timedOut = false)
    {
        if (!lifetime.IsCancellationRequested && SameTarget(target, _target) && ReferenceEquals(_operation, operation))
        {
            _status = error is OperationCanceledException ? timedOut ? "The AI request timed out." : "AI operation cancelled."
                : SafeError(error, fallback);
        }
    }

    /// <summary>Compares application-owned workspace and document identities, independently of UI objects.</summary>
    private static bool SameTarget(WindowsAiTarget? left, WindowsAiTarget? right) => left == null || right == null
        ? left == right : ReferenceEquals(left.Catalog, right.Catalog) && left.Document.Path == right.Document.Path &&
            left.Document.Fingerprint == right.Document.Fingerprint;

    /// <summary>Uses provider-approved errors and keeps arbitrary exception details out of the assistant panel.</summary>
    private static string SafeError(Exception error, string fallback) => error is WorkspaceException issue ? issue.Message : fallback;

    /// <summary>Publishes current state when the application still owns the session.</summary>
    private void Notify()
    {
        if (!lifetime.IsCancellationRequested)
        {
            Changed?.Invoke();
        }
    }
}
