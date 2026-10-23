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
