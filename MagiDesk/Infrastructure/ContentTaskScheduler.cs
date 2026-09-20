using System.Windows.Threading;

namespace MagiDesk.Infrastructure;

/// <summary>Content completions must not inherit ApplicationIdle from initial activation.</summary>
internal sealed class ContentTaskScheduler(Dispatcher dispatcher) : TaskScheduler
{
    internal const DispatcherPriority Priority = DispatcherPriority.Loaded;
    protected override void QueueTask(Task task)
        => dispatcher.BeginInvoke(new Action(() => TryExecuteTask(task)), Priority);
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    protected override IEnumerable<Task>? GetScheduledTasks() => null;
}
