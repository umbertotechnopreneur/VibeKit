/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; cancellable full-content workspace search
 * Human guidance: filter the Pages tree using the contents of workspace documents
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.IO;
using MarkVibe.Core;

namespace MarkVibe.Windows;

/// <summary>Records the matching canonical paths and the number of documents that could not be searched.</summary>
internal sealed record WorkspacePageMatches(IReadOnlySet<string> Paths, int Unreadable);

/// <summary>Searches the complete Markdown inventory without parsing pages or changing catalog or navigation state.</summary>
internal static class WorkspacePageSearch
{
    /// <summary>Captures catalog and draft snapshots on their owner thread before searching one bounded file at a time.</summary>
    public static Task<WorkspacePageMatches> FindAsync(DesktopWorkspace workspace, string query, IProgress<int> progress, CancellationToken token)
    {
        var cards = workspace.Catalog.Cards;
        var drafts = workspace.Catalog.Sessions.ToDictionary(session => session.Path, session => session.CurrentSource, StringComparer.Ordinal);
        return Task.Run(async () =>
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var unreadable = 0;
            var reported = Stopwatch.GetTimestamp();
            for (var index = 0; index < cards.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var path = cards[index].Path;
                try
                {
                    var source = drafts.TryGetValue(path, out var draft)
                        ? draft : await workspace.Provider.ReadDocumentAsync(path, token).ConfigureAwait(false);
                    if (source.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(path);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkspaceException)
                {
                    unreadable++;
                }
                if (Stopwatch.GetElapsedTime(reported) >= TimeSpan.FromMilliseconds(120))
                {
                    progress.Report(index + 1);
                    reported = Stopwatch.GetTimestamp();
                }
            }
            token.ThrowIfCancellationRequested();
            return new WorkspacePageMatches(paths, unreadable);
        }, token);
    }
}
