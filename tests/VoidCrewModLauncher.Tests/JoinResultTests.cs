using System.Text.Json;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

/// <summary>
/// A join the lobby turned away for mods says so in its result, so the launcher shows only the mods dialog (FIX AND
/// REJOIN) that follows. Bridges before 0.9.1 leave the reason out, and launchers before it skip the field.
/// </summary>
public class JoinResultTests
{
    /// <summary>A result as the bridge writes it (camelCase, nulls left out).</summary>
    private const string Refused = """{"type":"joinResult","roomId":"A1","ok":false,"error":"The lobby's mods don't match yours.","reason":"mods"}""";

    [Fact]
    public void The_join_result_says_why_and_an_older_bridges_result_still_reads()
    {
        JoinResultMessage refused = JsonSerializer.Deserialize<JoinResultMessage>(Refused, JsonFiles.Protocol)!;
        Assert.Equal("mods", refused.Reason);
        Assert.Equal("A1", refused.RoomId);
        Assert.False(refused.Ok);

        JoinResultMessage older = JsonSerializer.Deserialize<JoinResultMessage>(
            """{"type":"joinResult","roomId":"A1","ok":false,"error":"The lobby's mods don't match yours."}""", JsonFiles.Protocol)!;
        Assert.Null(older.Reason);
        Assert.Equal("The lobby's mods don't match yours.", older.Error);
    }

    [Fact]
    public void A_launcher_before_the_reason_reads_the_result_as_it_always_did()
    {
        OldJoinResult result = JsonSerializer.Deserialize<OldJoinResult>(Refused, JsonFiles.Protocol)!;
        Assert.Equal("A1", result.RoomId);
        Assert.False(result.Ok);
        Assert.Equal("The lobby's mods don't match yours.", result.Error);
    }

    /// <summary>JoinResultMessage as launcher 0.8.2 had it.</summary>
    private sealed class OldJoinResult
    {
        public string? Type { get; set; }
        public string? RoomId { get; set; }
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }

    // ---------------------------------------------------------------- errors in the player's language

    [Fact]
    public void Every_error_code_has_the_same_English_sentence_on_both_sides()
    {
        // The bridge sends the shared English sentence in Error; the launcher's Loc key for the code must be that sentence,
        // so English players read exactly what they did before, and every translation is of the right sentence.
        var details = new List<string> { "eu", "45" };
        foreach (string code in BridgeErrors.All)
        {
            string? english = BridgeErrors.English(code, details);
            Assert.False(string.IsNullOrEmpty(english), code);
            Assert.Equal(english, Core.Bridge.BridgeErrorText.Text(code, details));
        }
        Assert.Equal(BridgeErrors.All.Length, BridgeErrors.All.Distinct().Count());
        Assert.Null(BridgeErrors.English("fromANewerBridge", details));
        Assert.Null(Core.Bridge.BridgeErrorText.Text("fromANewerBridge", details));
        Assert.Null(Core.Bridge.BridgeErrorText.Text(null, null));
    }

    [Fact]
    public void A_sentence_with_missing_details_still_reads()
    {
        Assert.Equal("This lobby is in another region (). Leave your game, then join it from the main menu.", BridgeErrors.English(BridgeErrors.OtherRegion, null!));
        Assert.Equal("Photon refused the join (32760): ", Core.Bridge.BridgeErrorText.Text(BridgeErrors.JoinRefused, new List<string> { "32760" }));
    }

    [Fact]
    public async Task The_launcher_shows_a_coded_error_in_the_players_language_and_an_uncoded_one_as_sent()
    {
        // As a bridge after 0.9.1 writes it: the code with its details, and the English sentence for older launchers.
        const string Coded = """{"type":"hostResult","ok":false,"error":"Void Crew didn't open the lobby within 45 seconds.","errorCode":"hostTimeout","errorDetails":["45"]}""";
        HostResultMessage result = await ReceiveAsync<HostResultMessage>(Coded, (server, received) => server.HostResult += r => received.TrySetResult(r));
        Assert.Equal("hostTimeout", result.ErrorCode);
        Assert.Equal(Core.Bridge.BridgeErrorText.Text(BridgeErrors.HostTimeout, new List<string> { "45" }), result.Error);

        // A bridge up to 0.9.1, or a code this launcher doesn't know: the bridge's own sentence.
        const string Plain = """{"type":"joinResult","roomId":"A1","ok":false,"error":"Something new.","errorCode":"somethingNew"}""";
        JoinResultMessage join = await ReceiveAsync<JoinResultMessage>(Plain, (server, received) => server.JoinResult += r => received.TrySetResult(r));
        Assert.Equal("Something new.", join.Error);

        // Launchers up to 0.9.1 read the new fields' message as they always did.
        OldJoinResult old = JsonSerializer.Deserialize<OldJoinResult>(Coded.Replace("hostResult", "joinResult"), JsonFiles.Protocol)!;
        Assert.Equal("Void Crew didn't open the lobby within 45 seconds.", old.Error);
    }

    /// <summary>Connects to a launcher's bridge port as the game would, sends <paramref name="line"/> and returns what the launcher raised for it.</summary>
    private static async Task<T> ReceiveAsync<T>(string line, Action<Core.Bridge.BridgeServer, TaskCompletionSource<T>> subscribe)
    {
        const string Token = "0123456789abcdef0123456789abcdef";
        using var server = new Core.Bridge.BridgeServer(Token);
        Assert.True(server.Start());
        var received = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(server, received);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
        System.Net.Sockets.NetworkStream stream = client.GetStream();
        string nonce = BridgeAuth.NewNonce();
        var hello = new HelloMessage { Nonce = nonce, Proof = BridgeAuth.Proof(Token, BridgeAuth.HelloPurpose, nonce, server.Port) };
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hello, JsonFiles.Protocol) + "\n" + line + "\n");
        await stream.WriteAsync(bytes);
        return await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
