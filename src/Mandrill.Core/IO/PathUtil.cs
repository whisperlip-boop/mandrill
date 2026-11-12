using System.Text;

namespace Mandrill.Core.IO;

/// <summary>
/// Pure, UI-thread-safe path rules (architecture §3, §10). Internal paths are normalized DOS form ("C:\a\b") without the
/// \\?\ prefix. Unlike Path.GetFullPath this never trims trailing spaces or dots from segments, so names such as
/// "folder " survive (E-1.11); <see cref="ToIo"/> adds \\?\ where System.IO would otherwise alter the path.
/// </summary>
public static class PathUtil
{
    /// <summary>Length from which <see cref="ToIo"/> always adds the extended-length prefix.</summary>
    public const int MaxPath = 260;

    /// <summary>
    /// The "This PC" location (ADR-0015): Explorer's parsing name. Not a folder: <see cref="TryNormalize"/> rejects it, so it
    /// never matches a real path.
    /// </summary>
    public const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    private static readonly char[] InvalidNameChars = ['<', '>', '"', '/', '\\', '|', '?', '*', ':'];

    // DOS device names, reserved with or without an extension ("con.txt"); Explorer rejects them at input time.
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Normalizes user or stored text to an absolute DOS path: optionally expands %VARS%, accepts '/' as separator,
    /// strips a \\?\ prefix and surrounding quotes, resolves "." and "..", removes duplicate separators and the trailing
    /// '\' (except at the root), and upper-cases the drive letter. UNC paths are <see cref="FsErrorKind.Unsupported"/>.
    /// </summary>
    /// <param name="text">Path text.</param>
    /// <param name="path">The normalized path when successful; otherwise empty.</param>
    /// <param name="error">The error when unsuccessful; otherwise null.</param>
    /// <param name="expandEnvironment">
    /// True only for text the user typed (address bar). Stored paths (session, favorites, history) and shell results must
    /// not expand: a real folder whose name contains a %NAME% pair would otherwise resolve to a different location.
    /// </param>
    /// <returns>True when <paramref name="path"/> is valid.</returns>
    public static bool TryNormalize(string? text, out string path, out FsError? error, bool expandEnvironment = false)
    {
        path = string.Empty;
        error = null;
        string raw = text ?? string.Empty;

        // Leading whitespace, line breaks and tabs are never part of a path; trailing spaces can be (E-1.11).
        string s = raw.TrimStart().TrimEnd('\r', '\n', '\t');
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            s = s[1..^1];
        }

        if (expandEnvironment)
        {
            s = Environment.ExpandEnvironmentVariables(s);
        }

        s = s.Replace('/', '\\');
        if (s.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = new FsError(FsErrorKind.Unsupported, raw, 0);
            return false;
        }

