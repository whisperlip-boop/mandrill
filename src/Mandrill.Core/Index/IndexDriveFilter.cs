using Mandrill.Core.Diagnostics;

namespace Mandrill.Core.Index;

/// <summary>
/// Perf runs only (WP-2.7): <c>MANDRILL_INDEX_DRIVES=Q</c> (letters, separated by ',', ';' or spaces) restricts the index to
/// those fixed drives, so a measurement on a synthetic drive never reads the user's own drives. Honoured only together with
/// <c>MANDRILL_DEBUG=1</c>; otherwise every fixed drive is indexed.
/// </summary>
public static class IndexDriveFilter
{
    /// <summary>Name of the environment variable.</summary>
    public const string Variable = "MANDRILL_INDEX_DRIVES";

    /// <summary>Name of the variable that selects the GC latency mode during builds ("sustained"); debug runs only.</summary>
    public const string GcVariable = "MANDRILL_INDEX_GC";

    /// <summary>
    /// True when a MANDRILL_DEBUG perf run asks for <c>SustainedLowLatency</c> while the index builds (WP-2.7 A/B for R6).
    /// </summary>
    /// <returns>True for <c>MANDRILL_INDEX_GC=sustained</c> with <c>MANDRILL_DEBUG=1</c>.</returns>
    public static bool? LowLatencyGcFromEnvironment()
        => MandrillLogging.IsDebugEnabled && Environment.GetEnvironmentVariable(GcVariable) is { Length: > 0 } value
            ? string.Equals(value, "sustained", StringComparison.OrdinalIgnoreCase)
            : null;

    /// <summary>The filter from the environment, or null when none applies.</summary>
    /// <returns>A predicate over drive roots ("Q:\"), or null.</returns>
    public static Func<string, bool>? FromEnvironment()
        => MandrillLogging.IsDebugEnabled ? Parse(Environment.GetEnvironmentVariable(Variable)) : null;

    /// <summary>Parses a letter list; null or no letters → null (no filter).</summary>
    /// <param name="value">E.g. "Q", "q, r".</param>
    /// <returns>A predicate over drive roots, or null.</returns>
    public static Func<string, bool>? Parse(string? value)
    {
        var letters = new HashSet<char>();
        foreach (string part in (value ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.TrimEnd(':', '\\').Length == 1 && char.IsAsciiLetter(part[0]))
            {
                letters.Add(char.ToUpperInvariant(part[0]));
            }
        }

        return letters.Count == 0 ? null : root => root.Length > 0 && letters.Contains(char.ToUpperInvariant(root[0]));
    }
}
