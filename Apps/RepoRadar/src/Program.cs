using Spectre.Console;
using Spectre.Console.Rendering;
using WorkspaceRepo;
using System.Text;
using System.Text.RegularExpressions;

return await Entry.Run(args);

static class Entry
{
    // The PowerShell launcher owns both startup and menu-initiated preflight.
    public const int PreflightRequested = 78;
    public static string Version => typeof(Entry).Assembly.GetName().Version!.ToString(3);

    public static async Task<int> Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool dashboardOwnsCancellation = false;
        Console.CancelKeyPress += (_, e) =>
        {
            if (dashboardOwnsCancellation) return;
            e.Cancel = true;
            Git.Stop.Cancel();
        };
        try
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                Console.WriteLine($"""
                    {ProductBrand.Name} {Version} {ProductBrand.AuthorCredit}
                    {ProductBrand.Description}

                    {ProductBrand.Command} [--catalog FILE] [--settings FILE] [--root FOLDER] [--no-emoji]
                    {ProductBrand.Command} --dry-run --root FOLDER     Inspect local state only
                    {ProductBrand.Command} --tools                     Open clone, update and branch tools
                    {ProductBrand.Command} --demo SCREEN               Preview home, select, location, settings, progress
                    {ProductBrand.Command} --self-test                 Run offline Git integration tests in artifacts
                    {ProductBrand.Command} --version

                    Launcher commands: --preflight (Windows x64 prerequisite installation), --preflight-check (report).
                    Interactive updates use fetch and fast-forward. Branch deletion is a separate action.
                    Installed builds use per-user state. Portable builds store state beside the launcher.
                    Set REPO_RADAR_DATA to use a different absolute state directory.
                    Configure Git authentication beforehand, for example with Git Credential Manager or an SSH agent.
                    """);
                return 0;
            }
            if (args.Contains("--version")) { Console.WriteLine($"{ProductBrand.Name} {Version}"); return 0; }
            string? Option(string key)
            {
                var i = Array.IndexOf(args, key);
                if (i < 0) return null;
                if (i == args.Length - 1 || args[i + 1].StartsWith("--")) throw new ArgumentException("Missing value for " + key);
                return args[i + 1];
            }
            var valid = new HashSet<string> { "--catalog", "--settings", "--root", "--demo", "--self-test", "--dry-run", "--no-emoji", "--tools" };
            for (int i = 0; i < args.Length; i++)
            {
                if (!valid.Contains(args[i])) throw new ArgumentException("Unknown argument: " + args[i]);
                if (args[i] is "--catalog" or "--settings" or "--root" or "--demo") { _ = Option(args[i]); i++; }
            }
            Ui.UseEmoji = !args.Contains("--no-emoji");
            var demo = Option("--demo");
            if (demo != null) { Ui.Demo(demo); return 0; }
            bool selfTest = args.Contains("--self-test");
            bool dryRun = args.Contains("--dry-run");
            if (!selfTest && !dryRun && (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive))
                throw new InvalidOperationException("Use a real terminal for the interactive menu, or --dry-run / --help for automation.");
            var home = Safety.Full(ProductPaths.HomeOverride ?? AppContext.BaseDirectory);
            var dataHome = ProductPaths.DataHome(home, import: !selfTest && !dryRun);
            if (selfTest)
            {
                var fixtures = Path.Combine(dataHome, "artifacts", "self-tests");
                int result = await SelfTests.Run(fixtures);
                if (result == 0)
                {
                    await WorkspaceIndexTests.Run(fixtures);
                    TerminalTextTests.Run();
                    Console.WriteLine($"Terminal viewport self-test passed ({TerminalViewportTests.Run()} checks).");
                    WorkspaceTileViewModelTests.Run();
                    Console.WriteLine($"Repository Info self-test passed ({RepositoryInfoTests.Run()} checks).");
                    Console.WriteLine($"Bulk operations self-test passed ({await BulkOperationsTests.Run(fixtures)} checks).");
                }
                return result;
            }
            var catalogOverride = Option("--catalog");
            var catalogPath = Path.GetFullPath(catalogOverride ?? Path.Combine(dataHome, "repos.json"));
            var bundledCatalog = catalogOverride is null ? Path.Combine(home, "repos.json") : catalogPath;
            var settingsPath = Path.GetFullPath(Option("--settings") ?? Path.Combine(dataHome, "settings.json"));
            using var appLock = dryRun ? null : OpenSettingsLock(settingsPath);
            var app = new App(catalogPath, settingsPath, bundledCatalog);
            if (Option("--root") is { } root) app.Prefs.Root = Safety.Full(root);
            if (dryRun) { await app.Status(false); return 0; }
            if (args.Contains("--tools")) return await app.Menu();
            int exitCode = 0;
            dashboardOwnsCancellation = true;
            await Dashboard.RunAsync(app.Prefs.Root, dataHome, async (scanRoot, discovered) =>
            {
                app.UseDiscoveredWorkspace(scanRoot, discovered);
                dashboardOwnsCancellation = false;
                try { exitCode = await app.Menu(); }
                finally { dashboardOwnsCancellation = true; }
                if (exitCode == PreflightRequested) throw new PreflightRequestException();
            }, Option("--root"));
            return exitCode;
        }
        catch (PreflightRequestException) { return PreflightRequested; }
        catch (OperationCanceledException e) { Ui.Warn(e.Message + " Existing files and recovery stashes were preserved."); return 130; }
        catch (Exception e) { Ui.Error(e.Message); return 1; }
    }

    static FileStream OpenSettingsLock(string settingsPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        return new FileStream(settingsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}

sealed class PreflightRequestException : Exception { }

sealed record MenuItem(string Label, string Description, string? Target = null, string? Icon = null, Color? Accent = null);

static class Ui
{
    public static bool UseEmoji = true;
    public static readonly Color Gold = new(232, 184, 106);
    public static readonly Color Teal = new(112, 219, 204);
    public static readonly Color Muted = new(167, 185, 205);
    static readonly Color Ink = new(15, 23, 42);
    public static readonly Color SaveGreen = new(137, 229, 162);
    public static readonly Color ExitRed = new(245, 143, 143);
    static bool ShowIcons => UseEmoji && AnsiConsole.Profile.Capabilities.Unicode;
    public static string E(string value) => Markup.Escape(Safety.Redact(UseEmoji ? value : Regex.Replace(value, @"[\p{Cs}\p{So}\uFE0F]", "").Trim()));

    static IRenderable BrandStamp()
    {
        string mark = ShowIcons ? "◉  " : "";
        var identity = $"[bold #3CF8FF]{mark}{ProductBrand.Name}[/]  [#A7B9CD]{ProductBrand.AuthorCredit}[/]";
        var clock = $"{(ShowIcons ? "🕒  " : "")}{DateTime.Now:yyyy-MM-dd HH:mm}";
        if (AnsiConsole.Profile.Width < 51)
            return new Rows(new Markup(identity), new Markup(clock, new Style(Muted)));
        return new Grid { Expand = true }
            .AddColumn(new GridColumn { Padding = new Padding(0, 0, 0, 0) })
            .AddColumn(new GridColumn { Alignment = Justify.Right, Padding = new Padding(1, 0, 0, 0) })
            .AddRow(new Markup(identity), new Markup(clock, new Style(Muted)));
    }

    public static IRenderable ScreenHeading(string title, string? description = null, string? context = null)
    {
        var rows = new List<IRenderable>
        {
            BrandStamp(), new Rule().RuleStyle(new Style(Teal)), new Markup($"[bold #E8B86A]{E(title)}[/]")
        };
        if (context is not null) rows.Add(new Markup(E(context), new Style(Teal)) { Overflow = Overflow.Ellipsis });
        if (description is not null)
        {
            rows.Add(new Text(" "));
            rows.Add(new Markup(E(description), new Style(Muted)));
        }
        rows.Add(new Text(" "));
        return new Rows(rows);
    }

    public static void Header(string crumb, bool clear = true, string? description = null)
    {
        if (clear && AnsiConsole.Profile.Capabilities.Interactive) AnsiConsole.Clear();
        AnsiConsole.Write(ScreenHeading(crumb, description));
    }
    public static IRenderable ScreenFooter(string? content = null)
    {
        var rows = new List<IRenderable> { new Text(" "), new Rule().RuleStyle(new Style(Teal)) };
        if (content is not null) rows.Add(new Markup(content));
        return new Rows(rows);
    }
    public static void Footer() => AnsiConsole.Write(ScreenFooter());
    public static void Warn(string text) => AnsiConsole.MarkupLine("[yellow]" + E(text) + "[/]");
    public static void Error(string text) => AnsiConsole.MarkupLine("[red]" + E(text) + "[/]");
    public static int PageSize => Math.Clamp(AnsiConsole.Profile.Height - 18, 3, 15);
    public static string Short(string text)
    {
        var limit = Math.Max(12, AnsiConsole.Profile.Width - 10);
        return text.Length <= limit ? text : text[..(limit - 3)] + "...";
    }
    public static List<MenuItem> WorkspaceActions(bool preflight)
    {
        var actions = new List<MenuItem>
        {
            new("Clone / update repositories", "Clone missing repositories and fast-forward existing ones. Review destinations and changes before running.", Icon: "📦"),
            new("Workspace status", "Inspect local folders, branches and pending changes. This does not contact remotes or modify files.", Icon: "📊"),
            new("Branch cleanup", "Prune stale tracking references or review merged branches for deletion locally or on origin.", Icon: "🌿"),
            new("Manage catalog", "View, edit, import or export the list of repositories and their destinations.", Icon: "📚"),
            new("Locations and preferences", "Choose where to keep repositories on this PC and whether to include submodules in new clones.", Icon: "📁")
        };
        if (preflight) actions.Add(new("Preflight", "Check Git and .NET 10. On Windows x64, install missing prerequisites through WinGet.", Icon: "🔧"));
        actions.Add(new("Exit", "Close Repo and return to the terminal.", Icon: "❌", Accent: ExitRed));
        return actions;
    }
    const string LocationsDescription = "Choose the parent folder where Repo keeps the catalog's repositories.";
    static List<MenuItem> LocationChoices(string root, IEnumerable<string> savedRoots)
    {
        var choices = savedRoots.Prepend(root).Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new MenuItem(path, "Use this saved folder as the workspace root.", path, "📍")).ToList();
        choices.Add(new("Browse folders", "Navigate available drives and folders from the terminal.", Icon: "📂"));
        choices.Add(new("Enter or paste a path", "Use an absolute path, including a folder that will be created on the first clone.", Icon: "📝"));
        choices.Add(new("Back", "Return without changing the workspace root.", Icon: "🔙"));
        return choices;
    }
    public static Task<MenuItem> ChooseLocation(Preferences prefs) =>
        ChooseMenu("Locations and preferences", LocationChoices(prefs.Root, prefs.SavedRoots),
            LocationsDescription, prefs.Root.Length > 0 ? "Current root: " + prefs.Root : null);

    static bool SideBySide => AnsiConsole.Profile.Width >= 94 && AnsiConsole.Profile.Height >= 21;
    static int VisibleRows(int count)
    {
        var reserved = SideBySide ? 12 : AnsiConsole.Profile.Width < 51 ? 17 : 15;
        return Math.Clamp(AnsiConsole.Profile.Height - reserved, 1, count);
    }

    static IRenderable MenuFrame(string title, string? description, string? context, IReadOnlyList<MenuItem> options, int selected)
    {
        var rows = new List<IRenderable> { ScreenHeading(title, description, context) };

        var sideBySide = SideBySide;
        var icons = ShowIcons && options.Any(option => option.Icon is not null);
        var visible = VisibleRows(options.Count);
        var first = Math.Clamp(selected - visible / 2, 0, options.Count - visible);
        var entries = new List<IRenderable>();
        for (var index = first; index < first + visible; index++)
        {
            var option = options[index];
            var active = index == selected;
            if (index > first && option.Label is "Back" or "Exit" or "Skip this repository")
                entries.Add(new Text(" "));
            var icon = icons ? (option.Icon ?? "  ") + "  " : "";
            var label = (active ? " > " : "   ") + icon + E(option.Label);
            if (sideBySide) label += new string(' ', Math.Max(1, 35 - option.Label.Length - (icons ? 7 : 3)));
            var style = active ? new Style(Ink, option.Accent ?? Gold, Decoration.Bold) : new Style(option.Accent);
            entries.Add(new Markup(label, style) { Overflow = Overflow.Ellipsis });
        }

        var selectedOption = options[selected];
        var detail = new Markup(E(selectedOption.Description), new Style(Muted));
        if (sideBySide)
        {
            var selectedTitle = selectedOption.Target ?? selectedOption.Label;
            var panel = new Panel(new Rows(new Markup(E(selectedTitle), new Style(selectedOption.Accent ?? Gold, decoration: Decoration.Bold)),
                new Text(" "), detail))
                .Header(selectedOption.Target is null ? "[#70DBCC]Selected action[/]" : "[#70DBCC]Selected path[/]")
                .Padding(2, 1, 2, 1)
                .RoundedBorder()
                .BorderColor(Teal)
                .Expand();
            var grid = new Grid().Width(Math.Min(104, AnsiConsole.Profile.Width))
                .AddColumn(new GridColumn { Width = 39, Padding = new Padding(0, 0, 3, 0) })
                .AddColumn(new GridColumn { Padding = new Padding(0, 0, 0, 0) });
            grid.AddRow(new Rows(entries), panel);
            rows.Add(grid);
        }
        else
        {
            rows.Add(new Rows(entries));
            rows.Add(new Text(" "));
            rows.Add(new Markup("[#70DBCC]About this choice[/]"));
            rows.Add(new Text(" "));
            rows.Add(detail);
        }
        var scroll = visible < options.Count ? $"  [{selected + 1}/{options.Count}]" : "";
        var backLabel = options[^1].Label switch { "Exit" => "Exit", "Skip this repository" => "Skip", _ => "Back" };
        rows.Add(ScreenFooter("[#70DBCC]Up/Down[/] Move  [#70DBCC]Enter[/] Select  [#70DBCC]Esc[/] " + E(backLabel + scroll)));
        return new Rows(rows);
    }

    public static Task<MenuItem> ChooseMenu(string title, IReadOnlyList<MenuItem> options, string? description = null,
        string? context = null, int selected = 0)
    {
        AnsiConsole.Clear();
        IRenderable Render() => MenuFrame(title, description, context, options, selected);
        return AnsiConsole.Live(Render()).AutoClear(true).StartAsync(async display =>
        {
            display.Refresh();
            while (true)
            {
                var read = AnsiConsole.Console.Input.ReadKeyAsync(true, Git.Stop.Token);
                while (!read.IsCompleted)
                {
                    var untilNextMinute = TimeSpan.FromTicks(TimeSpan.TicksPerMinute - DateTime.Now.Ticks % TimeSpan.TicksPerMinute);
                    try { await read.WaitAsync(untilNextMinute, Git.Stop.Token); }
                    catch (TimeoutException) { display.UpdateTarget(Render()); }
                }
                var key = await read;
                Git.Stop.Token.ThrowIfCancellationRequested();
                if (key is null || key.Value.Key == ConsoleKey.Escape) return options[^1];
                if (key.Value.Key == ConsoleKey.Enter) return options[selected];
                var page = VisibleRows(options.Count);
                var next = key.Value.Key switch
                {
                    ConsoleKey.UpArrow => selected - 1,
                    ConsoleKey.DownArrow => selected + 1,
                    ConsoleKey.PageUp => selected - page,
                    ConsoleKey.PageDown => selected + page,
                    ConsoleKey.Home => 0,
                    ConsoleKey.End => options.Count - 1,
                    _ => selected
                };
                selected = Math.Clamp(next, 0, options.Count - 1);
                display.UpdateTarget(Render());
            }
        });
    }

    static IReadOnlyList<MenuItem> WorkspacePreferencesChoices(bool includeSubmodules) =>
        [
            new("Save settings", "Save this workspace root and the submodule preference on this PC.", Icon: "✅", Accent: SaveGreen),
            new($"Include submodules: {(includeSubmodules ? "On" : "Off")}", "Toggle whether new clones include Git submodules.", Icon: "🔧"),
            new("Back", "Return to locations without saving these changes.", Icon: "🔙")
        ];
    public static Task<MenuItem> ChooseWorkspacePreferences(string root, bool includeSubmodules, int selected) =>
        ChooseMenu("Workspace preferences", WorkspacePreferencesChoices(includeSubmodules),
            "Confirm the destination and cloning preference.", root, selected);

    internal sealed class PromptBackException : Exception { }

    // Escape cancels the whole prompt, so its callers cannot mistake it for approval or empty input.
    private sealed class EscapeAwareConsole(IAnsiConsole console) : IAnsiConsole
    {
        public Profile Profile => console.Profile;
        public IAnsiConsoleCursor Cursor => console.Cursor;
        public IAnsiConsoleInput Input { get; } = new EscapeAwareInput(console.Input);
        public IExclusivityMode ExclusivityMode => console.ExclusivityMode;
        public RenderPipeline Pipeline => console.Pipeline;
        public void Clear(bool home) => console.Clear(home);
        public void Write(IRenderable renderable) => console.Write(renderable);
        public void WriteAnsi(Action<AnsiWriter> action) => console.WriteAnsi(action);
    }

    private sealed class EscapeAwareInput(IAnsiConsoleInput input) : IAnsiConsoleInput
    {
        public bool IsKeyAvailable() => input.IsKeyAvailable();
        public ConsoleKeyInfo? ReadKey(bool intercept) => Check(input.ReadKey(intercept));
        public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Check(await input.ReadKeyAsync(intercept, cancellationToken));

        private static ConsoleKeyInfo? Check(ConsoleKeyInfo? key) =>
            key?.Key == ConsoleKey.Escape ? throw new PromptBackException() : key;
    }

    public static T Prompt<T>(IPrompt<T> prompt)
    {
        AnsiConsole.MarkupLine("[#70DBCC]Esc[/] Back");
        return new EscapeAwareConsole(AnsiConsole.Console).Prompt(prompt);
    }

    public static bool Confirm(string question) => Prompt(new ConfirmationPrompt(question) { DefaultValue = false });

    public static List<T> Many<T>(string title, IEnumerable<T> options, Func<T, string> label, IEnumerable<T>? selected = null) where T : notnull
    {
        var prompt = new MultiSelectionPrompt<T>().Title("[bold]" + E(title) + "[/]").NotRequired().PageSize(PageSize)
            .HighlightStyle(new Style(Gold)).InstructionsText("[#70DBCC]Up/Down[/] Move  [#70DBCC]Space[/] Toggle  [#70DBCC]Enter[/] Continue")
            .AddChoices(options).UseConverter(x => E(Short(label(x))));
        foreach (var item in selected ?? []) prompt.Select(item);
        return Prompt(prompt);
    }
    public static Table Table(params string[] columns)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Teal);
        foreach (var c in columns) table.AddColumn("[bold #E8B86A]" + E(c) + "[/]");
        return table;
    }
    public static void WaitToReturn()
    {
        AnsiConsole.MarkupLine("[#70DBCC]Enter / Esc[/] Back");
        var input = AnsiConsole.Console.Input;
        while (true)
        {
            var key = input.ReadKeyAsync(true, Git.Stop.Token).GetAwaiter().GetResult();
            Git.Stop.Token.ThrowIfCancellationRequested();
            if (key is null || key.Value.Key is ConsoleKey.Enter or ConsoleKey.Escape) return;
        }
    }
    public static void Pause() { Footer(); WaitToReturn(); }
    public static async Task<T> Busy<T>(string title, Func<Action<string>, Task<T>> work)
    {
        return await AnsiConsole.Progress().AutoClear(false).HideCompleted(false)
            .Columns(new SpinnerColumn(), new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
            .StartAsync(async context =>
            {
                var task = context.AddTask(E(title)); task.IsIndeterminate = true;
                var gate = new object();
                var result = await work(line =>
                {
                    lock (gate)
                    {
                        var match = Regex.Match(line, @"(Receiving objects|Resolving deltas|Counting objects|Compressing objects|Updating files):\s+(\d+)%");
                        if (match.Success) { task.Description = E(title + " · " + match.Groups[1].Value); task.IsIndeterminate = false; task.Value = int.Parse(match.Groups[2].Value); }
                    }
                });
                    task.StopTask();
                return result;
            });
    }
    public static void Demo(string screen)
    {
        if (screen == "home")
        {
            AnsiConsole.Write(MenuFrame("Workspace manager", "58 repositories in catalog", "Root: Choose a location",
                WorkspaceActions(true), 0));
            return;
        }
        if (screen == "location")
        {
            var choices = LocationChoices(@"D:\Dev", [@"C:\Projects"]);
            AnsiConsole.Write(MenuFrame("Locations and preferences", LocationsDescription, @"Current root: D:\Dev",
                choices, 2));
            return;
        }
        if (screen == "settings")
        {
            AnsiConsole.Write(MenuFrame("Preview / Workspace preferences", "Confirm the destination and cloning preference.",
                @"Root: D:\Dev", WorkspacePreferencesChoices(true), 0));
            return;
        }
        if (screen == "progress")
        {
            AnsiConsole.Write(SyncRun.Preview());
            return;
        }
        Header("Preview / " + screen, false);
        switch (screen)
        {
            case "select":
                var t = Table("Selected", "Repository", "Relative folder");
                t.AddRow("[gold1]" + Markup.Escape("[✓]") + "[/]", "Cody", "[cyan]Cody[/]");
                t.AddRow("[gold1]" + Markup.Escape("[✓]") + "[/]", "HelpMe", "[cyan]MeUp-Workspace/HelpMe[/]");
                t.AddRow("[grey]" + Markup.Escape("[ ]") + "[/]", "AgentInbox", "[cyan]MeUp-Workspace/AgentInbox[/]");
                AnsiConsole.Write(t); AnsiConsole.MarkupLine("[grey]↑↓ Move · Space Toggle · Enter Continue[/]"); break;
            default: throw new ArgumentException("Demo screen: home, select, location, settings or progress.");
        }
        Footer();
    }
}

