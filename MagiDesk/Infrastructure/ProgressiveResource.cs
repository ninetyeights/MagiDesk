namespace MagiDesk.Infrastructure;

internal static class ProgressiveResource
{
    internal static async Task Deliver<T>(Func<Task<T?>> preview, Func<Task<T?>> full,
        Func<T, Task> publish, CancellationToken cancellation) where T : class
    {
        cancellation.ThrowIfCancellationRequested();
        var first = await preview().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (first is not null) await publish(first).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var final = await full().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (final is not null && !ReferenceEquals(first, final)) await publish(final).ConfigureAwait(false);
    }
}
