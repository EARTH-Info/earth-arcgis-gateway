namespace Earth.ArcGIS.Gateway;

public sealed record RateCost(int Units, string Reason);

public interface IRateCostPolicy
{
    RateCost GetCost(ArcGisResource resource, IQueryCollection query);
}

public sealed class RateCostPolicy : IRateCostPolicy
{
    public RateCost GetCost(ArcGisResource resource, IQueryCollection query)
    {
        if (resource.Operation.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            return new(1, "metadata");

        if (resource.Operation.Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTrue(query, "returnCountOnly")) return new(1, "count");
            var geometry = query.ContainsKey("geometry") || IsTrue(query, "returnGeometry");
            var allFields = query.TryGetValue("outFields", out var fields) && fields.Any(x => x == "*");
            if (geometry && allFields) return new(5, "query_geometry_all_fields");
            if (geometry) return new(4, "query_geometry");
            if (allFields) return new(3, "query_all_fields");
            return new(2, "query");
        }

        if (resource.Operation.Equals("identify", StringComparison.OrdinalIgnoreCase) ||
            resource.Operation.Equals("find", StringComparison.OrdinalIgnoreCase))
            return new(3, resource.Operation.ToLowerInvariant());

        return new(2, resource.Operation.ToLowerInvariant());
    }

    private static bool IsTrue(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var value) &&
        value.Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase));
}
