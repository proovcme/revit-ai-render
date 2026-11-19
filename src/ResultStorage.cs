using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitAiRender;

[DataContract]
internal sealed class RenderJob
{
    [DataMember] public string Id { get; set; } = "";
    [DataMember] public DateTime CreatedUtc { get; set; }
    [DataMember] public string Project { get; set; } = "";
    [DataMember] public string View { get; set; } = "";
    [DataMember] public string ViewUniqueId { get; set; } = "";
    [DataMember] public bool Perspective { get; set; }
    [DataMember] public string Prompt { get; set; } = "";
    [DataMember] public string FullPrompt { get; set; } = "";
    [DataMember] public bool PreserveGeometry { get; set; }
    [DataMember] public string Model { get; set; } = Settings.Model;
    [DataMember] public string? SshTarget { get; set; }
    [DataMember] public string? CliPath { get; set; }
    [DataMember] public string State { get; set; } = "new";
    [DataMember] public string? Message { get; set; }
    [DataMember] public bool SubmissionAttempted { get; set; }
    [DataMember] public string? RemoteId { get; set; }
    [DataMember] public string? ImageId { get; set; }
    [DataMember] public ulong? Seed { get; set; }
    [DataMember] public int? Steps { get; set; }
    [DataMember] public int SourceWidth { get; set; }
    [DataMember] public int SourceHeight { get; set; }
    [DataMember] public int InputWidth { get; set; }
    [DataMember] public int InputHeight { get; set; }
    [DataMember] public int OutputWidth { get; set; }
    [DataMember] public int OutputHeight { get; set; }
    [DataMember] public int? Evaluations { get; set; }
    [DataMember] public double? RenderSeconds { get; set; }
    [DataMember] public bool? NeuralEngine { get; set; }
    [DataMember] public string? TensorUnits { get; set; }

    [DataMember(EmitDefaultValue = false)] public string? Backend { get; set; }
    [DataMember(EmitDefaultValue = false)] public string[]? ElementIds { get; set; }
    [DataMember(EmitDefaultValue = false)] public string? MfluxPython { get; set; }
    [DataMember(EmitDefaultValue = false)] public string? MfluxCli { get; set; }
    [DataMember(EmitDefaultValue = false)] public string? MfluxModelPath { get; set; }
    [DataMember(EmitDefaultValue = false)] public double? PeakMlxGb { get; set; }
    [DataMember(EmitDefaultValue = false)] public int InsideChanged { get; set; }
    [DataMember(EmitDefaultValue = false)] public int OutsideChanged { get; set; }

    internal bool IsMasked => Backend == "mflux";
    internal bool Legacy { get; set; }
    internal string DirectoryPath => Path.Combine(Legacy ? ResultStorage.LegacyRoot : ResultStorage.Root, Guid.ParseExact(Id, "N").ToString("N"));
    internal string SourcePath => Path.Combine(DirectoryPath, "source.png");
    internal string ResultPath => Path.Combine(DirectoryPath, "result.png");
    internal string MaskPath => Path.Combine(DirectoryPath, "mask.png");
    internal bool CanResume => !Legacy && SubmissionAttempted && State != "done" && State != "cancelled" && State != "failed";
    public string HistorySubtitle => $"{CreatedUtc.ToLocalTime():g} · {Project}{(IsMasked ? " · маска" : "")}";
    public string Label => $"{CreatedUtc.ToLocalTime():g} · {Project} · {View}{(IsMasked ? " · маска" : "")}";
}

internal static class ResultStorage
{
    internal static readonly string Root = Path.Combine(LocalFiles.Root, "jobs");
    internal static readonly string LegacyRoot = Path.Combine(LocalFiles.LegacyRoot, "jobs");
    internal const int PixelArea = 1024 * 1024;

