using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace RevitAiRender;

internal sealed class McpPipeServer : IDisposable
{
    private readonly RenderPanel panel;
    private readonly RevitViewService views;
    private readonly CancellationTokenSource shutdown = new();
    private readonly string name;

    internal McpPipeServer(RenderPanel panel, RevitViewService views)
    {
        this.panel = panel;
        this.views = views;
        using var process = Process.GetCurrentProcess();
        name = McpWire.PipeName(process.Id);
        for (var i = 0; i < 4; i++)
            _ = ListenAsync(shutdown.Token);
    }

    public void Dispose() => shutdown.Cancel();

    private NamedPipeServerStream NewPipe()
    {
        const int instances = 4;
#if NETFRAMEWORK
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("No Windows user SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
#else
        return new NamedPipeServerStream(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = NewPipe();
                using var disposeOnShutdown = token.Register(() => pipe.Dispose());
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var client = CancellationTokenSource.CreateLinkedTokenSource(token);
                client.CancelAfter(TimeSpan.FromSeconds(20));
                using var disposeOnTimeout = client.Token.Register(() => pipe.Dispose());
                var request = await McpWire.ReadAsync<McpRequest>(pipe, client.Token).ConfigureAwait(false);
                McpReply reply;
                try { reply = await DispatchAsync(request, client.Token).ConfigureAwait(false); }
                catch (RenderException error)
                {
                    reply = new McpReply { Error = error.Code, Message = error.Message };
                }
                catch (Exception error) when (!client.IsCancellationRequested)
                {
                    Diagnostics.Exception("mcp-dispatch", error);
                    reply = new McpReply { Error = "mcp-failed", Message = "AI Render не смог выполнить запрос." };
                }
                await McpWire.WriteAsync(pipe, reply, client.Token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is EndOfStreamException || error is OperationCanceledException || error is ObjectDisposedException) { }
            catch (Exception error)
            {
                Diagnostics.Exception("mcp-connection", error);
                try { await Task.Delay(300, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<McpReply> DispatchAsync(McpRequest request, CancellationToken token)
    {
        switch (request.Operation)
        {
            case "views":
                var list = await panel.Dispatcher.InvokeAsync(() => views.ListAsync(), System.Windows.Threading.DispatcherPriority.Normal, token)
                    .Task.Unwrap().ConfigureAwait(false);
                return new McpReply
                {
                    Project = list.Project,
                    ProjectToken = list.Views.Length == 0 ? null : ProjectToken(list.Views[0]),
                    ActiveViewId = list.ActiveId,
                    Views = list.Views.Skip(Math.Max(0, request.Offset)).Take(100).Select(view =>
                        new McpView { Id = view.UniqueId, Name = view.Name, Perspective = view.Perspective }).ToArray(),
                    NextOffset = request.Offset >= 0 && request.Offset < list.Views.Length - 100 ? request.Offset + 100 : (int?)null
                };
            case "start":
            case "start_elements":
                var job = await panel.Dispatcher.InvokeAsync(() => panel.StartMcpAsync(request, token), System.Windows.Threading.DispatcherPriority.Normal, token)
                    .Task.Unwrap().ConfigureAwait(false);
                return new McpReply { Job = Summarize(job) };
            case "status":
                var current = ResultStorage.Find(McpId(request.Id));
                if (current == null)
                    throw new RenderException("job-missing", "Задача с таким ID не найдена.");
                var summary = Summarize(current);
                var progress = panel.ReadMcpProgress(current.Id);
                if (progress != null && (current.State == "new" || current.State == "submitted" || current.State == "running"))
                {
                    summary.Progress = progress.Fraction;
                    summary.Message = progress.Message;
                }
                return new McpReply { Job = summary };
            case "results":
                var limit = Math.Min(25, Math.Max(1, request.Limit));
                var offset = Math.Max(0, request.Offset);
                var jobs = ResultStorage.ReadHistory().Where(item => item.State == "done" && File.Exists(item.ResultPath) &&
                    (string.IsNullOrWhiteSpace(request.Project) || string.Equals(item.Project, request.Project, StringComparison.OrdinalIgnoreCase)))
                    .Skip(offset).Take(limit + 1).ToArray();
                return new McpReply
                {
                    Jobs = jobs.Take(limit).Select(Summarize).ToArray(),
                    NextOffset = jobs.Length > limit ? offset + limit : (int?)null
                };
            default:
                throw new RenderException("mcp-operation", "Неизвестная команда AI Render.");
        }
    }

    internal static string ProjectToken(ViewChoice view)
    {
        using var process = Process.GetCurrentProcess();
        return process.Id.ToString(CultureInfo.InvariantCulture) + ":" + view.Document.ToString(CultureInfo.InvariantCulture);
    }

    internal static Guid McpId(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new RenderException("job-id", "Укажите корректный UUID задачи.");

    private static McpJob Summarize(RenderJob job) => new()
    {
        Id = job.Id,
        Project = job.Project,
        View = job.View,
        CreatedUtc = job.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        State = job.State,
        Message = job.Message,
        Backend = job.IsMasked ? "mflux" : "siliconed",
        Width = job.OutputWidth,
        Height = job.OutputHeight,
        ResultReady = job.State == "done" && File.Exists(job.ResultPath),
        Legacy = job.Legacy,
        CanResume = job.CanResume
    };
}
