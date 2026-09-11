namespace MagiDesk.Infrastructure;

/// <summary>UI-thread owned: multiple requests before dispatch become one action.</summary>
internal sealed class CoalescedAction(Action<Action> schedule, Action action)
{
    private bool _pending;
    public void Request()
    {
        if (_pending) return;
        _pending = true;
        schedule(() => { _pending = false; action(); });
    }
}
