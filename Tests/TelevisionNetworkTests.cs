using System.Text.Json;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static class TelevisionNetworkTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Старые настройки LG сохраняют пробуждение и адрес", () => Run(LegacySettings)),
        ("Hisense использует только свои адрес и флаг пробуждения", () => Run(ProviderRouting)),
        ("Настройки Hisense и LG переживают копирование и сохранение", () => Run(SettingsRoundTrip)),
        ("Hisense в оверлее не требует сопряжения LG", () => Run(HisenseOverlay)),
        ("Hisense не вызывает сетевой опрос webOS", HisensePowerProbe),
        ("Смена бренда отклоняет поздний ключ и сертификат LG", () => Run(StaleLgPairing)),
        ("Пакет пробуждения содержит только выбранный MAC", () => Run(MagicPacket)),
        ("Неизвестный сетевой профиль не отправляет WoL", () => Run(UnknownProvider))
    ];

    private static void LegacySettings()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""
            {"WakeTelevision":true,"TelevisionIpAddress":"192.168.1.10","TelevisionMacAddress":"02:11:22:33:44:55"}
            """)!;
        Check(settings.TelevisionNetworkProvider == TelevisionNetworkProvider.LG && TelevisionNetworkSettings.WakeEnabled(settings), "Legacy LG changed");
        Check(TelevisionNetworkSettings.WakeTarget(settings).Address == "192.168.1.10", "Legacy address lost");
        Check(TelevisionNetworkSettings.SupportsPowerControl(settings), "LG power control disabled");
    }

    private static AppSettings BothProfiles() => new()
    {
        TelevisionNetworkProvider = TelevisionNetworkProvider.Hisense,
        TelevisionIpAddress = "192.168.1.10", TelevisionMacAddress = "02:11:22:33:44:55",
        HisenseIpAddress = "192.168.1.20", HisenseMacAddress = "02:AA:BB:CC:DD:EE",
        WakeTelevision = true, WakeHisense = true
    };

    private static void ProviderRouting()
    {
        AppSettings settings = BothProfiles();
        Check(TelevisionNetworkSettings.WakeTarget(settings) == (settings.HisenseIpAddress, settings.HisenseMacAddress), "LG destination used for Hisense");
        settings.WakeHisense = false;
        Check(!TelevisionNetworkSettings.WakeEnabled(settings), "Inactive LG flag enabled Hisense wake");
        settings.WakeHisense = true;
        settings.WakeTelevision = false;
        Check(TelevisionNetworkSettings.WakeEnabled(settings), "Hisense wake depends on LG flag");
        settings.TelevisionNetworkProvider = TelevisionNetworkProvider.LG;
        Check(!TelevisionNetworkSettings.WakeEnabled(settings), "Hisense flag enabled LG wake");
        Check(TelevisionNetworkSettings.WakeTarget(settings) == (settings.TelevisionIpAddress, settings.TelevisionMacAddress), "Hisense destination used for LG");
    }

    private static void SettingsRoundTrip()
    {
        AppSettings settings = BothProfiles();
        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings.Copy()))!;
        Check(restored.TelevisionNetworkProvider == TelevisionNetworkProvider.Hisense && restored.WakeHisense && restored.WakeTelevision, "Wake profile lost");
        Check(restored.HisenseIpAddress == settings.HisenseIpAddress && restored.HisenseMacAddress == settings.HisenseMacAddress &&
            restored.TelevisionIpAddress == settings.TelevisionIpAddress && restored.TelevisionMacAddress == settings.TelevisionMacAddress, "Addresses lost");
    }

    private static void HisenseOverlay()
    {
        AppSettings settings = BothProfiles();
        settings.TelevisionClientKey = "old-lg-key";
        foreach (bool wake in new[] { false, true })
        {
            settings.WakeHisense = wake;
            TelevisionOverlayState local = TelevisionNetworkSettings.LocalState(settings, false, true);
            Check(local.WakeEnabled == wake && !local.PowerControlSupported && !local.PowerCheckPending, "Hisense requests LG probe");
            OverlayPresentation play = OverlayPresentation.FromState(local);
            Check(play.PrimaryAction == OverlayPrimaryAction.PlayOnTv && play.Warning is null, "Hisense cannot play without LG certificate");
            foreach (TelevisionPowerState power in Enum.GetValues<TelevisionPowerState>())
            {
                OverlayPresentation back = OverlayPresentation.FromState(local with { TelevisionActive = true, MonitorsActive = false, PowerState = power });
                Check(back.PrimaryAction == OverlayPrimaryAction.ReturnToPc && back.Warning is null, "Hisense offers LG power-off");
            }
        }
    }

    private static async Task HisensePowerProbe()
    {
        AppSettings settings = BothProfiles();
        settings.TelevisionClientKey = "old-lg-key";
        int calls = 0;
        TelevisionPowerProbe probe = new((_, _) => { calls++; return Task.FromResult(TelevisionPowerState.On); });
        Check(await probe.ReadAsync(settings, default) == TelevisionPowerState.Unknown && calls == 0, "Hisense reached webOS probe");
        ExpectInvalid(() => WebOsSecurity.Endpoint(settings));
    }

    private static void StaleLgPairing()
    {
        AppSettings lg = BothProfiles();
        lg.TelevisionNetworkProvider = TelevisionNetworkProvider.LG;
        lg.TelevisionClientKey = "key";
        lg.TelevisionCertificateSha256 = new string('A', 64);
        TelevisionPairingTarget target = TelevisionPairingTarget.From(lg);
        AppSettings hisense = lg.Copy();
        hisense.TelevisionNetworkProvider = TelevisionNetworkProvider.Hisense;
        WebOsSecurity.ReconcileSettings(lg, hisense);
        Check(hisense.TelevisionClientKey is null && hisense.TelevisionCertificateSha256 is null, "Trust crossed provider boundary");
        ExpectInvalid(() => target.ApplyKey(hisense, "late-key"));
        ExpectInvalid(() => WebOsSecurity.ApplyCertificate(hisense, target, new string('B', 64)));
    }

    private static void MagicPacket()
    {
        byte[] packet = TelevisionWakeService.CreateMagicPacket("02-aa-bb-cc-dd-ee");
        Check(packet.Length == 102 && packet.Take(6).All(b => b == 255), "Invalid WoL header");
        byte[] mac = [2, 170, 187, 204, 221, 238];
        for (int index = 0; index < 16; index++)
            Check(packet.AsSpan(6 + index * 6, 6).SequenceEqual(mac), "Wrong target MAC in packet");
        ExpectInvalid(() => TelevisionWakeService.CreateMagicPacket("invalid"));
    }

    private static void UnknownProvider()
    {
        AppSettings settings = BothProfiles();
        settings.TelevisionNetworkProvider = (TelevisionNetworkProvider)123;
        Check(!TelevisionNetworkSettings.WakeEnabled(settings) && !TelevisionNetworkSettings.SupportsPowerControl(settings), "Unknown provider enabled");
        ExpectInvalid(() => TelevisionNetworkSettings.WakeTarget(settings));
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected InvalidOperationException");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
}
