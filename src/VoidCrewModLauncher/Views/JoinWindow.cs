using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Infrastructure;

namespace VoidCrewModLauncher.Views;

/// <summary>
/// JOIN's check before anything happens: the lobby's mods (tickable where the launcher only guessed they're needed),
/// what will be downloaded, which of your mods stay on, and whether the game restarts.
/// </summary>
public sealed class JoinWindow : Window
{
    private readonly JoinPlan _plan;
    private readonly Func<JoinPlan, ResolvedJoin> _resolve;
    private readonly Func<ResolvedJoin, bool> _needsRestart;
    private readonly TextBlock _summary;
    private readonly Button _join;
    private readonly bool _running;
    private readonly bool _inGame;

    /// <param name="needsRestart">Whether the running game has to restart for this mod set (ticking mods can change it).</param>
    /// <param name="inGame">The game is in a hub or on a mission: a restart leaves it at once, which the summary says.</param>
    public JoinWindow(JoinPlan plan, Func<JoinPlan, ResolvedJoin> resolve, Func<ResolvedJoin, bool> needsRestart, bool running, bool inGame = false)
    {
        _plan = plan;
        _resolve = resolve;
        _needsRestart = needsRestart;
        _running = running;
        _inGame = inGame;
        _join = Dialogs.MakeButton(Loc.T("JOIN"), ButtonKind.Primary);
        var body = new StackPanel { Width = 600 };

        body.Children.Add(Text($"{plan.Lobby.Name}", 17, Brushes.White, FontWeights.SemiBold));
        body.Children.Add(Text(Loc.F("hosted by {0} · {1}/{2} crew", plan.Lobby.Room.Host, plan.Lobby.Room.Players, plan.Lobby.Room.MaxPlayers), 13, Brush("TextMuted")));

        var needed = plan.Lines.Where(l => l.Mod.JoinerNeeds).ToList();
        var notNeeded = plan.Lines.Where(l => !l.Mod.JoinerNeeds).ToList();
        if (needed.Count > 0)
        {
            body.Children.Add(Heading(Loc.T("THIS LOBBY NEEDS")));
            foreach (JoinPlanLine line in needed) body.Children.Add(LineRow(line));
        }
        else
        {
            body.Children.Add(Heading(Loc.T("MODS")));
            body.Children.Add(Text(plan.Lobby.ModsUnknown ? Loc.T("The lobby doesn't list its mods.") : Loc.T("None."), 13, Brush("TextSecondary")));
        }
        if (notNeeded.Count > 0)
        {
            body.Children.Add(Text(Loc.F("Host only: {0}", string.Join(", ", notNeeded.Select(l => l.Mod.DisplayName))), 12.5, Brush("TextMuted"), margin: new Thickness(0, 8, 0, 0)));
        }

        body.Children.Add(Heading(Loc.T("YOUR MODS FOR THIS GAME")));
        body.Children.Add(Text(plan.Kept.Count == 0 ? Loc.T("Stay on: none") : Loc.F("Stay on: {0}", string.Join(", ", plan.Kept.Select(m => m.DisplayName))), 13, Brush("TextSecondary")));
        if (plan.TurnedOff.Count > 0)
        {
            body.Children.Add(Text(Loc.F("Off for this game: {0}", string.Join(", ", plan.TurnedOff.Select(m => m.DisplayName))), 13, Brush("TextMuted"), margin: new Thickness(0, 4, 0, 0)));
        }
        foreach (string warning in plan.Warnings)
        {
            body.Children.Add(Text(warning, 13, Brush("Orange"), margin: new Thickness(0, 10, 0, 0)));
        }

        _summary = Text("", 13.5, Brushes.White, FontWeights.SemiBold, new Thickness(0, 16, 0, 0));
        body.Children.Add(_summary);
        UpdateSummary();

        Dialogs.Decorate(this, Loc.T("Join lobby"), new ScrollViewer { Content = body, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, out StackPanel buttons);
        _join.IsDefault = true;
        _join.Click += (_, _) => DialogResult = true;
        Button cancel = Dialogs.MakeButton(Loc.T("CANCEL"), ButtonKind.Normal);
        cancel.IsCancel = true;
        buttons.Children.Add(_join);
        buttons.Children.Add(cancel);
    }

    private UIElement LineRow(JoinPlanLine line)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brush("Row") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = line.Include,
            IsEnabled = line.Optional && line.Status != LobbyModStatus.Unavailable,
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.Checked += (_, _) => { line.Include = true; UpdateSummary(); };
        check.Unchecked += (_, _) => { line.Include = false; UpdateSummary(); };
        row.Children.Add(check);

        var name = new StackPanel { Margin = new Thickness(6, 7, 8, 7) };
        string version = line.Version?.VersionNumber ?? line.Mod.Version;
        name.Children.Add(Text($"{line.Mod.DisplayName}  v{version}", 14, Brushes.White, FontWeights.SemiBold));
        string by = line.Mod.Package != null ? Loc.F("by {0}", line.Mod.Package.Author) : line.Mod.Local ? Loc.T("the host's own mod") : Loc.T("not on Thunderstore");
        name.Children.Add(Text(by, 12, Brush("TextMuted")));
        if (line.Note != null) name.Children.Add(Text(line.Note, 12, Brush("Orange")));
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        (string text, string color) = line.Status switch
        {
            LobbyModStatus.Ready => (Loc.T("INSTALLED"), "Green"),
            LobbyModStatus.Download => (Loc.T("DOWNLOAD"), "LightBlue"),
            LobbyModStatus.Unavailable => (Loc.T("CAN'T GET"), "Red"),
            _ => (Loc.T("NOT NEEDED"), "LightGrey"),
        };
        var badge = new Border
        {
            Style = (Style)Application.Current.Resources["Badge"],
            BorderBrush = Brush(color),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["BadgeText"], Foreground = Brush(color) },
        };
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);
        return row;
    }

    private void UpdateSummary()
    {
        ResolvedJoin join = _resolve(_plan);
        bool restart = _running && _needsRestart(join);
        string downloads = join.Downloads.Count == 0 ? ""
            : join.Downloads.Count == 1 ? Loc.F("Downloads {0} mod ({1}).", join.Downloads.Count, Format.Bytes(join.DownloadBytes)) + " "
            : Loc.F("Downloads {0} mods ({1}).", join.Downloads.Count, Format.Bytes(join.DownloadBytes)) + " ";
        bool leavesGame = restart && _inGame;
        string game = !_running ? Loc.T("Starts Void Crew and joins.")
            : leavesGame ? Loc.T("You're in a game right now. Void Crew restarts with the lobby's mods and joins it.")
            : restart ? Loc.T("Restarts Void Crew and joins.")
            : Loc.T("Joins right away.");
        _summary.Text = downloads + game;
        _summary.Foreground = leavesGame ? Brush("Orange") : Brushes.White;
        _join.Content = restart ? Loc.T("RESTART AND JOIN") : Loc.T("JOIN");
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["Label"],
        FontSize = 13,
        Margin = new Thickness(0, 16, 0, 6),
    };

    private static TextBlock Text(string text, double size, Brush color, FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0),
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
