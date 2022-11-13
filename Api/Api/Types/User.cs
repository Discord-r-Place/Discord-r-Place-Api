using System.Text.Json.Serialization;

namespace Api.Types;

public record User(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("avatar")] string Avatar,
    [property: JsonPropertyName("avatar_decoration")] object AvatarDecoration,
    [property: JsonPropertyName("discriminator")] string Discriminator,
    [property: JsonPropertyName("public_flags")] int PublicFlags,
    [property: JsonPropertyName("flags")] int Flags,
    [property: JsonPropertyName("banner")] object Banner,
    [property: JsonPropertyName("banner_color")] string BannerColor,
    [property: JsonPropertyName("accent_color")] int AccentColor,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("mfa_enabled")] bool MfaEnabled,
    [property: JsonPropertyName("premium_type")] int PremiumType
);

