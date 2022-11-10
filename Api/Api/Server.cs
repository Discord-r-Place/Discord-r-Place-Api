using System.Text.Json.Serialization;

namespace Api;

public record Server(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("owner")] bool Owner,
    [property: JsonPropertyName("permissions")] object Permissions,
    [property: JsonPropertyName("features")] IReadOnlyList<string> Features,
    [property: JsonPropertyName("permissions_new")] string PermissionsNew
);