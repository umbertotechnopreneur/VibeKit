using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VibeKit.StudioControls.Windows;

public enum CameraProperty { Pan = 0, Tilt = 1, Zoom = 3 }
public sealed record CameraDevice(string Id, string Name);
public sealed record CameraValue(int Minimum, int Maximum, int Step, int Default, int Value, bool Manual);

/// <summary>Native DirectShow camera controls, without building a graph or opening a preview feed.</summary>
public sealed class CameraController : IDisposable
{
    private readonly BlockingCollection<Action> work = new(1);
    private readonly Thread thread;
    private object? source;
    private IAMCameraControl? control;
    private bool disposed;
    private static readonly Guid VideoInputCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");

    public CameraController()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Studio camera controls" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Run()
    {
        try { foreach (var action in work.GetConsumingEnumerable()) action(); }
        finally { CloseDevice(); }
    }

    private Task<T> Enqueue<T>(Func<T> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!work.TryAdd(() =>
        {
            if (token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
            try { completion.TrySetResult(action()); }
            catch (Exception error) { completion.TrySetException(error); }
        })) throw new InvalidOperationException("Camera controls are busy. Wait for the current adjustment.");
        return completion.Task;
    }

    public Task<IReadOnlyList<CameraDevice>> ListAsync(CancellationToken token) => Enqueue<IReadOnlyList<CameraDevice>>(() =>
    {
        var result = new List<CameraDevice>();
        VisitDevices((moniker, id, name) => { result.Add(new(id, name)); return false; });
        return result;
    }, token);

    public Task<IReadOnlyDictionary<CameraProperty, CameraValue>> OpenAsync(string deviceId, CancellationToken token) => Enqueue(() =>
    {
        CloseDevice();
        VisitDevices((moniker, id, _) =>
        {
            if (id != deviceId) return false;
            Guid identifier = new("00000000-0000-0000-C000-000000000046");
            moniker.BindToObject(null!, null, ref identifier, out object device);
            source = device;
            control = device as IAMCameraControl;
            if (control is null) { CloseDevice(); throw new NotSupportedException("This camera does not expose Windows pan, tilt or zoom controls."); }
            return true;
        });
        if (control is null) throw new InvalidOperationException("Camera not found. Refresh the device list.");
        return Snapshot();
    }, token);

    public Task<IReadOnlyDictionary<CameraProperty, CameraValue>> SetAsync(CameraProperty property, int value, CancellationToken token) => Enqueue(() =>
    {
        Set(property, value);
        return Snapshot();
    }, token);

    public Task<IReadOnlyDictionary<CameraProperty, CameraValue>> FrameAsync(CameraFraming? frame, CancellationToken token) => Enqueue(() =>
    {
        var before = Snapshot();
        var changed = new List<CameraProperty>();
        try
        {
            foreach (var (property, range) in before)
            {
                if (!range.Manual) continue;
                token.ThrowIfCancellationRequested();
                int value = frame is null ? range.Default : property switch
                {
                    CameraProperty.Zoom => frame.Zoom, CameraProperty.Pan => frame.Pan, _ => frame.Tilt
                };
                Set(property, value);
                changed.Add(property);
            }
        }
        catch
        {
            // A handled partial preset failure restores the controls already changed.
            foreach (var property in changed)
            {
                if (token.IsCancellationRequested) break;
                try { Set(property, before[property].Value); }
                catch (Exception error) { Diagnostics.Record("camera.preset.rollback", error); }
            }
            throw;
        }
        return Snapshot();
    }, token);

    private void Set(CameraProperty property, int value)
    {
        var ranges = Snapshot();
        if (!ranges.TryGetValue(property, out var range) || !range.Manual)
            throw new NotSupportedException("The camera does not support this manual adjustment.");
        value = Math.Clamp(value, range.Minimum, range.Maximum);
        int snapped = range.Minimum + (int)Math.Round((value - (double)range.Minimum) / range.Step) * range.Step;
        Marshal.ThrowExceptionForHR(control!.Set(property, Math.Clamp(snapped, range.Minimum, range.Maximum), 2));
    }

    private IReadOnlyDictionary<CameraProperty, CameraValue> Snapshot()
    {
        if (control is null) throw new InvalidOperationException("Choose a camera and open its controls first.");
        var values = new Dictionary<CameraProperty, CameraValue>();
        foreach (var property in Enum.GetValues<CameraProperty>())
        {
            int result = control.GetRange(property, out int minimum, out int maximum, out int step, out int initial, out int flags);
            if (result < 0 || minimum > maximum) continue;
            result = control.Get(property, out int current, out _);
            if (result < 0) continue;
            values[property] = new(minimum, maximum, Math.Max(step, 1), initial, current, (flags & 2) != 0);
        }
        return values;
    }

    private static void VisitDevices(Func<IMoniker, string, string, bool> visitor)
    {
        var type = Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"), true)!;
        object enumeratorObject = Activator.CreateInstance(type)!;
        IEnumMoniker? enumerator = null;
        try
        {
            Guid category = VideoInputCategory;
            int result = ((ICreateDevEnum)enumeratorObject).CreateClassEnumerator(ref category, out enumerator, 0);
            if (result == 1 || enumerator is null) return;
            Marshal.ThrowExceptionForHR(result);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, 0) == 0)
            {
                IMoniker moniker = monikers[0];
                object? propertyBag = null;
                try
                {
                    moniker.GetDisplayName(null!, null, out string id);
                    Guid bagId = typeof(IPropertyBag).GUID;
                    moniker.BindToStorage(null!, null, ref bagId, out propertyBag);
                    ((IPropertyBag)propertyBag).Read("FriendlyName", out object name, 0);
                    if (visitor(moniker, id, name as string ?? "Camera")) break;
                }
                finally
                {
                    if (propertyBag is not null) Marshal.ReleaseComObject(propertyBag);
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            Marshal.ReleaseComObject(enumeratorObject);
        }
    }

    private void CloseDevice()
    {
        control = null;
        if (source is not null) Marshal.ReleaseComObject(source);
        source = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        work.CompleteAdding();
        // Do not block the UI on a faulty driver. The background STA releases its
        // device after the active call; queued closures observe lifetime cancellation.
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker? enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, nint errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("C6E13370-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig] int GetRange(CameraProperty property, out int minimum, out int maximum, out int step, out int initial, out int flags);
        [PreserveSig] int Set(CameraProperty property, int value, int flags);
        [PreserveSig] int Get(CameraProperty property, out int value, out int flags);
    }
}
