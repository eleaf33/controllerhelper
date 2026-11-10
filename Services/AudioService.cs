using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ControllerHelper.Services;

public sealed class AudioService
{
    private static readonly Guid FriendlyNameKey = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    public void EnsureSwitchSupported()
    {
        try
        {
            IPolicyConfig policy = CreatePolicyConfig();
            Marshal.ReleaseComObject(policy);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            throw new InvalidOperationException(
                "Эта версия Windows не предоставляет используемый интерфейс переключения звука. Экран не переключён.", error);
        }
    }

    public IReadOnlyList<AudioEndpoint> Enumerate()
    {
        IMMDeviceEnumerator enumerator = CreateEnumerator();
        try
        {
            Check(enumerator.EnumAudioEndpoints(0, 0xF, out IMMDeviceCollection collection));
            try
            {
                Check(collection.GetCount(out uint count));
                List<AudioEndpoint> endpoints = [];
                for (uint index = 0; index < count; index++)
                {
                    Check(collection.Item(index, out IMMDevice device));
                    try
                    {
                        Check(device.GetId(out string id));
                        Check(device.GetState(out uint state));
                        string name;
                        try { name = ReadName(device) ?? id; }
                        catch (COMException) { name = id; }
                        endpoints.Add(new AudioEndpoint(id, name, state));
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                return endpoints;
            }
            finally { Marshal.ReleaseComObject(collection); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public void SetDefault(string? id, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Выберите устройство вывода звука.");
        Stopwatch wait = Stopwatch.StartNew();
        while (!IsActive(id))
        {
            cancellation.ThrowIfCancellationRequested();
            if (wait.Elapsed >= TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Выбранное устройство звука не появилось после переключения экрана. Проверьте подключение телевизора и аудиовыход в настройках.");
            cancellation.WaitHandle.WaitOne(250);
        }

        cancellation.ThrowIfCancellationRequested();
        IPolicyConfig policy = CreatePolicyConfig();
        try
        {
            AudioRoleOperations.Apply(id, (endpoint, role) => Check(policy.SetDefaultEndpoint(endpoint, role)), cancellation);
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    internal AudioDefaults CaptureDefaults()
    {
        string?[] endpointIds = new string?[AudioRoleOperations.RoleCount];
        IMMDeviceEnumerator enumerator = CreateEnumerator();
        try
        {
            for (int role = 0; role < endpointIds.Length; role++)
            {
                int result = enumerator.GetDefaultAudioEndpoint(0, role, out IMMDevice device);
                if (result == unchecked((int)0x80070490)) continue;
                Check(result);
                try
                {
                    Check(device.GetId(out string id));
                    endpointIds[role] = id;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return new AudioDefaults(endpointIds);
    }

    internal void RestoreDefaults(AudioDefaults snapshot)
    {
        IPolicyConfig policy = CreatePolicyConfig();
        try
        {
            AudioRoleOperations.Restore(snapshot, WaitUntilActive,
                (endpoint, role) => Check(policy.SetDefaultEndpoint(endpoint, role)));
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    private static void WaitUntilActive(string id)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!IsActive(id))
        {
            if (wait.Elapsed >= TimeSpan.FromSeconds(10))
                throw new InvalidOperationException($"Аудиовыход {id} не появился после восстановления экранов.");
            Thread.Sleep(250);
        }
    }

    private static string? ReadName(IMMDevice device)
    {
        Check(device.OpenPropertyStore(0, out IPropertyStore store));
        try
        {
            PropertyKey key = new() { FormatId = FriendlyNameKey, PropertyId = 14 };
            Check(store.GetValue(ref key, out PropertyValue value));
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Text) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("bcde0395-e52f-467c-8e3d-c4579291692e"))!)!;

    private static IPolicyConfig CreatePolicyConfig() => (IPolicyConfig)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!)!;

    private static bool IsActive(string id)
    {
        IMMDeviceEnumerator enumerator = CreateEnumerator();
        try
        {
            int result = enumerator.GetDevice(id, out IMMDevice device);
            if (result == unchecked((int)0x80070490)) return false;
            Check(result);
            try
            {
                Check(device.GetState(out uint state));
                return state == 1;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Text;
    }

    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }

    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, out nint instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyValue value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
