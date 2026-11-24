namespace VibeKit.StudioControls.Core;

public enum WorkflowMode { Rehearsal, Live, Free }
public enum WorkflowState { Idle, Switching, Running, Paused, Finished, Stopped }

/// <summary>Driven on the UI thread. The host sends one OBS request per switching ticket.</summary>
public sealed class WorkflowPlayer(TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private WorkflowStep[] steps = [];
    private long startedAt;
    private TimeSpan interval;
    public WorkflowMode Mode { get; private set; }
    public WorkflowState State { get; private set; }
    public long Ticket { get; private set; }
    public int Index { get; private set; } = -1;
    public string Reason { get; private set; } = "";
    public WorkflowStep? Current => Index >= 0 && Index < steps.Length ? steps[Index] : null;
    public WorkflowStep? NextStep => Current is not null && Index + 1 < steps.Length ? steps[Index + 1] : null;
    private TimeSpan Remaining => State == WorkflowState.Running
        ? interval - clock.GetElapsedTime(startedAt) : interval;
    public int RemainingSeconds
    {
        get
        {
            double value = Math.Ceiling(Remaining.TotalSeconds);
            return (int)Math.Clamp(value, Mode == WorkflowMode.Free ? int.MinValue : 0, int.MaxValue);
        }
    }
    public int TotalRemainingSeconds => RemainingSeconds + afterCurrent;
    private int afterCurrent;

    public void Start(Workflow flow, bool live)
    {
        flow.Validate();
        if (flow.Steps.Length == 0) throw new ArgumentException("Add a scene before playing.");
        Stop();
        Mode = live ? WorkflowMode.Live : WorkflowMode.Rehearsal;
        steps = [.. flow.Steps];
        afterCurrent = steps.Sum(step => step.Seconds);
        EnterNext();
    }

    public void StartFree(int seconds)
    {
        WorkflowLibrary.CheckSeconds(seconds);
        Stop();
        Mode = WorkflowMode.Free;
        interval = TimeSpan.FromSeconds(seconds);
        Run();
    }

    public void Stop(string reason = "")
    {
        Ticket++;
        State = WorkflowState.Stopped;
        steps = [];
        Index = -1;
        afterCurrent = 0;
        interval = TimeSpan.Zero;
        Reason = reason;
    }

    public void Confirm(long ticket)
    {
        if (ticket == Ticket && State == WorkflowState.Switching) Run();
    }

    public void Fail(long ticket, string reason)
    {
        if (ticket == Ticket && State == WorkflowState.Switching) Stop(reason);
    }

    public void Pause()
    {
        if (State != WorkflowState.Running) return;
        interval = Remaining;
        State = WorkflowState.Paused;
    }

    public void Resume()
    {
        if (State == WorkflowState.Paused) Run();
    }

    public void Next()
    {
        if (State == WorkflowState.Running && Mode != WorkflowMode.Free) EnterNext();
    }

    public void Tick()
    {
        while (State == WorkflowState.Running && Mode != WorkflowMode.Free)
        {
            TimeSpan left = Remaining;
            if (left > TimeSpan.Zero) break;
            EnterNext();
            // Offline rehearsal follows the planned timeline even after a delayed UI tick.
            // Live intervals start only once OBS confirms the scene.
            if (State == WorkflowState.Running) interval += left;
        }
    }

    private void EnterNext()
    {
        Ticket++;
        if (Index + 1 == steps.Length)
        {
            interval = TimeSpan.Zero;
            State = WorkflowState.Finished;
            return;
        }
        Index++;
        afterCurrent -= steps[Index].Seconds;
        interval = TimeSpan.FromSeconds(steps[Index].Seconds);
        if (Mode == WorkflowMode.Live) State = WorkflowState.Switching;
        else Run();
    }

    private void Run()
    {
        startedAt = clock.GetTimestamp();
        State = WorkflowState.Running;
    }
}
