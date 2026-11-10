namespace ControllerHelper.Models;

// The native payload remains Windows-specific. Its ABI, schema and display bindings are explicit.
public sealed class DisplayProfile
{
    public int Version { get; set; }
    public int PathSize { get; set; }
    public int ModeSize { get; set; }
    public string? Paths { get; set; }
    public string? Modes { get; set; }
    public string[]? DisplayIds { get; set; }

    public DisplayProfile Copy() => new()
    {
        Version = Version, PathSize = PathSize, ModeSize = ModeSize,
        Paths = Paths, Modes = Modes, DisplayIds = DisplayIds is null ? null : [.. DisplayIds]
    };
}
