namespace WorkspaceRepo;

internal enum PathPickerMode { File, Folder, Either }
internal sealed record PathPickerEntry(string Name, string Path, bool IsFolder, DateTime? Modified, bool IsParent = false);
internal sealed record PathTreeRow(string Label, string Path, int Depth, bool Expanded);

/// <summary>Keyboard file/folder browser. SelectedPath is the exact absolute path chosen by the caller.</summary>
internal sealed partial class TerminalPathPicker
{
    static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    readonly PathPickerMode mode;
    readonly HashSet<string> expanded = new(Comparer);
    readonly Dictionary<string, List<PathPickerEntry>> children = new(Comparer);
    readonly List<string> locations;
    readonly Dictionary<string, string> volumeLabels = new(Comparer);
    readonly List<PathTreeRow> tree = [];
    List<PathPickerEntry> contents = [];
    int treeRow, fileRow, treeOffset, fileOffset, pageSize = 5;
    bool treeFocus, showFiles, editing;
    string input = "";
    public string Folder { get; private set; }
    string[] breadcrumb = [];
    readonly string locationsTitle = OperatingSystem.IsWindows() ? "This PC" : "Locations";
    public string? SelectedPath { get; private set; }
    public bool Cancelled { get; private set; }
    public string Error { get; private set; } = "";
    public bool Finished => Cancelled || SelectedPath != null;

