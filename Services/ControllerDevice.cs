namespace ControllerHelper.Services;

public readonly record struct ControllerAxes(float LeftX, float LeftY, float RightX, float RightY);

public sealed record ControllerDevice(string Id, string Name, string Fingerprint, ushort VendorId,
    ushort ProductId, uint Buttons, bool[] ControllerButtons, ControllerAxes Axes)
{
    internal bool HasReading { get; init; } = true;
    public override string ToString() => $"{Name} ({VendorId:X4}:{ProductId:X4}, {Id[..Math.Min(8, Id.Length)]})";
}
