using System.Runtime.InteropServices;
using System.Text;
using Mandrill.Core.IO;
using Windows.Win32;

namespace Mandrill.Core.Shell;

/// <summary>What a drop does (AC-3.18).</summary>
public enum DropAction
{
    /// <summary>Nothing (rejected, or no items).</summary>
    None,

    /// <summary>Copy the items into the target folder.</summary>
    Copy,

    /// <summary>Move the items into the target folder.</summary>
    Move,
}

/// <summary>Whether a drop may happen (AC-3.19).</summary>
public enum DropCheck
{
    /// <summary>The drop may happen.</summary>
    Ok,

    /// <summary>No file-system items (virtual items, an empty list).</summary>
    NoItems,

    /// <summary>A folder dropped onto itself.</summary>
    OntoItself,

    /// <summary>A folder dropped into one of its own subfolders.</summary>
    IntoDescendant,

    /// <summary>Every item is already in the target folder: nothing to do.</summary>
    SameFolder,
}

/// <summary>The effects a drag source allows (its DataPackageOperation Copy and Move; Link is not offered).</summary>
[Flags]
public enum DropEffects
{
    /// <summary>Neither copy nor move.</summary>
    None = 0,

    /// <summary>The source allows a copy.</summary>
    Copy = 1,

    /// <summary>The source allows a move.</summary>
    Move = 2,
}

/// <summary>What a drop does, decided the same way while hovering (DragOver) and on the drop (WP-3.4 QA).</summary>
/// <param name="Action">Copy or Move; None when the drop is refused, has nothing to do, or the source allows neither.</param>
/// <param name="Check">Why: <see cref="DropCheck.Ok"/>, or the refusal the caption names (AC-3.19).</param>
/// <param name="Sources">The items to copy or move: a move leaves out items already in the target (QA m3, like Paste).</param>
/// <param name="Reported">
/// The effect told to the drag source after the drop: Mandrill runs a move itself, so only its own drags hear Move (§7, N1:
/// an external source must not delete as well).
/// </param>
public sealed record DropOutcome(DropAction Action, DropCheck Check, IReadOnlyList<string> Sources, DropEffects Reported);

/// <summary>
/// The rules of drag and drop (WP-3.4, architecture §7, AC-3.17–3.21), pure so they are tested without a drag: reading a
/// FileDrop (CF_HDROP) block, Explorer's default effect, and what may not be dropped where.
/// </summary>
public static class DragDropRules
{
    /// <summary>The private format a Mandrill drag carries: the full paths, one per line (read before any shell format).</summary>
    public const string PathsFormat = "Mandrill.Paths";

    /// <summary>Reads the paths of a FileDrop (CF_HDROP) block: a DROPFILES header, then NUL-separated paths.</summary>
    /// <param name="data">The block.</param>
    /// <returns>The paths; empty for a malformed block.</returns>
    public static IReadOnlyList<string> ParseFileDrop(ReadOnlySpan<byte> data)
    {
        const int HeaderSize = 20; // DWORD pFiles, POINT pt, BOOL fNC, BOOL fWide
        if (data.Length < HeaderSize)
        {
            return [];
        }

        int offset = BitConverter.ToInt32(data[..4]);
        bool wide = BitConverter.ToInt32(data.Slice(16, 4)) != 0;
        if (offset < HeaderSize || offset >= data.Length)
        {
            return [];
        }

        var list = new List<string>();
        if (wide)
        {
            var text = MemoryMarshal.Cast<byte, char>(data[offset..(offset + ((data.Length - offset) / 2 * 2))]);
            while (text.Length > 0 && text[0] != '\0')
            {
                int end = text.IndexOf('\0');
                if (end < 0)
                {
                    break; // not terminated: malformed
                }

                list.Add(new string(text[..end]));
                text = text[(end + 1)..];
            }
        }
        else
        {
            // The system ANSI code page (GetACP, e.g. 949), not the user's culture (reviewer m1).
            var ansi = AnsiEncoding((int)PInvoke.GetACP());
            var bytes = data[offset..];
            while (bytes.Length > 0 && bytes[0] != 0)
            {
                int end = bytes.IndexOf((byte)0);
                if (end < 0)
                {
                    break;
                }

                list.Add(ansi.GetString(bytes[..end]));
                bytes = bytes[(end + 1)..];
            }
        }

        return list;
    }

