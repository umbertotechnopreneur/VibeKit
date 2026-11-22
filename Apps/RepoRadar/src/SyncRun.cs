using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;
using WorkspaceRepo;

sealed record SyncPlan(Repository Repository, string Action, string BlockedReason)
{
    public string Label => Action switch
    {
        "clone" => "Clone",
        "blocked" => "Blocked",
        "skip" => "Skip",
        "stash" => "Stash / update",
        _ => "Update"
    };
}

enum SyncState { Queued, Active, Complete, Issue, Skipped, NotRun }

sealed class SyncRun(string root, IReadOnlyList<SyncPlan> plans)
{
    sealed class Item(SyncPlan plan)
    {
        public SyncPlan Plan { get; } = plan;
        public SyncState State { get; set; } = plan.Action switch
        {
            "blocked" => SyncState.Issue,
            "skip" => SyncState.Skipped,
            _ => SyncState.Queued
        };
        public SyncResult? Result { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    static readonly Regex GitPercent = new(
        @"(?:^|:\s*)(Counting objects|Compressing objects|Receiving objects|Resolving deltas|Unpacking objects|Updating files|Checking out files|Filtering content):\s*(\d{1,3})%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    readonly Item[] items = plans.Select(plan => new Item(plan)).ToArray();
    readonly Stopwatch elapsed = new();
    readonly Stopwatch operation = new();
    readonly object progressLock = new();
    string phase = "";
    int? percent;
    Item? active;
    bool stopped;

    void SetStage(string stage)
    {
        lock (progressLock) { phase = stage; percent = null; }
    }

    void Report(string line)
    {
        var match = GitPercent.Match(line);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var value) || value > 100) return;
        lock (progressLock) { phase = match.Groups[1].Value; percent = value; }
    }

    public async Task Execute(Func<SyncPlan, Action<string>, Action<string>, Task<SyncResult>> sync)
    {
        AnsiConsole.Clear();
        elapsed.Start();
        try
        {
            await AnsiConsole.Live(Frame(false)).AutoClear(true).StartAsync(async display =>
            {
                display.Refresh();
                foreach (var item in items.Where(item => item.State == SyncState.Queued))
                {
                    Git.Stop.Token.ThrowIfCancellationRequested();
                    active = item;
                    item.State = SyncState.Active;
                    SetStage("Starting");
                    operation.Restart();
                    display.UpdateTarget(Frame(false));
                    try
                    {
                        var work = sync(item.Plan, Report, SetStage);
                        while (!work.IsCompleted)
                        {
                            try { await work.WaitAsync(RefreshInterval); }
                            catch (TimeoutException)
                            {
                                if (!work.IsCompleted) display.UpdateTarget(Frame(false));
                            }
                        }
                        item.Result = await work;
                        item.State = item.Result.Skipped ? SyncState.Skipped
                            : item.Result.Success ? SyncState.Complete : SyncState.Issue;
                    }
                    catch (OperationCanceledException failure)
                    {
                        item.Result = new(item.Plan.Repository.Name, false,
                            Git.Stop.IsCancellationRequested ? "Interrupted by the operator. " + failure.Message
                                : "Operation interrupted. " + failure.Message);
                        item.State = SyncState.Issue;
                        throw;
                    }
                    catch (Exception failure)
                    {
                        item.Result = new(item.Plan.Repository.Name, false, failure.Message);
                        item.State = SyncState.Issue;
                    }
                    finally
                    {
                        operation.Stop();
                        item.Elapsed = operation.Elapsed;
                        active = null;
                    }
                    display.UpdateTarget(Frame(false));
                }
            });
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            foreach (var item in items.Where(item => item.State == SyncState.Queued)) item.State = SyncState.NotRun;
            throw;
        }
        finally
        {
            elapsed.Stop();
            AnsiConsole.Write(Frame(true));
        }
    }

    public static IRenderable Preview()
    {
        var run = new SyncRun(@"C:\Work\Repos",
        [
            new(new("corelib", "", "corelib"), "pull", ""),
            new(new("ramtaskman", "", "ramtaskman"), "clone", ""),
            new(new("sitegen", "", "sitegen"), "clone", "")
        ]);
        run.items[0].State = SyncState.Complete;
        run.active = run.items[1];
        run.active.State = SyncState.Active;
        run.Report("Receiving objects: 48%");
        return run.Frame(false, preview: true);
    }

