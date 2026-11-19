using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RevitAiRender;

internal interface IRenderBackend
{
    string Name { get; }
    Task StageAsync(CancellationToken token);
    Task SubmitAsync(CancellationToken token);
    Task MonitorAsync(RenderJob job, Func<bool> takeCancel, Action<string, double?> report, CancellationToken shutdown);
}

internal sealed class SiliconedBackend : IRenderBackend
{
    private readonly MacBridge bridge;
    internal SiliconedBackend(MacBridge bridge) => this.bridge = bridge;
    public string Name => "Siliconed";
    public Task StageAsync(CancellationToken token) => bridge.StageAsync(token);
    public async Task SubmitAsync(CancellationToken token) =>
        RequireOk((await bridge.ControlAsync("launch", token).ConfigureAwait(false)).Single());

    public async Task MonitorAsync(RenderJob job, Func<bool> takeCancel, Action<string, double?> report,
        CancellationToken shutdown)
    {
        var noReceipt = 0;
        var requestedRecovery = false;
        while (true)
        {
            shutdown.ThrowIfCancellationRequested();
            var lines = await bridge.ControlAsync("probe", shutdown).ConfigureAwait(false);
            if (lines.Any(value => value.Phase == "not_submitted"))
                throw BridgeErrors.For("not_submitted");

            var accepted = Payload(lines, "accepted");
            if (accepted == null)
            {
                report("Mac принимает запрос…", null);
                if (lines.Any(value => value.Phase == "worker_stopped") && ++noReceipt >= 3)
                    throw new RenderException("no-receipt", "Mac не подтвердил идентификатор. Проверьте очередь Siliconed и историю.");
            }
            else
            {
                if (accepted.Ok == false)
                {
                    var error = BridgeErrors.For(accepted.Error);
                    RenderJobService.Record(job, "failed", error.Message);
                    Diagnostics.Write("queue-rejected", $"job={job.Id} code={RenderJobService.SafeCode(error.Code)}");
                    return;
                }
                RequireOk(accepted);
                if (!accepted.IsOwnEdit || (job.RemoteId != null && job.RemoteId != accepted.Job))
                    throw BridgeErrors.For("protocol");
                if (job.RemoteId == null)
                {
                    job.RemoteId = accepted.Job;
                    job.Steps = accepted.Steps;
                    job.Seed = accepted.Seeds?.FirstOrDefault();
                    job.OutputWidth = accepted.Width;
                    job.OutputHeight = accepted.Height;
                    RenderJobService.Record(job, "running", "Задача принята Siliconed.");
                    Diagnostics.Write("queue-accepted", $"job={job.Id} remote={job.RemoteId} image-edit=1");
                }

                if (lines.Any(value => value.Phase == "finished"))
                {
                    await FinishAsync(job, lines, report, shutdown).ConfigureAwait(false);
                    return;
                }

                var current = Payload(lines, "status") ?? throw BridgeErrors.For("protocol");
                RequireOk(current);
                if (takeCancel())
                {
                    var reply = (await bridge.ControlAsync("cancel", shutdown).ConfigureAwait(false)).Single();
                    if (reply.Error != "already_finished" && reply.Error != "unknown_job")
                        RequireOk(reply);
                    report("Запрошена отмена своей задачи…", null);
                }
                else
                {
                    ReportStatus(job, current, report);
                }

                if (!requestedRecovery && lines.Any(value => value.Phase == "worker_stopped"))
                {
                    var reply = (await bridge.ControlAsync("recover", shutdown).ConfigureAwait(false)).Single();
                    if (reply.Error != "already_finished")
                        RequireOk(reply);
                    requestedRecovery = true;
                }
                else if (requestedRecovery && lines.Any(value => value.Phase == "worker_stopped"))
                {
                    throw BridgeErrors.For("recovery_exists");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), shutdown).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(RenderJob job, CliMessage[] lines, Action<string, double?> report, CancellationToken shutdown)
    {
        var finish = Array.FindIndex(lines, line => line.Phase == "finished");
        var verdict = lines.Skip(finish + 1).FirstOrDefault(line => line.Phase == null && line.Ok.HasValue)
            ?? throw BridgeErrors.For("protocol");
        var outcome = verdict.Jobs?.SingleOrDefault(item => item.Job == job.RemoteId);
        if (outcome?.Outcome == "stopped" || outcome?.Outcome == "removed")
        {
            RenderJobService.Record(job, "cancelled", "Задача отменена.");
            return;
        }
        if (outcome?.Outcome == "error")
        {
            var error = BridgeErrors.For(outcome.Code);
            RenderJobService.Record(job, "failed", error.Message);
            Diagnostics.Write("render-failed", $"job={job.Id} code={RenderJobService.SafeCode(error.Code)}");
            return;
        }
        RequireOk(verdict);
        var image = lines.Skip(finish + 1).SingleOrDefault(line => line.Type == "image" && line.Job == job.RemoteId);
        if (outcome?.Outcome != "done" || image == null || !image.IsOwnEdit ||
            image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > ResultStorage.PixelArea)
            throw BridgeErrors.For("protocol");
        if (image.Path == null || image.WriteError != null)
            throw BridgeErrors.For("write");

        report("Скачивание результата с Mac…", null);
        await bridge.DownloadAsync(image.Path, image.Width, image.Height, shutdown).ConfigureAwait(false);
        job.OutputWidth = image.Width;
        job.OutputHeight = image.Height;
        job.Seed = image.Seed ?? job.Seed;
        job.ImageId = image.Image;
        job.Evaluations = image.Evaluations;
        job.RenderSeconds = image.Seconds;
        job.NeuralEngine = image.NeuralEngine;
        job.TensorUnits = image.TensorUnits;
        RenderJobService.Record(job, "done", "Результат готов.");
        Diagnostics.Write("render-saved", $"job={job.Id} width={image.Width} height={image.Height}");
    }

    private void ReportStatus(RenderJob job, CliMessage status, Action<string, double?> report)
    {
        if (status.WaitingForLicense?.Any(item => item.Job == job.RemoteId) == true)
        {
            report("Нужно принять лицензию Qwen в Siliconed на Mac.", null);
            return;
        }
        if (status.Running is CliMessage running && running.Job == job.RemoteId)
        {
            var stage = running.Stage switch
            {
                "text" => "Обработка промпта",
                "image" => "Кодирование исходного кадра",
                "denoising" => "Генерация",
                "decoding" => "Декодирование изображения",
                _ => "Обработка в Siliconed"
            };
            if (running.StepsTotal > 0)
                stage += $" · шаг {running.Step ?? 0} из {running.StepsTotal}";
            report(stage, running.Fraction);
            return;
        }
        var queueIndex = Array.FindIndex(status.Queue ?? Array.Empty<CliMessage>(), item => item.Job == job.RemoteId);
        report(queueIndex < 0 ? "Ожидание результата Siliconed…" : $"В очереди · позиция {queueIndex + 1}", null);
    }

    private static CliMessage? Payload(CliMessage[] lines, string phase)
    {
        var index = Array.FindIndex(lines, value => value.Phase == phase);
        return index >= 0 && index + 1 < lines.Length && lines[index + 1].Phase == null ? lines[index + 1] : null;
    }

    private static void RequireOk(CliMessage result)
    {
        if (result.Ok != true)
            throw BridgeErrors.For(result.Error ?? "protocol");
    }

}
