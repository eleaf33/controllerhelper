using System.Text;

namespace ControllerHelper.Services;

internal static class CommandProtocol
{
    // The Local mutex is session-scoped. Retain its legacy name to prevent
    // simultaneous old/new versions; scope the pipe to the same session and user.
    internal static string PipeName(string userSid, int sessionId) =>
        $"ControllerHelper.Overlay.Commands.{sessionId}.{Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))}";

    internal const int MaximumCommandBytes = 32;
    internal static bool IsSupported(string command) => command is "show" or "--to-tv" or "--to-pc" or "--overlay";

    // One bounded ASCII line per connection. Slow/malformed peers cannot hold the pipe indefinitely.
    internal static async Task<string?> ReadAsync(Stream stream, CancellationToken cancellation,
        TimeSpan? readTimeout = null)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(readTimeout ?? TimeSpan.FromSeconds(2));
        byte[] bytes = new byte[MaximumCommandBytes + 1];
        int count = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(count, 1), deadline.Token);
                if (read == 0) return null; // No newline: incomplete command.
                byte value = bytes[count];
                if (value == (byte)'\n')
                {
                    if (count > 0 && bytes[count - 1] == (byte)'\r') count--;
                    string command = Encoding.ASCII.GetString(bytes, 0, count);
                    return IsSupported(command) ? command : null;
                }
                if (count == MaximumCommandBytes || value > 0x7F || (value < 0x20 && value != (byte)'\r'))
                    return null;
                count++;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
    }
}
