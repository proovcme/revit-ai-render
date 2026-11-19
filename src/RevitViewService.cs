using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAiRender;

internal sealed class ViewChoice
{
    internal ViewChoice(int document, string project, View3D view)
    {
        Document = document;
        Project = project;
        UniqueId = view.UniqueId;
        Name = view.Name;
        Perspective = view.IsPerspective;
    }

    internal int Document { get; }
    internal string Project { get; }
    internal string UniqueId { get; }
    internal string Name { get; }
    internal bool Perspective { get; }
    public string Label => Name + (Perspective ? " · перспектива" : " · аксонометрия");
}

internal sealed class ViewList
{
    internal ViewList(string project, ViewChoice[] views, string? activeId)
    {
        Project = project;
        Views = views;
        ActiveId = activeId;
    }
    internal string Project { get; }
    internal ViewChoice[] Views { get; }
    internal string? ActiveId { get; }
}

internal sealed class RevitViewService : IExternalEventHandler, IDisposable
{
    private readonly ExternalEvent externalEvent;
    private Action<UIApplication>? request;

    internal RevitViewService() => externalEvent = ExternalEvent.Create(this);

    internal Task<ViewList> ListAsync() => Invoke(app =>
    {
        var uiDocument = app.ActiveUIDocument;
        var current = app.ActiveUIDocument?.Document;
        if (current == null || current.IsFamilyDocument)
            return new ViewList("", Array.Empty<ViewChoice>(), null);

        var documentId = current.GetHashCode();
        var items = new FilteredElementCollector(current).OfClass(typeof(View3D)).Cast<View3D>()
            .Where(IsAvailable).OrderBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(view => new ViewChoice(documentId, current.Title, view)).ToArray();
        var activeId = uiDocument.ActiveView is View3D active && IsAvailable(active) ? active.UniqueId : null;
        return new ViewList(current.Title, items, activeId);
    });

    internal Task<string> ExportAsync(ViewChoice target, string directory, int pixelSize) => Invoke(app =>
    {
        var current = app.ActiveUIDocument?.Document;
        if (current == null || current.GetHashCode() != target.Document)
            throw new RenderException("project-changed", "Активный проект сменился. Выберите 3D-вид заново.");
        if (current.GetElement(target.UniqueId) is not View3D view || !IsAvailable(view))
            throw new RenderException("view-missing", "Выбранный 3D-вид недоступен. Обновите список видов.");

        Directory.CreateDirectory(directory);
        using var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(directory, "view"),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
            ZoomType = ZoomFitType.FitToPage,
            FitDirection = FitDirectionType.Horizontal,
            PixelSize = pixelSize,
            ShouldCreateWebSite = false
        };
        options.SetViewsAndSheets(new[] { view.Id });
        var modifiedBefore = current.IsModified;
        try
        {
            current.ExportImage(options);
            var files = Directory.GetFiles(directory, "*.png");
            if (files.Length != 1 || new FileInfo(files[0]).Length == 0)
                throw new IOException("Unexpected ExportImage output.");
            var path = Path.Combine(directory, "source.png");
            File.Move(files[0], path);
            Diagnostics.Write("revit-export", $"perspective={view.IsPerspective}; modified={modifiedBefore}/{current.IsModified}; pixels={pixelSize}");
            return path;
        }
        catch (Exception error)
        {
            Diagnostics.Exception("revit-export", error);
            throw new RenderException("export-failed", "Revit не смог экспортировать PNG выбранного 3D-вида.");
        }
    });

    public string GetName() => "AI Render: 3D views and export";

    public void Execute(UIApplication app)
    {
        var work = request;
        request = null;
        work?.Invoke(app);
    }

    public void Dispose() => externalEvent.Dispose();

    private static bool IsAvailable(View3D view) => view.IsValidObject && !view.IsTemplate && view.CanBePrinted;

    private Task<T> Invoke<T>(Func<UIApplication, T> operation)
    {
        if (request != null)
            throw new RenderException("revit-pending", "Revit ещё выполняет предыдущий запрос.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        request = app =>
        {
            try { completion.SetResult(operation(app)); }
            catch (Exception error) { completion.SetException(error); }
        };
        var result = externalEvent.Raise();
        if (result != ExternalEventRequest.Accepted)
        {
            request = null;
            completion.SetException(new RenderException("revit-unavailable", "Revit занят. Повторите после завершения текущей команды."));
        }
        return completion.Task;
    }
}