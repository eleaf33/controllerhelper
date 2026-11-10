using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

public sealed record TelevisionConnectionResult(bool Reachable, string? MacAddress);

public sealed class TelevisionWakeService
{
    private const int WakePort = 9;

    public static IPAddress ParseAddress(string? value)
    {
        if (!IPAddress.TryParse(value?.Trim(), out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) ||
            IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] >= 224)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("TelevisionWakeService_191"));
        return address;
    }

    public static string NormalizeMacAddress(string? value)
    {
        string hex = (value ?? string.Empty).Replace(":", "").Replace("-", "").Replace(" ", "");
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("TelevisionWakeService_192"));
        return string.Join(":", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2).ToUpperInvariant()));
    }

    public async Task<TelevisionConnectionResult> CheckConnectionAsync(string? addressText)
    {
        IPAddress address = ParseAddress(addressText);
        var network = FindLocalNetwork(address);
        string? macAddress = await Task.Run(() => ResolveMacAddress(address, network.LocalAddress));
        bool reachable = macAddress is not null;
        if (!reachable)
        {
            using Ping ping = new();
            try { reachable = (await ping.SendPingAsync(address, 1200)).Status == IPStatus.Success; }
            catch (PingException) { }
            catch (SocketException) { }
        }
        return new TelevisionConnectionResult(reachable, macAddress);
    }

    public async Task SendWakeAsync(AppSettings settings, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        try
        {
        var target = TelevisionNetworkSettings.WakeTarget(settings);
        IPAddress address = ParseAddress(target.Address);
        string macAddress = NormalizeMacAddress(target.Mac);
        ErrorLog.Event("television.wake.target", new { provider = settings.TelevisionNetworkProvider.ToString(),
            addressTag = Tag(address.ToString()), macTag = Tag(macAddress) });
        var network = FindLocalNetwork(address);

        byte[] packet = CreateMagicPacket(macAddress);

        using UdpClient client = new(new IPEndPoint(network.LocalAddress, 0)) { EnableBroadcast = true };
        IPEndPoint destination = new(network.BroadcastAddress, WakePort);
        ErrorLog.Event("television.wake.send", new { provider = settings.TelevisionNetworkProvider.ToString(), port = WakePort, attempts = 3 });
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            int bytes = await client.SendAsync(packet.AsMemory(), destination, cancellation);
            ErrorLog.Event("television.wake.udp-sent", new { attempt = attempt + 1, bytes, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            await Task.Delay(500, cancellation);
        }
        ErrorLog.Event("television.wake.sent-unconfirmed", new { elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
        catch (OperationCanceledException) { ErrorLog.Event("television.wake.canceled"); throw; }
        catch (Exception error) { ErrorLog.Write("television.wake.failed", error); throw; }
    }

    internal static string Tag(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    internal static byte[] CreateMagicPacket(string macAddress)
    {
        byte[] mac = Convert.FromHexString(NormalizeMacAddress(macAddress).Replace(":", ""));
        byte[] packet = new byte[6 + 16 * mac.Length];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (int index = 0; index < 16; index++)
            mac.CopyTo(packet, 6 + index * mac.Length);

        return packet;
    }

    private static (IPAddress LocalAddress, IPAddress BroadcastAddress) FindLocalNetwork(IPAddress television)
    {
        byte[] target = television.GetAddressBytes();
        int examined = 0;
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
        {
            foreach (UnicastIPAddressInformation unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;
                byte[] local = unicast.Address.GetAddressBytes();
                byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                bool matches = Enumerable.Range(0, 4).All(index => (local[index] & mask[index]) == (target[index] & mask[index]));
                examined++;
                ErrorLog.Event("television.network.candidate", new { interfaceTag = Tag(network.Id), type = network.NetworkInterfaceType.ToString(),
                    addressTag = Tag(unicast.Address.ToString()), prefixLength = unicast.PrefixLength, sameSubnet = matches });
                if (!matches)
                    continue;
                byte[] broadcast = Enumerable.Range(0, 4)
                    .Select(index => (byte)(local[index] | ~mask[index])).ToArray();
                ErrorLog.Event("television.network.selected", new { interfaceTag = Tag(network.Id), broadcastTag = Tag(new IPAddress(broadcast).ToString()) });
                return (unicast.Address, new IPAddress(broadcast));
            }
        }
        ErrorLog.Event("television.network.no-route", new { examined });
        throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("TelevisionWakeService_193"));
    }

    private static string? ResolveMacAddress(IPAddress destination, IPAddress source)
    {
        byte[] mac = new byte[6];
        uint length = (uint)mac.Length;
        int error = SendARP(BitConverter.ToUInt32(destination.GetAddressBytes()),
            BitConverter.ToUInt32(source.GetAddressBytes()), mac, ref length);
        return error == 0 && length == mac.Length && mac.Any(value => value != 0)
            ? NormalizeMacAddress(Convert.ToHexString(mac))
            : null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(uint destinationIp, uint sourceIp, byte[] macAddress, ref uint length);
}
