using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitAiRender;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var panel = new RenderPanel(new RevitViewService());
        var mode = (CheckBox)panel.FindName("MaskedMode");
        var editor = (MaskEditor)panel.FindName("MaskCanvas");
        if (editor.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Mask canvas unexpectedly shown in the full-frame mode.");
        mode.IsChecked = true;
        if (editor.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Mask canvas missing in the local mode.");
        var tool = (System.Windows.Controls.ComboBox)panel.FindName("MaskToolBox");
        tool.SelectedIndex = 2;
        if (editor.Tool != MaskTool.Rectangle)
            throw new InvalidOperationException("The mask tool selector did not update the editor.");
        mode.IsChecked = false;
        if (editor.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Mode change did not hide the mask canvas.");
        editor = new MaskEditor();
        editor.SetFrame(BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, new int[64 * 32], 64 * 4));
        Arrange(editor, 320, 240);
        CheckPixel(editor, new Point(50, 65), 10, 5);
        Invoke(editor, "Paint", 10, 5, 17, 8, true, null);
        var first = GetMask(editor);
        if (first.Count(x => x == 255) != 32 || first[5 * 64 + 10] != 255 || first[5 * 64 + 9] != 0)
            throw new InvalidOperationException("Mask does not use original pixel coordinates.");
        editor.SetZoom(2);
        CheckPixel(editor, new Point(240, 120), 40, 16);
        Arrange(editor, 640, 320);
        CheckPixel(editor, new Point(320, 160), 32, 16);
        if (!GetMask(editor).SequenceEqual(first))
            throw new InvalidOperationException("Mask changed with zoom or panel size.");
        editor.BrushDiameter = 16;
        editor.Tool = MaskTool.Eraser;
        Invoke(editor, "BrushStroke", new Point(13, 6), new Point(14, 7));
        if (editor.HasSelection)
            throw new InvalidOperationException("Eraser did not clear the selected pixels.");
        editor.Tool = MaskTool.Brush;
        Invoke(editor, "BrushStroke", new Point(8, 8), new Point(21, 8));
        if (!editor.HasSelection || GetMask(editor)[8 * 64 + 14] != 255)
            throw new InvalidOperationException("Brush did not cover the entire stroke.");
        editor.ClearMask();
        if (editor.HasSelection)
            throw new InvalidOperationException("Clear did not reset selection.");
        Console.WriteLine("PASS WPF panel modes, rectangle, brush, eraser, clear, letterbox, 200% zoom, panel resize, binary PNG pixels.");
        if (args.Length == 1 && args[0] == "--mcp-fixture")
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var fixtureViews = new RevitViewService();
            using var gateway = new McpPipeServer(new RenderPanel(fixtureViews), fixtureViews);
            Console.WriteLine($"READY {Process.GetCurrentProcess().Id}");
            using var stop = new System.Threading.Timer(_ =>
            {
                if (Debugger.IsAttached)
                    return;
                app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
            }, null, TimeSpan.FromSeconds(90), System.Threading.Timeout.InfiniteTimeSpan);
            app.Run();
            return;
        }
        CheckResultViewport();
        CheckMfluxSettings();
        CheckConnectionSettings();
        if (args.Length == 1 && args[0] == "--connection-check")
        {
            MacBridge.CheckConnectionAsync(Settings.Load(), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS live SSH, Siliconed status and configured MFLUX paths.");
        }
        if (args.Length == 2 && (args[0] == "--saved-result" || args[0] == "--preview"))
            CheckSavedResult(panel, args[1], args[0] == "--preview");
    }

    private static void CheckConnectionSettings()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "RevitAiRender-check-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            if (Settings.ReadForEditing(path).SshTarget != null)
                throw new InvalidOperationException("First launch must have an empty SSH target.");
            File.WriteAllText(path, "{\"sshTarget\":\"existing-alias\",\"unrelatedField\":\"keep-me\"}");
            var settings = Settings.ReadForEditing(path);
            var dialog = new ConnectionWindow(settings);
            if (((TextBox)dialog.FindName("TargetBox")).Text != "existing-alias" ||
                ((TextBox)dialog.FindName("SilicontrolBox")).Text != Settings.DefaultCli)
                throw new InvalidOperationException("Connection fields did not load saved values or the default CLI.");
            dialog.Close();
            settings.SshTarget = "updated-alias";
            settings.MfluxPython = "/opt/mflux/bin/python";
            try
            {
                settings.Save(path);
                throw new InvalidOperationException("Saving must reject incomplete MFLUX paths.");
            }
            catch (RenderException error) when (error.Code == "mflux-settings") { }
            if (Settings.ReadForEditing(path).SshTarget != "existing-alias")
                throw new InvalidOperationException("Validation failure modified the saved SSH target.");
            settings.MfluxCli = "/opt/mflux tools/bin/editor";
            settings.MfluxModelPath = "/opt/render models/qwen";
            settings.Save(path);
            var reopened = Settings.ReadForEditing(path);
            if (reopened.SshTarget != settings.SshTarget || reopened.MfluxModelPath != settings.MfluxModelPath ||
                !File.ReadAllText(path).Contains("keep-me"))
                throw new InvalidOperationException("Connection settings did not round-trip with existing fields.");
        }
        finally { Directory.Delete(directory.FullName, true); }
        Console.WriteLine("PASS connection dialog, first launch, validation and local settings round-trip.");
    }

    private static void CheckMfluxSettings()
    {
        var view = new ViewChoice();
        var settings = new Settings { SshTarget = "render-mac" };
        var fullFrame = ResultStorage.Create(view, "fixture", true, settings);
        if (fullFrame.IsMasked || fullFrame.MfluxCli != null)
            throw new InvalidOperationException("Full-frame processing must work without MFLUX settings.");

        try
        {
            ResultStorage.Create(view, "fixture", true, settings, masked: true);
            throw new InvalidOperationException("A masked job must require its MFLUX paths.");
        }
        catch (RenderException error) when (error.Code == "mflux-settings") { }

        settings.MfluxPython = "/opt/mflux/bin/python";
        settings.MfluxCli = "/opt/mflux/bin/mflux-generate-qwen-2.1-edit";
        settings.MfluxModelPath = "/opt/render models/qwen";
        var masked = ResultStorage.Create(view, "fixture", true, settings, masked: true);
        if (!masked.IsMasked || masked.MfluxPython != settings.MfluxPython || masked.MfluxCli != settings.MfluxCli ||
            masked.MfluxModelPath != settings.MfluxModelPath)
            throw new InvalidOperationException("A masked job must use the configured absolute paths.");
        Console.WriteLine("PASS optional MFLUX settings and explicit paths with spaces.");
    }

    private static void CheckResultViewport()
    {
        var viewer = new ResultViewport();
        var before = (Image)viewer.FindName("BeforeImage");
        var after = (FrameworkElement)viewer.FindName("AfterLayer");
        var compare = (CheckBox)viewer.FindName("CompareToggle");
        var slider = (Slider)viewer.FindName("CompareSlider");
        var split = (FrameworkElement)viewer.FindName("SplitTools");
        var source = SolidImage(unchecked((int)0xffff0000));
        var render = SolidImage(unchecked((int)0xff0000ff));

        viewer.Display(source, render, "");
        viewer.Measure(new Size(320, 240));
        viewer.Arrange(new Rect(0, 0, 320, 240));
        if (!viewer.HasResult || compare.IsChecked == true || split.Visibility != Visibility.Collapsed ||
            before.Visibility == Visibility.Visible || after.Clip != null || after.Visibility != Visibility.Visible)
            throw new InvalidOperationException("The full render must be the initial view.");

        compare.IsChecked = true;
        viewer.UpdateLayout();
        if (split.Visibility != Visibility.Visible || before.Visibility != Visibility.Visible || after.Clip == null)
            throw new InvalidOperationException("The comparison must reveal the split view.");
        CheckSplitPixels(viewer);
        slider.Value = 0;
        if (before.Visibility == Visibility.Visible || after.Clip != null || after.Visibility != Visibility.Visible)
            throw new InvalidOperationException("The slider must show the full result at 0%.");
        slider.Value = 100;
        if (before.Visibility != Visibility.Visible || after.Visibility == Visibility.Visible)
            throw new InvalidOperationException("The slider must show the full source at 100%.");
        viewer.Display(source, render, "");
        if (compare.IsChecked == true || after.Visibility != Visibility.Visible || after.Clip != null)
            throw new InvalidOperationException("A subsequent render must open in full even after comparing.");
        viewer.Display(null, render, "");
        if (!viewer.HasResult || ((FrameworkElement)viewer.FindName("ComparisonTools")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("A render without a source must remain viewable.");
        Console.WriteLine("PASS result viewport: fullscreen default, opt-in split, original/result endpoints, next result reset, missing source.");
    }

    private static BitmapSource SolidImage(int color) =>
        BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat(color, 64 * 64).ToArray(), 64 * 4);

    private static void CheckSplitPixels(ResultViewport viewer)
    {
        var stage = (FrameworkElement)viewer.FindName("Stage");
        var center = stage.TranslatePoint(new Point(stage.ActualWidth / 2, stage.ActualHeight / 2), viewer);
        var frame = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32);
        frame.Render(viewer);
        var left = new byte[4];
        var right = new byte[4];
        frame.CopyPixels(new Int32Rect((int)center.X - 6, (int)center.Y, 1, 1), left, 4, 0);
        frame.CopyPixels(new Int32Rect((int)center.X + 6, (int)center.Y, 1, 1), right, 4, 0);
        if (left[2] != 255 || left[0] != 0 || right[0] != 255 || right[2] != 0)
            throw new InvalidOperationException("The comparison pixel boundary does not match the divider.");
    }

    private static void CheckSavedResult(RenderPanel panel, string jobPath, bool holdPreview)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new Window { Content = panel, Width = 215, Height = 720, Title = "AI Render 0.2.1 — проверка интерфейса" };
        int errors = 0;
        host.Closed += (_, _) => app.Shutdown(errors);
        host.Loaded += async (_, _) =>
        {
            try
            {
                var job = ResultStorage.ReadJob(Path.GetFullPath(jobPath));
                panel.HostWindow = new WindowInteropHelper(host).Handle;
                await panel.ShowResultAsync(job, openLarge: true);
                var tabs = (TabControl)panel.FindName("Tabs");
                var viewport = (ResultViewport)panel.FindName("ResultViewer");
                var large = app.Windows.OfType<RenderPreviewWindow>().Single();
                var largeViewport = (ResultViewport)large.FindName("Viewer");
                if (tabs.SelectedItem != panel.FindName("ResultTab") || !viewport.HasResult || !largeViewport.HasResult ||
                    !large.IsVisible || new WindowInteropHelper(large).Owner != new WindowInteropHelper(host).Handle)
                    throw new InvalidOperationException("The saved render did not open in both result surfaces.");
                Console.WriteLine("PASS saved PNG read, Result tab selected, owned modeless window visible.");
                if (!holdPreview)
                    large.Close();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                errors = 1;
            }
            if (!holdPreview || errors != 0)
                host.Close();
        };
        Environment.ExitCode = app.Run(host);
    }

    private static void Arrange(MaskEditor editor, int width, int height)
    {
        editor.Measure(new Size(width, height));
        editor.Arrange(new Rect(0, 0, width, height));
    }

    private static byte[] GetMask(MaskEditor editor)
    {
        var data = new byte[64 * 32];
        editor.CaptureMask().CopyPixels(data, 64, 0);
        return data;
    }

    private static void CheckPixel(MaskEditor editor, Point point, int x, int y)
    {
        var actual = (Point)Invoke(editor, "ToPixel", point)!;
        if (Math.Abs(actual.X - x) > 0.0001 || Math.Abs(actual.Y - y) > 0.0001)
            throw new InvalidOperationException($"Expected ({x}, {y}), got {actual}.");
    }

    private static object? Invoke(MaskEditor editor, string name, params object?[] args) =>
        typeof(MaskEditor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);
}
