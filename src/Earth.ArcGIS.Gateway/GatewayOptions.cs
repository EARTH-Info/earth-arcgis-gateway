namespace Earth.ArcGIS.Gateway;

public sealed class GatewayOptions
{
    public string ArcGisBaseUrl { get; set; } = "";
    public string CredentialProvider { get; set; } = "federated-11.3";

    public string PortalTokenEndpoint { get; set; } = "";
    public string FederatedServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public string OAuthTokenEndpoint { get; set; } = "";
    public string OAuthClientId { get; set; } = "";
    public string OAuthClientSecret { get; set; } = "";
    public bool OAuthExchangeForFederatedServer { get; set; } = true;

    public int TokenExpirationMinutes { get; set; } = 30;
    public int RefreshSkewSeconds { get; set; } = 120;
    public string[] AllowedPathPrefixes { get; set; } = Array.Empty<string>();
}
