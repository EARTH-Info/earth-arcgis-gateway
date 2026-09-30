using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ArcGisResourceResolverTests
{
    private readonly ArcGisResourceResolver _resolver = new();

    [Fact]
    public void Resolves_FeatureLayerQuery()
    {
        Assert.True(_resolver.TryResolve("arcgis/rest/services/Land/Parcels/FeatureServer/0/query", out var r));
        Assert.Equal("Land/Parcels", r.ServiceName);
        Assert.Equal("FeatureServer", r.ServiceType);
        Assert.Equal(0, r.LayerId);
        Assert.Equal("query", r.Operation);
    }

    [Theory]
    [InlineData("arcgis/rest/services/../admin")]
    [InlineData("arcgis/rest/services/%2e%2e/admin")]
    [InlineData("arcgis//rest/services/Land/Parcels/FeatureServer/0/query")]
    [InlineData("arcgis\\rest\\services\\Land")]
    [InlineData("sharing/rest/search")]
    public void Rejects_NonCanonicalOrUnsafePaths(string path)
    {
        Assert.False(_resolver.TryResolve(path, out _));
    }
}
