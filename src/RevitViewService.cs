using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
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
        var current = RequireDocument(app, target);
        return Export(current, RequireView(current, target), directory, pixelSize);
    });

    internal Task<ElementFrames> ExportElementFramesAsync(ViewChoice target, string directory, int pixelSize,
        string[]? elementIds = null) => Invoke(app =>
    {
        var current = RequireDocument(app, target);
        var view = RequireView(current, target);
        if (current.IsReadOnly || current.IsWorkshared || !view.CanViewBeDuplicated(ViewDuplicateOption.Duplicate) ||
            view.IsTemporaryHideIsolateActive() ||
            (view.DisplayStyle != DisplayStyle.Shading && view.DisplayStyle != DisplayStyle.ShadingWithEdges &&
             view.DisplayStyle != DisplayStyle.FlatColors && view.DisplayStyle != DisplayStyle.HLR))
            throw new RenderException("element-view", "Для маски по элементам выберите обычный 3D-вид локального проекта с отображением поверхностей.");

        var elements = elementIds == null
            ? app.ActiveUIDocument.Selection.GetElementIds().Select(current.GetElement).ToArray()
            : elementIds.Select(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? current.GetElement(new ElementId(value)) : current.GetElement(id)).ToArray();
        if (elements.Length == 0 || elements.Any(element => element == null || element is ElementType || element is RevitLinkInstance))
            throw new RenderException("element-selection", "Выберите один или несколько элементов активного проекта.");

        var ids = new HashSet<ElementId>(elements.Select(element => element.Id));
        var nested = new Queue<ElementId>(ids);
        while (nested.Count > 0)
        {
            if (current.GetElement(nested.Dequeue()) is not FamilyInstance family)
                continue;
            foreach (var child in family.GetSubComponentIds())
                if (ids.Add(child))
                    nested.Enqueue(child);
        }
        var fill = new FilteredElementCollector(current).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill);
        if (fill == null)
            throw new RenderException("element-fill", "Сплошная заливка для построения маски недоступна.");

        var source = Export(current, view, directory, pixelSize);
        var modifiedBefore = current.IsModified;
        using var transaction = new Transaction(current, "AI Render: временный вид для маски");
        if (transaction.Start() != TransactionStatus.Started)
            throw new RenderException("element-view", "Revit не открыл временный вид для построения маски.");
        try
        {
            var duplicate = (View3D)current.GetElement(view.Duplicate(ViewDuplicateOption.Duplicate));
            current.Regenerate();
            var reference = Export(current, duplicate, Path.Combine(directory, "mask-reference"), pixelSize);
            var first = ExportElementColor(current, duplicate, ids, fill.Id, new Color(255, 0, 255),
                Path.Combine(directory, "mask-magenta"), pixelSize);
            var second = ExportElementColor(current, duplicate, ids, fill.Id, new Color(0, 255, 0),
                Path.Combine(directory, "mask-green"), pixelSize);
            return new ElementFrames(source, reference, first, second);
        }
        finally
        {
            if (transaction.RollBack() != TransactionStatus.RolledBack || current.IsModified != modifiedBefore)
                throw new RenderException("element-rollback", "Revit не подтвердил откат временного вида. Проверьте состояние проекта.");
        }
    });

    private static string ExportElementColor(Document document, View3D view, IEnumerable<ElementId> ids,
        ElementId fill, Color color, string directory, int pixelSize)
    {
        foreach (var id in ids)
        {
            using var overrides = view.GetElementOverrides(id);
            overrides.SetSurfaceForegroundPatternId(fill);
            overrides.SetSurfaceForegroundPatternVisible(true);
            overrides.SetSurfaceForegroundPatternColor(color);
            overrides.SetCutForegroundPatternId(fill);
            overrides.SetCutForegroundPatternVisible(true);
            overrides.SetCutForegroundPatternColor(color);
            overrides.SetProjectionLineColor(color);
            overrides.SetCutLineColor(color);
            view.SetElementOverrides(id, overrides);
        }
        document.Regenerate();
        return Export(document, view, directory, pixelSize);
    }

    private static Document RequireDocument(UIApplication app, ViewChoice target)
    {
        var document = app.ActiveUIDocument?.Document;
        if (document == null || document.GetHashCode() != target.Document)
            throw new RenderException("project-changed", "Активный проект сменился. Выберите 3D-вид заново.");
        return document;
    }

    private static View3D RequireView(Document document, ViewChoice target)
    {
        if (document.GetElement(target.UniqueId) is not View3D view || !IsAvailable(view))
            throw new RenderException("view-missing", "Выбранный 3D-вид недоступен. Обновите список видов.");
        return view;
    }

    private static string Export(Document current, View3D view, string directory, int pixelSize)
    {
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
    }

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