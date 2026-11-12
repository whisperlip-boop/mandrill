using System.Globalization;

namespace Mandrill.Core.State;

/// <summary>Helpers for <see cref="IStateDocument{T}.Normalize"/>: keep a valid value or fall back and record why.</summary>
internal static class Fixes
{
    /// <summary>Returns <paramref name="value"/> when valid, else <paramref name="fallback"/> plus a log line.</summary>
    public static T Check<T>(ICollection<string> fixes, string field, T value, bool valid, T fallback)
    {
        if (valid)
        {
            return value;
        }

        fixes.Add(string.Create(CultureInfo.InvariantCulture, $"{field}: '{value?.ToString() ?? "null"}' → '{fallback}'"));
        return fallback;
    }

    /// <summary>True for a finite, strictly positive number.</summary>
    public static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}
