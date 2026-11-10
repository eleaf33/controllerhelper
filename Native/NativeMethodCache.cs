using System.Runtime.InteropServices;

namespace ControllerHelper.Native;

// Used only under GameInputService's read lock. Cache the function, never a
// reading/device COM pointer: each returned reading still owns its own lifetime.
internal sealed class NativeMethodCache<T>(int slot) where T : Delegate
{
    private nint _address;
    private T? _method;

    internal T Get(nint instance)
    {
        nint address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size);
        if (_method is null || address != _address)
        {
            T method = Marshal.GetDelegateForFunctionPointer<T>(address);
            _address = address;
            _method = method;
        }
        return _method;
    }
}