    internal static RenderJob Create(ViewChoice view, string prompt, bool preserve, Settings settings, bool masked = false, Guid? requestId = null) => new()
    {
        Id = (requestId ?? Guid.NewGuid()).ToString("N"),
        CreatedUtc = DateTime.UtcNow,
        Project = view.Project,
        View = view.Name,
        ViewUniqueId = view.UniqueId,
        Perspective = view.Perspective,
        Prompt = prompt,
        Backend = masked ? "mflux" : "siliconed",
        Model = masked ? Settings.MfluxModel : Settings.Model,
        MfluxPython = masked ? Settings.MacPath(settings.MfluxPython) : null,
        MfluxCli = masked ? Settings.MacPath(settings.MfluxCli) : null,
        MfluxModelPath = masked ? Settings.MacPath(settings.MfluxModelPath) : null,
        FullPrompt = (masked ? "Edit only the selected area. " + (preserve
            ? "Preserve the camera angle, room layout and building geometry. "
            : "") : (preserve
            ? "Transform the provided Revit 3D view into a photorealistic architectural visualization. Preserve the original camera angle, room layout, building geometry, walls, windows, doors, columns and major equipment positions. Apply the following visual changes:"
            : "Transform the provided Revit 3D view into a photorealistic architectural visualization. Apply the following visual changes:") + "\n") + prompt,
        PreserveGeometry = preserve,
        SshTarget = settings.SshTarget,
        CliPath = settings.SilicontrolPath
    };

    internal static void Save(RenderJob job, bool replaceExisting = true)
    {
        if (job.Legacy) throw new InvalidOperationException("Legacy history is read-only.");
        Directory.CreateDirectory(job.DirectoryPath);
        var path = Path.Combine(job.DirectoryPath, "job.json");
        var temporary = Path.Combine(job.DirectoryPath, "metadata-" + Guid.NewGuid().ToString("N") + ".json");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            Json.Write(stream, job);
        if (replaceExisting && File.Exists(path))
            File.Replace(temporary, path, null);
        else
            File.Move(temporary, path);
    }

    internal static FileStream Lock(RenderJob job) => new(Path.Combine(job.DirectoryPath, "monitor.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal static RenderJob ReadJob(string directory)
    {
        using var file = new FileStream(Path.Combine(directory, "job.json"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var job = Json.Read<RenderJob>(file);
        if (!Guid.TryParseExact(job.Id, "N", out var id) ||
            !string.Equals(id.ToString("N"), Path.GetFileName(directory), StringComparison.Ordinal) ||
            job.CreatedUtc == default)
            throw new InvalidDataException("Invalid job metadata.");
        return job;
    }

    internal static RenderJob? Find(Guid id)
    {
        var name = id.ToString("N");
        var current = Path.Combine(Root, name);
        if (File.Exists(Path.Combine(current, "job.json")))
            return ReadJob(current);
        var legacy = Path.Combine(LegacyRoot, name);
        if (!File.Exists(Path.Combine(legacy, "job.json")))
            return null;
        var job = ReadJob(legacy);
        job.Legacy = true;
        return job;
    }

    internal static RenderJob[] ReadHistory()
    {
        var jobs = new List<RenderJob>();
        var roots = new[] { Root, LegacyRoot };
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                continue;
            try
            {
                var job = ReadJob(directory);
                job.Legacy = root == LegacyRoot;
                jobs.Add(job);
            }
            catch (Exception error) when (error is IOException || error is SerializationException || error is UnauthorizedAccessException)
            {
                Diagnostics.Exception("history-read", error);
            }
        }
        return jobs.OrderByDescending(job => job.CreatedUtc).ToArray();
    }

    internal static void PrepareInput(RenderJob job)
    {
        var source = LocalFiles.OpenPng(job.SourcePath);
        job.SourceWidth = source.PixelWidth;
        job.SourceHeight = source.PixelHeight;
        if (job.IsMasked)
            return;
        var scale = Math.Min(1, Math.Sqrt((double)PixelArea / ((long)source.PixelWidth * source.PixelHeight)));
        BitmapSource input = source;
        if (scale < 1)
        {
            // The same factor on both axes. Reduce it slightly if integer rounding exceeds the area.
            do
            {
                input = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scale *= 0.999;
            }
            while ((long)input.PixelWidth * input.PixelHeight > PixelArea);
        }

        job.InputWidth = input.PixelWidth;
        job.InputHeight = input.PixelHeight;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(input));
        using var file = new FileStream(Path.Combine(job.DirectoryPath, "input.png"), FileMode.CreateNew);
        encoder.Save(file);
    }
}