using StackExchange.Redis;
using QbchRequestRestore.Data;

namespace QbchRequestRestore.Redis;

/// <summary>
/// Сборка и запись хэша в Redis в точности так, как это делал qbch_api
/// (см. KeyValueStorageService.AddHashArray и QBCHProcessingCompleteHandlerV3).
/// Восстанавливаем только запросную часть: ответных/подписанных данных в логах нет.
/// </summary>
public sealed class RedisWriter : IDisposable
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly int _ttlMinutes;

    public RedisWriter(string connectionString, int dbIndex, int ttlMinutes)
    {
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase(dbIndex);
        _ttlMinutes = ttlMinutes;
    }

    /// <summary>Ключ Redis для пропавшего dlrequest.</summary>
    public static string KeyFor(string transactionId) => $"QBCH:dlrequest:{transactionId}";

    /// <summary>
    /// Собрать поля хэша из найденных данных.
    /// Все значения текстовые (бинарных полей вроде *_signed_data при восстановлении нет),
    /// поэтому, как и оригинал, кладём их строками.
    /// </summary>
    public static HashEntry[] BuildHash(MissingJoin row)
    {
        var entries = new List<HashEntry>
        {
            new("api_version", "3.0"),
            new("contract_version", "3.0"),
            new("request_xml", row.RequestXml),
            new("response_guid", row.TransactionId),
            new("request_id", row.RequestId),
            // В логах нет отпечатка сертификата — ставим "-", как делает сам qbch_api при отсутствии сертификата.
            new("request_certificate_thumbprint", "-"),
            // 0, чтобы saver не счёл запись "ожидающей результат" (код 12) и сохранил её.
            new("error_code", "0")
        };

        if (!string.IsNullOrWhiteSpace(row.RequestDateTime))
            entries.Add(new HashEntry("request_date_time", row.RequestDateTime));

        return entries.ToArray();
    }

    /// <summary>Записать хэш в Redis и проставить TTL.</summary>
    public void Write(string transactionId, HashEntry[] entries)
    {
        var key = KeyFor(transactionId);
        _db.HashSet(key, entries);
        _db.KeyExpire(key, TimeSpan.FromMinutes(_ttlMinutes));
    }

    public void Dispose() => _redis.Dispose();
}
