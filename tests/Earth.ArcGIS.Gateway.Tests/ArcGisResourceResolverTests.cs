using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ArcGisResourceResolverTests
{
    private readonly ArcGisResourceResolver _resolver = new();

    [Fact]
    public void Resolves_FeatureLayerQuery()
    {
        Assert.True(_resolver.TryResolve(
            "arcgis/rest/services/Land/Parcels/FeatureServer/0/query",
            out var resource));

        Assert.Equal("Land/Parcels", resource.ServiceName);
        Assert.Equal("FeatureServer", resource.ServiceType);
        Assert.Equal(0, resource.LayerId);
        Assert.Equal("query", resource.Operation);
        Assert.Empty(resource.OperationTail ?? []);
    }

    [Fact]
    public void CanonicalizesOperationAndServiceTypeCasing()
    {
        Assert.True(_resolver.TryResolve(
            "arcgis/rest/services/Land/Parcels/featureserver/0/QuErY",
            out var resource));

        Assert.Equal("FeatureServer", resource.ServiceType);
        Assert.Equal("query", resource.Operation);
        Assert.Equal(
            "/arcgis/rest/services/Land/Parcels/FeatureServer/0/query",
            resource.CanonicalPath);
    }

    [Fact]
    public void ResolvesAspNetCatchAllRouteRelativePathToSameCanonicalResource()
    {
        Assert.True(_resolver.TryResolve(
            "rest/services/Land/Parcels/FeatureServer/0/query",
            out var resource));

        Assert.Equal("Land/Parcels", resource.ServiceName);
        Assert.Equal("FeatureServer", resource.ServiceType);
        Assert.Equal(0, resource.LayerId);
        Assert.Equal("query", resource.Operation);
        Assert.Equal(
            "/arcgis/rest/services/Land/Parcels/FeatureServer/0/query",
            resource.CanonicalPath);
    }

    [Theory]
    [InlineData("arcgis/rest/services/Land/Parcels/FeatureServer/0/attachments/12", "FeatureServer", 0, "attachments", "12")]
    [InlineData("arcgis/rest/services/Basemap/Base/VectorTileServer/tile/10/315/421.pbf", "VectorTileServer", null, "tile", "10|315|421.pbf")]
    [InlineData("arcgis/rest/services/Imagery/Orthophoto/ImageServer/exportImage", "ImageServer", null, "exportimage", "")]
    [InlineData("arcgis/rest/services/Buildings/Main/SceneServer/layers/3/nodes/root", "SceneServer", 3, "nodes", "root")]
    public void ResolvesSupportedReadShapes(
        string path,
        string serviceType,
        int? layerId,
        string operation,
        string tail)
    {
        Assert.True(_resolver.TryResolve(path, out var resource));
        Assert.Equal(serviceType, resource.ServiceType);
        Assert.Equal(layerId, resource.LayerId);
        Assert.Equal(operation, resource.Operation);
        Assert.Equal(
            tail,
            string.Join('|', resource.OperationTail ?? []));
    }

    [Fact]
    public void AllowsSafeEncodedServiceNameAndCanonicalizesIt()
    {
        Assert.True(_resolver.TryResolve(
            "arcgis/rest/services/Land/My%20Parcels/FeatureServer/0/query",
            out var resource));

        Assert.Equal("Land/My Parcels", resource.ServiceName);
        Assert.Equal(
            "/arcgis/rest/services/Land/My%20Parcels/FeatureServer/0/query",
            resource.CanonicalPath);
    }

    [Theory]
    [InlineData("arcgis/rest/services/../admin")]
    [InlineData("arcgis/rest/services/%2e%2e/admin")]
    [InlineData("arcgis/rest/services/%252e%252e/admin")]
    [InlineData("arcgis/rest/services/Land%5cAdmin/FeatureServer")]
    [InlineData("arcgis/rest/services/Land%25Admin/FeatureServer")]
    [InlineData("arcgis/rest/services/Land%ZZAdmin/FeatureServer")]
    [InlineData("arcgis/rest/services/Land%2fAdmin/FeatureServer")]
    [InlineData("arcgis/rest/services/Land%252fAdmin/FeatureServer")]
    [InlineData("arcgis//rest/services/Land/Parcels/FeatureServer/0/query")]
    [InlineData("arcgis\\rest\\services\\Land")]
    [InlineData("arcgis/rest/services/http:evil/FeatureServer")]
    [InlineData("arcgis/rest/services/Land/Parcels/FeatureServer/-1/query")]
    [InlineData("sharing/rest/search")]
    public void Rejects_NonCanonicalOrUnsafePaths(string path)
    {
        Assert.False(_resolver.TryResolve(path, out _));
    }
}
