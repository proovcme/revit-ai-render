using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RevitAiRender;

[DataContract]
internal sealed class McpRequest
{
    [DataMember] public string Operation { get; set; } = "";
    [DataMember] public string? Id { get; set; }
    [DataMember] public string? ViewId { get; set; }
    [DataMember] public string? Project { get; set; }
    [DataMember] public string? ProjectToken { get; set; }
    [DataMember] public string? Prompt { get; set; }
    [DataMember] public bool PreserveGeometry { get; set; } = true;
    [DataMember] public int Limit { get; set; } = 10;
    [DataMember] public int Offset { get; set; }
}

[DataContract]
internal sealed class McpReply
{
    [DataMember] public string? Error { get; set; }
    [DataMember] public string? Message { get; set; }
    [DataMember] public string? Project { get; set; }
    [DataMember] public string? ProjectToken { get; set; }
    [DataMember] public string? ActiveViewId { get; set; }
    [DataMember] public McpView[]? Views { get; set; }
    [DataMember] public McpJob[]? Jobs { get; set; }
    [DataMember] public McpJob? Job { get; set; }
    [DataMember] public int? NextOffset { get; set; }
}

[DataContract]
internal sealed class McpView
{
    [DataMember] public string Id { get; set; } = "";
    [DataMember] public string Name { get; set; } = "";
    [DataMember] public bool Perspective { get; set; }
}

[DataContract]
internal sealed class McpJob
{
    [DataMember] public string Id { get; set; } = "";
    [DataMember] public string Project { get; set; } = "";
    [DataMember] public string View { get; set; } = "";
    [DataMember] public string CreatedUtc { get; set; } = "";
    [DataMember] public string State { get; set; } = "";
    [DataMember] public string? Message { get; set; }
    [DataMember] public double? Progress { get; set; }
    [DataMember] public string Backend { get; set; } = "";
    [DataMember] public int Width { get; set; }
    [DataMember] public int Height { get; set; }
    [DataMember] public bool ResultReady { get; set; }
    [DataMember] public bool Legacy { get; set; }
    [DataMember] public bool CanResume { get; set; }
}

internal static class McpWire
{
    private const int MaxPayload = 512 * 1024;
    internal static string PipeName(int pid) => "revit-ai-render-v030-" + pid;

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[sizeof(int)];
        await ReadExactAsync(stream, header, token).ConfigureAwait(false);
        var size = BitConverter.ToInt32(header, 0);
        if (size <= 0 || size > MaxPayload)
            throw new InvalidDataException("Invalid pipe message size.");
        var bytes = new byte[size];
        await ReadExactAsync(stream, bytes, token).ConfigureAwait(false);
        using var payload = new MemoryStream(bytes, writable: false);
        return new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T)).ReadObject(payload) is T value
            ? value : throw new InvalidDataException("Empty pipe message.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        using var payload = new MemoryStream();
        new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T)).WriteObject(payload, value);
        if (payload.Length > MaxPayload)
            throw new InvalidDataException("Pipe message is too large.");
        var header = BitConverter.GetBytes((int)payload.Length);
        await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
        payload.Position = 0;
        await payload.CopyToAsync(stream, 81920, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var position = 0;
        while (position < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer, position, buffer.Length - position, token).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException();
            position += count;
        }
    }
}
