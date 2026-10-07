using System.Text.Json;

namespace Earth.ArcGIS.Gateway;

internal static class BoundedJsonReader
{
    public static async Task<JsonDocument> ReadAsync(
        Stream stream,
        int maxBytes,
        string invalidJsonMessage,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var remaining = maxBytes + 1 - checked((int)buffer.Length);
            if (remaining <= 0)
                throw new InvalidOperationException(
                    $"JSON response exceeds the configured {maxBytes}-byte limit.");

            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                cancellationToken);

            if (read == 0)
                break;

            await buffer.WriteAsync(
                chunk.AsMemory(0, read),
                cancellationToken);

            if (buffer.Length > maxBytes)
            {
                throw new InvalidOperationException(
                    $"JSON response exceeds the configured {maxBytes}-byte limit.");
            }
        }

        buffer.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(
                buffer,
                cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(invalidJsonMessage, ex);
        }
    }
}
