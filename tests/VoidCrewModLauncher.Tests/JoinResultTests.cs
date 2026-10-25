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
}
