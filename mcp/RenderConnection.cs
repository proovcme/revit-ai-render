using System.Diagnostics;
using System.IO.Pipes;
using ModelContextProtocol;

namespace RevitAiRender;

internal sealed class RenderConnection(int? targetPid)
{
    internal async Task<McpReply> CallAsync(McpRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(22));
        try
        {
            using var pipe = await ConnectAsync(deadline.Token);
            await McpWire.WriteAsync(pipe, request, deadline.Token);
            var reply = await McpWire.ReadAsync<McpReply>(pipe, deadline.Token);
            if (reply.Error != null)
                throw new McpException(reply.Message ?? "Revit отклонил запрос.");
            return reply;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new McpException(Unavailable(request));
        }
        catch (IOException)
        {
            throw new McpException(Unavailable(request));
        }
    }

    private static string Unavailable(McpRequest request) => (request.Operation is "start" or "start_elements")
        ? $"Связь с Revit прервалась. Проверьте задачу {request.Id} перед повтором. Повторный запрос должен содержать тот же requestId."
        : "Revit не ответил. Проверьте, что открыта нужная сессия AI Render.";

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        if (targetPid is int pid)
            return await TryConnectAsync(pid, token)
                ?? throw new McpException($"Revit с PID {pid} не принимает запросы AI Render.");

        var processes = Process.GetProcessesByName("Revit");
        try
        {
            var discovered = await Task.WhenAll(processes.Select(async process =>
                (process.Id, Pipe: await TryConnectAsync(process.Id, token))));
            var available = discovered.Where(item => item.Pipe != null).ToArray();
            if (available.Length == 1)
                return available[0].Pipe!;
            foreach (var item in available)
                item.Pipe!.Dispose();
            if (available.Length > 1)
                throw new McpException("Открыто несколько сеансов Revit с AI Render. Укажите --revit-pid. Доступны PID: " +
                    string.Join(", ", available.Select(item => item.Id)));
            throw new McpException("Запустите Revit с AI Render 0.3 и включённой интеграцией MCP.");
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static async Task<NamedPipeClientStream?> TryConnectAsync(int pid, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", McpWire.PipeName(pid), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(750, token);
            return pipe;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            pipe.Dispose();
            return null;
        }
    }
}
