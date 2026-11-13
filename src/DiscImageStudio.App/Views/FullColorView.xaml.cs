using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class FullColorView : UserControl
{
    public FullColorView(FullColorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Inspector is null || InspectorColumn is null) return;
        bool narrow = ActualWidth < 1000;
        InspectorColumn.Width = narrow ? new GridLength(0) : new GridLength(360);
        Grid.SetRow(Inspector, narrow ? 1 : 0);
        Grid.SetColumn(Inspector, narrow ? 0 : 1);
        Inspector.Margin = narrow ? new Thickness(0, 20, 0, 0) : new Thickness(0);
        PreviewPanel.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 20, 0);
    }
}
