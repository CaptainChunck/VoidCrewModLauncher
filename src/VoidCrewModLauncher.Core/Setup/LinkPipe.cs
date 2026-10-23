using System.IO.Pipes;
using System.Text;

namespace VoidCrewModLauncher.Core.Setup;

/// <summary>
/// Passes a voidcrewmod:// link from a second start of the launcher (Windows starts one per link) to the launcher that is
/// already open, over a named pipe only this user's processes on this PC can reach.
/// </summary>
public sealed class LinkPipe : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly string _name;

    public LinkPipe(string? name = null)
    {
        _name = name ?? DefaultName;
    }

    public static string DefaultName => "VoidCrewModLauncher-links-" + Environment.UserName;

    /// <summary>Gives <paramref name="link"/> to the open launcher; false when none answers.</summary>
    public static async Task<bool> SendAsync(string link, string? name = null, int timeoutMs = 3000)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", name ?? DefaultName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs);
            byte[] bytes = Encoding.UTF8.GetBytes(link.Replace("\r", "").Replace("\n", "") + "\n");
            await client.WriteAsync(bytes);
            await client.FlushAsync();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for links in the background; <paramref name="received"/> runs on a background thread.</summary>
    public void Start(Action<string> received) => _ = Task.Run(() => ListenAsync(received));

    private async Task ListenAsync(Action<string> received)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (!string.IsNullOrWhiteSpace(line)) received(line.Trim());
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                AppLog.Warn($"Link pipe: {e.Message}");
                await Task.Delay(500);
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
