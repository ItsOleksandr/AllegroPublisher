namespace Allegro.Core;

public class AllegroSettings
{
    // From your Allegro Developer app (https://apps.developer.allegro.pl).
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    // Currency used when pushing new prices.
    public string Currency { get; set; } = "PLN";

    // Filled in by the OAuth2 device flow.
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime AccessTokenExpiresUtc { get; set; } = DateTime.MinValue;

    public bool IsConnected => !string.IsNullOrEmpty(RefreshToken);
}
