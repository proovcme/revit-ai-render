using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Media.Imaging;

namespace RevitAiRender;

internal static class LocalFiles
{
    internal static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAiRender");
    internal static readonly string Root = Path.Combine(Path.GetDirectoryName(LegacyRoot)!, "RevitAiRender020");
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string NewPreview() => Path.Combine(Root, "previews", Guid.NewGuid().ToString("N"));

    internal static BitmapSource OpenPng(string path)
    {
        using var file = File.OpenRead(path);
        var png = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = png.Frames[0];
        frame.Freeze();
        return frame;
    }
}

internal static class Json
{
    internal static T Read<T>(Stream stream) => new DataContractJsonSerializer(typeof(T)).ReadObject(stream) is T result ? result : throw new InvalidDataException("Empty JSON object.");
    internal static T Parse<T>(string value)
    {
        using var stream = new MemoryStream(LocalFiles.Utf8.GetBytes(value));
        return Read<T>(stream);
    }
    internal static void Write<T>(Stream stream, T value) =>
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
}

internal static class Diagnostics
{
    private static readonly object Gate = new();
    internal static void Write(string operation, string details)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LocalFiles.Root);
                File.AppendAllText(Path.Combine(LocalFiles.Root, "diagnostics.log"),
                    $"{DateTime.UtcNow:O} {operation} {details}{Environment.NewLine}", LocalFiles.Utf8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Exception(string operation, Exception error) =>
        Write(operation, error.GetType().FullName ?? error.GetType().Name);
}

internal sealed class RenderException : Exception
{
    internal RenderException(string code, string message) : base(message) => Code = code;
    internal string Code { get; }
}