using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitAiRender;

internal sealed class ElementFrames
{
    internal ElementFrames(string source, string reference, string first, string second)
    {
        Source = source;
        Reference = reference;
        First = first;
        Second = second;
    }

    internal string Source { get; }
    internal string Reference { get; }
    internal string First { get; }
    internal string Second { get; }
}

internal static class ElementMask
{
    internal static BitmapSource Extract(ElementFrames paths)
    {
        var source = LocalFiles.OpenPng(paths.Source);
        if (!LocalFiles.HaveSamePixels(source, LocalFiles.OpenPng(paths.Reference)))
            throw new RenderException("element-alignment", "Временный вид отличается от исходного ракурса. Используйте ручную маску.");
        return Extract(source.PixelWidth, source.PixelHeight, LocalFiles.OpenPng(paths.First), LocalFiles.OpenPng(paths.Second));
    }

    internal static BitmapSource Extract(int width, int height, BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != width || second.PixelWidth != width || first.PixelHeight != height || second.PixelHeight != height)
            throw new RenderException("element-alignment", "Размер служебного кадра изменился. Используйте ручную маску.");

        var stride = checked(width * 4);
        var left = new byte[checked(stride * height)];
        var right = new byte[left.Length];
        new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0).CopyPixels(left, stride, 0);
        new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0).CopyPixels(right, stride, 0);
        var values = new byte[checked(width * height)];
        var any = false;
        for (int index = 0, pixel = 0; index < values.Length; index++, pixel += 4)
        {
            if (Math.Abs(left[pixel] - right[pixel]) < 30 && Math.Abs(left[pixel + 1] - right[pixel + 1]) < 30 &&
                Math.Abs(left[pixel + 2] - right[pixel + 2]) < 30)
                continue;
            values[index] = 255;
            any = true;
        }
        if (!any)
            throw new RenderException("element-hidden", "У выбранных элементов нет видимой поверхности в этом 3D-виде.");

        var mask = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, values, width);
        mask.Freeze();
        return mask;
    }

    internal static void Save(BitmapSource mask, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(mask));
        using var file = new FileStream(path, FileMode.CreateNew);
        png.Save(file);
    }
}
