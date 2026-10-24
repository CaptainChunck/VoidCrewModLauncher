using System.ComponentModel;
using System.Windows;
using VoidCrewModLauncher.ViewModels;

namespace VoidCrewModLauncher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => OnWindowStateChanged();
        Closing += OnClosing;
    }

    /// <summary>Closing in the middle of a download or install leaves it half done: ask first.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBusy: true, ClosingForUpdate: false } model) return;
        if (!model.Dialogs.Confirm(Loc.T("Close the launcher"), Loc.T("The launcher is still busy. Close it anyway?"), Loc.T("CLOSE"))) e.Cancel = true;
    }

    private void OnWindowStateChanged()
    {
        // A maximised borderless window reaches past the screen by its resize border; keep the content on screen.
        Frame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(1);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
