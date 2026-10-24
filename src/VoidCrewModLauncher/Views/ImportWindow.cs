using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Infrastructure;

namespace VoidCrewModLauncher.Views;

/// <summary>
/// Picks which mods of another BepInEx folder to bring into the launcher, and for those that are on Thunderstore,
/// whether to install Thunderstore's copy (updates, shareable with lobbies) or copy the files as they are (your own
/// newer build, say).
/// </summary>
public sealed class ImportWindow : Window
{
    private readonly List<(ImportCandidate Candidate, CheckBox Import, RadioButton? Thunderstore)> _rows = new();

    public ImportWindow(List<ImportCandidate> candidates, string folder)
    {
        var body = new StackPanel { Width = 640 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.F("Mods in {0}", folder),
            Foreground = (Brush)Application.Current.Resources["TextSecondary"],
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        int group = 0;
        foreach (ImportCandidate candidate in candidates)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Background = (Brush)Application.Current.Resources["Row"] };
            var import = new CheckBox
            {
                IsChecked = true,
                Margin = new Thickness(10, 8, 10, 4),
                Content = new TextBlock
                {
                    Text = candidate.Files.Count == 1
                        ? Loc.F("{0}  v{1}  ·  {2} file", candidate.Name, candidate.Version, candidate.Files.Count)
                        : Loc.F("{0}  v{1}  ·  {2} files", candidate.Name, candidate.Version, candidate.Files.Count),
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            row.Children.Add(import);

            RadioButton? fromThunderstore = null;
            if (candidate.Match != null)
            {
                string name = "import" + group++;
                fromThunderstore = Choice(Loc.F("Install {0} {1} from Thunderstore (gets updates; other players can download it)", candidate.Match.DisplayName, candidate.Match.Latest?.VersionNumber), name);
                RadioButton local = Choice(candidate.LocalIsNewer ? Loc.T("Copy these files as they are (newer than Thunderstore's)") : Loc.T("Copy these files as they are"), name);
                (candidate.LocalIsNewer ? local : fromThunderstore).IsChecked = true;
                row.Children.Add(fromThunderstore);
                row.Children.Add(local);
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = Loc.T("Not on Thunderstore: copied as a local mod (other players would have to get it themselves)."),
                    Foreground = (Brush)Application.Current.Resources["TextMuted"],
                    FontSize = 12,
                    Margin = new Thickness(36, 0, 10, 8),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            _rows.Add((candidate, import, fromThunderstore));
            body.Children.Add(row);
        }

        body.Children.Add(new TextBlock
        {
            Text = Loc.T("Their settings files (BepInEx\\config) are copied too, unless the launcher already has them. The folder you import from isn't changed."),
            Foreground = (Brush)Application.Current.Resources["TextMuted"],
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        Dialogs.Decorate(this, Loc.T("Import mods"), new ScrollViewer { Content = body, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, out StackPanel buttons);
        Button ok = Dialogs.MakeButton(Loc.T("IMPORT"), ButtonKind.Primary);
        ok.IsDefault = true;
        ok.Click += (_, _) => DialogResult = true;
        Button cancel = Dialogs.MakeButton(Loc.T("CANCEL"), ButtonKind.Normal);
        cancel.IsCancel = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
    }

    public List<(ImportCandidate Candidate, bool FromThunderstore)> Selection =>
        _rows.Where(r => r.Import.IsChecked == true).Select(r => (r.Candidate, r.Thunderstore?.IsChecked == true)).ToList();

    private static RadioButton Choice(string text, string group) => new()
    {
        GroupName = group,
        Margin = new Thickness(36, 2, 10, 6),
        Foreground = (Brush)Application.Current.Resources["TextSecondary"],
        FontSize = 12.5,
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
        Cursor = System.Windows.Input.Cursors.Hand,
    };
}
