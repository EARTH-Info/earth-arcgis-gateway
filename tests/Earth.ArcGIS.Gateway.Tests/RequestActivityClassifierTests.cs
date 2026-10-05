using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Http;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class RequestActivityClassifierTests
{
    private readonly RequestActivityClassifier classifier =
        new(new GisCostOptions());

    [Fact]
    public void OmittedReturnGeometry_IsClassifiedAsGeometryQuery()
    {
        var activity = classifier.Classify(
            Resource("query"),
            Query(("outFields", "OBJECTID,LOTNO")));

        Assert.True(activity.ReturnsGeometry);
        Assert.Equal("query_geometry", activity.Class);
        Assert.Equal(3, activity.CostUnits);
    }

    [Fact]
    public void ExplicitReturnGeometryFalse_IsAttributeQuery()
    {
        var activity = classifier.Classify(
            Resource("query"),
            Query(("returnGeometry", "false"), ("outFields", "OBJECTID")));

        Assert.False(activity.ReturnsGeometry);
        Assert.Equal("query_attribute", activity.Class);
        Assert.Equal(2, activity.CostUnits);
    }

    [Theory]
    [InlineData("returnCountOnly", "query_count", 1)]
    [InlineData("returnIdsOnly", "query_ids", 1)]
    [InlineData("returnExtentOnly", "query_extent", 1)]
    public void CheapQueryModes_AreRecognized(
        string parameter,
        string expectedClass,
        int expectedCost)
    {
        var activity = classifier.Classify(
            Resource("query"),
            Query((parameter, "true")));

        Assert.Equal(expectedClass, activity.Class);
        Assert.Equal(expectedCost, activity.CostUnits);
        Assert.False(activity.IsHeavy);
    }

    [Fact]
    public void SpatialAllFieldsPagedQuery_IsHeavyAndExtractionLike()
    {
        var activity = classifier.Classify(
            Resource("query"),
            Query(
                ("geometry", "116,5"),
                ("outFields", "*"),
                ("resultOffset", "2000"),
                ("resultRecordCount", "2000")));

        Assert.True(activity.IsSpatial);
        Assert.True(activity.ReturnsGeometry);
        Assert.True(activity.IsPaged);
        Assert.True(activity.IsHeavy);
        Assert.True(activity.IsExtractionLike);
        Assert.Equal(8, activity.CostUnits);
        Assert.NotNull(activity.QueryFingerprint);
    }

    [Fact]
    public void EquivalentPaginationOffsets_ProduceSameFingerprint()
    {
        var a = classifier.Classify(
            Resource("query"),
            Query(("where", "STATUS='A'"), ("resultOffset", "0"), ("resultRecordCount", "2000")));
        var b = classifier.Classify(
            Resource("query"),
            Query(("where", "STATUS='A'"), ("resultOffset", "4000"), ("resultRecordCount", "2000")));

        Assert.Equal(a.QueryFingerprint, b.QueryFingerprint);
    }

    [Fact]
    public void PostForm_IsClassifiedUsingSameRules()
    {
        var body = Encoding.UTF8.GetBytes(
            "geometry=116%2C5&returnGeometry=true&outFields=*&f=json");

        var activity = classifier.Classify(
            Resource("query"),
            new QueryCollection(),
            body,
            "application/x-www-form-urlencoded; charset=utf-8");

        Assert.True(activity.IsSpatial);
        Assert.True(activity.ReturnsGeometry);
        Assert.Contains("all_fields", activity.Class, StringComparison.Ordinal);
    }

    private static ArcGisResource Resource(string operation) =>
        new(
            "Land/Parcels/FeatureServer",
            "Land/Parcels",
            "FeatureServer",
            0,
            operation,
            "/arcgis/rest/services/Land/Parcels/FeatureServer/0/" + operation);

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(
            pairs.ToDictionary(
                x => x.Key,
                x => new Microsoft.Extensions.Primitives.StringValues(x.Value),
                StringComparer.OrdinalIgnoreCase));
}
