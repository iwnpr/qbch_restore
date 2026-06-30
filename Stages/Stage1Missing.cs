using System.Text.RegularExpressions;
using QbchRequestRestore.Data;

namespace QbchRequestRestore.Stages;

/// <summary>
/// Этап 1. Сбор списка пропавших ключей из логов qbch_db_saver_sdc.
/// Ищем записи вида: Не удалось найти ключ в кеше "QBCH:dlrequest:&lt;guid&gt;"
/// Результат пишем в таблицу missing и (для удобства) в файл missing.txt.
/// </summary>
public static class Stage1Missing
{
    // QBCH:dlrequest:<guid> внутри сообщения "Не удалось найти ключ в кеше"
    private static readonly Regex MissingKeyRegex = new(
        @"Не удалось найти ключ в кеше\s+""?QBCH:dlrequest:(?<guid>[0-9a-fA-F-]+)""?",
        RegexOptions.Compiled);

    public static void Run(string saverLogsDir, string missingTxtPath, RestoreDb db)
    {
        if (!Directory.Exists(saverLogsDir))
        {
            Console.WriteLine($"[stage1] Папка логов saver не найдена: {saverLogsDir}");
            return;
        }

        var logFiles = Directory.GetFiles(saverLogsDir, "*.log");
        Console.WriteLine($"[stage1] Файлов логов saver: {logFiles.Length}");

        // Уникальные id транзакций (один ключ обычно встречается в логах много раз)
        var found = new HashSet<string>();

        foreach (var file in logFiles)
        {
            foreach (var line in File.ReadLines(file))
            {
                var match = MissingKeyRegex.Match(line);
                if (match.Success)
                    found.Add(match.Groups["guid"].Value);
            }
        }

        // Запись в БД одной транзакцией
        db.RunInTransaction(() =>
        {
            foreach (var id in found)
                db.UpsertMissing(id);
        });

        // Дублируем в missing.txt в том же формате, что и пример из задачи (полный ключ)
        File.WriteAllLines(missingTxtPath, found.Select(id => $"QBCH:dlrequest:{id}"));

        Console.WriteLine($"[stage1] Уникальных пропавших ключей: {found.Count}");
        Console.WriteLine($"[stage1] Записано в БД (missing) и в файл {missingTxtPath}");
    }
}
