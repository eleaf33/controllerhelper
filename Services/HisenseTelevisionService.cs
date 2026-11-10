using System.Diagnostics;
using System.Net.Sockets;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal sealed class HisenseTelevisionService(Func<IHisenseTransport>? createTransport = null)
{
    internal HisenseSession CreateSession(AppSettings settings) =>
        new(createTransport?.Invoke() ?? new HisenseMqttTransport(), settings.HisenseClientId!);

    internal async Task<HisensePowerOffResult> PowerOffAsync(AppSettings settings, CancellationToken cancellation,
        Action<HisenseSession>? persistConnection = null)
    {
        HisenseSettings.Validate(settings);
        if (!settings.HisensePaired) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseTelevisionService_141"));
        // No WoL, input switching, PIN prompt or retry of a power toggle here.
        return await HisenseSession.Stage("power.off", async token =>
        {
            using HisenseSession session = CreateSession(settings);
            await session.ConnectAsync(settings, token);
            token.ThrowIfCancellationRequested();
            persistConnection?.Invoke(session);
            return await session.PowerOffAsync(token);
        }, cancellation, TimeSpan.FromSeconds(20));
    }

    internal async Task<string?> PrepareInputAsync(AppSettings settings, CancellationToken cancellation,
        Action<HisenseSession>? persistConnection = null)
    {
        if (settings.TelevisionNetworkProvider != TelevisionNetworkProvider.Hisense || string.IsNullOrEmpty(settings.HisenseInputId))
            return null;
        try
        {
            HisenseSettings.Validate(settings);
            if (!settings.HisensePaired) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseTelevisionService_142"));
            if (settings.WakeHisense) await WaitForBrokerAsync(settings.HisenseIpAddress, cancellation);
            using HisenseSession session = CreateSession(settings);
            await session.ConnectAsync(settings, cancellation);
            cancellation.ThrowIfCancellationRequested();
            persistConnection?.Invoke(session);
            await session.SelectInputAsync(settings.HisenseInputId, cancellation);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            ErrorLog.Write("hisense.input.warning", error);
            return ControllerHelper.Localization.Text.Get("HisenseTelevisionService_143");
        }
    }

    private static Task<bool> WaitForBrokerAsync(string? address, CancellationToken cancellation) =>
        HisenseSession.Stage("wake.broker", async token =>
        {
            string host = TelevisionWakeService.ParseAddress(address).ToString();
            long start = Stopwatch.GetTimestamp();
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using TcpClient tcp = new();
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await tcp.ConnectAsync(host, HisenseMqttTransport.Port, probe.Token);
                    ErrorLog.Event("hisense.wake.tcp-ready", new { attempt, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
                    return true;
                }
                catch (SocketException error)
                { ErrorLog.Event("hisense.wake.tcp-wait", new { attempt, code = error.SocketErrorCode.ToString() }); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { ErrorLog.Event("hisense.wake.tcp-wait", new { attempt, code = "timeout" }); }
                await Task.Delay(500, token);
            }
        }, cancellation, TimeSpan.FromSeconds(30));
}
