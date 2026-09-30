namespace Earth.ArcGIS.Gateway;

public sealed record ArcGisResource(
    string ServicePath,
    string ServiceName,
    string ServiceType,
    int? LayerId,
    string Operation,
    string CanonicalPath);

public interface IArcGisResourceResolver
{
    bool TryResolve(string? rawPath, out ArcGisResource resource);
}

public sealed class ArcGisResourceResolver : IArcGisResourceResolver
{
    private static readonly HashSet<string> ServiceTypes =
        new(["FeatureServer", "MapServer", "SceneServer"], StringComparer.OrdinalIgnoreCase);

    public bool TryResolve(string? rawPath, out ArcGisResource resource)
    {
        resource = null!;
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains('\\') || rawPath.Contains("..", StringComparison.Ordinal))
            return false;

        string decoded;
        try { decoded = Uri.UnescapeDataString(rawPath); }
        catch (UriFormatException) { return false; }

        if (!string.Equals(decoded, rawPath, StringComparison.Ordinal) ||
            decoded.Contains("..", StringComparison.Ordinal) ||
            decoded.Contains('\\') ||
            decoded.Contains("//", StringComparison.Ordinal))
            return false;

        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 ||
            !segments[0].Equals("arcgis", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("rest", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("services", StringComparison.OrdinalIgnoreCase))
            return false;

        var typeIndex = Array.FindIndex(segments, 3, x => ServiceTypes.Contains(x));
        if (typeIndex < 4) return false;

        var serviceName = string.Join('/', segments[3..typeIndex]);
        int? layerId = null;
        var cursor = typeIndex + 1;
        if (cursor < segments.Length && int.TryParse(segments[cursor], out var parsedLayer))
        {
            layerId = parsedLayer;
            cursor++;
        }

        var operation = cursor < segments.Length ? segments[cursor] : "metadata";
        if (cursor + 1 < segments.Length) return false;

        var canonical = "/" + string.Join('/', segments);
        resource = new ArcGisResource(
            string.Join('/', segments[3..(typeIndex + 1)]),
            serviceName,
            segments[typeIndex],
            layerId,
            operation,
            canonical);
        return true;
    }
}
