using QbchRequestRestore.Data;
using QbchRequestRestore.Kafka;
using QbchRequestRestore.Redis;

namespace QbchRequestRestore.Stages;

/// <summary>
/// Этап 3. Свести пропавшие ключи с найденными данными и:
///   --dry-run (по умолчанию) — только отчёт и превью того, что будет записано;
///   --push — записать хэш в Redis и отправить событие в Kafka.
/// Результат по каждому ключу пишется в таблицу restore.
/// </summary>
public static class Stage3BuildAndPush
{
    public static async Task Run(AppConfig config, RestoreDb db, bool push)
    {
        var rows = db.SelectMissingJoined().ToList();
        if (rows.Count == 0)
        {
            Console.WriteLine("[stage3] В таблице missing пусто. Сначала запустите stage1.");
            return;
        }

        var matched = rows.Where(r => r.RequestXml is not null).ToList();
        var noLink = rows.Count(r => r.RequestId is null);
        var noXml = rows.Count(r => r.RequestId is not null && r.RequestXml is null);

        Console.WriteLine($"[stage3] Всего пропавших: {rows.Count}");
        Console.WriteLine($"[stage3]   можно восстановить (есть XML): {matched.Count}");
        Console.WriteLine($"[stage3]   нет связки TransactionId->RequestId: {noLink}");
        Console.WriteLine($"[stage3]   связка есть, но нет XML: {noXml}");

        // Зафиксируем причины для нерешённых
        //foreach (var row in rows.Where(r => r.RequestXml is null))
        //{
        //    var status = row.RequestId is null ? "no_link" : "no_xml";
        //    db.UpsertRestore(row.TransactionId, row.RequestId, status, null, null);
        //}

        if (matched.Count > 0)
            PrintPreview(matched[0]);

        if (!push)
        {
            Console.WriteLine("[stage3] Режим dry-run: запись НЕ выполнялась. Для записи добавьте --push.");
            foreach (var row in matched)
                db.UpsertRestore(row.TransactionId, row.RequestId, "matched", null, null);
            return;
        }

        await PushAll(config, db, matched);
    }

    private static async Task PushAll(AppConfig config, RestoreDb db, List<MissingJoin> matched)
    {
        Console.WriteLine($"[stage3] Запись {matched.Count} ключей в Redis ({config.RedisConnectionString}) и Kafka ({config.KafkaTopic})...");

        using var redis = new RedisWriter(config.RedisConnectionString, config.RedisDbIndex, config.RedisTtlMinutes);
        using var kafka = new KafkaSender(config.KafkaBootstrapServers, config.KafkaTopic);

        var ok = 0;
        var failed = 0;

        foreach (var row in matched)
        {
            try
            {
                var entries = RedisWriter.BuildHash(row);
                redis.Write(row.TransactionId, entries);
                await kafka.ProduceAsync(RedisWriter.KeyFor(row.TransactionId));

                db.UpsertRestore(row.TransactionId, row.RequestId, "pushed", null, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
                ok++;
            }
            catch (Exception ex)
            {
                db.UpsertRestore(row.TransactionId, row.RequestId, "error", ex.Message, null);
                failed++;
                Console.WriteLine($"[stage3] ОШИБКА по {row.TransactionId}: {ex.Message}");
            }
        }

        Console.WriteLine($"[stage3] Записано успешно: {ok}, с ошибкой: {failed}");
    }

    private static void PrintPreview(MissingJoin row)
    {
        Console.WriteLine();
        Console.WriteLine("[stage3] Превью записи для первого ключа:");
        Console.WriteLine($"  Redis key: {RedisWriter.KeyFor(row.TransactionId)}");
        foreach (var entry in RedisWriter.BuildHash(row))
        {
            var value = entry.Value.ToString() ?? string.Empty;
            if (entry.Name == "request_xml")
                value = $"<{value.Length} символов XML>";
            Console.WriteLine($"    {entry.Name} = {value}");
        }
        Console.WriteLine($"  Kafka -> topic, value = {RedisWriter.KeyFor(row.TransactionId)}");
        Console.WriteLine();
    }
}
