using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ArcGisOperationPolicyTests
{
    private readonly ArcGisOperationPolicy _policy = new();

    [Theory]
    [InlineData("query", "GET", true)]
    [InlineData("query", "POST", true)]
    [InlineData("metadata", "GET", true)]
    [InlineData("queryAttachments", "GET", true)]
    [InlineData("exportImage", "POST", true)]
    [InlineData("applyEdits", "POST", false)]
    [InlineData("addFeatures", "POST", false)]
    [InlineData("updateFeatures", "POST", false)]
    [InlineData("deleteFeatures", "POST", false)]
    [InlineData("upload", "POST", false)]
    [InlineData("unknownOperation", "GET", false)]
    [InlineData("query", "DELETE", false)]
    public void ExplicitAllowListIsFailClosed(
        string operation,
        string method,
        bool expected)
    {
        var resource = new ArcGisResource(
            "Land/Parcels/FeatureServer",
            "Land/Parcels",
            "FeatureServer",
            0,
            operation,
            "/arcgis/rest/services/Land/Parcels/FeatureServer/0/" + operation);

        Assert.Equal(expected, _policy.IsAllowed(resource, method));
    }

    [Fact]
    public void AttachmentDownloadRequiresGetAndNumericId()
    {
        var valid = Resource("FeatureServer", 0, "attachments", ["12"]);
        var invalidId = Resource("FeatureServer", 0, "attachments", ["not-an-id"]);

        Assert.True(_policy.IsAllowed(valid, "GET"));
        Assert.False(_policy.IsAllowed(valid, "POST"));
        Assert.False(_policy.IsAllowed(invalidId, "GET"));
    }

    [Fact]
    public void VectorTileRequiresExactlyThreeSafeCoordinates()
    {
        var valid = Resource("VectorTileServer", null, "tile", ["10", "315", "421.pbf"]);
        var tooDeep = Resource("VectorTileServer", null, "tile", ["10", "315", "421", "extra"]);
        var traversal = Resource("VectorTileServer", null, "tile", ["10", "..", "421.pbf"]);

        Assert.True(_policy.IsAllowed(valid, "GET"));
        Assert.False(_policy.IsAllowed(valid, "POST"));
        Assert.False(_policy.IsAllowed(tooDeep, "GET"));
        Assert.False(_policy.IsAllowed(traversal, "GET"));
    }

    [Fact]
    public void SceneNodesRequireSceneLayerAndGet()
    {
        var valid = Resource("SceneServer", 3, "nodes", ["root"]);
        var noLayer = Resource("SceneServer", null, "nodes", ["root"]);

        Assert.True(_policy.IsAllowed(valid, "GET"));
        Assert.False(_policy.IsAllowed(valid, "POST"));
        Assert.False(_policy.IsAllowed(noLayer, "GET"));
    }

    private static ArcGisResource Resource(
        string serviceType,
        int? layerId,
        string operation,
        IReadOnlyList<string>? tail = null) =>
        new(
            $"Land/Parcels/{serviceType}",
            "Land/Parcels",
            serviceType,
            layerId,
            operation,
            $"/arcgis/rest/services/Land/Parcels/{serviceType}/{operation}",
            tail);
}
