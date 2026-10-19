using System.Windows;

namespace VoidCrewModLauncher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => OnWindowStateChanged();
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
