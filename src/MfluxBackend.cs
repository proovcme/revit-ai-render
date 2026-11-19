using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitAiRender;

[DataContract]
internal sealed class MfluxConfig
{
    [DataMember(Name = "cli")] public string Cli { get; set; } = "";
    [DataMember(Name = "model")] public string Model { get; set; } = "";
    [DataMember(Name = "silicontrol")] public string Silicontrol { get; set; } = "";
    [DataMember(Name = "seed")] public ulong Seed { get; set; }
}

[DataContract]
internal sealed class MfluxStatus
{
    [DataMember(Name = "state")] public string State { get; set; } = "";
    [DataMember(Name = "code")] public string? Code { get; set; }
    [DataMember(Name = "step")] public int Step { get; set; }
    [DataMember(Name = "width")] public int Width { get; set; }
    [DataMember(Name = "height")] public int Height { get; set; }
    [DataMember(Name = "rawWidth")] public int RawWidth { get; set; }
    [DataMember(Name = "rawHeight")] public int RawHeight { get; set; }
    [DataMember(Name = "seconds")] public double Seconds { get; set; }
    [DataMember(Name = "peakMlxGb")] public double? PeakMlxGb { get; set; }
    [DataMember(Name = "insideChanged")] public int InsideChanged { get; set; }
    [DataMember(Name = "outsideChanged")] public int OutsideChanged { get; set; }
}

internal sealed class MfluxBackend : IRenderBackend
{
    private readonly MacBridge bridge;
    internal MfluxBackend(MacBridge bridge) => this.bridge = bridge;
    public string Name => "MFLUX";
    public Task StageAsync(CancellationToken token) => bridge.StageAsync(token);

    public async Task SubmitAsync(CancellationToken token)
    {
        var reply = (await bridge.MfluxControlAsync("launch", token).ConfigureAwait(false)).Single();
        if (reply.State != "submitted")
            throw Error(reply.Code);
    }

