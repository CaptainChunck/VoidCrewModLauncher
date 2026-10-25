using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VoidCrewModLauncher.ViewModels;

namespace VoidCrewModLauncher.Views;

public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The ••• button opens its menu on a left click too. The menu's commands live on the page's view model, which the
    /// button's Tag carries (bound in the XAML, so a right click's menu has them as well).
    /// </summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Loads a package's icon when its row first appears.</summary>
    private void PackageRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PackageViewModel package } && DataContext is ModsViewModel model) model.EnsureIcon(package);
    }
}
