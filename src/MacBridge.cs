using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RevitAiRender;

internal sealed class MacBridge
{
    private readonly RenderJob job;
    private readonly string target;
    private string RemoteParent => job.IsMasked ? ".revit-ai-render-v020/jobs" : ".revit-ai-render/jobs";
    private string RemoteDirectory => RemoteParent + "/" + Guid.ParseExact(job.Id, "N").ToString("N");

    internal MacBridge(RenderJob job)
    {
        this.job = job;
        target = Settings.ValidateTarget(job.SshTarget);
    }

    internal async Task StageAsync(CancellationToken token)
    {
        var remote = RemoteDirectory;
        var ready = await SshAsync("umask 077; /bin/mkdir -p " + RemoteParent + " && /bin/mkdir " + remote, token)
            .ConfigureAwait(false);
        if (ready.Code != 0)
            throw BridgeErrors.For("write");

        if (job.IsMasked)
        {
            await StageMfluxAsync(token).ConfigureAwait(false);
            return;
        }

        var scriptPath = Path.Combine(job.DirectoryPath, "bridge.sh");
        using (var resource = typeof(MacBridge).Assembly.GetManifestResourceStream("RevitAiRender.bridge.sh")
            ?? throw new InvalidOperationException("Missing bridge resource."))
        using (var output = new FileStream(scriptPath, FileMode.CreateNew))
            resource.CopyTo(output);

        // Braces are literal in this plugin. Siliconed's alternatives may otherwise expand into several jobs.
        var prompt = job.FullPrompt.Replace("{", "\\{").Replace("}", "\\}");
        File.WriteAllText(Path.Combine(job.DirectoryPath, "prompt.txt"), prompt, LocalFiles.Utf8);
        File.WriteAllText(Path.Combine(job.DirectoryPath, "cli-path.txt"), job.CliPath ?? Settings.DefaultCli, LocalFiles.Utf8);
        var batch = string.Join("\n", new[] { "input.png", "prompt.txt", "cli-path.txt", "bridge.sh" }
            .Select(file => $"put {file} {remote}/{file}")) + "\n";
        await SftpAsync(batch, "upload", token).ConfigureAwait(false);
    }