    public async Task MonitorAsync(RenderJob job, Func<bool> takeCancel, Action<string, double?> report,
        CancellationToken shutdown)
    {
        var missingWorker = 0;
        while (true)
        {
            shutdown.ThrowIfCancellationRequested();
            var reply = await bridge.MfluxControlAsync("probe", shutdown).ConfigureAwait(false);
            var state = reply.FirstOrDefault(item => item.State != "worker_alive" && item.State != "worker_lost");
            switch (state?.State)
            {
                case "not_submitted":
                    throw BridgeErrors.For("not_submitted");
                case "failed":
                    await SaveLogAsync(job, shutdown).ConfigureAwait(false);
                    RenderJobService.Record(job, "failed", Error(state.Code).Message);
                    Diagnostics.Write("mflux-failed", $"job={job.Id} code={RenderJobService.SafeCode(state.Code ?? "unknown")}");
                    return;
                case "cancelled":
                    await SaveLogAsync(job, shutdown).ConfigureAwait(false);
                    RenderJobService.Record(job, "cancelled", "Задача MFLUX отменена.");
                    return;
                case "done":
                    await FinishAsync(job, state, report, shutdown).ConfigureAwait(false);
                    return;
            }

            if (reply.Any(item => item.State == "worker_lost") && ++missingWorker >= 3)
                throw new RenderException("mflux-worker-lost", "Процесс MFLUX завершился без результата. Файлы и журналы задачи доступны на Mac.");
            if (job.RemoteId == null && state?.State is "preparing" or "running" or "composing")
            {
                job.RemoteId = job.Id;
                RenderJobService.Record(job, "running", "Задача принята MFLUX.");
            }
            if (takeCancel())
            {
                await bridge.MfluxControlAsync("cancel", shutdown).ConfigureAwait(false);
                report("Запрошена отмена своей задачи…", null);
            }
            else if (state?.State == "running")
            {
                report(state.Step == 0 ? "MFLUX загружает модель…" : $"MFLUX · шаг {state.Step} из 8", state.Step / 8.0);
            }
            else
            {
                report(state?.State == "composing" ? "Сборка результата по маске…" : "Подготовка MFLUX на Mac…", null);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), shutdown).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(RenderJob job, MfluxStatus result, Action<string, double?> report, CancellationToken token)
    {
        if (result.Width != job.SourceWidth || result.Height != job.SourceHeight ||
            result.RawWidth <= 0 || result.RawHeight <= 0 ||
            (long)result.RawWidth * result.RawHeight > ResultStorage.PixelArea ||
            result.InsideChanged <= 0 || result.OutsideChanged != 0)
            throw Error("mflux-integrity");

        report("Скачивание и проверка PNG…", null);
        foreach (var image in new[] { "raw.png", "working-source.png", "working-mask.png" })
            await bridge.FetchMfluxAsync(image, result.RawWidth, result.RawHeight, token).ConfigureAwait(false);
        foreach (var file in new[] { "pixels.json", "generation.log", "state.json" })
            await bridge.FetchMfluxAsync(file, null, null, token).ConfigureAwait(false);
        await bridge.FetchMfluxAsync("result.png", result.Width, result.Height, token).ConfigureAwait(false);
        if (CheckPixels(job) != result.InsideChanged)
            throw Error("mflux-integrity");
        job.RemoteId = job.Id;
        job.InputWidth = result.RawWidth;
        job.InputHeight = result.RawHeight;
        job.OutputWidth = result.Width;
        job.OutputHeight = result.Height;
        job.RenderSeconds = result.Seconds;
        job.InsideChanged = result.InsideChanged;
        job.OutsideChanged = result.OutsideChanged;
        job.PeakMlxGb = result.PeakMlxGb;
        RenderJobService.Record(job, "done", "Результат готов. Вне маски пиксели совпадают.");
        Diagnostics.Write("mflux-saved", $"job={job.Id} width={result.Width} height={result.Height} outside=0");
    }

    private async Task SaveLogAsync(RenderJob job, CancellationToken token)
    {
        foreach (var log in new[] { "worker.log", "generation.log" })
        {
            try { await bridge.FetchMfluxAsync(log, null, null, token).ConfigureAwait(false); }
            catch (RenderException error) when (error.Code == "download")
            {
                Diagnostics.Write("mflux-log-missing", $"job={job.Id} file={log}");
            }
        }
    }

    internal static int CheckPixels(RenderJob job)
    {
        var source = new FormatConvertedBitmap(LocalFiles.OpenPng(job.SourcePath), PixelFormats.Bgra32, null, 0);
        var result = new FormatConvertedBitmap(LocalFiles.OpenPng(job.ResultPath), PixelFormats.Bgra32, null, 0);
        var mask = new FormatConvertedBitmap(LocalFiles.OpenPng(job.MaskPath), PixelFormats.Gray8, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        if (result.PixelWidth != width || result.PixelHeight != height || mask.PixelWidth != width || mask.PixelHeight != height)
            throw Error("mflux-integrity");
        var selected = new byte[width * height];
        var before = new int[width * height];
        var after = new int[width * height];
        source.CopyPixels(before, width * 4, 0);
        result.CopyPixels(after, width * 4, 0);
        mask.CopyPixels(selected, width, 0);
        var changedInside = 0;
        for (var i = 0; i < selected.Length; i++)
        {
            if (selected[i] != 0 && selected[i] != 255)
                throw Error("mflux-integrity");
            if (before[i] == after[i])
                continue;
            if (selected[i] == 0)
                throw Error("mflux-integrity");
            changedInside++;
        }
        return changedInside;
    }

    private static RenderException Error(string? code) => new(code ?? "mflux-worker", code switch
    {
        "mflux-settings" => "Проверьте путь к Python, CLI и весам Qwen Turbo на Mac.",
        "siliconed-status" => "Не удалось проверить состояние Siliconed на Mac.",
        "siliconed-busy" => "Siliconed занят. Дождитесь завершения очереди.",
        "siliconed-conflict" => "Siliconed начал генерацию. Задача MFLUX остановлена, чтобы освободить память.",
        "mflux-busy" => "На Mac уже выполняется задача MFLUX.",
        "invalid-mask" or "mask-small" => "Маска пуста или слишком мала. Выделите область крупнее.",
        "frame-small" => "Кадр слишком узкий для локального редактирования.",
        "inside-unchanged" => "MFLUX завершился, но выделенная область не изменилась.",
        "mflux-integrity" or "outside-changed" or "wrong-result-size" => "Проверка размеров и пикселей PNG не пройдена.",
        "mflux-exit" => "MFLUX завершился с ошибкой. Откройте generation.log в папке задачи.",
        _ => "Не удалось выполнить задачу MFLUX. Проверьте worker.log в папке задачи."
    });
}