    public TerminalPathPicker(string startFolder, PathPickerMode mode)
    {
        this.mode = mode;
        showFiles = mode == PathPickerMode.File;
        Folder = Directory.Exists(startFolder) ? Path.GetFullPath(startFolder) : Environment.CurrentDirectory;
        locations = [Path.DirectorySeparatorChar.ToString()];
        if (OperatingSystem.IsWindows())
        {
            var drives = DriveInfo.GetDrives().OrderBy(d => d.Name, Comparer);
            locations = [];
            foreach (var drive in drives)
            {
                locations.Add(drive.Name);
                // Leave removable and network drives unopened until the user picks one.
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady && drive.VolumeLabel is { Length: > 0 } label)
                        volumeLabels[drive.Name] = label;
                }
                catch (Exception e) when (IsPathError(e)) { }
            }
        }
        OpenFolder(Folder);
    }

    public static string? PickFile(string startFolder) => Run(startFolder, PathPickerMode.File);
    public static string? PickFolder(string startFolder) => Run(startFolder, PathPickerMode.Folder);

    static string? Run(string startFolder, PathPickerMode mode)
    {
        var picker = new TerminalPathPicker(startFolder, mode);
        using var terminal = new DashboardTerminal();
        int lastWidth = -1, lastHeight = -1;
        bool repaint = true;
        while (!picker.Finished)
        {
            int width = terminal.Width, height = terminal.Height;
            if (repaint || width != lastWidth || height != lastHeight)
            {
                var grid = new DashboardGrid(width, height);
                int w = Math.Min(112, width), h = Math.Min(26, height);
                picker.Draw(grid, (width - w) / 2, (height - h) / 2, w, h);
                terminal.Paint(grid); lastWidth = width; lastHeight = height; repaint = false;
            }
            if (Console.KeyAvailable) { picker.Key(Console.ReadKey(true)); repaint = true; }
            else Thread.Sleep(40);
        }
        return picker.SelectedPath;
    }

    static bool IsPathError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;

    List<PathPickerEntry> Read(string path, bool foldersOnly, bool reportError = true)
    {
        var info = new DirectoryInfo(path);
        var entries = new List<PathPickerEntry>();
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        try
        {
            foreach (var item in info.EnumerateFileSystemInfos("*", options))
            {
                try
                {
                    bool folder = item.Attributes.HasFlag(FileAttributes.Directory);
                    if (!foldersOnly || folder) entries.Add(new(item.Name, item.FullName, folder, item.LastWriteTime));
                }
                catch (Exception ex) when (IsPathError(ex)) { /* Individual entries can disappear during browsing. */ }
            }
        }
        catch (Exception ex) when (IsPathError(ex)) { if (reportError) Error = ex.Message; }
        return entries.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, Comparer).ToList();
    }

    List<PathPickerEntry> Subfolders(string path)
    {
        if (!children.TryGetValue(path, out var list)) children[path] = list = Read(path, true, reportError: false);
        return list;
    }

    void RebuildTree(string selected)
    {
        tree.Clear();
        void Add(string path, string name, int depth)
        {
            bool open = expanded.Contains(path);
            tree.Add(new(name, path, depth, open));
            if (!open) return;
            foreach (var child in Subfolders(path)) Add(child.Path, child.Name, depth + 1);
        }
        foreach (var location in locations)
        {
            var label = volumeLabels.TryGetValue(location, out var volume) ? $"{location}  ({volume})" : location;
            Add(location, label, 0);
        }
        treeRow = Math.Max(0, tree.FindIndex(r => Comparer.Equals(r.Path, selected)));
    }

    void OpenFolder(string path)
    {
        Error = "";
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path)) { Error = "Folder unavailable: " + path; return; }
        var ancestors = new DirectoryInfo(path);
        if (!locations.Contains(ancestors.Root.FullName, Comparer)) locations.Add(ancestors.Root.FullName);
        for (var p = ancestors.Parent; p != null; p = p.Parent) expanded.Add(p.FullName);
        expanded.Add(path);
        ReadContents(path);
        RebuildTree(path);
    }

    void ReadContents(string path)
    {
        Folder = path;
        var directory = new DirectoryInfo(path);
        var parts = new Stack<string>();
        for (var p = directory; p.Parent != null; p = p.Parent) parts.Push(p.Name);
        var root = directory.Root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        breadcrumb = [locationsTitle, root.Length > 0 ? root : directory.Root.FullName, .. parts];
        contents = Read(path, !showFiles);
        children[path] = contents.Where(e => e.IsFolder).ToList();
        if (directory.Parent is { } parent) contents.Insert(0, new("..", parent.FullName, true, null, IsParent: true));
        fileOffset = 0;
        fileRow = contents.Count > 1 && contents[0].IsParent ? 1 : 0;
    }

    void MoveTree(int row)
    {
        if (tree.Count == 0) return;
        treeRow = Math.Clamp(row, 0, tree.Count - 1);
        ReadContents(tree[treeRow].Path);
    }

    void Select(string path, bool folder)
    {
        if (folder && mode == PathPickerMode.File) { Error = "Choose a file in the contents pane."; return; }
        if (!folder && mode == PathPickerMode.Folder) { Error = "Choose a folder."; return; }
        if (folder ? !Directory.Exists(path) : !File.Exists(path)) { Error = "The selected path is no longer available."; return; }
        SelectedPath = Path.GetFullPath(path);
    }

    void EnterPath(ConsoleKeyInfo k)
    {
        if (k.Key == ConsoleKey.Escape) { editing = false; Error = ""; return; }
        if (k.Key == ConsoleKey.Backspace) { input = TerminalText.RemoveLast(input); return; }
        if (k.Key != ConsoleKey.Enter)
        {
            if (!char.IsControl(k.KeyChar)) input += k.KeyChar;
            return;
        }
        try
        {
            var typed = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
            if (typed.Length == 0) { Error = "Enter a file or folder path."; return; }
            var full = Path.GetFullPath(typed, Folder);
            if (Directory.Exists(full)) { editing = false; treeFocus = false; OpenFolder(full); }
            else if (File.Exists(full)) Select(full, false);
            else Error = "Path unavailable: " + full;
        }
        catch (Exception ex) when (IsPathError(ex)) { Error = ex.Message; }
    }

    public void Key(ConsoleKeyInfo k)
    {
        if (Finished) return;
        if (editing) { EnterPath(k); return; }
        Error = "";
        switch (k.Key)
        {
            case ConsoleKey.Escape: Cancelled = true; return;
            case ConsoleKey.Tab: case ConsoleKey.F6: treeFocus = !treeFocus; return;
            case ConsoleKey.D:
                treeFocus = true;
                var drive = Path.GetPathRoot(Folder);
                if (drive != null) MoveTree(tree.FindIndex(r => Comparer.Equals(r.Path, drive)));
                return;
            case ConsoleKey.F when mode == PathPickerMode.Either:
                showFiles = !showFiles; ReadContents(Folder); return;
            case ConsoleKey.G: editing = true; input = ""; return;
            case ConsoleKey.S when mode != PathPickerMode.File: Select(Folder, true); return;
            case ConsoleKey.Backspace:
                if (Directory.GetParent(Folder) is { } parent) OpenFolder(parent.FullName);
                return;
        }
        int count = treeFocus ? tree.Count : contents.Count;
        if (count == 0) return;
        int at = treeFocus ? treeRow : fileRow;
        int next = k.Key switch
        {
            ConsoleKey.UpArrow => at - 1, ConsoleKey.DownArrow => at + 1,
            ConsoleKey.PageUp => at - pageSize, ConsoleKey.PageDown => at + pageSize,
            ConsoleKey.Home => 0, ConsoleKey.End => count - 1, _ => at
        };
        if (next != at)
        {
            if (treeFocus) MoveTree(next); else fileRow = Math.Clamp(next, 0, count - 1);
            return;
        }
        if (treeFocus)
        {
            var t = tree[treeRow];
            switch (k.Key)
            {
                case ConsoleKey.RightArrow:
                    if (expanded.Add(t.Path)) RebuildTree(t.Path);
                    else if (treeRow + 1 < tree.Count && tree[treeRow + 1].Depth == t.Depth + 1) MoveTree(treeRow + 1);
                    break;
                case ConsoleKey.LeftArrow:
                    if (expanded.Remove(t.Path)) RebuildTree(t.Path);
                    else if (Directory.GetParent(t.Path) is { } parent)
                        MoveTree(tree.FindIndex(r => Comparer.Equals(r.Path, parent.FullName)));
                    break;
                case ConsoleKey.Enter: OpenFolder(t.Path); treeFocus = false; break;
                case ConsoleKey.Spacebar: Select(t.Path, true); break;
            }
            return;
        }
        var choice = contents[fileRow];
        if (k.Key == ConsoleKey.LeftArrow && Directory.GetParent(Folder) is { } up) OpenFolder(up.FullName);
        else if (choice.IsFolder && (k.Key is ConsoleKey.Enter or ConsoleKey.RightArrow || choice.IsParent && k.Key == ConsoleKey.Spacebar)) OpenFolder(choice.Path);
        else if (k.Key is ConsoleKey.Enter or ConsoleKey.Spacebar) Select(choice.Path, choice.IsFolder);
    }
}
