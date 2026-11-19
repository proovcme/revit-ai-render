using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RevitAiRender;

[McpServerToolType]
internal sealed class RenderTools(RenderConnection connection)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [McpServerTool(Name = "render_views", ReadOnly = true, OpenWorld = false)]
    [Description("Список 3D-видов активного проекта Revit с ID для запуска рендера.")]
    public async Task<CallToolResult> Views(CancellationToken cancellationToken, int offset = 0)
    {
        var reply = await connection.CallAsync(new McpRequest { Operation = "views", Offset = offset }, cancellationToken);
        return Report(new { reply.Project, reply.ProjectToken, reply.ActiveViewId, reply.Views, reply.NextOffset });
    }

    [McpServerTool(Name = "start_render", Destructive = false, Idempotent = true)]
    [Description("Принимает асинхронную задачу на экспорт 3D-вида. Для полного кадра используется Siliconed, при elementIds используется MFLUX. Возвращает ID до экспорта. Проверяйте render_status до готовности PNG или завершения с ошибкой.")]
    public async Task<CallToolResult> Start(
        [Description("Новый UUID задачи. Для повтора того же запроса используйте тот же UUID.")] string requestId,
        [Description("Точное название активного проекта из render_views.")] string project,
        [Description("Токен активного документа из render_views.")] string projectToken,
        [Description("ID 3D-вида из render_views.")] string viewId,
        [Description("Изменения, которые нужно применить к кадру.")] string prompt,
        CancellationToken cancellationToken,
        bool preserveGeometry = true,
        [Description("Необязательный список из 1–32 ElementId или UniqueId элементов текущего проекта. При наличии списка используется MFLUX и маска видимых частей. Опустите список для полного кадра.")] string[]? elementIds = null)
    {
        var reply = await connection.CallAsync(new McpRequest
        {
            Operation = elementIds == null ? "start" : "start_elements",
            Id = requestId, Project = project, ProjectToken = projectToken, ViewId = viewId,
            Prompt = prompt, PreserveGeometry = preserveGeometry, ElementIds = elementIds
        }, cancellationToken);
        return Report(reply.Job!);
    }

    [McpServerTool(Name = "render_status", ReadOnly = true, OpenWorld = false)]
    [Description("Состояние, доступный прогресс и готовность результата по ID задачи.")]
    public async Task<CallToolResult> Status(string jobId, CancellationToken cancellationToken) =>
        Report(await FindAsync(jobId, cancellationToken));

    [McpServerTool(Name = "list_renders", ReadOnly = true, OpenWorld = false)]
    [Description("Готовые рендеры из локальной истории, включая результаты с масками и старые задачи. Фильтр project необязателен.")]
    public async Task<CallToolResult> List(CancellationToken cancellationToken, string? project = null, int limit = 10, int offset = 0)
    {
        var reply = await connection.CallAsync(new McpRequest
        {
            Operation = "results", Project = project, Limit = limit, Offset = offset
        }, cancellationToken);
        return Report(new { reply.Jobs, reply.NextOffset });
    }

    [McpServerTool(Name = "get_render", ReadOnly = true, OpenWorld = false)]
    [Description("Возвращает готовый PNG из истории AI Render как изображение MCP. Можно запросить исходный PNG.")]
    public async Task<CallToolResult> Get(string jobId, CancellationToken cancellationToken, bool original = false)
    {
        var job = await FindAsync(jobId, cancellationToken);
        if (!job.ResultReady)
            throw new McpException("Итоговый PNG ещё не готов. Проверьте render_status.");
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var id = Guid.ParseExact(job.Id, "N").ToString("N");
        var directory = Path.Combine(root, job.Legacy ? "RevitAiRender" : "RevitAiRender020", "jobs", id);
        var path = Path.Combine(directory, original ? "source.png" : "result.png");
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is < 8 or > 8 * 1024 * 1024)
                throw new McpException("Размер PNG выходит за пределы передачи через MCP. Откройте задачу в панели Revit.");
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellationToken);
            if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new McpException("Файл результата повреждён.");
            var report = Report(new { job.Id, job.Project, job.View, original, mimeType = "image/png" });
            report.Content.Add(ImageContentBlock.FromBytes(bytes, "image/png"));
            return report;
        }
        catch (IOException)
        {
            throw new McpException("PNG задачи недоступен.");
        }
    }

    private async Task<McpJob> FindAsync(string id, CancellationToken token)
    {
        var reply = await connection.CallAsync(new McpRequest { Operation = "status", Id = id }, token);
        return reply.Job ?? throw new McpException("Задача не найдена.");
    }

    private static CallToolResult Report<T>(T value)
    {
        var structured = JsonSerializer.SerializeToElement(value, JsonOptions);
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }
}
