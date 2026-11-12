using System.Text.Json.Serialization;

namespace Mandrill.Core.State;

/// <summary>Source-generated JSON metadata for all persisted state documents (camelCase, enums as strings).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(FavoritesDocument))]
internal sealed partial class MandrillJsonContext : JsonSerializerContext;
