namespace Earth.ArcGIS.Gateway;

public sealed record RequestActivity(
    string Class,
    int CostUnits,
    bool IsSpatial,
    bool ReturnsGeometry,
    bool IsPaged,
    bool IsHeavy,
    bool IsExtractionLike,
    string? QueryFingerprint);
