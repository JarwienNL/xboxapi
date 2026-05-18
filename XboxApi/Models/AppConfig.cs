namespace XboxApi.Models;
public class AppConfig
{
    public string? IgdbClientId { get; set; }
    public string? IgdbClientSecret { get; set; }
    public bool EnableCoverArt => !string.IsNullOrWhiteSpace(IgdbClientId) && !string.IsNullOrWhiteSpace(IgdbClientSecret);
}
