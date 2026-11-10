using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal sealed class TelevisionPowerProbe(
    Func<AppSettings, CancellationToken, Task<TelevisionPowerState>> probe,
    TimeSpan? timeout = null, TimeSpan? cacheLifetime = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private (TelevisionPairingTarget Target, string? Key)? _cachedIdentity;
    private DateTimeOffset _expires;
    private TelevisionPowerState _cachedPower;
    private long _generation;

    internal void Invalidate() { _generation++; _cachedIdentity = null; }

    internal async Task<TelevisionPowerState> ReadAsync(AppSettings settings, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (settings.TelevisionNetworkProvider != TelevisionNetworkProvider.LG || string.IsNullOrWhiteSpace(settings.TelevisionClientKey))
            return TelevisionPowerState.Unknown;
        var identity = (TelevisionPairingTarget.From(settings), settings.TelevisionClientKey);
        if (_cachedIdentity == identity && _clock.GetUtcNow() < _expires) return _cachedPower;
        long generation = _generation;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
        TelevisionPowerState power;
        try
        {
            // WaitAsync bounds even a misbehaving implementation that ignores cancellation.
            power = await probe(settings.Copy(), deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { power = TelevisionPowerState.Unknown; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            StartupTrace.Write($"TV power probe failed: {error}");
            power = TelevisionPowerState.Unknown;
        }
        finally { deadline.Cancel(); }
        cancellation.ThrowIfCancellationRequested();
        if (generation != _generation) return TelevisionPowerState.Unknown;
        _cachedIdentity = identity;
        _cachedPower = power;
        _expires = _clock.GetUtcNow() + (cacheLifetime ?? TimeSpan.FromSeconds(3));
        return power;
    }
}
