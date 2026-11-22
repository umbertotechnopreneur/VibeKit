using System.Runtime.InteropServices;

namespace WorkspaceRepo;

// Use the SQLite library supplied by Windows or Debian.
internal sealed class IndexDatabase : IDisposable
{
    const string Library = "repo_radar_system_sqlite3";
    const int Done = 101, Row = 100;
    IntPtr handle;
    static IndexDatabase()
    {
        NativeLibrary.SetDllImportResolver(typeof(IndexDatabase).Assembly, (name, assembly, searchPath) =>
        {
            if (name != Library) return IntPtr.Zero;
            if (OperatingSystem.IsWindows())
                return NativeLibrary.Load("winsqlite3.dll", assembly, DllImportSearchPath.System32);
            if (OperatingSystem.IsLinux()) return NativeLibrary.Load("libsqlite3.so.0");
            throw new PlatformNotSupportedException($"{ProductBrand.Name} supports Windows x64 and Debian amd64.");
        });
    }

    public IndexDatabase(string path) : this(path, writable: true) { }

    private IndexDatabase(string path, bool writable)
    {
        int result = sqlite3_open_v2(path, out handle, writable ? 2 | 4 : 1, IntPtr.Zero);
        if (result != 0)
        {
            var error = Error(result);
            Dispose();
            throw error;
        }
        sqlite3_busy_timeout(handle, 5000);
        if (writable && !OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { Dispose(); throw; }
        }
    }

    public static void Backup(string source, string target)
    {
        using var previous = new IndexDatabase(source, writable: false);
        using var copy = new IndexDatabase(target);
        IntPtr backup = sqlite3_backup_init(copy.handle, "main", previous.handle, "main");
        if (backup == IntPtr.Zero) throw copy.Error(sqlite3_errcode(copy.handle));
        int result;
        try { result = sqlite3_backup_step(backup, -1); }
        finally { sqlite3_backup_finish(backup); }
        if (result != Done) throw copy.Error(result);
    }

    Exception Error(int code) => new IOException("SQLite " + code + ": " +
        (handle == IntPtr.Zero ? "Cannot open the workspace index." : Marshal.PtrToStringUTF8(sqlite3_errmsg(handle))));
    IntPtr Prepare(string sql, string[] values)
    {
        int result = sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero);
        if (result != 0) throw Error(result);
        for (int i = 0; i < values.Length; i++)
        {
            result = sqlite3_bind_text(statement, i + 1, values[i], -1, new IntPtr(-1));
            if (result == 0) continue;
            sqlite3_finalize(statement);
            throw Error(result);
        }
        return statement;
    }
    public void Execute(string sql, params string[] values)
    {
        var statement = Prepare(sql, values);
        try { var result = sqlite3_step(statement); if (result != Done) throw Error(result); }
        finally { sqlite3_finalize(statement); }
    }
    public List<string> Query(string sql, params string[] values)
    {
        var statement = Prepare(sql, values);
        try
        {
            var rows = new List<string>();
            int result;
            while ((result = sqlite3_step(statement)) == Row)
                rows.Add(Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) ?? "");
            if (result != Done) throw Error(result);
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }
    public void Dispose() { if (handle == IntPtr.Zero) return; sqlite3_close(handle); handle = IntPtr.Zero; }
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_errcode(IntPtr db);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern IntPtr sqlite3_backup_init(IntPtr destination,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationName, IntPtr source, [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_backup_step(IntPtr backup, int pages);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_backup_finish(IntPtr backup);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_close(IntPtr db);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Library, CallingConvention=CallingConvention.Winapi)] static extern int sqlite3_bind_text(IntPtr stmt, int pos, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);
}
