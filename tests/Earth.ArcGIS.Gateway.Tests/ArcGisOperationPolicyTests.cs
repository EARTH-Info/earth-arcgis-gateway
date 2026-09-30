using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ArcGisOperationPolicyTests
{
    private readonly ArcGisOperationPolicy _policy = new();

    [Theory]
    [InlineData("query", "GET", true)]
    [InlineData("query", "POST", true)]
    [InlineData("metadata", "GET", true)]
    [InlineData("applyEdits", "POST", false)]
    [InlineData("deleteFeatures", "POST", false)]
    [InlineData("unknownOperation", "GET", false)]
    [InlineData("query", "DELETE", false)]
    public void ExplicitAllowListIsFailClosed(string operation, string method, bool expected)
    {
        var resource = new ArcGisResource("Land/Parcels/FeatureServer", "Land/Parcels",
            "FeatureServer", 0, operation, "/arcgis/rest/services/Land/Parcels/FeatureServer/0/" + operation);

        Assert.Equal(expected, _policy.IsAllowed(resource, method));
    }
}
