using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace RevitAiRender;

[DataContract]
internal sealed class Settings
{
    internal const string MfluxModel = "qwen-image-2.1-turbo";
    internal const string Model = "qwen-image-2.1";
    internal const string DefaultCli = "/Applications/Siliconed.app/Contents/Helpers/silicontrol";

    [DataMember(Name = "sshTarget")] public string? SshTarget { get; set; }
    [DataMember(Name = "silicontrolPath")] public string? SilicontrolPath { get; set; }

    [DataMember(Name = "mfluxPython")] public string? MfluxPython { get; set; }
    [DataMember(Name = "mfluxCli")] public string? MfluxCli { get; set; }
    [DataMember(Name = "mfluxModel")] public string? MfluxModelPath { get; set; }

    internal static string MacPath(string? path)
    {
        if (path == null || !path.StartsWith("/", StringComparison.Ordinal) || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            throw new RenderException("mflux-settings", "Укажите в settings.json абсолютные пути mfluxPython, mfluxCli и mfluxModel на Mac.");
        return path;
    }

    internal static string ValidateTarget(string? target)
    {
        if (target == null || !Regex.IsMatch(target, @"\A(?:[a-zA-Z0-9._-]+@)?[a-zA-Z0-9][a-zA-Z0-9._-]*\z"))
            throw new RenderException("ssh-config", "Укажите пользователя и адрес Mac или псевдоним OpenSSH в settings.json.");
        return target;
    }

    internal static Settings Load()
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(LocalFiles.Root, "settings.json"));
            var settings = Json.Read<Settings>(file);
            settings.SshTarget = ValidateTarget(settings.SshTarget);
            settings.SilicontrolPath ??= DefaultCli;
            if (!settings.SilicontrolPath.StartsWith("/", StringComparison.Ordinal) ||
                settings.SilicontrolPath.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new RenderException("cli-config", "Укажите абсолютный путь к silicontrol на Mac в settings.json.");
            return settings;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SerializationException)
        {
            throw new RenderException("settings", "Не удалось прочитать settings.json. Проверьте установку и настройки подключения.");
        }
    }
}