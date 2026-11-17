using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LGMods.MapDoctor.App.ViewModels;

namespace LGMods.MapDoctor.App.Views;

public partial class ResultsView : UserControl
{
    private ResultsViewModel? _vm;

    public ResultsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as ResultsViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
    }

    // A new map or clash always starts at the top.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsViewModel.Detail)) DetailScroll.ScrollToTop();
    }

    // Jumping to a map or clash from elsewhere should bring it into view in the list.
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } list) list.ScrollIntoView(item);
    }
}
