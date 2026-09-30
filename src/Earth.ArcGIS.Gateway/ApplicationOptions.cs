namespace Earth.ArcGIS.Gateway;

public sealed class ApplicationOptions
{
    public Dictionary<string, ApplicationRegistration> Registrations { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class ApplicationRegistration
{
    public string[] Audiences { get; set; } = Array.Empty<string>();
    public string[] ClientIds { get; set; } = Array.Empty<string>();
    public string? AccessApiBaseUrl { get; set; }
}
