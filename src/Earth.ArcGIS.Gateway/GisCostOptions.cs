namespace Earth.ArcGIS.Gateway;

public sealed class GisCostOptions
{
    public string ProfileVersion { get; set; } = "gis-cost-v1";
    public int Metadata { get; set; } = 1;
    public int Tile { get; set; } = 1;
    public int SceneNode { get; set; } = 1;
    public int CountOnly { get; set; } = 1;
    public int IdsOnly { get; set; } = 1;
    public int ExtentOnly { get; set; } = 1;
    public int AttributeQuery { get; set; } = 2;
    public int GeometryQuery { get; set; } = 3;
    public int SpatialSurcharge { get; set; } = 2;
    public int AllFieldsSurcharge { get; set; } = 1;
    public int PaginationSurcharge { get; set; } = 2;
    public int Identify { get; set; } = 3;
    public int Find { get; set; } = 3;
    public int Attachment { get; set; } = 3;
    public int Export { get; set; } = 8;
    public int Fallback { get; set; } = 2;
    public int HeavyThreshold { get; set; } = 6;

    public void Validate()
    {
        var values = new[]
        {
            Metadata, Tile, SceneNode, CountOnly, IdsOnly, ExtentOnly,
            AttributeQuery, GeometryQuery, SpatialSurcharge, AllFieldsSurcharge,
            PaginationSurcharge, Identify, Find, Attachment, Export, Fallback,
            HeavyThreshold
        };

        if (values.Any(x => x <= 0))
            throw new InvalidOperationException("All GIS cost values must be positive.");

        if (string.IsNullOrWhiteSpace(ProfileVersion))
            throw new InvalidOperationException("GisCost:ProfileVersion is required.");
    }
}
