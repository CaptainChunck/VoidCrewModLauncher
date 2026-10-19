using System.Windows.Controls;
using System.Windows.Input;
using VoidCrewModLauncher.ViewModels;

namespace VoidCrewModLauncher.Views;

public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
    }

    /// <summary>Double-clicking a lobby joins it, as in the game's own list.</summary>
    private void Lobbies_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BrowserViewModel model && model.Selected is { } lobby && model.JoinCommand.CanExecute(lobby))
        {
            model.JoinCommand.Execute(lobby);
        }
    }
}
