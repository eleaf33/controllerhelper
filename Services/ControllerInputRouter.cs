namespace ControllerHelper.Services;

internal enum ControllerInputMode { Overlay, Flydigi, Confirmation }

// Owned by the UI thread. A disposed window can never regain input ownership.
internal sealed class ControllerInputRouter
{
    private readonly List<ControllerInputScope> _scopes = [];
    internal event Action? Changed;
    internal long Version { get; private set; }
    internal ControllerInputScope? Current =>
        _scopes.LastOrDefault(scope => scope.Mode == ControllerInputMode.Confirmation) ?? _scopes.LastOrDefault();

    internal ControllerInputScope Acquire(ControllerInputMode mode, Action? confirm = null,
        Action? cancel = null, Action<int>? move = null, Action<int>? verticalMove = null,
        Action<ControllerDevice, ControllerDevice?>? flydigi = null, Func<bool>? hasFocus = null,
        Action? suspend = null)
    {
        Current?.Suspend?.Invoke();
        ControllerInputScope scope = new(this, mode, confirm, cancel, move, verticalMove, flydigi, hasFocus, suspend);
        _scopes.Add(scope);
        Version++;
        Changed?.Invoke();
        return scope;
    }

    internal void ChangeMode(ControllerInputScope scope, ControllerInputMode mode)
    {
        if (!_scopes.Contains(scope) || scope.Mode == mode) return;
        scope.Suspend?.Invoke();
        scope.Mode = mode;
        Version++;
        Changed?.Invoke();
    }

    internal void Release(ControllerInputScope scope)
    {
        if (!_scopes.Remove(scope)) return;
        scope.Suspend?.Invoke();
        Version++;
        Changed?.Invoke();
    }
}

internal sealed class ControllerInputScope(ControllerInputRouter router, ControllerInputMode mode,
    Action? confirm, Action? cancel, Action<int>? move, Action<int>? verticalMove,
    Action<ControllerDevice, ControllerDevice?>? flydigi, Func<bool>? hasFocus, Action? suspend) : IDisposable
{
    internal ControllerInputMode Mode { get; set; } = mode;
    internal bool HasFocus => hasFocus?.Invoke() ?? true;
    internal Action? Suspend { get; } = suspend;
    internal Action? Confirm { get; } = confirm;
    internal Action? Cancel { get; } = cancel;
    internal Action<int>? Move { get; } = move;
    internal Action<int>? VerticalMove { get; } = verticalMove;
    internal Action<ControllerDevice, ControllerDevice?>? Flydigi { get; } = flydigi;
    internal void SetMode(ControllerInputMode mode) => router.ChangeMode(this, mode);
    public void Dispose() => router.Release(this);
}
