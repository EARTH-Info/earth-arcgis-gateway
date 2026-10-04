using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Http;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class RateCostPolicyTests
{
    private readonly RateCostPolicy _policy = new();

    [Fact]
    public void CountOnlyQuery_IsCheap()
    {
        var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        { ["returnCountOnly"] = "true" });
        Assert.Equal(1, _policy.GetCost(Resource("query"), q).Units);
    }

    [Fact]
    public void GeometryAndAllFieldsQuery_IsExpensive()
    {
        var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        { ["returnGeometry"] = "true", ["outFields"] = "*" });
        Assert.Equal(5, _policy.GetCost(Resource("query"), q).Units);
    }

    private static ArcGisResource Resource(string operation) =>
        new("Land/Parcels/FeatureServer", "Land/Parcels", "FeatureServer", 0, operation,
            "/arcgis/rest/services/Land/Parcels/FeatureServer/0/" + operation);
}
