using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Bridge;

/// <summary>
/// The launcher's end of the bridge: listens on the first free loopback port of <see cref="BridgeDefaults.Ports"/>
/// and talks to the bridge plugin inside the game. A connection only counts once it says hello with this install's
/// token (from launch.json). Events are raised on a background thread.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TcpListener? _listener;
    private Connection? _current;

    public BridgeServer(string token)
    {
        _token = token;
    }

    public int Port { get; private set; }

    public bool IsConnected => _current != null;

    public HelloMessage? Hello { get; private set; }
    public StateMessage? State { get; private set; }
    public RoomsMessage? Rooms { get; private set; }

    public event Action<HelloMessage>? Connected;
    public event Action? Disconnected;
    public event Action<StateMessage>? StateChanged;
    public event Action<RoomsMessage>? RoomsChanged;
    public event Action<JoinResultMessage>? JoinResult;
    public event Action<HostResultMessage>? HostResult;
    public event Action<DisconnectedMessage>? GameLeftRoom;

    /// <summary>Starts listening; returns false when every port is taken (another launcher is probably running).</summary>
    public bool Start()
    {
        foreach (int port in BridgeDefaults.Ports)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
                _listener = listener;
                Port = port;
                // Off the UI thread, so the connection keeps reading while the launcher shows a dialog.
                _ = Task.Run(() => AcceptLoop(listener));
                AppLog.Info($"Listening for the game on 127.0.0.1:{port}.");
                return true;
            }
            catch (SocketException)
            {
                // Port in use; try the next.
            }
        }
        AppLog.Warn("Every bridge port is in use; the server browser can't see the game. Is another copy of the launcher open?");
        return false;
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                AppLog.Warn($"Bridge accept failed: {e.Message}");
                await Task.Delay(500);
                continue;
            }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var connection = new Connection(stream);
            try
            {
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                string? first = await reader.ReadLineAsync(helloTimeout.Token);
                HelloMessage? hello = first == null ? null : Parse<HelloMessage>(first, MessageTypes.Hello);
                if (hello == null || !string.Equals(hello.Token, _token, StringComparison.Ordinal))
                {
                    AppLog.Warn("Something connected to the launcher's bridge port without this launcher's token; closed it.");
                    return;
                }

                Connection? previous = Interlocked.Exchange(ref _current, connection);
                previous?.Close();
                Hello = hello;
                State = null;
                Rooms = null;
                await SendOn(connection, new WelcomeMessage { Token = _token, LauncherVersion = AppInfo.Version });
                AppLog.Info($"Void Crew connected (bridge {hello.BridgeVersion}, game {hello.GameVersion}, {hello.Plugins?.Count ?? 0} plugins).");
                Connected?.Invoke(hello);

                while (!_stop.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(_stop.Token);
                    if (line == null) break;
                    Dispatch(line);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The game closed, or the launcher is shutting down.
            }
            catch (Exception e)
            {
                AppLog.Error("Bridge connection failed", e);
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _current, null, connection) == connection)
                {
                    State = null;
                    Rooms = null;
                    AppLog.Info("Void Crew disconnected from the launcher.");
                    Disconnected?.Invoke();
                }
            }
        }
    }

    private void Dispatch(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            AppLog.Warn("The bridge sent a line the launcher couldn't read.");
            return;
        }
        string? type = node?["type"]?.GetValue<string>();
        switch (type)
        {
            case MessageTypes.State:
                State = node.Deserialize<StateMessage>(JsonFiles.Protocol);
                if (State != null) StateChanged?.Invoke(State);
                break;
            case MessageTypes.Rooms:
                Rooms = node.Deserialize<RoomsMessage>(JsonFiles.Protocol);
                if (Rooms != null) RoomsChanged?.Invoke(Rooms);
                break;
            case MessageTypes.JoinResult:
                if (node.Deserialize<JoinResultMessage>(JsonFiles.Protocol) is { } join) JoinResult?.Invoke(join);
                break;
            case MessageTypes.HostResult:
                if (node.Deserialize<HostResultMessage>(JsonFiles.Protocol) is { } host) HostResult?.Invoke(host);
                break;
            case MessageTypes.Disconnected:
                if (node.Deserialize<DisconnectedMessage>(JsonFiles.Protocol) is { } left) GameLeftRoom?.Invoke(left);
                break;
        }
    }

    private static T? Parse<T>(string line, string type) where T : Message
    {
        try
        {
            JsonNode? node = JsonNode.Parse(line);
            return node?["type"]?.GetValue<string>() == type ? node.Deserialize<T>(JsonFiles.Protocol) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends a command to the game; false when the game isn't connected.</summary>
    public async Task<bool> SendAsync(Message message)
    {
        Connection? connection = _current;
        if (connection == null) return false;
        try
        {
            await SendOn(connection, message);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task SendOn(Connection connection, Message message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, message.GetType(), JsonFiles.Protocol) + "\n");
        await _writeGate.WaitAsync();
        try
        {
            await connection.Stream.WriteAsync(bytes);
            await connection.Stream.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // Already stopped.
        }
        _current?.Close();
    }

    private sealed class Connection(NetworkStream stream)
    {
        public NetworkStream Stream { get; } = stream;

        public void Close()
        {
            try
            {
                Stream.Close();
            }
            catch (IOException)
            {
                // Already closed.
            }
        }
    }
}
