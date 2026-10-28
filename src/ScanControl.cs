// SPDX-License-Identifier: GPL-3.0-or-later
namespace HashVista;

/// <summary>
/// Cooperative, in-process pause. It retains the open stream and incremental hash
/// state; it is not a persisted checkpoint and does not change refresh semantics.
/// Dispose only after the associated scan task has completed.
/// </summary>
public sealed class ScanControl : IDisposable
{
    private readonly object gate = new();
    private readonly ManualResetEventSlim continued = new(true);
    private int requested, paused;
    private bool disposed;

    public bool PauseRequested => Volatile.Read(ref requested) != 0;
    public bool IsPaused => Volatile.Read(ref paused) != 0;

    public void RequestPause()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Volatile.Write(ref requested, 1);
            continued.Reset();
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Volatile.Write(ref requested, 0);
            continued.Set();
        }
    }

    internal void WaitForResume(CancellationToken token, Action acknowledge)
    {
        if (!PauseRequested) return;
        Volatile.Write(ref paused, 1);
        try
        {
            acknowledge();
            // Cancellation wakes this wait even when Continue has not been pressed.
            continued.Wait(token);
            token.ThrowIfCancellationRequested();
        }
        finally { Volatile.Write(ref paused, 0); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            continued.Dispose();
        }
    }
}
