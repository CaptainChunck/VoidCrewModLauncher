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
    /// <summary>
    /// The longest first line read: a hello lists the game's plugins, a few hundred bytes each. Anything can connect to
    /// the port, so a longer line closes the connection rather than filling the memory.
    /// </summary>
    internal const int MaxHelloLength = 1024 * 1024;

    /// <summary>
    /// The longest message read from the game once it has said hello: the whole lobby list, with every lobby's mod lists
    /// (the bridge leaves out over-long ones). A longer one is skipped; the connection stays.
    /// </summary>
    internal const int MaxLineLength = 16 * 1024 * 1024;

    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _seenNonces = new(StringComparer.Ordinal);
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

    /// <summary>The game put its advert on a lobby it hosts.</summary>
    public event Action<AdvertMessage>? AdvertPublished;

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
            var reader = new LineReader(stream);
            var connection = new Connection(stream);
            try
            {
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                string? first;
                try
                {
                    first = await reader.ReadLineAsync(MaxHelloLength, helloTimeout.Token);
                }
                catch (LineTooLongException)
                {
                    first = null;
                }
                HelloMessage? hello = first == null ? null : Parse<HelloMessage>(first, MessageTypes.Hello);
                int port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                if (hello == null || !BridgeAuth.Check(_token, BridgeAuth.HelloPurpose, hello.Nonce, port, hello.Proof) || !FirstUse(hello.Nonce))
                {
                    AppLog.Warn("Something connected to the launcher's bridge port without this launcher's token; closed it.");
                    return;
                }

                Connection? previous = Interlocked.Exchange(ref _current, connection);
                previous?.Close();
                Hello = hello;
                State = null;
                Rooms = null;
                await SendOn(connection, new WelcomeMessage { Proof = BridgeAuth.Proof(_token, BridgeAuth.WelcomePurpose, hello.Nonce, port), LauncherVersion = AppInfo.Version });
                AppLog.Info($"Void Crew connected (bridge {hello.BridgeVersion}, game {hello.GameVersion}, {hello.Plugins?.Count ?? 0} plugins).");
                Connected?.Invoke(hello);

                bool skippedOne = false;
                while (!_stop.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(MaxLineLength, _stop.Token);
                    }
                    catch (LineTooLongException)
                    {
                        // Lobby data from other players that got far too big; the next list is likely fine. Said once.
                        if (!skippedOne) AppLog.Warn($"The game sent a message longer than {MaxLineLength / (1024 * 1024)} MB (a lobby list?); skipped it.");
                        skippedOne = true;
                        continue;
                    }
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

    /// <summary>A hello is good once: one copied off the wire can't be sent again.</summary>
    private bool FirstUse(string nonce)
    {
        lock (_seenNonces)
        {
            if (_seenNonces.Count > 1000) _seenNonces.Clear();
            return _seenNonces.Add(nonce);
        }
    }

    /// <summary>
    /// Handles one message from the game. A message the launcher can't read (wrong types, say) is logged and skipped, and
    /// so is a handler that fails: neither drops the connection.
    /// </summary>
    private void Dispatch(string line)
    {
        Action? raise;
        try
        {
            raise = ReadMessage(line);
        }
        catch (Exception e)
        {
            AppLog.Warn($"The bridge sent a message the launcher couldn't read ({e.Message}); skipped it.");
            return;
        }
        try
        {
            raise?.Invoke();
        }
        catch (Exception e)
        {
            AppLog.Error("Handling a message from the game failed", e);
        }
    }

    /// <summary>Reads a message (keeping the newest state and rooms) and returns the event to raise for it, if any.</summary>
    private Action? ReadMessage(string line)
    {
        JsonNode? node = JsonNode.Parse(line);
        if (node is not JsonObject message || message["type"] is not JsonValue typeValue || !typeValue.TryGetValue(out string? type)) return null;
        switch (type)
        {
            case MessageTypes.State:
                if (message.Deserialize<StateMessage>(JsonFiles.Protocol) is not { } state) return null;
                State = state;
                return () => StateChanged?.Invoke(state);
            case MessageTypes.Rooms:
                if (message.Deserialize<RoomsMessage>(JsonFiles.Protocol) is not { } rooms) return null;
                Rooms = rooms;
                return () => RoomsChanged?.Invoke(rooms);
            case MessageTypes.JoinResult:
                if (message.Deserialize<JoinResultMessage>(JsonFiles.Protocol) is not { } join) return null;
                // Its error in the player's language (the bridge says it in English).
                BridgeErrorText.Translate(join);
                return () => JoinResult?.Invoke(join);
            case MessageTypes.HostResult:
                if (message.Deserialize<HostResultMessage>(JsonFiles.Protocol) is not { } host) return null;
                BridgeErrorText.Translate(host);
                return () => HostResult?.Invoke(host);
            case MessageTypes.Disconnected:
                return message.Deserialize<DisconnectedMessage>(JsonFiles.Protocol) is { } left ? () => GameLeftRoom?.Invoke(left) : null;
            case MessageTypes.Advert:
                return message.Deserialize<AdvertMessage>(JsonFiles.Protocol) is { } advert ? () => AdvertPublished?.Invoke(advert) : null;
            default:
                return null;
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

/// <summary>
/// Reads the bridge's lines (UTF-8, ending in \n) up to a length: StreamReader keeps reading a line that never ends, so
/// a program on the port, or a lobby list grown out of all proportion, could fill the memory.
/// </summary>
internal sealed class LineReader(Stream stream)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Decoder _decoder = Utf8.GetDecoder();
    private readonly byte[] _bytes = new byte[16 * 1024];
    private readonly char[] _chars = new char[Utf8.GetMaxCharCount(16 * 1024)];
    private readonly StringBuilder _line = new();
    private int _next;
    private int _end;

    /// <summary>The rest of a line that was too long, skipped before the next line is read.</summary>
    private bool _skipping;

    /// <summary>
    /// The next line without its line break, or null when the stream has ended. Throws <see cref="LineTooLongException"/>
    /// as soon as a line passes <paramref name="maxLength"/> characters, without reading the rest of it: the next call
    /// skips that first.
    /// </summary>
    public async Task<string?> ReadLineAsync(int maxLength, CancellationToken cancel)
    {
        while (true)
        {
            if (_next == _end)
            {
                int read = await stream.ReadAsync(_bytes, cancel);
                if (read == 0) return null;
                _end = _decoder.GetChars(_bytes, 0, read, _chars, 0);
                _next = 0;
                continue;
            }
            int start = _next;
            int newline = Array.IndexOf(_chars, '\n', start, _end - start);
            int count = (newline >= 0 ? newline : _end) - start;
            _next = newline >= 0 ? newline + 1 : _end;
            if (_skipping)
            {
                if (newline >= 0) _skipping = false;
                continue;
            }
            if (_line.Length + count > maxLength)
            {
                _line.Clear();
                _skipping = newline < 0;
                throw new LineTooLongException(maxLength);
            }
            _line.Append(_chars, start, count);
            if (newline < 0) continue;
            if (_line.Length > 0 && _line[^1] == '\r') _line.Length--;
            string line = _line.ToString();
            _line.Clear();
            return line;
        }
    }
}

/// <summary>A line from the bridge was longer than the reader takes.</summary>
internal sealed class LineTooLongException(int maxLength) : IOException($"A line from the game was longer than {maxLength} characters.");