sealed class App
{
    readonly string catalogPath, settingsPath;
    readonly Git git = new();
    readonly RepositoryService service;
    Catalog catalog;
    public Preferences Prefs;
    public App(string catalogPath, string settingsPath, string bundledCatalog)
    {
        this.catalogPath = catalogPath; this.settingsPath = settingsPath;
        catalog = Storage.Read<Catalog>(File.Exists(catalogPath) ? catalogPath : bundledCatalog);
        Prefs = Storage.Read<Preferences>(settingsPath);
        service = new RepositoryService(git);
    }
    void Save() => Storage.Write(settingsPath, Prefs);
    public void UseDiscoveredWorkspace(string root, IReadOnlyList<IndexedRepo> discovered)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var known = catalog.Repositories.Select(r => r.RelativePath.Replace('\\', '/')).ToHashSet(comparer);
        var additions = new List<Repository>();
        foreach (var row in discovered)
        {
            string relative = Path.GetRelativePath(root, row.Path).Replace('\\', '/');
            if (relative == "." || known.Contains(relative) || string.IsNullOrWhiteSpace(row.RemoteUrl)) continue;
            var entry = new Repository(row.Name, row.RemoteUrl, relative);
            try { Safety.ValidateCatalog(new Catalog { Repositories = [entry] }, root); }
            catch (InvalidDataException) { continue; }
            additions.Add(entry);
            known.Add(relative);
        }
        if (additions.Count > 0)
        {
            var next = new Catalog { Repositories = [.. catalog.Repositories, .. additions] };
            Storage.Write(catalogPath, next);
            catalog = next;
        }
        if (!comparer.Equals(Prefs.Root, root)) { Prefs.Root = root; Save(); }
    }
    void Validate() => Safety.ValidateCatalog(catalog, string.IsNullOrEmpty(Prefs.Root) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Repos") : Prefs.Root);
    public async Task<int> Menu()
    {
        Validate();
        var choices = Ui.WorkspaceActions(ProductPaths.HomeOverride != null);
        var selected = 0;
        while (!Git.Stop.IsCancellationRequested)
        {
            var action = await Ui.ChooseMenu("Workspace manager", choices, $"{catalog.Repositories.Count} repositories in catalog",
                "Root: " + (Prefs.Root.Length > 0 ? Prefs.Root : "Choose a location"), selected);
            selected = choices.IndexOf(action);
            try
            {
                switch (action.Label)
                {
                    case "Exit": return 0;
                    case "Preflight": return Entry.PreflightRequested;
                    case "Clone / update repositories": await Sync(); break;
                    case "Workspace status":
                        await EnsureRoot();
                        if (Prefs.Root.Length > 0) { await Status(true); Ui.Pause(); }
                        break;
                    case "Branch cleanup": await Branches(); break;
                    case "Manage catalog": await ManageCatalog(); break;
                    default: await Locations(); break;
                }
            }
            catch (Ui.PromptBackException) { }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Ui.Error(e.Message); Ui.Pause(); }
        }
        return 0;
    }
    Task EnsureRoot() => Prefs.Root.Length == 0 ? Locations() : Task.CompletedTask;
    public async Task Status(bool interactive)
    {
        if (Prefs.Root.Length == 0) throw new InvalidOperationException("Specify --root for inspection.");
        Validate();
        if (interactive) AnsiConsole.Clear();
        AnsiConsole.Write(Ui.ScreenHeading("Workspace status", "Inspect local branches and changes.", "Workspace: " + Prefs.Root));
        var present = 0;
        var missing = 0;
        var changed = 0;
        var issues = 0;
        var table = Ui.Table("Repository", "Relative folder", "Branch", "State").Expand();
        foreach (var repo in catalog.Repositories)
        {
            try
            {
                var state = await service.Inspect(repo, Prefs.Root);
                if (state.Dirty) changed++;
                string status;
                if (state.Blocked.Length > 0)
                {
                    issues++;
                    status = "[#F58F8F]" + Ui.E(state.Blocked) + "[/]";
                }
                else if (state.Status.StartsWith("Missing", StringComparison.Ordinal))
                {
                    missing++;
                    status = "[#A7B9CD]Missing (not cloned)[/]";
                }
                else
                {
                    present++;
                    status = state.Dirty ? "[#E8B86A]Working changes[/]" : "[#89E5A2]Clean worktree[/]";
                }
                table.AddRow(Ui.E(repo.Name), Ui.E(repo.RelativePath), Ui.E(state.Branch), status);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                issues++;
                table.AddRow(Ui.E(repo.Name), Ui.E(repo.RelativePath), "", "[#F58F8F]" + Ui.E(e.Message) + "[/]");
            }
        }
        AnsiConsole.MarkupLine($"[#89E5A2]{present}[/] Present  [#A7B9CD]|[/]  {missing} Missing  [#A7B9CD]|[/]  [#E8B86A]{changed}[/] With changes  [#A7B9CD]|[/]  [#F58F8F]{issues}[/] Issues");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        if (!interactive) AnsiConsole.Write(Ui.ScreenFooter("[#A7B9CD]Local inspection[/]"));
    }
    async Task Locations()
    {
        while (true)
        {
            var choice = await Ui.ChooseLocation(Prefs);
            if (choice.Label == "Back" && choice.Target is null) return;
            var root = choice.Target ?? (choice.Label == "Browse folders" ? await BrowseFolders() : ReadWorkspacePath());
            if (root is null) continue;
            try
            {
                if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("An absolute path is required.");
                root = Safety.Full(root);
                Safety.NoLinks(root);
                if (File.Exists(root)) throw new InvalidDataException("The workspace root is a file.");
                Safety.ValidateCatalog(catalog, root);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                Ui.Error(e.Message);
                Ui.Pause();
                continue;
            }
            var includeSubmodules = Prefs.CloneSubmodules;
            var preferences = await Ui.ChooseWorkspacePreferences(root, includeSubmodules, 0);
            while (preferences.Label.StartsWith("Include submodules", StringComparison.Ordinal))
            {
                includeSubmodules = !includeSubmodules;
                preferences = await Ui.ChooseWorkspacePreferences(root, includeSubmodules, 1);
            }
            if (preferences.Label == "Back") continue;
            Prefs.CloneSubmodules = includeSubmodules;
            Prefs.Root = root;
            if (!Prefs.SavedRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) Prefs.SavedRoots.Add(root);
            Save();
            return;
        }
    }

    static string? ReadWorkspacePath()
    {
        Ui.Header("Enter a workspace path", description: "Paste or type an absolute path. Press Enter with an empty field to go back.");
        var answer = Ui.Prompt(new TextPrompt<string>("Path:").AllowEmpty()).Trim();
        if (answer.Length >= 2 && answer.StartsWith('"') && answer.EndsWith('"')) answer = answer[1..^1];
        return answer.Length == 0 ? null : Environment.ExpandEnvironmentVariables(answer);
    }

    async Task<string?> BrowseFolders()
    {
        string? current = Directory.Exists(Prefs.Root) ? Prefs.Root : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(current)) current = null;
        while (true)
        {
            var choices = new List<MenuItem>();
            string? error = null;
            if (current is null)
            {
                choices.AddRange(DriveInfo.GetDrives().Select(drive => new MenuItem(drive.Name, "Browse this drive.", drive.RootDirectory.FullName, "💽")));
            }
            else
            {
                choices.Add(new("Use this folder", "Use the current folder as the workspace root.", Icon: "✅"));
                try
                {
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                    };
                    choices.AddRange(Directory.EnumerateDirectories(current, "*", options)
                        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                        .Select(path => new MenuItem(Path.GetFileName(path), "Open this folder to browse or select it.", path, "📂")));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    error = e.Message;
                    choices.RemoveAt(0);
                }
                if (error is null) choices.Add(new("Name a new folder", "Name a subfolder to create on the first clone.", Icon: "📝"));
                if (Directory.GetParent(current) is { } parent)
                    choices.Add(new("..  Parent folder", "Go up one folder.", parent.FullName, "🔼"));
                choices.Add(new("Show drives", "Choose another drive.", Icon: "💽"));
            }
            choices.Add(new("Back", "Return to workspace locations without selecting a folder.", Icon: "🔙"));
            var choice = await Ui.ChooseMenu("Browse for a workspace folder", choices,
                error ?? (current is null ? "Choose a drive to browse its folders." : "Open a folder or use the current one."), current);
            if (choice.Target is not null) { current = choice.Target; continue; }
            switch (choice.Label)
            {
                case "Back": return null;
                case "Use this folder": return current;
                case "Show drives": current = null; break;
                case "Name a new folder":
                    Ui.Header("Name a new folder", description: "Enter a folder name. Repo creates it when you clone a repository.");
                    var name = Ui.Prompt(new TextPrompt<string>("Folder name:").AllowEmpty()).Trim();
                    if (name.Length == 0) break;
                    if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        Ui.Error("Enter one folder name without path separators.");
                        Ui.Pause();
                        break;
                    }
                    return Path.Combine(current!, name);
            }
        }
    }
    async Task Sync()
    {
        await EnsureRoot(); if (Prefs.Root.Length == 0) return; Validate();
        if (catalog.Repositories.Count == 0) { Ui.Warn("Add or import a repository catalog first."); Ui.Pause(); return; }
        var preset = (await Ui.ChooseMenu("Clone and update / Selection",
        [
            new("Active projects", "Start with projects outside archive, references and third_party folders.", Icon: "🚀"),
            new("All repositories", "Start with every repository in the catalog selected.", Icon: "📦"),
            new("Saved selection", "Reuse the repository selection saved on this PC.", Icon: "📌"),
            new("Choose individually", "Start with an empty selection and pick repositories one at a time.", Icon: "📝"),
            new("Back", "Return to the workspace menu.", Icon: "🔙")
        ], "Choose a starting set, then adjust it before reviewing the actions.", Prefs.Root)).Label;
        if (preset == "Back") return;
        var initial = preset.StartsWith("Active") ? catalog.Repositories.Where(r => !r.RelativePath.Replace('\\', '/').Split('/').Any(p => new[] { "archive", "archives", "references", "third_party" }.Contains(p, StringComparer.OrdinalIgnoreCase)))
            : preset.StartsWith("All") ? catalog.Repositories
            : preset.StartsWith("Saved") ? catalog.Repositories.Where(r => Prefs.SelectedPaths.Contains(r.RelativePath, StringComparer.OrdinalIgnoreCase))
            : Enumerable.Empty<Repository>();
        Ui.Header("Clone and update / Repositories", description: "Toggle the repositories you want to clone or update on this PC.");
        var selected = Ui.Many("Repositories", catalog.Repositories, r => r.Name + "  ·  " + r.RelativePath,
            initial);
        if (selected.Count == 0) return;
        Prefs.SelectedPaths = selected.Select(r => r.RelativePath).ToList(); Save();
        Ui.Header("Clone and update / Review", description: "Check each destination and the action Repo will take.");
        var plans = new List<SyncPlan>();
        var table = Ui.Table("Repository", "Destination", "Action");
        foreach (var repo in selected.OrderBy(r => r.RelativePath.Count(c => c is '/' or '\\')))
        {
            var s = await service.Inspect(repo, Prefs.Root);
            var action = s.Blocked.Length > 0 ? "blocked" : s.Status.StartsWith("Missing") ? "clone" : "pull";
            if (s.Dirty && action != "blocked")
            {
                var choice = (await Ui.ChooseMenu("Clone and update / Local changes",
                [
                    new("Stash, pull, restore", "Temporarily stash your changes, fast-forward, then restore them. The recovery stash is retained.", Icon: "📦"),
                    new("Pull without stashing", "Try to fast-forward with the edits in place. Git may refuse if they overlap.", Icon: "📥"),
                    new("Skip this repository", "Leave this repository alone and continue reviewing the others.", Icon: "⏭️")
                ], "Choose how to handle uncommitted edits before syncing.", repo.Name)).Label;
                Ui.Header("Clone and update / Review", description: "Check each destination and the action Repo will take.");
                action = choice.StartsWith("Stash") ? "stash" : choice.StartsWith("Pull") ? "pull" : "skip";
            }
            plans.Add(new(repo, action, s.Blocked));
            table.AddRow(Ui.E(repo.Name), Ui.E(s.Path), Ui.E(action == "blocked" ? s.Blocked : action));
        }
        AnsiConsole.Write(table);
        Ui.Warn("Existing repositories: fetch/prune remote-tracking refs, then fast-forward only. No branch deletion or push.");
        if (!Ui.Confirm("Run these actions?")) return;
        var run = new SyncRun(Prefs.Root, plans);
        await run.Execute((plan, progress, stage) =>
            service.Sync(plan.Repository, Prefs.Root, plan.Action, Prefs.CloneSubmodules, progress, stage));
        Ui.WaitToReturn();
    }
    async Task ManageCatalog()
    {
        while (true)
        {
            var action = (await Ui.ChooseMenu("Repository catalog",
            [
                new("View catalog", "Read the repository names, Git URLs and relative destination folders.", Icon: "📖"),
                new("Add repository", "Add a Git URL and its destination to the catalog.", Icon: "➕"),
                new("Edit repository", "Change an entry's name, URL or destination.", Icon: "📝"),
                new("Import catalog", "Load and validate a JSON catalog, then review whether to replace the current list.", Icon: "📥"),
                new("Export catalog", "Save a copy of the current catalog as a JSON file.", Icon: "📤"),
                new("Remove from catalog", "Remove an entry from the list. Its local files stay in place.", Icon: "🗑️"),
                new("Back", "Return to the workspace menu.", Icon: "🔙")
            ], $"{catalog.Repositories.Count} repositories. Changes affect the catalog on this PC.", catalogPath)).Label;
            if (action == "Back") return;
            if (action == "View catalog")
            {
                Ui.Header("Repository catalog / View", description: "Inspect the Git URL and destination saved for each repository.");
                var table = Ui.Table("Repository", "Git URL", "Relative folder").Expand();
                foreach (var repo in catalog.Repositories) table.AddRow(Ui.E(repo.Name), Ui.E(repo.Url), Ui.E(repo.RelativePath));
                AnsiConsole.Write(table);
                Ui.Pause();
                continue;
            }
            Ui.Header("Repository catalog / " + action, description: action switch
            {
                "Import catalog" => "Choose the JSON catalog you want to review and import.",
                "Export catalog" => "Enter the destination path for a portable JSON copy of this catalog.",
                _ => "Edit catalog entries and their relative destinations."
            });
            if (action == "Export catalog")
            {
                var path = Path.GetFullPath(Ui.Prompt(new TextPrompt<string>("Export file path:")));
                if (File.Exists(path) && !Ui.Confirm("Replace existing file (a backup will be kept)?")) continue;
                Storage.Write(path, catalog); continue;
            }
            var updated = new Catalog { Repositories = [.. catalog.Repositories] };
            if (action == "Import catalog")
            {
                var start = Directory.Exists(Prefs.Root) ? Prefs.Root : Path.GetDirectoryName(catalogPath)!;
                var path = TerminalPathPicker.PickFile(start);
                if (path == null) continue;
                updated = Storage.Read<Catalog>(path);
                Safety.ValidateCatalog(updated, Prefs.Root.Length > 0 ? Prefs.Root : Path.Combine(Path.GetTempPath(), "RepoRadarPreview"));
                if (!Ui.Confirm($"Replace catalog with {updated.Repositories.Count} entries? Existing catalog gets a backup.")) continue;
            }
            else
            {
                Repository? old = null;
                if (action != "Add repository")
                {
                    if (catalog.Repositories.Count == 0) continue;
                    old = Ui.Prompt(new SelectionPrompt<Repository>().Title("Repository").PageSize(Ui.PageSize).AddChoices(catalog.Repositories).UseConverter(r => Ui.E(Ui.Short(r.Name + " · " + r.RelativePath))));
                }
                if (action == "Remove from catalog")
                {
                    if (!Ui.Confirm("Remove catalog entry only? Local files stay in place.")) continue;
                    updated.Repositories.Remove(old!);
                }
                else
                {
                    string Ask(string label, string? value) => Ui.Prompt(new TextPrompt<string>(label).DefaultValue(value ?? ""));
                    var repo = new Repository(Ask("Name:", old?.Name), Ask("Git URL (no credentials):", old?.Url), Ask("Relative folder:", old?.RelativePath));
                    if (old != null) updated.Repositories.Remove(old);
                    updated.Repositories.Add(repo);
                }
            }
            Safety.ValidateCatalog(updated, Prefs.Root.Length > 0 ? Prefs.Root : Path.Combine(Path.GetTempPath(), "RepoRadarPreview"));
            Storage.Write(catalogPath, updated); catalog = updated;
        }
    }
    async Task Branches()
    {
        await EnsureRoot(); if (Prefs.Root.Length == 0 || catalog.Repositories.Count == 0) return; Validate();
        Ui.Header("Branch cleanup / Repository", description: "Choose a local repository to review its tracking references or merged branches.");
        var repo = Ui.Prompt(new SelectionPrompt<Repository>().Title("Choose a local repository").PageSize(Ui.PageSize).AddChoices(catalog.Repositories).UseConverter(r => Ui.E(Ui.Short(r.Name + " · " + r.RelativePath))));
        var state = await service.Inspect(repo, Prefs.Root);
        if (state.Blocked.Length > 0 || state.Status.StartsWith("Missing")) { Ui.Warn(state.Blocked.Length > 0 ? state.Blocked : "Clone this repository first."); Ui.Pause(); return; }
        var action = (await Ui.ChooseMenu("Branch cleanup / Operation",
        [
            new("Prune stale tracking references", "Fetch origin and remove local references to branches already gone from the server.", Icon: "🧹"),
            new("Delete merged local branches", "Review eligible merged branches and confirm which ones to delete on this PC.", Icon: "💻"),
            new("Delete merged branches on origin", "Review eligible merged branches and explicitly confirm deletion on the server.", Icon: "🌐"),
            new("Back", "Return without fetching or changing branches.", Icon: "🔙")
        ], "Choose whether to clean up tracking references or review merged branches.", repo.Name)).Label;
        if (action == "Back") return;
        Ui.Header("Branch cleanup / Review", description: "Fetch origin before reviewing eligible branches or removing stale tracking references.");
        Ui.Warn("Fetch/prune removes local origin/* references to branches already deleted on the server. It does not delete local branches or server branches.");
        if (!Ui.Confirm("Fetch origin and prune stale tracking references?")) return;
        (await Ui.Busy("Fetch / prune", p => service.Fetch(state.Path, p))).Require();
        if (action.StartsWith("Prune")) { Ui.Pause(); return; }
        var remote = action.Contains("on origin");
        if (remote)
        {
            var urls = (await git.Value(state.Path, "remote", "get-url", "--push", "--all", "origin")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (urls.Length != 1 || !Safety.SameRemote(urls[0].Trim(), repo.Url)) throw new InvalidOperationException("Origin push URL differs from the catalog, or has multiple push URLs. Remote deletion disabled.");
        }
        var defaultBranch = await service.DefaultBranch(state.Path);
        var candidates = await service.Candidates(state.Path, remote, defaultBranch);
        if (candidates.Count == 0) { Ui.Warn("No eligible merged branches. Default, protected and checked-out branches are excluded."); Ui.Pause(); return; }
        AnsiConsole.MarkupLine("[grey]Only branches integrated into origin/" + Ui.E(defaultBranch) + " are listed.[/]");
        var chosen = Ui.Many(remote ? "Branches to delete ON ORIGIN" : "Local branches to delete", candidates, b => b.Name + " · " + b.Sha[..8]);
        if (chosen.Count == 0) return;
        AnsiConsole.MarkupLine("[yellow]" + Ui.E(string.Join("\n", chosen.Select(b => (remote ? repo.Url + " → " : state.Path + " → ") + b.Name))) + "[/]");
        var expected = "DELETE " + chosen.Count;
        if (Ui.Prompt(new TextPrompt<string>("Type [red]" + expected + "[/] to confirm:")) != expected) return;
        foreach (var branch in chosen)
        {
            var fresh = await service.Inspect(repo, Prefs.Root);
            if (fresh.Blocked.Length > 0) throw new InvalidOperationException(fresh.Blocked);
            var r = await service.DeleteBranch(state.Path, branch, remote);
            AnsiConsole.MarkupLine((r.Ok ? "[green]Deleted: " : "[yellow]Not deleted: ") + Ui.E(branch.Name + (r.Ok ? "" : " · " + r.Detail)) + "[/]");
        }
        if (remote) (await service.Fetch(state.Path)).Require();
        Ui.Pause();
    }
}
