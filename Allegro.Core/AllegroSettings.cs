namespace Allegro.Core;

public class AllegroSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Currency { get; set; } = "PLN";
    
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime AccessTokenExpiresUtc { get; set; } = DateTime.MinValue;

    public bool IsConnected => !string.IsNullOrEmpty(RefreshToken);
}
