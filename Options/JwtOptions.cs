namespace HAShop.Api.Options;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Key { get; set; } = "";
    // TTL mặc định (phút) nếu SP không trả expiresAt
    public int AccessTokenMinutes { get; set; } = 60 * 24 * 30;
}