    private async Task StageMfluxAsync(CancellationToken token)
    {
        foreach (var file in new[] { "mflux-control.sh", "mflux-worker.py" })
        {
            using var resource = typeof(MacBridge).Assembly.GetManifestResourceStream("RevitAiRender." + file)
                ?? throw new InvalidOperationException("Missing MFLUX bridge resource.");
            using var output = new FileStream(Path.Combine(job.DirectoryPath, file), FileMode.CreateNew);
            resource.CopyTo(output);
        }

        job.Seed = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0) & 0x7fffffffu;
        job.Steps = 8;
        var settings = new MfluxConfig
        {
            Cli = job.MfluxCli!,
            Model = job.MfluxModelPath!,
            Silicontrol = job.CliPath ?? Settings.DefaultCli,
            Seed = job.Seed.Value
        };
        using (var file = new FileStream(Path.Combine(job.DirectoryPath, "mflux.json"), FileMode.CreateNew))
            Json.Write(file, settings);
        File.WriteAllText(Path.Combine(job.DirectoryPath, "prompt.txt"), job.FullPrompt, LocalFiles.Utf8);
        File.WriteAllText(Path.Combine(job.DirectoryPath, "python-path.txt"), job.MfluxPython!, LocalFiles.Utf8);
        var files = new[] { "source.png", "mask.png", "mflux.json", "prompt.txt", "python-path.txt",
            "mflux-control.sh", "mflux-worker.py" };
        await SftpAsync(string.Join("\n", files.Select(file => $"put {file} {RemoteDirectory}/{file}")) + "\n",
            "upload", token).ConfigureAwait(false);
    }

    internal async Task<MfluxStatus[]> MfluxControlAsync(string mode, CancellationToken token)
    {
        var result = await SshAsync("cd " + RemoteDirectory + " && /bin/sh ./mflux-control.sh " + mode, token)
            .ConfigureAwait(false);
        if (result.Code != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw new RenderException("bridge", "На Mac недоступны файлы задачи MFLUX.");
        return result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Json.Parse<MfluxStatus>).ToArray();
    }

    internal async Task FetchMfluxAsync(string file, int? width, int? height, CancellationToken token)
    {
        // This method is called only with the fixed names defined by the worker protocol.
        var destination = Path.Combine(job.DirectoryPath, file);
        if (File.Exists(destination))
            return;
        var temporary = "transfer-" + Guid.NewGuid().ToString("N");
        await SftpAsync($"get {RemoteDirectory}/{file} {temporary}\n", "download", token).ConfigureAwait(false);
        var downloaded = Path.Combine(job.DirectoryPath, temporary);
        if (width.HasValue && height.HasValue)
            ValidatePng(downloaded, width.Value, height.Value);
        File.Move(downloaded, destination);
    }

    internal async Task<CliMessage[]> ControlAsync(string mode, CancellationToken token)
    {
        // mode values come exclusively from these fixed call sites, never from prompt or settings.
        switch (mode)
        {
            case "launch":
            case "probe":
            case "recover":
            case "cancel":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        var result = await SshAsync("cd " + RemoteDirectory + " && /bin/sh ./bridge.sh " + mode, token)
            .ConfigureAwait(false);
        if (result.Code != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw new RenderException("bridge", "На Mac недоступны файлы задачи. Проверьте состояние по её идентификатору.");
        return CliMessage.ReadLines(result.Output);
    }

    internal async Task DownloadAsync(string remotePath, int width, int height, CancellationToken token)
    {
        if (File.Exists(job.ResultPath))
        {
            ValidatePng(job.ResultPath, width, height);
            return;
        }
        var prefix = "/" + RemoteDirectory + "/output/";
        var index = remotePath.LastIndexOf(prefix, StringComparison.Ordinal);
        var basename = index < 0 ? "" : remotePath.Substring(index + prefix.Length);
        if (!Regex.IsMatch(basename, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\.png\z"))
            throw BridgeErrors.For("protocol");

        var localName = "download-" + Guid.NewGuid().ToString("N") + ".png";
        var localPath = Path.Combine(job.DirectoryPath, localName);
        await SftpAsync($"get {RemoteDirectory}/output/{basename} {localName}\n", "download", token)
            .ConfigureAwait(false);
        ValidatePng(localPath, width, height);
        File.Move(localPath, job.ResultPath);
    }

    private static void ValidatePng(string path, int width, int height)
    {
        try
        {
            var png = LocalFiles.OpenPng(path);
            if (png.PixelWidth != width || png.PixelHeight != height)
                throw new InvalidDataException("Wrong result size.");
            // Force WIC to decode the pixels before committing a transferred file.
            var stride = (png.PixelWidth * png.Format.BitsPerPixel + 7) / 8;
            png.CopyPixels(new byte[stride * png.PixelHeight], stride, 0);
        }
        catch (Exception error) when (error is IOException || error is NotSupportedException
            || error is FileFormatException || error is ArgumentException)
        {
            throw new RenderException("png-transfer", "Получен повреждённый PNG. Проверьте задачу в истории, чтобы скачать заново.");
        }
    }

    private async Task SftpAsync(string batch, string operation, CancellationToken token)
    {
        var result = await RunAsync("sftp", "-b - " + Quote(target), batch, TimeSpan.FromSeconds(90), token)
            .ConfigureAwait(false);
        if (result.Code != 0)
        {
            ThrowConnectionError(result.Error, false);
            throw new RenderException(operation, operation == "upload"
                ? "Не удалось передать исходный PNG и промпт на Mac."
                : "Не удалось скачать PNG с Mac. Проверьте задачу в истории.");
        }
    }

    private async Task<CommandResult> SshAsync(string command, CancellationToken token)
    {
        var result = await RunAsync("ssh", "-T " + Quote(target) + " " + Quote(command), null,
            TimeSpan.FromSeconds(25), token).ConfigureAwait(false);
        if (result.Code == 255)
            ThrowConnectionError(result.Error, true);
        return result;
    }

    private async Task<CommandResult> RunAsync(string program, string args, string? input, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", program + ".exe");
        if (!File.Exists(exe))
            throw new RenderException("openssh", "В Windows недоступен клиент OpenSSH.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe,
                "-o BatchMode=yes -o StrictHostKeyChecking=yes -o UpdateHostKeys=no -o ConnectTimeout=8 " +
                "-o ConnectionAttempts=1 -o ServerAliveInterval=6 -o ServerAliveCountMax=2 " + args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = job.DirectoryPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = LocalFiles.Utf8,
                StandardErrorEncoding = LocalFiles.Utf8
            }
        };

        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error)
        {
            Diagnostics.Exception("openssh-start", error);
            throw new RenderException("openssh", "Не удалось запустить клиент OpenSSH.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var work = Task.WhenAll(outputTask, errorTask);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = stop.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        stop.CancelAfter(timeout);
        try
        {
            using (var stdin = process.StandardInput.BaseStream)
            {
                if (input != null)
                {
                    var bytes = LocalFiles.Utf8.GetBytes(input);
                    await stdin.WriteAsync(bytes, 0, bytes.Length, stop.Token).ConfigureAwait(false);
                }
            }
            await work.ConfigureAwait(false);
            await Task.Run(process.WaitForExit, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await work.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowConnectionError(errorTask.Result, false);
            throw new RenderException("ssh-io", "Соединение с Mac прервалось во время передачи.");
        }
        token.ThrowIfCancellationRequested();
        if (stop.IsCancellationRequested)
            throw new RenderException("ssh-timeout", "Mac не ответил вовремя. Проверьте ZeroTier и SSH.");
        Diagnostics.Write("transport", $"job={job.Id} tool={program} exit={process.ExitCode}");
        return new CommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static void ThrowConnectionError(string error, bool fallback)
    {
        if (error.IndexOf("Host key verification failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
            error.IndexOf("REMOTE HOST IDENTIFICATION", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new RenderException("ssh-host-key", "SSH не подтвердил ключ Mac. Проверьте существующую запись known_hosts.");
        if (error.IndexOf("Permission denied (", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new RenderException("ssh-auth", "SSH не смог войти на Mac. Проверьте имя пользователя и существующую аутентификацию Windows.");
        if (fallback || Regex.IsMatch(error, "timed out|Could not resolve|No route|Connection reset|Connection refused", RegexOptions.IgnoreCase))
            throw new RenderException("ssh-connection", "Mac недоступен по SSH. Проверьте сеть ZeroTier, адрес и удалённый вход.");
    }

    // Windows CreateProcess argument quoting. This never constructs a remote shell token from the prompt.
    private static string Quote(string argument) =>
        "\"" + Regex.Replace(argument, "(\\\\*)\"|(\\\\+)$",
            match => match.Groups[1].Success ? match.Groups[1].Value + match.Groups[1].Value + "\\\"" : match.Value + match.Value) + "\"";

    private sealed class CommandResult
    {
        internal CommandResult(int code, string output, string error)
        {
            Code = code;
            Output = output;
            Error = error;
        }
        internal int Code { get; }
        internal string Output { get; }
        internal string Error { get; }
    }
}