namespace VoidCrewModLauncher.Core.Game;

/// <summary>Where Void Crew is, as far as its process and the bridge's connection tell (the bridge's own state says more).</summary>
public enum GameRunState
{
    NotRunning,

    /// <summary>Just started from the launcher: Steam or the game is still starting, and the bridge hasn't connected yet.</summary>
    Starting,

    /// <summary>Running without the launcher's bridge: started from Steam, or its bridge never loaded.</summary>
    RunningOutside,

    /// <summary>Its bridge was connected and has gone while the game's process is still there: the game is closing.</summary>
    Closing,

    /// <summary>The bridge is connected.</summary>
    Connected,
}

/// <summary>
/// Tells, each time the launcher looks (once a second), whether Void Crew is starting, running without the launcher,
/// closing or connected, from the game's process, the bridge's connection and the launcher's last launch. It remembers
/// which game process and which launch the bridge connected for: the bridge drops its connection the moment the game
/// starts to quit, seconds before the process ends, and that is a game closing, not one running without the launcher.
/// A launch whose bridge connected is never reported as one that didn't.
/// </summary>
public sealed class GameWatch
{
    /// <summary>How long the game has after a launch to connect before it counts as running without the launcher.</summary>
    public static readonly TimeSpan ConnectTime = TimeSpan.FromMinutes(3);

    /// <summary>How long Steam has after a launch to start the game's process (it may have to start and sign in first).</summary>
    public static readonly TimeSpan ProcessTime = TimeSpan.FromSeconds(150);

    /// <summary>
    /// How long a game whose bridge has gone may take to close. When only the connection broke, the bridge connects again
    /// within seconds; a game still running without it after this has lost the launcher.
    /// </summary>
    public static readonly TimeSpan CloseTime = TimeSpan.FromSeconds(60);

    /// <summary>The launch whose game process has been seen; once it has, the process going away means the game closed.</summary>
    private DateTime? _seenLaunch;

    /// <summary>The launch already reported as never connected (once per launch).</summary>
    private DateTime? _checkedLaunch;

    /// <summary>The launch the bridge connected for (null for a game started before this launcher session).</summary>
    private DateTime? _connectedLaunch;

    /// <summary>The game process the bridge connected from (0 when it didn't say), until that process has ended.</summary>
    private int? _connectedProcess;

    /// <summary>When the connected process's bridge went away.</summary>
    private DateTime? _goneUtc;

    /// <summary>The bridge connected from the game process <paramref name="processId"/>, after the launch <paramref name="lastLaunch"/>.</summary>
    public void Connected(int processId, DateTime? lastLaunch)
    {
        _connectedProcess = processId;
        _connectedLaunch = lastLaunch;
        _goneUtc = null;
    }

    /// <summary>
    /// The game's state now. <paramref name="checkLaunch"/> is true once for a launch whose game runs but never connected:
    /// the moment to write down what the launcher can see about why.
    /// </summary>
    /// <param name="processId">The running game's process id, when the launcher could read it.</param>
    /// <param name="lastLaunch">When the launcher last started the game (null when it hasn't this session).</param>
    public GameRunState Update(DateTime nowUtc, bool running, int? processId, bool bridgeConnected, DateTime? lastLaunch, out bool checkLaunch)
    {
        checkLaunch = false;
        if (!running)
        {
            // Whatever the bridge connected from has ended.
            _connectedProcess = null;
            _goneUtc = null;
            // Steam may need a while to start (and sign in) before the game's process appears.
            bool waitingForProcess = _seenLaunch != lastLaunch && lastLaunch is { } launched && nowUtc - launched < ProcessTime;
            return waitingForProcess ? GameRunState.Starting : GameRunState.NotRunning;
        }
        _seenLaunch = lastLaunch;
        if (bridgeConnected)
        {
            _goneUtc = null;
            return GameRunState.Connected;
        }
        // The process the bridge connected from (or one the launcher can't tell apart from it) is still there.
        if (_connectedProcess is { } connected && (connected == 0 || processId == null || processId == connected))
        {
            _goneUtc ??= nowUtc;
            if (nowUtc - _goneUtc < CloseTime) return GameRunState.Closing;
        }
        // Still starting until its bridge connects, for a while after the launch.
        if (lastLaunch is { } recent && nowUtc - recent < ConnectTime && _connectedLaunch != recent) return GameRunState.Starting;
        if (lastLaunch is { } launch && _checkedLaunch != launch && _connectedLaunch != launch)
        {
            _checkedLaunch = launch;
            checkLaunch = true;
        }
        return GameRunState.RunningOutside;
    }
}
