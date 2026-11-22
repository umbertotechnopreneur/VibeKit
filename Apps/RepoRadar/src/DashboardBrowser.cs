namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private TerminalPathPicker? browser;
    // The dashboard path entry dialog also reads the active picker error.
    private string browseError => browser?.Error ?? "";

    private void OpenBrowser()
    {
        browser = new TerminalPathPicker(root, PathPickerMode.Either);
        view = View.Browser;
    }

    private void ChooseRoot(string path)
    {
        root = Path.GetFullPath(path);
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals(".git", StringComparison.OrdinalIgnoreCase))
            root = Directory.GetParent(root)?.FullName ?? root;
        all = index.Load(root);
        search = ""; SetFilter("Overview");
        awaitingRoot = false;
        status = all.Count == 0 ? "Choose Scan to index this root." : "Showing saved observations.";
        consentChoice = 0;
        view = View.Consent;
    }

    private void BrowserKey(ConsoleKeyInfo key)
    {
        if (browser == null) { view = View.Grid; return; }
        browser.Key(key);
        if (browser.Cancelled) { view = View.Grid; return; }
        if (browser.SelectedPath is not { } path) return;
        if (Directory.Exists(path)) { ChooseRoot(path); return; }
        // Opening a file for the scan action selects its containing workspace.
        if (Path.GetDirectoryName(path) is { } folder) ChooseRoot(folder);
    }
}
