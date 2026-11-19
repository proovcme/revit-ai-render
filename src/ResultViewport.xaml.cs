using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitAiRender;

public partial class ResultViewport : UserControl
{
    public ResultViewport()
    {
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            if (args.WidthChanged)
                ApplyCompactHeight();
        };
    }

    public bool Compact { get; set; }

    internal bool HasResult => AfterImage.Source != null;

    internal void Display(BitmapSource? source, BitmapSource? result, string emptyMessage)
    {
        CompareToggle.IsChecked = false;
        BeforeImage.Source = source;
        AfterImage.Source = result;
        EmptyText.Text = emptyMessage;
        EmptyText.Visibility = result == null ? Visibility.Visible : Visibility.Collapsed;
        ComparisonTools.Visibility = source != null && result != null ? Visibility.Visible : Visibility.Collapsed;
        ApplyCompactHeight();
        ApplyComparison();
    }

    private void ApplyCompactHeight()
    {
        if (!Compact)
            return;

        StageBorder.Height = AfterImage.Source is BitmapSource image && ActualWidth > 2
            ? Math.Min(420, (ActualWidth - 2) * image.PixelHeight / image.PixelWidth + 2)
            : 140;
    }

    private void ComparisonToggled(object sender, RoutedEventArgs args)
    {
        if (CompareSlider == null)
            return;
        if (CompareToggle.IsChecked == true)
            CompareSlider.Value = 50;
        ApplyComparison();
    }

    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> args) => ApplyComparison();
    private void StageResized(object sender, SizeChangedEventArgs args) => ApplyComparison();

    private void ApplyComparison()
    {
        if (Divider == null || CompareSlider == null || SplitTools == null || AfterLayer == null)
            return;

        var enabled = CompareToggle.IsChecked == true && BeforeImage.Source != null && AfterImage.Source != null;
        var value = enabled ? CompareSlider.Value : 0;
        SplitTools.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        BeforeImage.Visibility = value > 0 ? Visibility.Visible : Visibility.Hidden;
        AfterLayer.Clip = null;
        AfterLayer.Visibility = value < 100 ? Visibility.Visible : Visibility.Hidden;
        Divider.Visibility = Visibility.Collapsed;
        if (value <= 0 || value >= 100 || AfterImage.Source is not BitmapSource image)
            return;

        var width = Stage.ActualWidth;
        var height = Stage.ActualHeight;
        if (width <= 0 || height <= 0)
            return;
        var scale = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
        var frameWidth = scale * image.PixelWidth;
        var frameHeight = scale * image.PixelHeight;
        var left = (width - frameWidth) / 2 + frameWidth * value / CompareSlider.Maximum;
        AfterLayer.Clip = new RectangleGeometry(new Rect(left, 0, Math.Max(0, width - left), height));
        Divider.Height = frameHeight;
        Canvas.SetTop(Divider, (height - frameHeight) / 2);
        Canvas.SetLeft(Divider, Math.Max(0, left - 1));
        Divider.Visibility = Visibility.Visible;
    }
}
