namespace WorkspaceRepo;

internal static class TerminalTextTests
{
    public static void Run()
    {
        Check(TerminalText.Measure("Ae\u0301界👩‍💻🇮🇹1️⃣") == 10, "combined marks, CJK, joined emoji, flags and keycaps");
        Check(TerminalText.Fit("go界x", 4) == "go…", "right clipping preserves the wide glyph");
        Check(TerminalText.Abbreviate("ab界👩‍💻.git", 8) == "ab….git", "middle clipping preserves the path suffix");
        Check(TerminalText.Wrap("界e\u0301👩‍💻\nabc def", 4).SequenceEqual(["界e\u0301", "👩‍💻", "abc", "def"]), "hard and word wrapping fit cells");
        Check(TerminalText.Wrap("界a", 1).SequenceEqual(["…", "a"]), "one-cell wrap always advances");
        Check(TerminalText.RemoveLast("repo👩‍💻") == "repo", "editing removes a whole grapheme");
        Check(TerminalText.Printable("\u001b") == " " && TerminalText.Printable("\u202e") == " ", "controls cannot affect the terminal");
        Console.WriteLine("Terminal text self-test passed (7 checks).");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("Terminal text self-test: " + message);
    }
}
