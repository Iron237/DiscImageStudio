using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class DvdPhotoRegistrationWindow : Window
{
    private readonly List<Point> _points = [];
    public IReadOnlyList<Point> Points => _points.AsReadOnly();
    private static readonly string[] Labels = ["T · 10", "R · 11", "B · 12", "L · 13"];
    internal DvdPhotoRegistrationWindow(string path)
    {
        InitializeComponent();
        Photo.Source = FullColorViewModel.ReadBitmap(path);
        Redraw();
    }

    private Rect ImageRect()
    {
        if (Photo.Source is not BitmapSource bitmap) return Rect.Empty;
        double scale = Math.Min(Photo.ActualWidth / bitmap.PixelWidth, Photo.ActualHeight / bitmap.PixelHeight);
        return new((Photo.ActualWidth - bitmap.PixelWidth * scale) / 2,
            (Photo.ActualHeight - bitmap.PixelHeight * scale) / 2, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
    }

    private void Photo_Click(object sender, MouseButtonEventArgs args)
    {
        Rect rect = ImageRect(); Point point = args.GetPosition(Photo);
        if (_points.Count >= 4 || rect.IsEmpty || rect.Width <= 0 || !rect.Contains(point)) return;
        _points.Add(new((point.X - rect.X) / rect.Width, (point.Y - rect.Y) / rect.Height));
        Redraw();
    }

    private void Reset_Click(object sender, RoutedEventArgs args) { _points.Clear(); Redraw(); }
    private void Save_Click(object sender, RoutedEventArgs args) { if (_points.Count == 4) DialogResult = true; }
    private void Image_SizeChanged(object sender, SizeChangedEventArgs args) { if (Photo is not null && Overlay is not null) Redraw(); }
    private void Redraw()
    {
        Overlay.Children.Clear();
        Rect rect = ImageRect();
        for (int i = 0; i < _points.Count; i++)
        {
            double x = rect.X + _points[i].X * rect.Width, y = rect.Y + _points[i].Y * rect.Height;
            Ellipse marker = new() { Width = 14, Height = 14, Stroke = Brushes.Lime, StrokeThickness = 2 };
            Canvas.SetLeft(marker, x - 7); Canvas.SetTop(marker, y - 7); Overlay.Children.Add(marker);
            TextBlock label = new() { Text = Labels[i], Foreground = Brushes.Lime, Background = Brushes.Black };
            Canvas.SetLeft(label, x + 9); Canvas.SetTop(label, y - 8); Overlay.Children.Add(label);
        }
        SaveButton.IsEnabled = _points.Count == 4;
        ProgressText.Text = _points.Count < 4 ? L.T("DvdCal.ClickNext") + " " + Labels[_points.Count] : L.T("DvdCal.PointsComplete");
    }
}
