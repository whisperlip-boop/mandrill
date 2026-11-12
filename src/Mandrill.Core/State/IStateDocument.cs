using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Mandrill.Core.State;

/// <summary>A persisted state document (settings, session, favorites) with its schema facts and value normalization.</summary>
/// <typeparam name="T">The document type itself.</typeparam>
public interface IStateDocument<T>
    where T : class, IStateDocument<T>
{
    /// <summary>Schema version written on save.</summary>
    static abstract int CurrentVersion { get; }

    /// <summary>Source-generated serialization metadata.</summary>
    static abstract JsonTypeInfo<T> TypeInfo { get; }

    /// <summary>Upgrades an older document (node, fromVersion); null = no migration, so older files count as corrupt.</summary>
    static virtual Func<JsonObject, int, JsonObject>? Migrate => null;

    /// <summary>Document used when the file is missing or corrupt (first-run defaults).</summary>
    /// <returns>A new default document.</returns>
    static abstract T CreateDefault();

    /// <summary>
    /// Returns a copy in which missing or out-of-range values are replaced by defaults, so one bad value does not discard
    /// the whole file. Structurally invalid JSON is still rejected by <see cref="JsonStore{T}"/> (.bak + defaults).
    /// </summary>
    /// <param name="fixes">Receives one log line per replaced value.</param>
    /// <returns>The normalized document.</returns>
    T Normalize(ICollection<string> fixes);
}
