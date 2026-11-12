using System.Text.Json.Serialization.Metadata;

namespace Mandrill.Core.State;

/// <summary>favorites.json (architecture §10, AC-1.28–1.30): saved layouts in bar order.</summary>
/// <param name="Favorites">Favorites in bar order; names are unique (<see cref="Favorite.NameComparer"/>).</param>
public sealed record FavoritesDocument(IReadOnlyList<Favorite> Favorites) : IStateDocument<FavoritesDocument>
{
    /// <summary>Schema version written on save.</summary>
    public static int CurrentVersion => 1;

    /// <summary>Source-generated serialization metadata.</summary>
    public static JsonTypeInfo<FavoritesDocument> TypeInfo => MandrillJsonContext.Default.FavoritesDocument;

    /// <summary>No favorites.</summary>
    /// <returns>A new default document.</returns>
    public static FavoritesDocument CreateDefault() => new([]);

    /// <inheritdoc/>
    public FavoritesDocument Normalize(ICollection<string> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);
        var result = new List<Favorite>();
        var names = new HashSet<string>(Favorite.NameComparer);
        foreach (Favorite? favorite in Favorites ?? [])
        {
            string name = favorite?.Name?.Trim() ?? string.Empty;
            if (favorite is null || name.Length == 0)
            {
                fixes.Add("favorites: removed an entry without a name");
            }
            else if (!names.Add(name))
            {
                fixes.Add($"favorites: removed duplicate '{name}'");
            }
            else
            {
                result.Add(favorite.Normalize(fixes, name));
            }
        }

        return new FavoritesDocument(result);
    }
}

/// <summary>One favorite: layout, per-pane path and tree state, splitter ratios (AC-1.28). View mode and history are not stored.</summary>
/// <param name="Name">Display name (trimmed, non-empty).</param>
/// <param name="Layout">Layout key (<see cref="LayoutKeys"/>).</param>
/// <param name="Panes">Panes in pane order (at most the layout's pane count).</param>
/// <param name="Splitters">Splitter ratios.</param>
public sealed record Favorite(string Name, string Layout, IReadOnlyList<FavoritePane> Panes, SplitterState Splitters)
{
    /// <summary>Name equality for duplicate detection (case-insensitive).</summary>
    public static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Normalizes a favorite; <paramref name="name"/> is the trimmed name. An unknown layout is repaired, never dropped (AC-1.31 spirit).</summary>
    internal Favorite Normalize(ICollection<string> fixes, string name)
    {
        string owner = $"favorites['{name}']";
        string layout = LayoutKeys.IsKnown(Layout) ? Layout : RepairLayout(fixes, owner);
        int paneCount = LayoutKeys.PaneCount(layout);
        var panes = new List<FavoritePane>();
        foreach (FavoritePane? pane in Panes ?? [])
        {
            if (pane is null)
            {
                fixes.Add(owner + ".panes: removed a null entry");
            }
            else if (panes.Count == paneCount)
            {
                fixes.Add($"{owner}.panes: removed entries beyond the {paneCount} panes of layout {layout}");
                break;
            }
            else
            {
                if (pane.Path is null)
                {
                    fixes.Add($"{owner}.panes[{panes.Count}].path: null → ''");
                }

                panes.Add(new FavoritePane(pane.Path ?? string.Empty, pane.TreeVisible));
            }
        }

        SplitterState splitters = Fixes.Check(fixes, owner + ".splitters", Splitters, Splitters is not null, SplitterState.Equal);
        return new Favorite(name, layout, panes, splitters.Normalize(fixes, owner + ".splitters"));
    }

    // Keeps the saved paths: the first-run default layout (AC-1.3b) for the stored pane count, clamped to 1–4.
    private string RepairLayout(ICollection<string> fixes, string owner)
    {
        int count = Math.Clamp(Panes?.Count(p => p is not null) ?? 0, 1, 4);
        return Fixes.Check(fixes, owner + ".layout", Layout, false, LayoutDefaults.Default.ForPaneCount(count));
    }
}

/// <summary>One pane of a favorite.</summary>
/// <param name="Path">Folder; a missing folder falls back to the nearest existing ancestor on restore (AC-1.31).</param>
/// <param name="TreeVisible">Whether the tree panel is expanded.</param>
public sealed record FavoritePane(string Path, bool TreeVisible);
