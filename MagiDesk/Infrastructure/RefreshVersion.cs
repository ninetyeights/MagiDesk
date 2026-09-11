namespace MagiDesk.Infrastructure;

/// <summary>UI-thread owned lifecycle/version guard for asynchronous results.</summary>
internal sealed class RefreshVersion
{
    private long _value;
    public long Next() => ++_value;
    public bool IsCurrent(long value) => value == _value;
}
