namespace Mandrill.Presentation.Navigation;

/// <summary>
/// Per-pane back/forward history (AC-1.16). Holds committed locations only: the pane calls <see cref="Navigate"/> after a
/// navigation succeeded. Memory-only (not persisted). Not thread-safe; UI thread only.
/// </summary>
public sealed class NavHistory
{
    // Windows paths are case-insensitive; same path-equality rule as architecture §10.
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    /// <summary>Current location; null before the first navigation.</summary>
    public string? Current { get; private set; }

    /// <summary>Whether <see cref="Back"/> has a target.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Whether <see cref="Forward"/> has a target.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>
    /// Records a completed navigation to a new location and truncates forward history (AC-1.16).
    /// Navigating to the current location (refresh, same path typed) only updates its spelling.
    /// </summary>
    /// <param name="path">Normalized folder path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    public void Navigate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Current is not null)
        {
            if (PathComparer.Equals(Current, path))
            {
                Current = path;
                return;
            }

            _back.Add(Current);
        }

        _forward.Clear();
        Current = path;
    }

    /// <summary>Moves one step back.</summary>
    /// <returns>The new current location, or null when there is nothing to go back to.</returns>
    public string? Back() => Step(_back, _forward);

    /// <summary>Moves one step forward.</summary>
    /// <returns>The new current location, or null when there is nothing to go forward to.</returns>
    public string? Forward() => Step(_forward, _back);

    /// <summary>
    /// Replaces the current location without adding history, e.g. when a back/forward target no longer exists and the
    /// pane opened its nearest existing ancestor (E-1.2). A neighbouring entry equal to the new location is dropped.
    /// </summary>
    /// <param name="path">Location actually shown.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    public void ReplaceCurrent(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Current = path;
        DropTopIfSame(_back, path);
        DropTopIfSame(_forward, path);
    }

    private string? Step(List<string> from, List<string> to)
    {
        if (from.Count == 0 || Current is null)
        {
            return null;
        }

        to.Add(Current);
        Current = from[^1];
        from.RemoveAt(from.Count - 1);
        return Current;
    }

    private static void DropTopIfSame(List<string> stack, string path)
    {
        if (stack.Count > 0 && PathComparer.Equals(stack[^1], path))
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }
}
