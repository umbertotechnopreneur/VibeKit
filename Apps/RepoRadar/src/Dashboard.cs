using System.Diagnostics;
using System.Text.Json;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private enum View { Grid, Consent, Browser, Busy, Details, Info, Layout, About, Help, Search, PathEntry, Refresh, Notice, Operations }
    private static readonly string[] Filters = ["Overview", "Sync", "Green", "Push", "Purple", "Gone", "Red", "Unknown", "Amber"];
    private static readonly string[] Sizes = ["Compact", "Standard", "Wide"];
    private static readonly string[] Sorts = ["Alignment", "Name", "Remote", "Folder"];
    private const int LayoutFieldCount = 9;
    private sealed class Options
    {
        public string Root { get; set; } = "";
        public int Size { get; set; } = 1;
        public int Sort { get; set; }
        public bool ShowPath { get; set; } = true;
        public bool ShowBranches { get; set; } = true;
        public bool ShowCounts { get; set; } = true;
        public bool ShowFiles { get; set; } = true;
        public bool ShowChecked { get; set; } = true;
        public bool PinFirst { get; set; } = true;
        public bool IncludeHidden { get; set; }
        public List<string> Pinned { get; set; } = [];
        public List<string> Hidden { get; set; } = [];
        public Options Copy() => (Options)MemberwiseClone();
    }

    private readonly DashboardTerminal terminal;
    private readonly WorkspaceIndex index;
    private readonly string settings;
    private readonly Func<string, IReadOnlyList<IndexedRepo>, Task> legacy;
    private Options options;
    private Options? savedLayout;
    private readonly StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly WorkspaceViewModel model = new();
    private List<IndexedRepo> all = [];
    private List<IndexedRepo> scoped => model.Scoped;
    private List<IndexedRepo> visible => model.Visible;
    private string root, status = "Ready", search = "", input = "", notice = "";
    private string filter = "Overview";
    private int selected, offset, layoutRow, detailOffset, presentation, consentChoice;
    private TerminalViewport? viewport;
    private WorkspaceTab tab;
    private bool running = true, dirtyFrame = true;
    private volatile bool interrupted;
    private View view;
    private View aboutReturn = View.Grid;
    private bool awaitingRoot;
    private Task<List<IndexedRepo>>? operation;
    private CancellationTokenSource? cancellation;
    private WorkspaceScanSession? scanSession;
    private long lastAnimation;
    private string operationTitle = "";

    public static async Task RunAsync(string? preferredRoot, string stateDirectory, Func<string, IReadOnlyList<IndexedRepo>, Task> legacyCallback, string? rootOverride = null)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new InvalidOperationException($"Open {ProductBrand.Name} in an interactive terminal.");
        Directory.CreateDirectory(stateDirectory);
        using var screen = new DashboardTerminal();
        var app = new Dashboard(screen, preferredRoot, stateDirectory, legacyCallback, rootOverride);
        await app.Run();
    }

    private Dashboard(DashboardTerminal screen, string? preferredRoot, string stateDirectory, Func<string, IReadOnlyList<IndexedRepo>, Task> callback, string? rootOverride)
    {
        terminal = screen;
        legacy = callback;
        settings = Path.Combine(stateDirectory, "dashboard.json");
        options = LoadOptions();
        root = rootOverride ?? (options.Root.Length > 0 ? options.Root : preferredRoot ?? "");
        if (!Directory.Exists(root)) { root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); awaitingRoot = true; }
        if (!Directory.Exists(root)) root = Environment.CurrentDirectory;
        root = Path.GetFullPath(root);
        index = new WorkspaceIndex(Path.Combine(stateDirectory, "workspace.sqlite3"));
        all = index.Load(root);
        Reorder();
        view = index.HasSnapshot(root) ? View.Grid : View.Consent;
        if (awaitingRoot) OpenBrowser();
        if (all.Count > 0) status = "Showing saved observations. F5 checks origin.";
    }

    private Options LoadOptions()
    {
        if (!File.Exists(settings)) return new();
        try
        {
            var loaded = JsonSerializer.Deserialize<Options>(File.ReadAllText(settings)) ?? new();
            loaded.Size = Math.Clamp(loaded.Size, 0, Sizes.Length - 1);
            loaded.Sort = Math.Clamp(loaded.Sort, 0, Sorts.Length - 1);
            loaded.Root ??= ""; loaded.Pinned ??= []; loaded.Hidden ??= [];
            return loaded;
        }
        catch (JsonException) { status = "Saved layout could not be read. Using the default layout."; return new(); }
    }

    private void Save()
    {
        var tmp = settings + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, settings, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = "Could not save dashboard preferences: " + ex.Message;
        }
    }

    private void Reorder(string? keepPath = null)
    {
        if (bulkMarked.Count > 0 || bulkQueue.Count > 0)
        {
            var loaded = all.Select(repo => repo.Path).ToHashSet(paths);
            bulkMarked.IntersectWith(loaded);
            foreach (string queued in bulkQueue.Keys.Where(path => !loaded.Contains(path)).ToArray()) bulkQueue.Remove(queued);
        }
        model.Update(all, options.IncludeHidden, options.PinFirst, options.Hidden, options.Pinned, search, filter, options.Sort,
            bulkSelectedOnly ? bulkMarked : null);
        if (keepPath != null && visible.FindIndex(r => paths.Equals(r.Path, keepPath)) is var i && i >= 0) selected = i;
        selected = Math.Clamp(selected, 0, Math.Max(0, visible.Count - 1));
        dirtyFrame = true;
    }

    private async Task Run()
    {
        ConsoleCancelEventHandler onCancel = (_, args) => { args.Cancel = true; interrupted = true; };
        Console.CancelKeyPress += onCancel;
        int width = -1, height = -1;
        try
        {
            while (running)
            {
                if (interrupted)
                {
                    interrupted = false;
                    if (BulkActive) RequestBulkStop();
                    else if (operation != null) Cancel();
                    else break;
                }
                if (operation?.IsCompleted == true) await FinishOperation();
                if (BulkActive) await UpdateBulkOperations();
                long now = Stopwatch.GetTimestamp();
                if ((operation != null || BulkActive) && Stopwatch.GetElapsedTime(lastAnimation, now).TotalMilliseconds >= 100)
                { dirtyFrame = true; lastAnimation = now; }
                int w = Math.Min(terminal.Width, 420), h = Math.Min(terminal.Height, 160);
                if (dirtyFrame || width != w || height != h)
                {
                    width = w; height = h; dirtyFrame = false;
                    Paint(w, h);
                }
                if (!Console.KeyAvailable) { await Task.Delay(55); continue; }
                var key = Console.ReadKey(true);
                try { await Handle(key); }
                catch (Exception ex) when (ex is not OperationCanceledException and not global::PreflightRequestException)
                { notice = ex.Message; view = View.Notice; status = "Action needs attention"; }
                dirtyFrame = true;
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            cancellation?.Cancel();
            if (operation != null)
            {
                try { await operation; } catch (OperationCanceledException) { }
            }
            cancellation?.Dispose();
            bulkCancellation?.Cancel();
            try
            {
                if (bulkPlanning != null) await bulkPlanning;
                if (bulkRunning != null) await bulkRunning;
            }
            catch (OperationCanceledException) { }
            finally { bulkCancellation?.Dispose(); }
        }
    }

    private async Task Handle(ConsoleKeyInfo k)
    {
        if (view == View.Help) { HandleHelp(k); return; }
        if ((k.Key == ConsoleKey.F1 || k.KeyChar == '?') &&
            view is View.Grid or View.Consent or View.Refresh or View.About)
        { OpenHelp(); return; }
        if (view == View.Operations) { HandleBulkOperations(k); return; }
        if (view == View.Busy) { if (k.Key == ConsoleKey.Escape) Cancel(); return; }
        if (view == View.Browser || view == View.PathEntry) { BrowserKey(k); return; }
        if (view == View.Search)
        {
            if (k.Key == ConsoleKey.Enter) { search = input.Trim(); selected = offset = 0; Reorder(); view = View.Grid; }
            else if (k.Key == ConsoleKey.Escape) view = View.Grid;
            else Type(k);
            return;
        }
        if (view == View.Consent || view == View.Refresh)
        {
            if (view == View.Consent && k.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Tab)
                consentChoice = (consentChoice + (k.Key == ConsoleKey.LeftArrow ? 2 : 1)) % 3;
            else if (view == View.Consent && k.Key == ConsoleKey.Enter && consentChoice == 1) OpenBrowser();
            else if (view == View.Consent && k.Key == ConsoleKey.Enter && consentChoice == 2) { view = View.Grid; status = "Ready"; }
            else if (k.Key is ConsoleKey.Enter or ConsoleKey.Y) StartOperation(view == View.Refresh, view == View.Consent);
            else if (view == View.Consent && k.Key == ConsoleKey.L) StartOperation(false, false);
            else if (view == View.Consent && k.Key == ConsoleKey.O) OpenBrowser();
            else if (k.Key is ConsoleKey.Escape or ConsoleKey.N) { view = View.Grid; status = "Ready"; }
            return;
        }
        if (view == View.Details)
        {
            if (k.Key is ConsoleKey.Escape or ConsoleKey.Enter) view = View.Grid;
            else if (k.Key == ConsoleKey.DownArrow) detailOffset++;
            else if (k.Key == ConsoleKey.UpArrow) detailOffset = Math.Max(0, detailOffset - 1);
            return;
        }
        if (view == View.Info) { HandleRepositoryInfo(k.Key); return; }
        if (view == View.Layout)
        {
            if (k.Key == ConsoleKey.Escape)
            {
                options = savedLayout ?? options;
                savedLayout = null;
                Reorder(); view = View.Grid;
            }
            else if (k.Key == ConsoleKey.Enter) { savedLayout = null; Save(); Reorder(); view = View.Grid; }
            else if (k.Key == ConsoleKey.R)
            {
                options.Size = 1;
                options.Sort = 0;
                options.ShowPath = options.ShowBranches = options.ShowCounts = options.ShowFiles = options.ShowChecked = options.PinFirst = true;
                options.IncludeHidden = false;
                Reorder();
            }
            else if (k.Key == ConsoleKey.UpArrow) layoutRow = (layoutRow + LayoutFieldCount - 1) % LayoutFieldCount;
            else if (k.Key == ConsoleKey.DownArrow) layoutRow = (layoutRow + 1) % LayoutFieldCount;
            else if (k.Key is ConsoleKey.Spacebar or ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
            {
                int direction = k.Key == ConsoleKey.LeftArrow ? -1 : 1;
                switch (layoutRow)
                {
                    case 0: options.Size = (options.Size + Sizes.Length + direction) % Sizes.Length; break;
                    case 1: options.Sort = (options.Sort + Sorts.Length + direction) % Sorts.Length; break;
                    case 2: options.ShowPath = !options.ShowPath; break;
                    case 3: options.ShowBranches = !options.ShowBranches; break;
                    case 4: options.ShowCounts = !options.ShowCounts; break;
                    case 5: options.IncludeHidden = !options.IncludeHidden; break;
                    case 6: options.ShowFiles = !options.ShowFiles; break;
                    case 7: options.ShowChecked = !options.ShowChecked; break;
                    case 8: options.PinFirst = !options.PinFirst; break;
                }
                Reorder();
            }
            return;
        }
        if (view == View.About)
        {
            if (k.Key is ConsoleKey.Escape or ConsoleKey.Enter or ConsoleKey.A) { view = aboutReturn; return; }
            string? link = k.Key switch
            {
                ConsoleKey.G => ProductBrand.SourceRepository,
                ConsoleKey.M => ProductBrand.Manifesto,
                ConsoleKey.U => ProductBrand.Website,
                _ => null
            };
            if (link != null) Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
            return;
        }
        if (view == View.Notice) { view = View.Grid; return; }
        if (k.Key == ConsoleKey.L && (k.Modifiers & ConsoleModifiers.Control) != 0)
        {
            bulkSelectedOnly = false; search = ""; SetFilter("Overview"); return;
        }
        if (k.Key == ConsoleKey.A && k.Modifiers.HasFlag(ConsoleModifiers.Control)) { SelectVisible(); return; }
        if (k.Key == ConsoleKey.X && k.Modifiers.HasFlag(ConsoleModifiers.Control)) { ClearMarks(); return; }
        switch (k.Key)
        {
            case ConsoleKey.Escape:
                bulkSelectedOnly = false; search = ""; SetFilter("Overview"); return;
            case ConsoleKey.Q: running = false; return;
            case ConsoleKey.D1: SetTab(WorkspaceTab.Dashboard); return;
            case ConsoleKey.D2: SetTab(WorkspaceTab.Repositories); return;
            case ConsoleKey.LeftArrow: case ConsoleKey.RightArrow:
            case ConsoleKey.UpArrow: case ConsoleKey.DownArrow:
            case ConsoleKey.PageUp: case ConsoleKey.PageDown:
            case ConsoleKey.Home: case ConsoleKey.End:
                if (viewport != null)
                {
                    int next = viewport.Move(selected, k.Key);
                    if (k.Modifiers.HasFlag(ConsoleModifiers.Shift) && visible.Count > 0)
                        for (int row = Math.Min(selected, next); row <= Math.Max(selected, next); row++)
                            bulkMarked.Add(visible[row].Path);
                    selected = next;
                }
                return;
            case ConsoleKey.Spacebar: case ConsoleKey.X: SwitchMark(); return;
            case ConsoleKey.Y:
                bulkSelectedOnly = !bulkSelectedOnly;
                Reorder(visible.ElementAtOrDefault(selected)?.Path);
                return;
            case ConsoleKey.Enter: if (visible.Count > 0) { detailOffset = 0; view = View.Details; } return;
            case ConsoleKey.I: OpenRepositoryInfo(); return;
            case ConsoleKey.O: OpenBrowser(); return;
            case ConsoleKey.S: consentChoice = 0; view = View.Consent; return;
            case ConsoleKey.F5: if (all.Count > 0) view = View.Refresh; else { view = View.Consent; } return;
            case ConsoleKey.L: case ConsoleKey.D4:
                layoutRow = 0;
                savedLayout = options;
                options = options.Copy();
                view = View.Layout; return;
            case ConsoleKey.A: aboutReturn = view; view = View.About; return;
            case ConsoleKey.Tab:
                filter = Filters[(Array.IndexOf(Filters, filter) + ((k.Modifiers & ConsoleModifiers.Shift) != 0 ? Filters.Length - 1 : 1)) % Filters.Length];
                selected = offset = 0; Reorder(); return;
            case ConsoleKey.V:
                ToggleFilter("Sync"); return;
            case ConsoleKey.W:
                presentation = (presentation + 1) % 3;
                tab = presentation == 1 ? WorkspaceTab.Repositories : WorkspaceTab.Dashboard;
                offset = 0; return;
            case ConsoleKey.G:
                ToggleFilter("Gone"); return;
            case ConsoleKey.D:
                ToggleFilter("Red"); return;
            case ConsoleKey.B:
                SetTab(WorkspaceTab.Branches); return;
            case ConsoleKey.M:
                SetTab(WorkspaceTab.Remotes); return;
            case ConsoleKey.P: if (visible.Count > 0) Toggle(options.Pinned, true); return;
            case ConsoleKey.N:
                for (int step = 1; step <= visible.Count; step++)
                {
                    int next = (selected + step) % visible.Count;
                    if (InFilter(visible[next], "Sync") || InFilter(visible[next], "Gone") || InFilter(visible[next], "Red"))
                    {
                        selected = next;
                        status = WorkspaceTileViewModel.Describe(visible[next]) + "  ·  " + visible[next].Path;
                        return;
                    }
                }
                status = "No visible repositories need sync, upstream review or a local file review.";
                return;
            case ConsoleKey.H: if (visible.Count > 0) Toggle(options.Hidden, false); return;
            case ConsoleKey.F: input = search; view = View.Search; return;
            case ConsoleKey.D3: OpenBulkOperations(); return;
            case ConsoleKey.T:
                terminal.Leave();
                try { await legacy(root, all); } finally { terminal.Enter(); }
                status = "Workspace tools closed. S rescans local changes."; return;
        }
        if (k.KeyChar == '/') { input = search; view = View.Search; }
    }

    private void SetFilter(string next)
    {
        filter = next; selected = offset = 0; Reorder();
    }

    private void ToggleFilter(string next) => SetFilter(filter == next ? "Overview" : next);

    private void SetTab(WorkspaceTab next)
    {
        tab = next;
        presentation = next == WorkspaceTab.Dashboard ? 0 : 1;
        offset = 0;
    }

    private void Toggle(List<string> list, bool pin)
    {
        var repo = visible[selected];
        var existing = list.FindIndex(p => paths.Equals(p, repo.Path));
        if (existing >= 0) list.RemoveAt(existing); else list.Add(repo.Path);
        status = (pin ? (existing >= 0 ? "Unpinned " : "Pinned ") : (existing >= 0 ? "Restored " : "Hidden ")) + repo.Name;
        Save(); Reorder(repo.Path);
    }

    private void Type(ConsoleKeyInfo k)
    {
        if (k.Key == ConsoleKey.Backspace) input = TerminalText.RemoveLast(input);
        else if (!char.IsControl(k.KeyChar) && input.Length < 2048) input += k.KeyChar;
    }

    private void StartOperation(bool fetch, bool verifyDuringScan)
    {
        if (operation != null) return;
        operationTitle = fetch ? "Checking origin" : "Scanning workspace";
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var session = scanSession = new WorkspaceScanSession(root, fetch);
        operation = fetch ? index.RefreshAsync(root, session, token) : index.ScanAsync(root, verifyDuringScan, session, token);
        view = View.Busy;
    }

    private void Cancel()
    {
        scanSession?.RequestCancel();
        cancellation?.Cancel(); status = "Cancelling…";
    }

    private async Task FinishOperation()
    {
        try
        {
            var keep = visible.ElementAtOrDefault(selected)?.Path;
            all = await operation!;
            options.Root = root;
            Save(); Reorder(keep);
            var duration = scanSession?.Elapsed ?? TimeSpan.Zero;
            var final = scanSession?.Capture().Activity;
            status = $"{operationTitle} finished in {duration.TotalSeconds:0.0}s · {all.Count} repositories"
                + (final is { Directories: > 0 } ? $" · {final.Directories} folders visited" : "");
            view = View.Grid;
        }
        catch (OperationCanceledException) { status = "Cancelled. Showing the last saved scan."; view = View.Grid; }
        catch (Exception ex) { notice = ex.Message; status = "Scan needs attention"; view = View.Notice; }
        finally { operation = null; scanSession = null; cancellation?.Dispose(); cancellation = null; dirtyFrame = true; }
    }
}
