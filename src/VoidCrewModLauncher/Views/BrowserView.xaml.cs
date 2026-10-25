using System.Windows;
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

    /// <summary>
    /// Double-clicking a lobby joins it, as in the game's own list. Only a lobby's row: not the scroll bar or the empty
    /// space under the rows, which belong to the list too.
    /// </summary>
    private void Lobbies_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject clicked || Lobbies.ContainerFromElement(clicked) is not ListBoxItem { DataContext: LobbyViewModel lobby }) return;
        if (DataContext is BrowserViewModel model && model.JoinCommand.CanExecute(lobby))
        {
            model.JoinCommand.Execute(lobby);
        }
    }
}
