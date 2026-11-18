using System;
using System.Threading;
using Serilog;
using Serilog.Core;
using Tools.Widgets;

namespace WaterRipple;

internal static class LoggingService
{
    internal const string AppId = "WaterRipple";
    private static readonly object Sync = new();
    private static Logger? logger;
    private static Func<long>? droppedEvents;

    internal static long DroppedEvents => Volatile.Read(ref droppedEvents)?.Invoke() ?? 0;

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (logger is not null) return;
            logger = VibeWareDiagnostics.CreateLogger(AppId, out var dropped);
            Volatile.Write(ref droppedEvents, dropped);
            Log.Logger = logger;
        }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            if (logger is null) return;
            Log.CloseAndFlush();
            logger = null;
            Volatile.Write(ref droppedEvents, null);
        }
    }
}
