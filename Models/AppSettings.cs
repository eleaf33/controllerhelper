using System.Text.Json.Serialization;

namespace ControllerHelper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<TelevisionNetworkProvider>))]
public enum TelevisionNetworkProvider { LG, Hisense }

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public string? TelevisionId { get; set; }
    public List<string> MonitorIds { get; set; } = [];
    public string? PcAudioId { get; set; }
    public string? TvAudioId { get; set; }
    public TelevisionVideoMode? TelevisionMode { get; set; }
    public int? TelevisionScalePercent { get; set; }
    public bool WakeTelevision { get; set; }
    public TelevisionNetworkProvider TelevisionNetworkProvider { get; set; }
    public bool WakeHisense { get; set; }
    public string? HisenseIpAddress { get; set; }
    public string? HisenseMacAddress { get; set; }
    public string? HisenseClientId { get; set; }
    public string? HisenseCertificateSha256 { get; set; }
    public bool HisensePaired { get; set; }
    public string? HisensePreferredAuthMethod { get; set; }
    [JsonIgnore]
    internal ControllerHelper.Services.HisenseAuthentication? HisenseAuthentication { get; set; }
    public string? HisenseAuthenticationProtected { get; set; }
    public string? HisenseInputId { get; set; }
    public string? HisenseInputName { get; set; }
    public string? TelevisionIpAddress { get; set; }
    public string? TelevisionMacAddress { get; set; }
    public string? LgInputId { get; set; }
    public string? LgInputName { get; set; }
    public bool AllowInsecureWebOs { get; set; }
    public string? TelevisionCertificateSha256 { get; set; }
    [JsonIgnore]
    public string? TelevisionClientKey { get; set; }
    public string? TelevisionClientKeyProtected { get; set; }
    public string HotkeyModifier { get; set; } = "A";
    public string HotkeyTrigger { get; set; } = "View";
    public bool EmergencyReturnEnabled { get; set; }
    public string EmergencyReturnModifier { get; set; } = "LeftShoulder";
    public string EmergencyReturnTrigger { get; set; } = "RightShoulder";
    public string? ControllerDeviceId { get; set; }
    public string? ControllerFingerprint { get; set; }
    public double FlydigiCursorSpeed { get; set; } = 1.0;
    public string? PcPaths { get; set; }
    public string? PcModes { get; set; }
    public string? TvPath { get; set; }
    public string? TvModes { get; set; }
    public DisplayProfile? PcProfile { get; set; }
    public DisplayProfile? TvProfile { get; set; }
    [JsonIgnore]
    public bool HasPcProfile => PcProfile is not null || (PcPaths is not null && PcModes is not null);
    [JsonIgnore]
    public bool HasTvProfile => TvProfile is not null || (TvPath is not null && TvModes is not null);

    public AppSettings Copy()
    {
        AppSettings copy = (AppSettings)MemberwiseClone();
        copy.MonitorIds = [.. MonitorIds];
        copy.PcProfile = PcProfile?.Copy();
        copy.TvProfile = TvProfile?.Copy();
        return copy;
    }
}
