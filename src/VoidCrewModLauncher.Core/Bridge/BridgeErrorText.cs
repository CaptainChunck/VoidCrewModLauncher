using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Bridge;

/// <summary>
/// The bridge's join and host errors in the player's language. The bridge sends a code with each (see
/// <see cref="BridgeErrors"/>) next to its English sentence; a code this launcher doesn't know, and a bridge up to 0.9.1
/// that sends none, keep the English.
/// </summary>
public static class BridgeErrorText
{
    /// <summary>The sentence of <paramref name="code"/> in the player's language; null for a code this launcher doesn't know.</summary>
    public static string? Text(string? code, IReadOnlyList<string>? details)
    {
        // The details come from the game: one that is missing is left empty rather than breaking the sentence.
        string Detail(int i) => details != null && i < details.Count ? details[i] ?? "" : "";
        return code switch
        {
            BridgeErrors.NoLobby => Loc.T("No lobby to join."),
            BridgeErrors.Starting => Loc.T("Void Crew is still starting."),
            BridgeErrors.Busy => Loc.T("Void Crew is already joining or hosting a lobby."),
            BridgeErrors.InThisLobby => Loc.T("You're already in this lobby."),
            BridgeErrors.GameBusy => Loc.F("Void Crew is busy right now ({0}). Try again from the main menu or the hub.", Detail(0)),
            BridgeErrors.NotConnected => Loc.T("Void Crew isn't connected to its lobby server yet."),
            BridgeErrors.OnMission => Loc.T("You're on a mission. Leave it in the game first, then join from the hub or the main menu."),
            BridgeErrors.ModdedJoinsOff => Loc.T("This lobby is [mod_session] and your Void Crew settings don't allow joining those. Turn on \"Can join [mod_session] games\" in the game's General settings, then join again."),
            BridgeErrors.HostBlocked => Loc.T("You've blocked this lobby's host."),
            BridgeErrors.CrossPlayNotAllowed => Loc.T("This lobby has crossplay on, and crossplay isn't allowed for your account."),
            BridgeErrors.OtherPlatform => Loc.T("This lobby is on another platform and has crossplay off."),
            BridgeErrors.OtherRegion => Loc.F("This lobby is in another region ({0}). Leave your game, then join it from the main menu.", Detail(0)),
            BridgeErrors.NoLobbySettings => Loc.T("No lobby settings."),
            BridgeErrors.NotInMenu => Loc.T("Lobbies are hosted from the main menu; leave the current game first."),
            BridgeErrors.TutorialFirst => Loc.T("Void Crew only lets you host after you've finished its tutorial (the Awakening). Play it once, then host."),
            BridgeErrors.HostPanelMissing => Loc.T("Void Crew's host panel isn't loaded yet."),
            BridgeErrors.HostPanelChanged => Loc.T("This version of Void Crew's host panel is different from the one the launcher knows; host from the game's menu instead."),
            BridgeErrors.JoinTimeout => Loc.F("Void Crew didn't join the lobby within {0} seconds.", Detail(0)),
            BridgeErrors.HostTimeout => Loc.F("Void Crew didn't open the lobby within {0} seconds.", Detail(0)),
            BridgeErrors.LobbyGone => Loc.T("The lobby doesn't exist any more."),
            BridgeErrors.LobbyFull => Loc.T("The lobby is full."),
            BridgeErrors.LobbyClosed => Loc.T("The lobby is closed (its crew may have started a mission)."),
            BridgeErrors.JoinRefused => Loc.F("Photon refused the join ({0}): {1}", Detail(0), Detail(1)),
            BridgeErrors.ModsMismatch => Loc.T("The lobby's mods don't match yours."),
            BridgeErrors.Kicked => Loc.T("The host removed you."),
            BridgeErrors.ConnectionLost => Loc.T("The connection was lost."),
            BridgeErrors.CreateFailed => Loc.F("Photon couldn't create the lobby ({0}): {1}", Detail(0), Detail(1)),
            _ => null,
        };
    }

    /// <summary>Puts a join result's error into the player's language, when the bridge sent a code this launcher knows.</summary>
    internal static void Translate(JoinResultMessage result) => result.Error = Text(result.ErrorCode, result.ErrorDetails) ?? result.Error;

    /// <summary>Puts a host result's error into the player's language, when the bridge sent a code this launcher knows.</summary>
    internal static void Translate(HostResultMessage result) => result.Error = Text(result.ErrorCode, result.ErrorDetails) ?? result.Error;
}
