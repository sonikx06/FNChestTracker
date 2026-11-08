using System.Windows.Controls;
using System.Windows.Input;
using ChestTracker.App.ViewModels;

namespace ChestTracker.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel vm && MatchesGrid.SelectedItem is MatchRowViewModel row)
        {
            vm.EditCommand.Execute(row);
        }
    }
}
