using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RevitAiRender;

internal sealed class JobProgress
{
    internal JobProgress(string message, double? fraction = null)
    {
        Message = message;
        Fraction = fraction;
    }
    internal string Message { get; }
    internal double? Fraction { get; }
}

internal sealed class RenderJobService
{
    private readonly IProgress<JobProgress> progress;
    private readonly CancellationToken shutdown;
    private int cancelRequested;

    internal RenderJobService(IProgress<JobProgress> progress, CancellationToken shutdown)
    {
        this.progress = progress;
        this.shutdown = shutdown;
    }

    internal void RequestCancellation() => Interlocked.Exchange(ref cancelRequested, 1);

    internal async Task<RenderJob> RunAsync(RenderJob initial, bool resume)
    {
        FileStream fileLock;
        try { fileLock = ResultStorage.Lock(initial); }
        catch (IOException)
        {
            throw new RenderException("job-locked", "Эту задачу уже проверяет другой сеанс Revit.");
        }

        using (fileLock)
        {
            var job = resume ? ResultStorage.ReadJob(initial.DirectoryPath) : initial;
            if (resume && !job.CanResume)
                return job;

            try
            {
                var bridge = new MacBridge(job);
                IRenderBackend backend = job.IsMasked ? new MfluxBackend(bridge) : new SiliconedBackend(bridge);
                if (!resume)
                {
                    ResultStorage.PrepareInput(job);
                    Report("Передача кадра и промпта на Mac…");
                    await backend.StageAsync(shutdown).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref cancelRequested, 0) == 1)
                    {
                        Record(job, "cancelled", "Задача отменена до отправки в очередь.");
                        return job;
                    }
                    job.SubmissionAttempted = true;
                    Record(job, "submitted", $"Запуск в {backend.Name}…");
                    await backend.SubmitAsync(shutdown).ConfigureAwait(false);
                }

                await backend.MonitorAsync(job, () => Interlocked.Exchange(ref cancelRequested, 0) == 1, Report, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                throw;
            }
            catch (RenderException error)
            {
                var definitive = !job.SubmissionAttempted || error.Code == "not_submitted" ||
                    error.Code == "unknown_job" || error.Code == "app_changed" || error.Code == "mflux-settings" || error.Code == "mflux-worker-lost" || error.Code == "mflux-integrity";
                Record(job, definitive ? "failed" : "interrupted", error.Message);
                Diagnostics.Write("job-error", $"job={job.Id} code={SafeCode(error.Code)}");
            }
            catch (Exception error)
            {
                Record(job, job.SubmissionAttempted ? "interrupted" : "failed",
                    job.SubmissionAttempted ? "Проверка прервалась. Возобновите её из истории." : "Не удалось подготовить файлы задачи.");
                Diagnostics.Exception("job-error", error);
            }
            return job;
        }
    }

    internal static void Record(RenderJob job, string state, string message)
    {
        job.State = state;
        job.Message = message;
        ResultStorage.Save(job);
    }

    private void Report(string message, double? fraction = null) => progress.Report(new JobProgress(message, fraction));

    internal static string SafeCode(string code) =>
        System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[A-Za-z0-9_-]{1,64}\z") ? code : "unknown";
}