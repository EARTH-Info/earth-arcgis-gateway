using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class GatewayOptionsTests
{
    [Fact]
    public void Defaults_AreFailSafeForResourceAllowList()
    {
        var options = new GatewayOptions();
        Assert.Empty(options.AllowedPathPrefixes);
    }

    [Theory]
    [InlineData("https://gis.example.gov/arcgis")]
    [InlineData("https://user:pass@gis.example.gov")]
    [InlineData("https://gis.example.gov?x=1")]
    [InlineData("http://gis.example.gov")]
    public void ArcGisBaseUrlMustBeHttpsOrigin(string baseUrl)
    {
        var options = ValidFederatedOptions();
        options.ArcGisBaseUrl = baseUrl;

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void ValidFederatedConfigurationPassesValidation()
    {
        var result = new GatewayOptionsValidator().Validate(
            null,
            ValidFederatedOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void RefreshSkewMustBeLessThanRequestedFederatedTokenLifetime()
    {
        var options = ValidFederatedOptions();
        options.TokenExpirationMinutes = 1;
        options.RefreshSkewSeconds = 60;

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void CanonicalEncodedSpaceInAllowedPrefixIsAccepted()
    {
        var options = ValidFederatedOptions();
        options.AllowedPathPrefixes =
            ["/arcgis/rest/services/Land/My%20Parcels/"];

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("/arcgis/rest/services/Land%2FAdmin/")]
    [InlineData("/arcgis/rest/services/Land%252FAdmin/")]
    [InlineData("/arcgis/rest/services/%252e%252e/Admin/")]
    [InlineData("/arcgis/rest/services/Land%25Admin/")]
    [InlineData("/arcgis/rest/services/http%3Aevil/")]
    public void UnsafeOrDoubleEncodedAllowedPrefixIsRejected(string prefix)
    {
        var options = ValidFederatedOptions();
        options.AllowedPathPrefixes = [prefix];

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    private static GatewayOptions ValidFederatedOptions() =>
        new()
        {
            ArcGisBaseUrl = "https://gis.example.gov",
            CredentialProvider = "federated-11.3",
            PortalTokenEndpoint =
                "https://portal.example.gov/portal/sharing/rest/generateToken",
            FederatedServerUrl =
                "https://gis.example.gov/arcgis",
            Username = "gateway-user",
            Password = "not-a-real-secret",
            AllowedPathPrefixes =
                ["/arcgis/rest/services/Approved/"]
        };
}
