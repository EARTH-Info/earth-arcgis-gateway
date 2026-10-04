namespace Earth.ArcGIS.Gateway;

public sealed record ArcGisResource(
    string ServicePath,
    string ServiceName,
    string ServiceType,
    int? LayerId,
    string Operation,
    string CanonicalPath,
    IReadOnlyList<string>? OperationTail = null);

public interface IArcGisResourceResolver
{
    bool TryResolve(string? rawPath, out ArcGisResource resource);
}

public sealed class ArcGisResourceResolver : IArcGisResourceResolver
{
    private static readonly HashSet<string> ServiceTypes =
        new(
        [
            "FeatureServer",
            "MapServer",
            "SceneServer",
            "ImageServer",
            "VectorTileServer"
        ],
        StringComparer.OrdinalIgnoreCase);

    public bool TryResolve(string? rawPath, out ArcGisResource resource)
    {
        resource = null!;

        if (string.IsNullOrWhiteSpace(rawPath) ||
            rawPath.Contains('\\') ||
            rawPath.Contains("..", StringComparison.Ordinal) ||
            rawPath.Contains("//", StringComparison.Ordinal))
            return false;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(rawPath);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!string.Equals(decoded, rawPath, StringComparison.Ordinal) ||
            decoded.Contains("..", StringComparison.Ordinal) ||
            decoded.Contains('\\') ||
            decoded.Contains("//", StringComparison.Ordinal))
            return false;

        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 ||
            !segments[0].Equals("arcgis", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("rest", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("services", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(IsUnsafeSegment))
            return false;

        var typeIndex = Array.FindIndex(
            segments,
            3,
            segment => ServiceTypes.Contains(segment));

        if (typeIndex < 4)
            return false;

        var serviceType = segments[typeIndex];
        var serviceName = string.Join('/', segments[3..typeIndex]);
        if (string.IsNullOrWhiteSpace(serviceName))
            return false;

        int? layerId = null;
        var cursor = typeIndex + 1;

        if (serviceType.Equals("SceneServer", StringComparison.OrdinalIgnoreCase) &&
            cursor + 1 < segments.Length &&
            segments[cursor].Equals("layers", StringComparison.OrdinalIgnoreCase) &&
            TryLayerId(segments[cursor + 1], out var sceneLayerId))
        {
            layerId = sceneLayerId;
            cursor += 2;
        }
        else if (cursor < segments.Length &&
                 TryLayerId(segments[cursor], out var parsedLayerId))
        {
            layerId = parsedLayerId;
            cursor++;
        }

        var operation = cursor < segments.Length
            ? segments[cursor]
            : "metadata";

        var tail = cursor + 1 < segments.Length
            ? segments[(cursor + 1)..]
            : Array.Empty<string>();

        var canonical = "/" + string.Join('/', segments);

        resource = new ArcGisResource(
            string.Join('/', segments[3..(typeIndex + 1)]),
            serviceName,
            serviceType,
            layerId,
            operation,
            canonical,
            tail);

        return true;
    }

    private static bool TryLayerId(string value, out int layerId) =>
        int.TryParse(value, out layerId) && layerId >= 0;

    private static bool IsUnsafeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment is "." or ".." ||
            segment.Contains(':') ||
            segment.Contains('?') ||
            segment.Contains('#'))
            return true;

        return segment.Any(char.IsControl);
    }
}
