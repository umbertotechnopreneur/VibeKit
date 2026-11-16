using System;
using System.Threading.Tasks;

namespace WaterRipple;

// The final native window must remain open until saving succeeds. Keeping this
// ordering independent of WinUI also lets tests exercise failures and explicit retries.
internal sealed class CloseSequence(Action stop, Func<Task<bool>> flush, Action close, Action failed)
{
    internal bool IsPending { get; private set; }

    internal async Task<bool> TryCloseAsync()
    {
        if (IsPending) return false;
        IsPending = true;
        try
        {
            stop();
            if (!await flush())
            {
                failed();
                return false;
            }
            close();
            return true;
        }
        finally { IsPending = false; }
    }
}
