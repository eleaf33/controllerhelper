using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ControllerHelper.Services;

internal static class WebOsKeyProtector
{
    internal static string Protect(string key) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(key), true));

    internal static string Unprotect(string encrypted) => Encoding.UTF8.GetString(
        Transform(Convert.FromBase64String(encrypted), false));

    private static byte[] Transform(byte[] input, bool protect)
    {
        nint inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            DataBlob inputBlob = new() { Length = input.Length, Data = inputPointer };
            DataBlob outputBlob;
            nint description = 0;
            bool success = protect
                ? CryptProtectData(ref inputBlob, "ControllerHelper webOS", 0, 0, 0, 0x1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, 0, 0, 0, 0x1, out outputBlob);
            try
            {
                if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
                byte[] output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (success) LocalFree(outputBlob.Data);
                if (description != 0) LocalFree(description);
            }
        }
        finally { Marshal.FreeHGlobal(inputPointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { internal int Length; internal nint Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, nint entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out nint description, nint entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