    /// <summary>
    /// The encoding of an ANSI FileDrop for <paramref name="codePage"/>: the code-pages provider for DBCS pages such as 949,
    /// .NET's built-in UTF-8 when the system runs "Beta: Use Unicode UTF-8" (65001, which the provider does not serve, QA
    /// m4), Latin-1 when neither knows the page.
    /// </summary>
    /// <param name="codePage">The system ANSI code page (GetACP).</param>
    /// <returns>The encoding.</returns>
    public static Encoding AnsiEncoding(int codePage)
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>
    /// What dropping <paramref name="sources"/> into <paramref name="target"/> does (AC-3.18, 3.19, 3.21): refused onto itself
    /// or into a descendant; into the items' own folder nothing, or with Ctrl a copy ("name - Copy", Explorer); otherwise
    /// Explorer's effect limited to what the source allows (QA m2: a held modifier the source refuses refuses the drop; without
    /// one the other allowed effect is taken). A source that reports neither effect is taken as allowing both (lead: some
    /// sources report None although both work); only an explicit Copy-only or Move-only restricts.
    /// </summary>
    /// <param name="sources">All dragged items (canonical paths).</param>
    /// <param name="target">The target folder.</param>
    /// <param name="ctrl">Ctrl is held.</param>
    /// <param name="shift">Shift is held.</param>
    /// <param name="own">The drag comes from Mandrill (it may hear Move after the drop).</param>
    /// <param name="allowed">The effects the source allows.</param>
    /// <returns>The outcome.</returns>
    public static DropOutcome Decide(IReadOnlyList<string> sources, string target, bool ctrl, bool shift, bool own, DropEffects allowed)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(target);
        if (allowed == DropEffects.None)
        {
            allowed = DropEffects.Copy | DropEffects.Move;
        }

        var check = Validate(sources, target);
        if (check == DropCheck.SameFolder)
        {
            return ctrl && allowed.HasFlag(DropEffects.Copy)
                ? new DropOutcome(DropAction.Copy, check, sources, DropEffects.Copy)
                : new DropOutcome(DropAction.None, check, [], DropEffects.None);
        }

        if (check != DropCheck.Ok)
        {
            return new DropOutcome(DropAction.None, check, [], DropEffects.None);
        }

        var action = DecideAction(sources[0], target, ctrl, shift) switch
        {
            DropAction.Move when allowed.HasFlag(DropEffects.Move) => DropAction.Move,
            DropAction.Move when !shift && allowed.HasFlag(DropEffects.Copy) => DropAction.Copy,
            DropAction.Copy when allowed.HasFlag(DropEffects.Copy) => DropAction.Copy,
            DropAction.Copy when !ctrl && allowed.HasFlag(DropEffects.Move) => DropAction.Move,
            _ => DropAction.None,
        };
        return action switch
        {
            DropAction.Copy => new DropOutcome(action, check, sources, DropEffects.Copy),
            DropAction.Move => new DropOutcome(
                action,
                check,
                [.. sources.Where(s => PathUtil.Parent(s) is not { } parent || !PathUtil.PathEquals(parent, target))],
                own ? DropEffects.Move : DropEffects.None),
            _ => new DropOutcome(DropAction.None, check, [], DropEffects.None),
        };
    }

    /// <summary>
    /// Explorer's effect (AC-3.18): Ctrl copies, Shift moves (Ctrl wins when both are held, as in Explorer's Ctrl+Shift =
    /// link, which Mandrill does not offer), otherwise a move on the same volume and a copy across volumes.
    /// </summary>
    /// <param name="firstSource">The first dragged item (a normalized drive or UNC path, no \\?\ prefix).</param>
    /// <param name="target">The target folder (normalized like <paramref name="firstSource"/>).</param>
    /// <param name="ctrl">Ctrl is held.</param>
    /// <param name="shift">Shift is held.</param>
    /// <returns>Copy or Move.</returns>
    public static DropAction DecideAction(string firstSource, string target, bool ctrl, bool shift)
    {
        ArgumentNullException.ThrowIfNull(firstSource);
        ArgumentNullException.ThrowIfNull(target);
        if (ctrl)
        {
            return DropAction.Copy;
        }

        if (shift)
        {
            return DropAction.Move;
        }

        return string.Equals(VolumeOf(firstSource), VolumeOf(target), StringComparison.OrdinalIgnoreCase) ? DropAction.Move : DropAction.Copy;
    }

    /// <summary>Whether <paramref name="sources"/> may be dropped into <paramref name="target"/> (AC-3.19).</summary>
    /// <param name="sources">The dragged items.</param>
    /// <param name="target">The target folder.</param>
    /// <returns>The check.</returns>
    public static DropCheck Validate(IReadOnlyList<string> sources, string target)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(target);
        if (sources.Count == 0)
        {
            return DropCheck.NoItems;
        }

        // QA n2: "C:\a\docs\" is the folder "C:\a\docs" (a root keeps its separator).
        target = Path.TrimEndingDirectorySeparator(target);
        sources = [.. sources.Select(s => Path.TrimEndingDirectorySeparator(s))];
        foreach (string source in sources)
        {
            if (PathUtil.PathEquals(source, target))
            {
                return DropCheck.OntoItself;
            }

            if (PathUtil.IsSameOrUnder(target, source))
            {
                return DropCheck.IntoDescendant;
            }
        }

        return sources.All(s => PathUtil.Parent(s) is { } parent && PathUtil.PathEquals(parent, target)) ? DropCheck.SameFolder : DropCheck.Ok;
    }

    /// <summary>The paths of a <see cref="PathsFormat"/> text.</summary>
    /// <param name="text">One path per line.</param>
    /// <returns>The paths.</returns>
    public static IReadOnlyList<string> ParsePaths(string? text)
        => text is null ? [] : [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l))]; // no trim: names may end in a space (E-1.11)

    // "C:\" for "C:\x\y"; "\\server\share\" for UNC. Volumes of mounted folders and subst drives are not resolved (rare).
    private static string VolumeOf(string path) => Path.GetPathRoot(path) ?? string.Empty;
}
