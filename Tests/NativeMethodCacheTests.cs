using System.Runtime.InteropServices;
using ControllerHelper.Native;

internal static class NativeMethodCacheTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Кэш нативного метода не удерживает экземпляр чтения", ReuseFunction),
        ("Кэш нативного метода обновляется при смене реализации", ChangeFunction)
    ];

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint Method(nint instance);

    private sealed class Instance : IDisposable
    {
        internal readonly nint Table = Marshal.AllocHGlobal(nint.Size * 2);
        internal readonly nint Pointer = Marshal.AllocHGlobal(nint.Size);
        internal Instance(Method method)
        {
            Marshal.WriteIntPtr(Pointer, Table);
            Marshal.WriteIntPtr(Table, 0); // Cache must use the requested slot.
            Set(method);
        }
        internal void Set(Method method) =>
            Marshal.WriteIntPtr(Table, nint.Size, Marshal.GetFunctionPointerForDelegate(method));
        public void Dispose() { Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(Table); }
    }

    private static Task ReuseFunction()
    {
        Method method = instance => instance;
        using Instance first = new(method), second = new(method);
        NativeMethodCache<Method> cache = new(1);
        Method wrapper = cache.Get(first.Pointer);
        if (!ReferenceEquals(wrapper, cache.Get(second.Pointer)) || wrapper(second.Pointer) != second.Pointer)
            throw new Exception("A cached function captured the old COM instance or was not reused.");
        GC.KeepAlive(method);
        return Task.CompletedTask;
    }

    private static Task ChangeFunction()
    {
        Method first = _ => 1, second = _ => 2;
        using Instance instance = new(first);
        NativeMethodCache<Method> cache = new(1);
        if (cache.Get(instance.Pointer)(instance.Pointer) != 1) throw new Exception("Wrong initial function.");
        instance.Set(second);
        if (cache.Get(instance.Pointer)(instance.Pointer) != 2) throw new Exception("Stale function after vtable change.");
        instance.Set(first);
        if (cache.Get(instance.Pointer)(instance.Pointer) != 1) throw new Exception("Wrong function after reconnect.");
        GC.KeepAlive(first); GC.KeepAlive(second);
        return Task.CompletedTask;
    }
}
