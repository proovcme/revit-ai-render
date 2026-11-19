using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace RevitAiRender;

[DataContract]
internal sealed class CliMessage
{
    [DataMember(Name = "ok")] public bool? Ok { get; set; }
    [DataMember(Name = "type")] public string? Type { get; set; }
    [DataMember(Name = "phase")] public string? Phase { get; set; }
    [DataMember(Name = "error")] public string? Error { get; set; }
    [DataMember(Name = "job")] public string? Job { get; set; }
    [DataMember(Name = "image")] public string? Image { get; set; }
    [DataMember(Name = "path")] public string? Path { get; set; }
    [DataMember(Name = "write_error")] public string? WriteError { get; set; }
    [DataMember(Name = "position")] public int? Position { get; set; }
    [DataMember(Name = "seeds")] public ulong[]? Seeds { get; set; }
    [DataMember(Name = "seed")] public ulong? Seed { get; set; }
    [DataMember(Name = "model")] public string? Model { get; set; }
    [DataMember(Name = "width")] public int Width { get; set; }
    [DataMember(Name = "height")] public int Height { get; set; }
    [DataMember(Name = "edit")] public bool Edit { get; set; }
    [DataMember(Name = "references")] public int References { get; set; }
    [DataMember(Name = "steps")] public int? Steps { get; set; }
    [DataMember(Name = "evaluations")] public int? Evaluations { get; set; }
    [DataMember(Name = "seconds")] public double? Seconds { get; set; }
    [DataMember(Name = "neural_engine")] public bool? NeuralEngine { get; set; }
    [DataMember(Name = "tensor_units")] public string? TensorUnits { get; set; }
    [DataMember(Name = "outcome")] public string? Outcome { get; set; }
    [DataMember(Name = "code")] public string? Code { get; set; }
    [DataMember(Name = "jobs")] public CliMessage[]? Jobs { get; set; }
    [DataMember(Name = "running")] public CliMessage? Running { get; set; }
    [DataMember(Name = "queue")] public CliMessage[]? Queue { get; set; }
    [DataMember(Name = "waiting_for_license")] public CliMessage[]? WaitingForLicense { get; set; }
    [DataMember(Name = "stage")] public string? Stage { get; set; }
    [DataMember(Name = "step")] public int? Step { get; set; }
    [DataMember(Name = "steps_total")] public int? StepsTotal { get; set; }
    [DataMember(Name = "fraction")] public double? Fraction { get; set; }

    internal static CliMessage[] ReadLines(string output)
    {
        try
        {
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line)).Select(Json.Parse<CliMessage>).ToArray();
        }
        catch (Exception error) when (error is SerializationException || error is InvalidDataException)
        {
            throw new RenderException("protocol", "Siliconed вернул неполный или несовместимый JSON. Проверьте задачу в истории.");
        }
    }

    internal bool IsOwnEdit => Job != null && Regex.IsMatch(Job, @"\At[0-9]+\z")
        && Model == Settings.Model && Edit && References == 1;
}

internal static class BridgeErrors
{
    internal static RenderException For(string? code) => new(code ?? "unknown", code switch
    {
        "app_not_running" or "app_not_found" => "Siliconed недоступен. Откройте приложение на Mac.",
        "app_changed" => "Сеанс Siliconed завершился или сменился. Проверьте историю приложения на Mac.",
        "cli_missing" => "На Mac не найден silicontrol. Проверьте путь в settings.json.",
        "remote_compute" => "В Siliconed выбран другой компьютер. Для этой задачи нужен режим Compute On → This Mac.",
        "model" or "library_absent" => "В Siliconed недоступна установленная модель Qwen-Image-2.1 Turbo.",
        "license_not_accepted" => "Лицензия Qwen не принята. Откройте уведомление в Siliconed на Mac.",
        "insufficient_memory" or "out_of_memory" or "memory" => "На Mac не хватило памяти для генерации.",
        "busy" => "Siliconed занят другой операцией, например установкой модели. Повторите после её завершения.",
        "queue_full" => "Очередь Siliconed заполнена. Повторите после освобождения места.",
        "disk_full" or "write" => "Siliconed не смог записать файлы. Проверьте свободное место и доступ на Mac.",
        "ref" or "unreadable_prompt" => "Siliconed не прочитал исходный PNG или промпт.",
        "format" => "Siliconed отклонил размеры изображения для редактирования.",
        "unknown_job" => "Siliconed не нашёл задачу в текущем сеансе. Проверьте историю на Mac.",
        "not_submitted" => "Mac не зарегистрировал запуск. Можно создать новую задачу.",
        "already_finished" => "Задача уже завершена. Проверьте результат в истории.",
        "recovery_exists" => "Ожидание уже восстанавливалось. Проверьте результат в истории Siliconed на Mac.",
        "stopped" or "removed" => "Задача отменена.",
        "protocol" => "Ответ Siliconed не соответствует ожидаемому формату.",
        _ => "Генерация в Siliconed завершилась ошибкой. Подробности можно проверить на Mac."
    });
}