        if (s.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            s = s[ExtendedPrefix.Length..];
        }

        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = new FsError(FsErrorKind.Unsupported, raw, 0);
            return false;
        }

        if (s.Length < 2 || !char.IsAsciiLetter(s[0]) || s[1] != ':' || (s.Length > 2 && s[2] != '\\'))
        {
            // Relative ("a\b"), rooted without drive ("\a") and drive-relative ("C:a") paths are not accepted.
            error = new FsError(FsErrorKind.InvalidPath, raw, 123);
            return false;
        }

        var segments = new List<string>();
        foreach (string segment in s[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Above the root stays at the root, as in Explorer.
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            if (!IsValidSegment(segment))
            {
                error = new FsError(FsErrorKind.InvalidPath, raw, 123);
                return false;
            }

            segments.Add(segment);
        }

        var sb = new StringBuilder(s.Length + 1);
        sb.Append(char.ToUpperInvariant(s[0])).Append(":\\");
        sb.AppendJoin('\\', segments);
        path = sb.ToString();
        return true;
    }

    /// <summary>
    /// The path-equality rule: <see cref="StringComparison.OrdinalIgnoreCase"/>. Every dictionary or set keyed by a path
    /// uses this comparer (composite keys compare their path part with it). There is deliberately no upper-cased string
    /// key: such a key depends on the OS casing tables and can disagree with OrdinalIgnoreCase.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>True when two normalized paths name the same location under the path-equality rule.</summary>
    /// <param name="a">First path.</param>
    /// <param name="b">Second path.</param>
    /// <returns>True when equal.</returns>
    public static bool PathEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The form to pass to System.IO: adds \\?\ when the path is ≥ 260 characters or any segment ends with a space or
    /// a dot, because .NET normalization would otherwise trim those characters (E-1.6, E-1.11).
    /// </summary>
    /// <param name="path">Normalized path.</param>
    /// <returns>The path for I/O calls.</returns>
    public static string ToIo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        return path.Length >= MaxPath || HasSegmentEndingInSpaceOrDot(path) ? ExtendedPrefix + path : path;
    }

    /// <summary>
    /// <see cref="ToIo"/> for a source/destination pair: if either needs the \\?\ prefix both get it, because
    /// System.IO move APIs reject paths whose roots differ in form ("Source and destination path must have identical roots").
    /// </summary>
    /// <param name="source">Normalized source path.</param>
    /// <param name="destination">Normalized destination path.</param>
    /// <returns>Both paths in I/O form.</returns>
    public static (string Source, string Destination) ToIoPair(string source, string destination)
    {
        string s = ToIo(source);
        string d = ToIo(destination);
        bool prefixed = s.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || d.StartsWith(ExtendedPrefix, StringComparison.Ordinal);
        return prefixed ? (WithPrefix(s), WithPrefix(d)) : (s, d);
    }

    /// <summary>Joins a normalized directory and a child name.</summary>
    /// <param name="directory">Normalized directory.</param>
    /// <param name="name">Child name (one segment).</param>
    /// <returns>The child's normalized path.</returns>
    public static string Combine(string directory, string name)
        => directory.EndsWith('\\') ? directory + name : directory + "\\" + name;

    /// <summary>Parent of a normalized path; null for a drive root.</summary>
    /// <param name="path">Normalized path.</param>
    /// <returns>The parent, or null.</returns>
    public static string? Parent(string path)
    {
        if (IsDriveRoot(path))
        {
            return null;
        }

        int i = path.LastIndexOf('\\');
        if (i < 0)
        {
            return null;
        }

        return i == 2 ? path[..3] : path[..i];
    }

    /// <summary>Explorer's "Copy as path" text (Ctrl+Shift+C, WP-3.6): every path in double quotes, one per line.</summary>
    /// <param name="paths">Full paths.</param>
    /// <returns>The text.</returns>
    public static string CopyAsPathText(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return string.Join("\r\n", paths.Select(p => "\"" + p + "\""));
    }

    /// <summary>The last segment ("C:\a\b" → "b"; "C:\" → "C:\").</summary>
    /// <param name="path">Normalized path.</param>
    /// <returns>The display name of the folder.</returns>
    public static string LeafName(string path)
    {
        if (IsDriveRoot(path))
        {
            return path;
        }

        int i = path.LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>True for the <see cref="ThisPc"/> token (ordinal, ignoring case).</summary>
    /// <param name="path">A navigation target or stored location.</param>
    /// <returns>True for This PC.</returns>
    public static bool IsThisPc(string? path) => string.Equals(path, ThisPc, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for "X:\".</summary>
    /// <param name="path">Normalized path.</param>
    /// <returns>True for a drive root.</returns>
    public static bool IsDriveRoot(string path)
        => path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies below it (segment-aware, case-insensitive).</summary>
    /// <param name="path">Normalized path.</param>
    /// <param name="root">Normalized ancestor candidate.</param>
    /// <returns>True when same or under.</returns>
    public static bool IsSameOrUnder(string path, string root)
    {
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == root.Length || root.EndsWith('\\') || path[root.Length] == '\\';
    }

    /// <summary>True when <paramref name="name"/> is usable as a single file or folder name.</summary>
    /// <param name="name">Candidate name.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidSegment(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c < 32 || Array.IndexOf(InvalidNameChars, c) >= 0)
            {
                return false;
            }
        }

        return !IsReservedDeviceName(name);
    }

    /// <summary>
    /// Cleans a user-typed name like Explorer does: trims surrounding whitespace and trailing dots.
    /// Returns null when nothing valid is left, the name contains characters Windows forbids, or it is a device name.
    /// </summary>
    /// <param name="name">Typed name.</param>
    /// <returns>The cleaned name, or null.</returns>
    public static string? CleanName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string cleaned = name.Trim().TrimEnd('.', ' ');
        return IsValidSegment(cleaned) ? cleaned : null;
    }

    private static bool IsReservedDeviceName(string name)
    {
        var stem = name.AsSpan();
        int dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        stem = stem.TrimEnd(' ');
        foreach (string reserved in ReservedNames)
        {
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string WithPrefix(string ioPath)
        => ioPath.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? ioPath : ExtendedPrefix + ioPath;

    private static bool HasSegmentEndingInSpaceOrDot(string path)
    {
        for (int i = 3; i < path.Length; i++)
        {
            bool segmentEnd = i == path.Length - 1 || path[i + 1] == '\\';
            if (segmentEnd && path[i] is ' ' or '.')
            {
                return true;
            }
        }

        return false;
    }
}
