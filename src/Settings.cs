using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace RevitAiRender;

[DataContract]
internal sealed class Settings : IExtensibleDataObject
{
    internal const string MfluxModel = "qwen-image-2.1-turbo";
    internal const string Model = "qwen-image-2.1";
    internal const string DefaultCli = "/Applications/Siliconed.app/Contents/Helpers/silicontrol";
    private static string FilePath => Path.Combine(LocalFiles.Root, "settings.json");

    [DataMember(Name = "sshTarget")] public string? SshTarget { get; set; }
    [DataMember(Name = "silicontrolPath")] public string? SilicontrolPath { get; set; }
    [DataMember(Name = "mfluxPython")] public string? MfluxPython { get; set; }
    [DataMember(Name = "mfluxCli")] public string? MfluxCli { get; set; }
    [DataMember(Name = "mfluxModel")] public string? MfluxModelPath { get; set; }
    public ExtensionDataObject? ExtensionData { get; set; }

    internal static string MacPath(string? path)
    {
        if (!IsAbsoluteMacPath(path))
            throw new RenderException("mflux-settings", "Укажите абсолютные пути к Python, команде MFLUX и модели на Mac.");
        return path!;
    }

    private static bool IsAbsoluteMacPath(string? path) => path != null &&
        path.StartsWith("/", StringComparison.Ordinal) && path.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0;

    internal static string ValidateTarget(string? target)
    {
        if (target == null || !Regex.IsMatch(target, @"\A(?:[a-zA-Z0-9._-]+@)?[a-zA-Z0-9][a-zA-Z0-9._-]*\z"))
            throw new RenderException("ssh-config", "Укажите псевдоним OpenSSH или пользователя и адрес Mac.");
        return target;
    }

    internal void ValidateConnection(bool requireCompleteMflux = false)
    {
        SshTarget = ValidateTarget(SshTarget);
        SilicontrolPath ??= DefaultCli;
        if (!IsAbsoluteMacPath(SilicontrolPath))
            throw new RenderException("cli-config", "Укажите абсолютный путь к silicontrol на Mac.");
        if (requireCompleteMflux && (MfluxPython != null || MfluxCli != null || MfluxModelPath != null))
        {
            MacPath(MfluxPython);
            MacPath(MfluxCli);
            MacPath(MfluxModelPath);
        }
    }

    internal static Settings Load()
    {
        var settings = ReadForEditing();
        settings.ValidateConnection();
        return settings;
    }

    internal static Settings ReadForEditing(string? path = null)
    {
        try
        {
            using var file = new FileStream(path ?? FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return Json.Read<Settings>(file);
        }
        catch (FileNotFoundException) { return new Settings(); }
        catch (DirectoryNotFoundException) { return new Settings(); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SerializationException)
        {
            Diagnostics.Exception("settings-read", error);
            throw new RenderException("settings", "Не удалось прочитать локальный settings.json.");
        }
    }

    internal void Save(string? path = null)
    {
        ValidateConnection(requireCompleteMflux: true);
        path ??= FilePath;
        var directory = Path.GetDirectoryName(path)!;
        var staged = Path.Combine(directory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Json.Write(file, this);
            if (File.Exists(path))
                File.Replace(staged, path, null);
            else
                File.Move(staged, path);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SerializationException)
        {
            Diagnostics.Exception("settings-save", error);
            throw new RenderException("settings-save", "Не удалось сохранить локальные настройки AI Render.");
        }
    }
}
