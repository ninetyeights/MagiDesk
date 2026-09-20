namespace MagiDesk.Infrastructure;

internal static class CancellationLifetime
{
    // Mark cancellation immediately; drain callbacks away from the unloading UI thread.
    internal static async Task CancelAndDisposeAsync(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Write($"IMAGE-CANCEL callback failed: {ex.GetType().Name}\n"); }
        finally { source.Dispose(); }
    }
}
