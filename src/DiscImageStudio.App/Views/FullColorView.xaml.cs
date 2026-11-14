using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class FullColorView : UserControl
{
    public FullColorView(FullColorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PropertyChangedEventManager.AddHandler(viewModel, ViewModel_PropertyChanged, nameof(FullColorViewModel.IsDvdChart));
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyLayout();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args) => ApplyLayout();

    private void ApplyLayout()
    {
        if (Inspector is null || InspectorColumn is null) return;
        bool narrow = ActualWidth < 1000;
        bool chart = DataContext is FullColorViewModel { IsDvdChart: true };
        InspectorColumn.Width = narrow || chart ? new GridLength(0) : new GridLength(360);
        Grid.SetRow(Inspector, narrow ? 1 : 0);
        Grid.SetColumn(Inspector, narrow ? 0 : 1);
        Inspector.Margin = narrow ? new Thickness(0, 20, 0, 0) : new Thickness(0);
        PreviewPanel.Margin = narrow || chart ? new Thickness(0) : new Thickness(0, 0, 20, 0);
    }
}
