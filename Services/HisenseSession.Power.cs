using System.Text.Json;

namespace ControllerHelper.Services;

internal enum HisensePowerOffResult { AlreadyOff, Confirmed, SentUnconfirmed }

internal sealed partial class HisenseSession
{
    internal async Task<HisensePowerOffResult> PowerOffAsync(CancellationToken cancellation, TimeSpan? confirmationTimeout = null)
    {
        TelevisionPowerState power = await ReadPowerSnapshotAsync(cancellation);
        if (power == TelevisionPowerState.Off)
        {
            ErrorLog.Event("hisense.power.off.skipped", new { reason = "already-standby" });
            return HisensePowerOffResult.AlreadyOff;
        }
        if (power != TelevisionPowerState.On)
            throw new InvalidOperationException("Hisense не подтвердил, что включён. Команда питания не отправлена, чтобы не включить уже спящий ТВ. Подробности в logs.");

        cancellation.ThrowIfCancellationRequested();
        // ba.k.z / U in the tested APK: this is a toggle, not an idempotent OFF.
        // Send once after the current connection's state snapshot/event says ON.
        // Never retry after an ambiguous send.
        ErrorLog.Event("hisense.power.off.send", new { command = "KEY_POWER", attempts = 1 });
        await transport.PublishAsync($"/remoteapp/tv/remote_service/{Client}/actions/sendkey", "KEY_POWER", cancellation);
        ErrorLog.Event("hisense.power.off.sent", new { confirmedByTv = false });
        try
        {
            await Stage("power.off.confirm", async token =>
            {
                while (true)
                {
                    var reply = await Receive([StateTopic, ResponseTopic("authentication")], token);
                    if (reply.Topic != StateTopic || RequiresAuthentication(reply.Payload))
                        throw new InvalidOperationException("Hisense запросил сопряжение после команды питания.");
                    var state = ParsePowerState(reply.Payload);
                    ErrorLog.Event("hisense.power.off.observed", new { state = state.ToString() });
                    if (state == TelevisionPowerState.Off) return true;
                }
            }, cancellation, confirmationTimeout ?? TimeSpan.FromSeconds(4));
            ErrorLog.Event("hisense.power.off.confirmed");
            return HisensePowerOffResult.Confirmed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Some TVs close MQTT as they enter standby. Neither a close nor a
            // successful QoS 0 publish proves that the panel is off.
            ErrorLog.Event("hisense.power.off.unconfirmed", new { exceptionType = error.GetType().FullName, retry = false });
            return HisensePowerOffResult.SentUnconfirmed;
        }
    }

    private Task<TelevisionPowerState> ReadPowerSnapshotAsync(CancellationToken cancellation) =>
        Stage("power.state", async token =>
        {
            // PowerOff uses a new authenticated session. VIDAA sends its latest
            // state as a retained snapshot on SUBSCRIBE; on the user's U7QF,
            // gettvstate does not generate another reply. Do not drain away the
            // snapshot, including one delivered before SUBACK/token persistence.
            // Only this initial power-state read accepts retained StateTopic.
            // PIN, sources, HDMI and post-command confirmation still use Receive,
            // which rejects retained replies.
            await Send("gettvstate", "", token);
            TelevisionPowerState? state = null;
            bool liveSeen = false;
            string source = "none";

            void Observe(HisenseMessage reply)
            {
                token.ThrowIfCancellationRequested();
                if (reply.Topic != StateTopic)
                {
                    if (!reply.Retained && reply.Topic == ResponseTopic("authentication"))
                        throw new InvalidOperationException("Hisense требует повторного сопряжения для выключения. Выполните его в настройках.");
                    if (!reply.Retained && reply.Topic == ResponseTopic("authenticationcodetoast"))
                        throw new InvalidOperationException("Hisense сообщает, что управление занято другим устройством.");
                    return;
                }
                // A late delivery of the initial retained snapshot cannot replace
                // a live event already observed on this connection.
                if (reply.Retained && liveSeen)
                {
                    ErrorLog.Event("hisense.power.snapshot.ignored", new { reason = "live-event-already-received", retained = true });
                    return;
                }
                if (!reply.Retained && RequiresAuthentication(reply.Payload))
                    throw new InvalidOperationException("Hisense требует повторного сопряжения для выключения. Выполните его в настройках.");
                state = ParsePowerState(reply.Payload);
                liveSeen |= !reply.Retained;
                source = reply.Retained ? "retained-broker-snapshot" : "live-event";
                ErrorLog.Event("hisense.power.snapshot.received", new { state = state.ToString(), source, retained = reply.Retained });
            }

            while (state is null) Observe(await transport.ReadAsync(token));
            // Observe, rather than discard, already queued updates before deciding
            // whether to send a toggle. In particular, a newer standby wins over ON.
            int count = 0;
            while (transport.TryRead(out var pending))
            {
                if (++count > 64) throw new InvalidDataException("Слишком много обновлений состояния Hisense перед выключением.");
                Observe(pending!);
            }
            ErrorLog.Event("hisense.power.state", new { state = state.Value.ToString(), source, queuedUpdates = count });
            return state.Value;
        }, cancellation, TimeSpan.FromSeconds(4));

    internal static TelevisionPowerState ParsePowerState(byte[] payload)
    {
        using var json = Parse(payload);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return TelevisionPowerState.Unknown;
        string? state = root.TryGetProperty("statetype", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        // Known VIDAA state replies, including sourceswitch while using HDMI.
        // fake_sleep_1 is a waking transition on some firmware, not confirmed OFF.
        // Unknown/authentication states must never authorize a toggle.
        if (state is "fake_sleep" or "fake_sleep_0") return TelevisionPowerState.Off;
        if (state is "app" or "source" or "sourceswitch" or "livetv" or "remote_launcher" or "remote_setting")
            return TelevisionPowerState.On;
        if (state is null && root.TryGetProperty("sourceid", out var id) && !string.IsNullOrWhiteSpace(Scalar(id)))
            return TelevisionPowerState.On;
        return TelevisionPowerState.Unknown;
    }
}