    IRenderable Frame(bool final, bool preview = false)
    {
        var compact = AnsiConsole.Profile.Height < 23 || AnsiConsole.Profile.Width < 55;
        var rows = new List<IRenderable>
        {
            Ui.ScreenHeading(final ? (stopped ? "Clone and update / Stopped" : "Clone and update / Results")
                : preview ? "Preview / Clone and update" : "Clone and update / Progress", context: "Workspace: " + root),
            new Markup(Counts()),
            new Text(" ")
        };
        if (active is not null) rows.Add(ActivePanel(active.Plan.Repository.Name, compact));

        if (final || !compact)
        {
            var table = Ui.Table("Repository", "Action", "Status").Expand();
            var limit = final ? items.Length : Math.Max(1, AnsiConsole.Profile.Height - 21);
            var index = active is null ? Array.FindIndex(items, item => item.State == SyncState.Queued) : Array.IndexOf(items, active);
            var first = Math.Clamp(index - limit / 2, 0, Math.Max(0, items.Length - limit));
            foreach (var item in items.Skip(first).Take(limit))
                table.AddRow(Ui.E(item.Plan.Repository.Name), Ui.E(item.Plan.Label), Status(item, final));
            if (items.Length > limit)
                rows.Add(new Markup($"[#A7B9CD]Showing {first + 1}-{first + limit} of {items.Length} repositories[/]"));
            rows.Add(table);
        }
        var footer = $"[#70DBCC]Run elapsed[/] {Duration(elapsed.Elapsed)}";
        if (active is not null)
        {
            lock (progressLock)
                footer = $"[bold #70DBCC]Git[/] {Ui.E(active.Plan.Repository.Name)} · {Ui.E(phase)}" +
                    (percent.HasValue ? $" {percent}%" : "") + "  ·  " + footer;
        }
        if (!final)
            footer += (preview ? "  [#A7B9CD]Example data[/]" : "  [#70DBCC]Ctrl+C[/] Stop") +
                (compact ? "\n" : "  ") + "[#A7B9CD]Git percentages are per phase[/]";
        rows.Add(Ui.ScreenFooter(footer));
        return new Rows(rows);
    }

    string Counts()
    {
        int Count(SyncState state) => items.Count(item => item.State == state);
        var counters = new List<string>
        {
            $"[bold]{items.Length}[/] Selected", $"[#89E5A2]{Count(SyncState.Complete)}[/] Complete",
            $"[#E8B86A]{Count(SyncState.Active)}[/] Active", $"[#A7B9CD]{Count(SyncState.Queued)}[/] Queued",
            $"[#F58F8F]{Count(SyncState.Issue)}[/] Issues"
        };
        if (Count(SyncState.Skipped) is var skipped && skipped > 0) counters.Add($"{skipped} Skipped");
        if (Count(SyncState.NotRun) is var notRun && notRun > 0) counters.Add($"{notRun} Not run");
        const string divider = "  [#A7B9CD]|[/]  ";
        return AnsiConsole.Profile.Width >= 58 ? string.Join(divider, counters)
            : string.Join(divider, counters.Take(3)) + "\n" + string.Join(divider, counters.Skip(3));
    }

    IRenderable ActivePanel(string name, bool compact)
    {
        string stage;
        int? value;
        lock (progressLock) { stage = phase; value = percent; }
        var label = new Markup($"[bold #E8B86A]{Ui.E(name)}[/]  [#A7B9CD]|[/]  {Ui.E(stage)}");
        var time = $"[#A7B9CD]Operation elapsed[/] {Duration(operation.Elapsed)}";
        if (value is int current)
        {
            var barWidth = Math.Clamp(AnsiConsole.Profile.Width - 41, 6, 46);
            var filled = current * barWidth / 100;
            time = $"[#E8B86A]{new string('━', filled)}[/][#62748A]{new string('━', barWidth - filled)}[/]  {current}% of phase\n" + time;
        }
        if (compact) return new Rows(label, new Markup(time));
        return new Panel(new Rows(label, new Text(" "), new Markup(time)))
            .Padding(1, 0, 1, 0).RoundedBorder().BorderColor(Ui.Teal).Expand();
    }

    string Status(Item item, bool final)
    {
        var label = item.State switch
        {
            SyncState.Active => "[#E8B86A]Active[/]",
            SyncState.Complete => "[#89E5A2]Complete[/]",
            SyncState.Issue => "[#F58F8F]Issue[/]",
            SyncState.Skipped => "[#A7B9CD]Skipped[/]",
            SyncState.NotRun => "[#A7B9CD]Not run[/]",
            _ => "[#A7B9CD]Queued[/]"
        };
        if (item.Result is not null) label += "  " + Duration(item.Elapsed);
        if (!final) return label;
        var detail = item.Result?.Message ?? item.Plan.BlockedReason;
        if (detail.Length > 0) label += "\n" + Ui.E(detail);
        if (item.Result is { Stash.Length: > 0 } result)
            label += "\n[#A7B9CD]Recovery stash retained:[/] " + Ui.E(result.Stash);
        return label;
    }

    static string Duration(TimeSpan time) => $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
}
