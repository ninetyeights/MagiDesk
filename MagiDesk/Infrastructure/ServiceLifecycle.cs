namespace MagiDesk.Infrastructure;

internal static class ServiceLifecycle
{
    internal static T? Start<T>(string name, Func<T> create, Action<T> start, ICollection<string> errors) where T : class, IDisposable
    {
        T? service = null;
        try { service = create(); start(service); return service; }
        catch (Exception ex)
        {
            errors.Add($"{name}：{ex.Message}");
            DiagnosticLog.Write($"START failed {name}: {ex}\n");
            Stop(name, () => service?.Dispose());
            return null;
        }
    }

    internal static void Stop(string name, Action stop)
    {
        try { stop(); }
        catch (Exception ex) { DiagnosticLog.Write($"STOP failed {name}: {ex}\n"); }
    }
}
