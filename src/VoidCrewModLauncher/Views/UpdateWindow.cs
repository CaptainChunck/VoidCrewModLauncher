using System.Windows;
using System.Windows.Controls;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Setup;
using VoidCrewModLauncher.Infrastructure;

namespace VoidCrewModLauncher.Views;

/// <summary>Tells the player the launcher is updating itself, with the download's progress and a way to stop it.</summary>
public sealed class UpdateWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _progress;
    private readonly CancellationTokenSource _cancel = new();
    private bool _done;
    private bool _closed;

    private UpdateWindow(LauncherRelease release)
    {
        var body = new StackPanel { Width = 460 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.F("{0} {1} is out. The launcher is downloading it now and opens again once it's installed.", AppInfo.Name, release.Version),
            Style = (Style)Application.Current.Resources["Body"],
            TextWrapping = TextWrapping.Wrap,
        });
        _bar = new ProgressBar { Maximum = 1, Height = 8, Margin = new Thickness(0, 18, 0, 0) };
        body.Children.Add(_bar);
        _progress = new TextBlock { Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 6, 0, 0), Text = Loc.T("Starting the download...") };
        body.Children.Add(_progress);

        Dialogs.Decorate(this, Loc.T("Updating"), body, out StackPanel buttons);
        Button cancel = Dialogs.MakeButton(Loc.T("CANCEL"), ButtonKind.Normal);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        Closing += (_, _) =>
        {
            if (!_done) _cancel.Cancel();
        };
        Closed += (_, _) => _closed = true;
    }

    /// <summary>Downloads and checks the release while the window shows. The setup's path, or null when cancelled or failed.</summary>
    public static string? Download(Window? owner, Updater updater, LauncherRelease release, out Exception? error)
    {
        var window = new UpdateWindow(release) { Owner = owner is { IsVisible: true } ? owner : null };
        if (window.Owner == null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        string? setup = null;
        Exception? failure = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var progress = new Progress<(long Done, long Total)>(p => window.Report(p.Done, p.Total));
                string file = await updater.DownloadAsync(release, progress, window._cancel.Token);
                window._progress.Text = Loc.T("Checking the download...");
                await Task.Run(() => Updater.CheckSetup(file, release.Version));
                setup = file;
            }
            catch (Exception e)
            {
                failure = e;
            }
            window._done = true;
            if (!window._closed) window.Close();
        };
        window.ShowDialog();
        error = failure;
        return setup;
    }

    private void Report(long done, long total)
    {
        _bar.Value = total > 0 ? Math.Min(1.0, done / (double)total) : 0;
        _progress.Text = total > 0 ? Loc.F("{0} of {1}", Format.Bytes(done), Format.Bytes(total)) : Format.Bytes(done);
    }
}
