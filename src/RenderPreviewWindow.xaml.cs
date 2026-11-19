using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace RevitAiRender;

public partial class RenderPreviewWindow : Window
{
    private RenderJob? current;

    internal RenderPreviewWindow() => InitializeComponent();

    internal void Display(RenderJob job, BitmapSource? source, BitmapSource result)
    {
        current = job;
        ViewTitle.Text = job.View;
        ViewTitle.ToolTip = job.View;
        RenderDetails.Text = $"{job.Project} · {RenderPanel.ResultDetails(job, result)}";
        RenderDetails.ToolTip = RenderDetails.Text;
        Feedback.Visibility = Visibility.Collapsed;
        Viewer.Display(source, result, "");
    }

    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (current == null)
            return;
        var message = await RenderPanel.SaveResultAsync(current, this);
        if (message != null)
        {
            Feedback.Text = message;
            Feedback.Visibility = Visibility.Visible;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            Close();
        }
        base.OnPreviewKeyDown(args);
    }
}
