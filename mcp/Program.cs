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
        options.ServerInfo = new() { Name = "Revit AI Render", Version = "0.3.0" };
        options.ServerInstructions = "Рабочий процесс: render_views, start_render, render_status, get_render. Готовые кадры доступны через list_renders. Используйте один и тот же requestId при повторной проверке неопределённого запуска.";
    })
    .WithStdioServerTransport()
    .WithTools<RenderTools>();
await builder.Build().RunAsync();
return 0;
