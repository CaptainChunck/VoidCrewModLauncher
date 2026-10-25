using VoidCrewModLauncher.Core.Game;

namespace VoidCrewModLauncher.Tests;

/// <summary>
/// What the top bar and the server browser say about the game, from its process, the bridge and the last launch. The
/// bridge drops its connection the moment the game starts to quit: that game is closing, not running without the
/// launcher, and only a launch that never connected gets the launch check written to the log.
/// </summary>
public class GameWatchTests
{
    private static readonly DateTime Launch = new(2026, 10, 24, 23, 50, 0, DateTimeKind.Utc);
    private const int Game = 4242;

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public void A_game_whose_bridge_connected_is_closing_when_the_bridge_goes_first()
    {
        // The player's own log: connected at 23:53, quit at 01:05, and the launcher said the game never connected.
        var watch = new GameWatch();
        Assert.Equal(GameRunState.Starting, watch.Update(Launch + 5 * Second, false, null, false, Launch, out bool check));
        Assert.Equal(GameRunState.Starting, watch.Update(Launch + 40 * Second, true, Game, false, Launch, out check));
        watch.Connected(Game, Launch);
        Assert.Equal(GameRunState.Connected, watch.Update(Launch + 3 * Minute, true, Game, true, Launch, out check));

        DateTime quit = Launch + 75 * Minute;
        Assert.Equal(GameRunState.Closing, watch.Update(quit, true, Game, false, Launch, out check));
        Assert.False(check);
        Assert.Equal(GameRunState.Closing, watch.Update(quit + 6 * Second, true, Game, false, Launch, out check));
        Assert.False(check);
        Assert.Equal(GameRunState.NotRunning, watch.Update(quit + 7 * Second, false, null, false, Launch, out check));
        Assert.False(check);
    }

    [Fact]
    public void A_launch_that_never_connects_is_checked_once_after_three_minutes()
    {
        var watch = new GameWatch();
        Assert.Equal(GameRunState.Starting, watch.Update(Launch + 20 * Second, true, Game, false, Launch, out bool check));
        Assert.Equal(GameRunState.Starting, watch.Update(Launch + GameWatch.ConnectTime - Second, true, Game, false, Launch, out check));
        Assert.False(check);

        Assert.Equal(GameRunState.RunningOutside, watch.Update(Launch + GameWatch.ConnectTime + Second, true, Game, false, Launch, out check));
        Assert.True(check);
        Assert.Equal(GameRunState.RunningOutside, watch.Update(Launch + GameWatch.ConnectTime + 2 * Second, true, Game, false, Launch, out check));
        Assert.False(check);
    }

    [Fact]
    public void Steam_has_a_while_to_start_the_game()
    {
        var watch = new GameWatch();
        Assert.Equal(GameRunState.NotRunning, watch.Update(Launch, false, null, false, null, out bool check));
        Assert.Equal(GameRunState.Starting, watch.Update(Launch + GameWatch.ProcessTime - Second, false, null, false, Launch, out check));
        Assert.Equal(GameRunState.NotRunning, watch.Update(Launch + GameWatch.ProcessTime + Second, false, null, false, Launch, out check));
        Assert.False(check);
    }

    [Fact]
    public void A_game_still_there_long_after_its_bridge_went_has_lost_the_launcher_but_did_connect()
    {
        var watch = new GameWatch();
        watch.Update(Launch + 30 * Second, true, Game, false, Launch, out _);
        watch.Connected(Game, Launch);
        DateTime gone = Launch + 20 * Minute;
        Assert.Equal(GameRunState.Closing, watch.Update(gone, true, Game, false, Launch, out bool check));
        Assert.Equal(GameRunState.Closing, watch.Update(gone + GameWatch.CloseTime - Second, true, Game, false, Launch, out check));

        Assert.Equal(GameRunState.RunningOutside, watch.Update(gone + GameWatch.CloseTime + Second, true, Game, false, Launch, out check));
        Assert.False(check);
    }

    [Fact]
    public void A_launch_that_connected_is_not_starting_again()
    {
        // Quit a minute after starting, and the game hangs on its way out.
        var watch = new GameWatch();
        watch.Update(Launch + 20 * Second, true, Game, false, Launch, out _);
        watch.Connected(Game, Launch);
        DateTime gone = Launch + Minute;
        Assert.Equal(GameRunState.Closing, watch.Update(gone, true, Game, false, Launch, out _));
        Assert.Equal(GameRunState.RunningOutside, watch.Update(gone + GameWatch.CloseTime + Second, true, Game, false, Launch, out bool check));
        Assert.False(check);
    }

    [Fact]
    public void The_bridge_connecting_again_ends_closing_and_a_later_drop_waits_afresh()
    {
        var watch = new GameWatch();
        watch.Connected(Game, Launch);
        DateTime drop = Launch + 10 * Minute;
        Assert.Equal(GameRunState.Closing, watch.Update(drop, true, Game, false, Launch, out _));

        watch.Connected(Game, Launch);
        Assert.Equal(GameRunState.Connected, watch.Update(drop + 50 * Second, true, Game, true, Launch, out _));

        DateTime again = drop + 100 * Second;
        Assert.Equal(GameRunState.Closing, watch.Update(again, true, Game, false, Launch, out _));
        Assert.Equal(GameRunState.Closing, watch.Update(again + GameWatch.CloseTime - Second, true, Game, false, Launch, out _));
        Assert.Equal(GameRunState.RunningOutside, watch.Update(again + GameWatch.CloseTime + Second, true, Game, false, Launch, out bool check));
        Assert.False(check);
    }

    [Fact]
    public void Another_game_after_the_one_that_connected_is_running_without_the_launcher()
    {
        var watch = new GameWatch();
        watch.Connected(Game, Launch);
        DateTime later = Launch + 30 * Minute;

        // Started from Steam right after (the launcher never saw the first one end): not the game that is closing.
        Assert.Equal(GameRunState.RunningOutside, watch.Update(later, true, 777, false, Launch, out bool check));
        // Its launch did connect, so there's nothing to check.
        Assert.False(check);

        // The same once the first game was seen to end.
        var other = new GameWatch();
        other.Connected(Game, Launch);
        Assert.Equal(GameRunState.NotRunning, other.Update(later, false, null, false, Launch, out _));
        Assert.Equal(GameRunState.RunningOutside, other.Update(later + Minute, true, 777, false, Launch, out check));
        Assert.False(check);
    }

    [Fact]
    public void A_game_whose_process_id_can_not_be_read_counts_as_the_one_that_connected()
    {
        // Running as administrator: the launcher can't open its process.
        var watch = new GameWatch();
        watch.Connected(Game, Launch);
        Assert.Equal(GameRunState.Closing, watch.Update(Launch + 30 * Minute, true, null, false, Launch, out _));
    }

    [Fact]
    public void A_game_started_before_the_launcher_closes_without_being_checked()
    {
        // The launcher was restarted while the game ran: no launch of its own.
        var watch = new GameWatch();
        Assert.Equal(GameRunState.RunningOutside, watch.Update(Launch, true, Game, false, null, out bool check));
        Assert.False(check);
        watch.Connected(Game, null);
        Assert.Equal(GameRunState.Connected, watch.Update(Launch + Second, true, Game, true, null, out _));
        Assert.Equal(GameRunState.Closing, watch.Update(Launch + Minute, true, Game, false, null, out check));
        Assert.False(check);
    }
}
