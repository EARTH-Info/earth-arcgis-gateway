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


    [Fact]
    public void SpatialAllFieldsPost_IsExpensive()
    {
        var q = new QueryCollection();
        var body = System.Text.Encoding.UTF8.GetBytes(
            "geometry=1%2C2&returnGeometry=true&outFields=*&f=json");

        var cost = _policy.GetCost(
            Resource("query"),
            q,
            body,
            "application/x-www-form-urlencoded");

        Assert.Equal(5, cost.Units);
        Assert.Equal("query_geometry_all_fields", cost.Reason);
    }

    [Fact]
    public void PagedPostAddsCost()
    {
        var q = new QueryCollection();
        var body = System.Text.Encoding.UTF8.GetBytes(
            "outFields=OBJECTID&resultOffset=2000&resultRecordCount=2000");

        var cost = _policy.GetCost(
            Resource("query"),
            q,
            body,
            "application/x-www-form-urlencoded; charset=utf-8");

        Assert.Equal(3, cost.Units);
        Assert.Equal("query_paged", cost.Reason);
    }

    private static ArcGisResource Resource(string operation) =>
        new("Land/Parcels/FeatureServer", "Land/Parcels", "FeatureServer", 0, operation,
            "/arcgis/rest/services/Land/Parcels/FeatureServer/0/" + operation);
}
