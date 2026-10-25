using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VoidCrewModLauncher.Core.Bridge;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

public class BridgeTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void A_proof_only_matches_the_same_token_nonce_and_port()
    {
        string proof = BridgeAuth.Proof(Token, BridgeAuth.HelloPurpose, "abc", 47815);
        Assert.True(BridgeAuth.Check(Token, BridgeAuth.HelloPurpose, "abc", 47815, proof));
        Assert.False(BridgeAuth.Check("another token", BridgeAuth.HelloPurpose, "abc", 47815, proof));
        Assert.False(BridgeAuth.Check(Token, BridgeAuth.HelloPurpose, "abd", 47815, proof));
        Assert.False(BridgeAuth.Check(Token, BridgeAuth.HelloPurpose, "abc", 47816, proof));
        Assert.False(BridgeAuth.Check(Token, BridgeAuth.WelcomePurpose, "abc", 47815, proof));
        Assert.False(BridgeAuth.Check(Token, BridgeAuth.HelloPurpose, "abc", 47815, null));
        Assert.DoesNotContain(Token, proof);
    }

    [Fact]
    public async Task The_launcher_welcomes_a_bridge_that_knows_the_token_and_proves_it_knows_it_too()
    {
        using var server = new BridgeServer(Token);
        Assert.True(server.Start());
        string nonce = BridgeAuth.NewNonce();
        var hello = new HelloMessage { Nonce = nonce, Proof = BridgeAuth.Proof(Token, BridgeAuth.HelloPurpose, nonce, server.Port), BridgeVersion = "test" };

        string? welcome = await HandshakeAsync(server.Port, hello);

        Assert.NotNull(welcome);
        using JsonDocument document = JsonDocument.Parse(welcome!);
        Assert.Equal(MessageTypes.Welcome, document.RootElement.GetProperty("type").GetString());
        string? proof = document.RootElement.GetProperty("proof").GetString();
        Assert.True(BridgeAuth.Check(Token, BridgeAuth.WelcomePurpose, nonce, server.Port, proof));
        Assert.DoesNotContain(Token, welcome);
    }

    [Fact]
    public async Task The_launcher_closes_a_connection_without_a_good_proof()
    {
        using var server = new BridgeServer(Token);
        Assert.True(server.Start());
        string nonce = BridgeAuth.NewNonce();

        // Wrong token, then a proof made for another port (passed on from somewhere else).
        Assert.Null(await HandshakeAsync(server.Port, new HelloMessage { Nonce = nonce, Proof = BridgeAuth.Proof("not it", BridgeAuth.HelloPurpose, nonce, server.Port) }));
        Assert.Null(await HandshakeAsync(server.Port, new HelloMessage { Nonce = nonce, Proof = BridgeAuth.Proof(Token, BridgeAuth.HelloPurpose, nonce, server.Port + 1) }));
        Assert.False(server.IsConnected);
    }

    [Fact]
    public async Task The_same_hello_only_works_once()
    {
        using var server = new BridgeServer(Token);
        Assert.True(server.Start());
        string nonce = BridgeAuth.NewNonce();
        var hello = new HelloMessage { Nonce = nonce, Proof = BridgeAuth.Proof(Token, BridgeAuth.HelloPurpose, nonce, server.Port) };

        Assert.NotNull(await HandshakeAsync(server.Port, hello));
        Assert.Null(await HandshakeAsync(server.Port, hello));
    }

    [Fact]
    public async Task A_first_line_longer_than_any_hello_closes_the_connection_without_being_read_to_its_end()
    {
        using var server = new BridgeServer(Token);
        Assert.True(server.Start());
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        NetworkStream stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        byte[] chunk = Encoding.ASCII.GetBytes(new string('x', 64 * 1024));
        bool closed = false;
        try
        {
            // A line that never ends, four times longer than the launcher reads before its hello window (10 s) is up.
            for (int sent = 0; sent < 4 * BridgeServer.MaxHelloLength; sent += chunk.Length) await stream.WriteAsync(chunk, timeout.Token);
            closed = await stream.ReadAsync(new byte[1], timeout.Token) == 0;
        }
        catch (IOException)
        {
            closed = true;
        }
        Assert.True(closed);
        Assert.False(server.IsConnected);
    }

    [Fact]
    public async Task Lines_are_read_up_to_a_length_and_a_longer_one_is_skipped()
    {
        string longLine = new('x', 40_000);
        byte[] bytes = Encoding.UTF8.GetBytes($"first\n{longLine}\nafter\r\n\n{longLine}\nlast\npartial");
        var reader = new LineReader(new MemoryStream(bytes));
        Assert.Equal("first", await reader.ReadLineAsync(50_000, default));
        // Longer than the limit (and than the reader's buffer): skipped to its end, then the next line reads as usual.
        await Assert.ThrowsAsync<LineTooLongException>(() => reader.ReadLineAsync(30_000, default));
        Assert.Equal("after", await reader.ReadLineAsync(30_000, default));
        Assert.Equal("", await reader.ReadLineAsync(30_000, default));
        Assert.Equal(longLine, await reader.ReadLineAsync(50_000, default));
        Assert.Equal("last", await reader.ReadLineAsync(50_000, default));
        // A last line without its line break never arrived whole.
        Assert.Null(await reader.ReadLineAsync(50_000, default));
    }

    [Fact]
    public async Task Characters_split_between_reads_come_out_whole()
    {
        const string text = "{\"name\":\"Équipage 日本語 ✓\"}";
        var reader = new LineReader(new Trickle(Encoding.UTF8.GetBytes(text + "\n" + text + "\n")));
        Assert.Equal(text, await reader.ReadLineAsync(100, default));
        Assert.Equal(text, await reader.ReadLineAsync(100, default));
        Assert.Null(await reader.ReadLineAsync(100, default));
    }

    /// <summary>A stream that gives one byte per read, as a slow socket might.</summary>
    private sealed class Trickle(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    /// <summary>Says hello like the bridge; returns the launcher's answer, or null when it closed the connection.</summary>
    private static async Task<string?> HandshakeAsync(int port, HelloMessage hello)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hello, Camel) + "\n");
        await stream.WriteAsync(line);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
