using System.Collections.Concurrent;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private enum BulkStage { Choose, Message, Planning, Review, Confirm, Running, Results }
    private static readonly BulkAction[] BulkActions =
        [BulkAction.Prune, BulkAction.Pull, BulkAction.Commit, BulkAction.Push, BulkAction.CommitPush,
         BulkAction.Stash, BulkAction.StashPull];

    private readonly HashSet<string> bulkMarked = new(WorkspaceViewModel.Paths);
    private readonly Dictionary<string, BulkTarget> bulkQueue = new(WorkspaceViewModel.Paths);
    private bool bulkSelectedOnly;
    private BulkStage bulkStage;
    private int bulkActionIndex, bulkItemIndex, bulkDetailOffset, bulkPageSize = 1;
    private bool bulkIncludeUntracked, bulkStopping, bulkQueueMessage, bulkUsingQueue;
    private string bulkFeedback = "";
    private string[] bulkMessagePaths = [];
    private BulkPlan? bulkPlan;
    private readonly List<BulkResult> bulkResults = [];
    private readonly ConcurrentQueue<BulkResult> bulkUpdates = new();
    private readonly BulkOperations bulkOperations = new();
    private CancellationTokenSource? bulkCancellation;
    private Task<BulkPlan>? bulkPlanning;
    private sealed record BulkCompletion(IReadOnlyList<BulkResult> Results, List<IndexedRepo>? Updated, string RefreshError);
    private Task<BulkCompletion>? bulkRunning;

    private int BulkSelectionCount => bulkMarked.Count;
    private int BulkQueuedCount => bulkQueue.Count;
    private BulkAction CurrentBulkAction => BulkActions[bulkActionIndex];
    private IReadOnlyList<IndexedRepo> BulkMarkedRepositories => all.Where(repo => bulkMarked.Contains(repo.Path)).ToArray();
    private bool BulkActive => bulkPlanning != null || bulkRunning != null;

    private void SwitchMark()
    {
        if (visible.ElementAtOrDefault(selected) is not { } repo) return;
        if (!bulkMarked.Add(repo.Path)) bulkMarked.Remove(repo.Path);
        Reorder(repo.Path);
    }

    private void SelectVisible()
    {
        bulkMarked.UnionWith(visible.Select(repo => repo.Path));
        dirtyFrame = true;
    }

    private void ClearMarks()
    {
        bulkMarked.Clear();
        bulkSelectedOnly = false;
        Reorder();
    }

    private void OpenBulkOperations()
    {
        bulkStage = BulkStage.Choose;
        bulkFeedback = "";
        bulkPlan = null;
        bulkResults.Clear();
        ResetBulkPosition();
        view = View.Operations;
    }

    private void ResetBulkPosition()
    {
        bulkItemIndex = bulkDetailOffset = 0;
    }

    private void HandleBulkOperations(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            switch (bulkStage)
            {
                case BulkStage.Planning:
                case BulkStage.Running: RequestBulkStop(); break;
                case BulkStage.Confirm: bulkStage = BulkStage.Review; break;
                case BulkStage.Message:
                case BulkStage.Review:
                    bulkStage = BulkStage.Choose; bulkFeedback = ""; ResetBulkPosition(); break;
                default: view = View.Grid; break;
            }
            return;
        }

        switch (bulkStage)
        {
            case BulkStage.Choose:
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: bulkActionIndex = Math.Max(0, bulkActionIndex - 1); break;
                    case ConsoleKey.DownArrow: bulkActionIndex = Math.Min(BulkActions.Length - 1, bulkActionIndex + 1); break;
                    case ConsoleKey.Home: bulkActionIndex = 0; break;
                    case ConsoleKey.End: bulkActionIndex = BulkActions.Length - 1; break;
                    case ConsoleKey.U when CurrentBulkAction is BulkAction.Stash or BulkAction.StashPull:
                        bulkIncludeUntracked = !bulkIncludeUntracked; break;
                    case ConsoleKey.Enter: ChooseBulkAction(queue: false); break;
                    case ConsoleKey.A: ChooseBulkAction(queue: true); break;
                    case ConsoleKey.R when bulkQueue.Count > 0: StartBulkPlanning(bulkQueue.Values.ToArray(), queued: true); break;
                    case ConsoleKey.X:
                        bulkQueue.Clear(); bulkFeedback = "Queued actions cleared."; break;
                }
                break;
            case BulkStage.Message:
                if (key.Key == ConsoleKey.Enter)
                {
                    if (string.IsNullOrWhiteSpace(input)) { bulkFeedback = "Enter a commit message."; return; }
                    PrepareBulkTargets(bulkMessagePaths, input.Trim(), bulkQueueMessage);
                }
                else Type(key);
                break;
            case BulkStage.Review:
                if (key.Key == ConsoleKey.Enter && bulkPlan?.Items.Any(item => item.Ready) == true)
                { bulkStage = BulkStage.Confirm; break; }
                MoveBulkPosition(key.Key, bulkPlan?.Items.Count ?? 0);
                break;
            case BulkStage.Confirm:
                // A different key commits the plan, so holding Enter can never pass both confirmations.
                if (key.Key == ConsoleKey.R) StartBulkRun();
                else MoveBulkPosition(key.Key, bulkPlan?.Items.Count ?? 0);
                break;
            case BulkStage.Running:
            case BulkStage.Results:
                MoveBulkPosition(key.Key, bulkResults.Count);
                break;
        }
    }

    private void MoveBulkPosition(ConsoleKey key, int count)
    {
        if (key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
        {
            bulkDetailOffset = Math.Max(0, bulkDetailOffset + (key == ConsoleKey.RightArrow ? 1 : -1));
            return;
        }
        int next = Math.Clamp(key switch
        {
            ConsoleKey.UpArrow => bulkItemIndex - 1,
            ConsoleKey.DownArrow => bulkItemIndex + 1,
            ConsoleKey.PageUp => bulkItemIndex - bulkPageSize,
            ConsoleKey.PageDown => bulkItemIndex + bulkPageSize,
            ConsoleKey.Home => 0,
            ConsoleKey.End => count - 1,
            _ => bulkItemIndex
        }, 0, Math.Max(0, count - 1));
        if (next != bulkItemIndex) bulkDetailOffset = 0;
        bulkItemIndex = next;
    }

    private void ChooseBulkAction(bool queue)
    {
        var targets = BulkMarkedRepositories.Select(repo => repo.Path).ToArray();
        if (targets.Length == 0)
        {
            bulkFeedback = "Go back and mark repositories with Space.";
            return;
        }
        if (CurrentBulkAction is BulkAction.Commit or BulkAction.CommitPush)
        {
            bulkMessagePaths = targets;
            bulkQueueMessage = queue;
            bulkStage = BulkStage.Message;
            input = bulkFeedback = "";
        }
        else PrepareBulkTargets(targets, "", queue);
    }

    private void PrepareBulkTargets(string[] paths, string message, bool queue)
    {
        var action = CurrentBulkAction;
        bool includeUntracked = bulkIncludeUntracked && action is BulkAction.Stash or BulkAction.StashPull;
        var targets = paths.Select(path => new BulkTarget(path, action, message, IncludeUntracked: includeUntracked)).ToArray();
        if (!queue) { StartBulkPlanning(targets, queued: false); return; }
        foreach (var target in targets) bulkQueue[target.Path] = target;
        view = View.Grid;
        status = $"Queued {BulkOperations.Label(action)} for {targets.Length} · {bulkQueue.Count} queued · [3] Operations";
    }

    private void StartBulkPlanning(IReadOnlyList<BulkTarget> targets, bool queued)
    {
        bulkUsingQueue = queued;
        bulkPlan = null;
        bulkFeedback = "";
        bulkStopping = false;
        bulkStage = BulkStage.Planning;
        ResetBulkPosition();
        bulkCancellation = new();
        var token = bulkCancellation.Token;
        bulkPlanning = Task.Run(() => bulkOperations.PlanAsync(targets, token));
    }

    private sealed class BulkProgress(ConcurrentQueue<BulkResult> updates) : IProgress<BulkResult>
    {
        public void Report(BulkResult value) => updates.Enqueue(value);
    }

    private void StartBulkRun()
    {
        if (bulkPlan is not { } plan || !plan.Items.Any(item => item.Ready)) return;
        bulkStage = BulkStage.Running;
        bulkFeedback = "";
        bulkResults.Clear();
        bulkUpdates.Clear();
        ResetBulkPosition();
        bulkCancellation = new();
        var token = bulkCancellation.Token;
        bulkRunning = Task.Run(async () =>
        {
            var results = await bulkOperations.ApplyAsync(plan, token, new BulkProgress(bulkUpdates));
            var paths = results.Where(result => !result.Skipped).Select(result => result.Path).ToArray();
            if (paths.Length == 0) return new BulkCompletion(results, null, "");
            try { return new BulkCompletion(results, await index.InspectModifiedAsync(root, paths), ""); }
            catch (Exception ex) { return new BulkCompletion(results, null, Safety.Redact(ex.Message)); }
        });
    }

    private void RequestBulkStop()
    {
        bulkStopping = true;
        bulkCancellation?.Cancel();
    }

    private async Task UpdateBulkOperations()
    {
        while (bulkUpdates.TryDequeue(out var item))
        { bulkResults.Add(item); dirtyFrame = true; }
        if (bulkPlanning is { IsCompleted: true } planning)
        {
            try
            {
                var reviewed = await planning;
                if (bulkStopping) { bulkStage = BulkStage.Choose; bulkFeedback = "Review cancelled."; }
                else { bulkPlan = reviewed; bulkStage = BulkStage.Review; }
            }
            catch (OperationCanceledException) { bulkStage = BulkStage.Choose; bulkFeedback = "Review cancelled."; }
            catch (Exception ex) { bulkStage = BulkStage.Choose; bulkFeedback = "Review failed. " + Safety.Redact(ex.Message); }
            finally { bulkPlanning = null; FinishBulkActivity(); }
        }
        if (bulkRunning is { IsCompleted: true } runningBatch)
        {
            try
            {
                var completed = await runningBatch;
                bulkResults.Clear();
                bulkResults.AddRange(completed.Results);
                if (bulkUsingQueue)
                    foreach (var result in completed.Results.Where(r => r.Success)) bulkQueue.Remove(result.Path);
                if (completed.Updated is { } updated)
                {
                    var keep = visible.ElementAtOrDefault(selected)?.Path;
                    all = updated;
                    Reorder(keep);
                }
                bulkFeedback = bulkStopping ? "Stopped after the current repository." : "Plan finished.";
                if (completed.RefreshError.Length > 0) bulkFeedback += " Local status refresh failed. " + completed.RefreshError;
                status = bulkFeedback;
            }
            catch (Exception ex) { bulkFeedback = "Plan interrupted. " + Safety.Redact(ex.Message); }
            finally { bulkRunning = null; bulkStage = BulkStage.Results; FinishBulkActivity(); }
        }
    }

    private void FinishBulkActivity()
    {
        bulkCancellation?.Dispose();
        bulkCancellation = null;
        bulkStopping = false;
        ResetBulkPosition();
        dirtyFrame = true;
    }
}
