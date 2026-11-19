using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitAiRender;

public sealed class RevitAddin : IExternalApplication
{
    internal static readonly DockablePaneId PaneId = new(new Guid("299525f7-989d-41de-a7ca-d84d30f1bf41"));
    internal static RenderPanel? Panel { get; private set; }
    private RevitViewService? views;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            views = new RevitViewService();
            Panel = new RenderPanel(views) { HostWindow = application.MainWindowHandle };
            application.RegisterDockablePane(PaneId, "AI Render 0.2", Panel);
            application.CreateRibbonTab("AI Render");
            var ribbon = application.CreateRibbonPanel("AI Render", "Визуализация");
            AddButton<ShowRenderPanel>(ribbon, "Кадр", "Редактировать полный кадр через Siliconed", "frame");
            AddButton<ShowMaskPanel>(ribbon, "Маска", "Выделить область для редактирования через MFLUX", "mask");
            AddButton<ShowResultPanel>(ribbon, "Результат", "Показать последний готовый рендер", "result");
            AddButton<ShowHistoryPanel>(ribbon, "История", "Открыть сохранённые задачи и параметры", "history");
            application.ViewActivated += OnViewActivated;
            application.ControlledApplication.DocumentClosed += OnDocumentClosed;
            Diagnostics.Write("addin-started", application.ControlledApplication.VersionNumber + " 0.2.1");
            return Result.Succeeded;
        }
        catch (Exception error)
        {
            Diagnostics.Exception("addin-startup", error);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ViewActivated -= OnViewActivated;
        application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
        Panel?.StopMonitoring();
        Panel = null;
        views?.Dispose();
        return Result.Succeeded;
    }

    internal static Result Show(ExternalCommandData data, int section, ref string message)
    {
        try
        {
            data.Application.GetDockablePane(PaneId).Show();
            if (Panel != null)
            {
                Panel.HostWindow = data.Application.MainWindowHandle;
                Panel.OpenSection(section);
            }
            return Result.Succeeded;
        }
        catch (Exception error)
        {
            Diagnostics.Exception("pane-open", error);
            message = "Revit не открыл панель AI Render. Откройте проект и повторите.";
            return Result.Failed;
        }
    }

    private static void AddButton<T>(RibbonPanel ribbon, string title, string tooltip, string glyph)
    {
        var data = new PushButtonData("RevitAiRender020." + glyph, title, Assembly.GetExecutingAssembly().Location, typeof(T).FullName)
        {
            ToolTip = tooltip,
            LargeImage = DrawIcon(glyph, 32),
            Image = DrawIcon(glyph, 16)
        };
        ribbon.AddItem(data);
    }

    private static ImageSource DrawIcon(string glyph, int size)
    {
        var visual = new DrawingVisual();
        var baseBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 99, 88));
        var pale = new SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 240, 232));
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            context.DrawRoundedRectangle(baseBrush, null, new Rect(1, 1, 30, 30), 5, 5);
            var pen = new Pen(Brushes.White, 1.75) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            switch (glyph)
            {
                case "frame":
                    context.DrawRoundedRectangle(null, pen, new Rect(6.5, 8, 19, 16), 2, 2);
                    context.DrawGeometry(null, pen, Geometry.Parse("M 8 22 L 13 16 L 17 20 L 21 15 L 25 20"));
                    context.DrawEllipse(pale, null, new System.Windows.Point(12, 12), 1.5, 1.5);
                    break;
                case "mask":
                    context.DrawRoundedRectangle(null, pen, new Rect(6.5, 7, 19, 18), 2, 2);
                    context.DrawRectangle(pale, null, new Rect(11, 11, 10, 10));
                    break;
                case "result":
                    context.DrawRoundedRectangle(null, pen, new Rect(6.5, 7.5, 19, 17), 2, 2);
                    context.DrawGeometry(null, pen, Geometry.Parse("M 11 16 L 15 20 L 22 12"));
                    break;
                default:
                    context.DrawEllipse(null, pen, new System.Windows.Point(16, 16), 9, 9);
                    context.DrawGeometry(null, pen, Geometry.Parse("M 16 10 L 16 16 L 21 18"));
                    break;
            }
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void OnViewActivated(object? sender, ViewActivatedEventArgs args) => Panel?.ScheduleRefresh();

    private static void OnDocumentClosed(object? sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs args) =>
        Panel?.ScheduleRefresh();
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowRenderPanel : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        RevitAddin.Show(data, 0, ref message);
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowMaskPanel : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        RevitAddin.Show(data, 1, ref message);
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowResultPanel : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        RevitAddin.Show(data, 3, ref message);
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowHistoryPanel : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        RevitAddin.Show(data, 2, ref message);
}
