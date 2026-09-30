namespace Earth.ArcGIS.Gateway;

public sealed class GatewayOptions
{
    public string ArcGisBaseUrl { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int TokenExpirationMinutes { get; set; } = 30;
    public string[] AllowedPathPrefixes { get; set; } = Array.Empty<string>();
}