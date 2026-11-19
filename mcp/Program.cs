using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RevitAiRender;

int? pid = null;
if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--revit-pid" || !int.TryParse(args[1], out var parsed) || parsed <= 0)
    {
        Console.Error.WriteLine("Usage: RevitAiRender.Mcp [--revit-pid <pid>]");
        return 2;
    }
    pid = parsed;
}

var builder = Host.CreateEmptyApplicationBuilder(settings: null);
builder.Services.AddSingleton(new RenderConnection(pid));
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "Revit AI Render", Version = "0.4.0" };
        options.ServerInstructions = "После render_views и start_render проверяйте render_status по ID задачи. Ответ start_render означает приём задачи до экспорта. Получайте PNG через get_render только при resultReady=true. При обрыве связи сохраняйте ID и проверяйте ту же задачу. Готовые кадры доступны через list_renders.";
    })
    .WithStdioServerTransport()
    .WithTools<RenderTools>();
await builder.Build().RunAsync();
return 0;
