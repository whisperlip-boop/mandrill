namespace Mandrill.Presentation.Common;

/// <summary>Queue priority on the UI thread. Input stays ahead of <see cref="Low"/> work (architecture §4).</summary>
public enum UiPriority
{
    /// <summary>Normal priority (first snapshot of a navigation, failures, user-visible state).</summary>
    Normal,

    /// <summary>Below input (later snapshots of a scan).</summary>
    Low,
}

/// <summary>Marshals work to the UI thread (WinUI: DispatcherQueue; WPF: Dispatcher; tests: a manual pump).</summary>
public interface IUiDispatcher
{
    /// <summary>True when called on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread. Never runs it inline.</summary>
    /// <param name="action">Work to run.</param>
    /// <param name="priority">Queue priority.</param>
    void Post(Action action, UiPriority priority = UiPriority.Normal);
}
