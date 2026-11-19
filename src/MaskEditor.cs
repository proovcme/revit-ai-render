using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitAiRender;

public enum MaskTool { Brush, Eraser, Rectangle }

public sealed class MaskEditor : FrameworkElement
{
    private static readonly Brush Highlight = FrozenBrush(Color.FromArgb(106, 21, 152, 123));
    private static readonly Pen Outline = FrozenPen();
    private const int OverlayPixel = unchecked((int)0x7223A883);
    private BitmapSource? frame;
    private WriteableBitmap? overlay;
    private byte[]? pixels;
    private int selected;
    private Point? first;
    private Point? last;
    private Point? panStart;
    private Vector originPan;
    private Vector pan;
    private double zoom = 1;

    public event EventHandler? Changed;
    public MaskTool Tool { get; set; }
    public int BrushDiameter { get; set; } = 40;
    public bool HasSelection => selected > 0;

    public MaskEditor()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    internal void SetFrame(BitmapSource? source)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        frame = source;
        pixels = source == null ? null : new byte[checked(source.PixelWidth * source.PixelHeight)];
        overlay = source == null ? null : new WriteableBitmap(source.PixelWidth, source.PixelHeight,
            96, 96, PixelFormats.Bgra32, null);
        selected = 0;
        pan = default;
        RefreshMask();
    }

    internal void LoadMask(BitmapSource mask)
    {
        if (frame == null || pixels == null || mask.Format != PixelFormats.Gray8 ||
            mask.PixelWidth != frame.PixelWidth || mask.PixelHeight != frame.PixelHeight)
            throw new InvalidOperationException("Mask has different dimensions or pixel format.");
        mask.CopyPixels(pixels, frame.PixelWidth, 0);
        selected = 0;
        foreach (var value in pixels)
            if (value == 255)
                selected++;
        Redraw(0, 0, frame.PixelWidth, frame.PixelHeight);
        RefreshMask();
    }

    internal void ClearMask()
    {
        if (pixels == null || !HasSelection)
            return;
        Array.Clear(pixels, 0, pixels.Length);
        selected = 0;
        Redraw(0, 0, frame!.PixelWidth, frame.PixelHeight);
        RefreshMask();
    }

    internal void SetZoom(double factor)
    {
        zoom = Math.Max(1, Math.Min(3, factor));
        pan = default;
        InvalidateVisual();
    }

    internal BitmapSource CaptureMask()
    {
        if (!HasSelection || frame == null || pixels == null)
            throw new InvalidOperationException("No mask selected.");
        var bitmap = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight,
            96, 96, PixelFormats.Gray8, null, (byte[])pixels.Clone(), frame.PixelWidth);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (frame == null || overlay == null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var bounds = FrameBounds();
        context.DrawImage(frame, bounds);
        context.DrawImage(overlay, bounds);
        if (Tool == MaskTool.Rectangle && first.HasValue && last.HasValue)
            context.DrawRectangle(Highlight, Outline, new Rect(ToDisplay(first.Value, bounds), ToDisplay(last.Value, bounds)));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        if (frame == null || IsMouseCaptured || !FrameBounds().Contains(args.GetPosition(this)))
            return;
        first = last = ToPixel(args.GetPosition(this));
        CaptureMouse();
        if (Tool != MaskTool.Rectangle)
            BrushStroke(first.Value, first.Value);
        args.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        if (panStart.HasValue && IsMouseCaptured)
        {
            pan = originPan + (args.GetPosition(this) - panStart.Value);
            InvalidateVisual();
            return;
        }
        if (!first.HasValue || !last.HasValue || !IsMouseCaptured)
            return;
        var next = ToPixel(args.GetPosition(this));
        if (Tool != MaskTool.Rectangle)
            BrushStroke(last.Value, next);
        last = next;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        if (!first.HasValue || !IsMouseCaptured)
            return;
        var next = ToPixel(args.GetPosition(this));
        if (Tool == MaskTool.Rectangle)
        {
            var a = first.Value;
            Paint(Math.Min((int)a.X, (int)next.X), Math.Min((int)a.Y, (int)next.Y),
                Math.Max((int)a.X, (int)next.X), Math.Max((int)a.Y, (int)next.Y), true, null);
        }
        else if (last.HasValue)
        {
            BrushStroke(last.Value, next);
        }
        ReleaseMouseCapture();
        args.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs args)
    {
        if (frame == null || zoom == 1 || IsMouseCaptured)
            return;
        // Snapshot the clamped offset so the next drag starts at the visible position.
        var bounds = FrameBounds();
        originPan = new Vector(bounds.X + bounds.Width / 2 - ActualWidth / 2,
            bounds.Y + bounds.Height / 2 - ActualHeight / 2);
        panStart = args.GetPosition(this);
        CaptureMouse();
        args.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs args)
    {
        if (!panStart.HasValue)
            return;
        ReleaseMouseCapture();
        args.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        first = last = panStart = null;
        InvalidateVisual();
    }

    private Rect FrameBounds()
    {
        var scale = Math.Min(ActualWidth / frame!.PixelWidth, ActualHeight / frame.PixelHeight) * zoom;
        var width = frame.PixelWidth * scale;
        var height = frame.PixelHeight * scale;
        var limitX = Math.Max(0, (width - ActualWidth) / 2);
        var limitY = Math.Max(0, (height - ActualHeight) / 2);
        return new Rect((ActualWidth - width) / 2 + Clamp(pan.X, -limitX, limitX),
            (ActualHeight - height) / 2 + Clamp(pan.Y, -limitY, limitY), width, height);
    }

    private Point ToPixel(Point point)
    {
        var rect = FrameBounds();
        var width = frame!.PixelWidth;
        var height = frame.PixelHeight;
        return new Point(Clamp((point.X - rect.X) / rect.Width * width, 0, width - 1),
            Clamp((point.Y - rect.Y) / rect.Height * height, 0, height - 1));
    }

    private Point ToDisplay(Point point, Rect rect) => new(
        rect.X + point.X / frame!.PixelWidth * rect.Width, rect.Y + point.Y / frame.PixelHeight * rect.Height);

    private void BrushStroke(Point a, Point b)
    {
        var radius = BrushDiameter / 2.0;
        var vector = b - a;
        var lengthSquared = vector.LengthSquared;
        Paint((int)Math.Floor(Math.Min(a.X, b.X) - radius), (int)Math.Floor(Math.Min(a.Y, b.Y) - radius),
            (int)Math.Ceiling(Math.Max(a.X, b.X) + radius), (int)Math.Ceiling(Math.Max(a.Y, b.Y) + radius),
            Tool != MaskTool.Eraser, (x, y) =>
            {
                var offset = new Vector(x - a.X, y - a.Y);
                var along = lengthSquared > 0 ? Clamp(Vector.Multiply(offset, vector) / lengthSquared, 0, 1) : 0;
                return (offset - along * vector).LengthSquared <= radius * radius;
            });
    }

    private void Paint(int left, int top, int right, int bottom, bool add, Func<int, int, bool>? contains)
    {
        if (pixels == null || frame == null)
            return;
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(frame.PixelWidth - 1, right);
        bottom = Math.Min(frame.PixelHeight - 1, bottom);
        var value = add ? (byte)255 : (byte)0;
        var previousSelection = selected;
        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++)
        {
            var index = y * frame.PixelWidth + x;
            if (pixels[index] == value || (contains != null && !contains(x, y)))
                continue;
            pixels[index] = value;
            selected += add ? 1 : -1;
        }
        Redraw(left, top, right - left + 1, bottom - top + 1);
        InvalidateVisual();
        if ((previousSelection == 0) != (selected == 0))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Redraw(int x, int y, int width, int height)
    {
        if (pixels == null || frame == null || overlay == null || width <= 0 || height <= 0)
            return;
        var block = new int[width * height];
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
            if (pixels[(row + y) * frame.PixelWidth + x + column] != 0)
                block[row * width + column] = OverlayPixel;
        overlay.WritePixels(new Int32Rect(x, y, width, height), block, width * 4, 0);
    }

    private void RefreshMask()
    {
        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen()
    {
        var pen = new Pen(FrozenBrush(Color.FromRgb(10, 114, 91)), 1.5);
        pen.Freeze();
        return pen;
    }
}
