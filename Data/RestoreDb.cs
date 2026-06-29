using Microsoft.Data.Sqlite;

namespace QbchRequestRestore.Data;

/// <summary>
/// Простая обёртка над локальной SQLite-базой.
/// Хранит промежуточные данные между этапами: связки, запросы, список пропавших и журнал восстановления.
/// Все операции идемпотентны (INSERT OR REPLACE) — этапы можно перезапускать.
/// </summary>
public sealed class RestoreDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public RestoreDb(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        CreateSchema();
    }

    private void CreateSchema()
    {
        Execute("""
            -- Связка: TransactionId (dlrequest) <-> ИдентификаторЗапроса (RequestId)
            CREATE TABLE IF NOT EXISTS links (
                transaction_id    TEXT PRIMARY KEY,
                request_id        TEXT,
                request_date_time TEXT
            );

            -- Запросный XML по ИдентификаторЗапроса
            CREATE TABLE IF NOT EXISTS requests (
                request_id  TEXT PRIMARY KEY,
                request_xml TEXT
            );

            -- Пропавшие ключи (из логов saver)
            CREATE TABLE IF NOT EXISTS missing (
                transaction_id TEXT PRIMARY KEY
            );

            -- Журнал восстановления (этап 3)
            CREATE TABLE IF NOT EXISTS restore (
                transaction_id TEXT PRIMARY KEY,
                request_id     TEXT,
                status         TEXT,   -- matched | no_link | no_xml | pushed | error
                note           TEXT,
                pushed_at      TEXT
            );
            """);
    }

    /// <summary>Добавить/обновить пропавший ключ.</summary>
    public void UpsertMissing(string transactionId) =>
        Execute("INSERT OR REPLACE INTO missing(transaction_id) VALUES($id);",
            ("$id", transactionId));

    /// <summary>Добавить/обновить связку TransactionId -> RequestId.</summary>
    public void UpsertLink(string transactionId, string requestId, string? requestDateTime) =>
        Execute("INSERT OR REPLACE INTO links(transaction_id, request_id, request_date_time) VALUES($t, $r, $d);",
            ("$t", transactionId), ("$r", requestId), ("$d", (object?)requestDateTime ?? DBNull.Value));

    /// <summary>Добавить/обновить запросный XML.</summary>
    public void UpsertRequest(string requestId, string requestXml) =>
        Execute("INSERT OR REPLACE INTO requests(request_id, request_xml) VALUES($r, $x);",
            ("$r", requestId), ("$x", requestXml));

    /// <summary>Записать результат восстановления одного ключа.</summary>
    public void UpsertRestore(string transactionId, string? requestId, string status, string? note, string? pushedAt) =>
        Execute("INSERT OR REPLACE INTO restore(transaction_id, request_id, status, note, pushed_at) VALUES($t, $r, $s, $n, $p);",
            ("$t", transactionId), ("$r", (object?)requestId ?? DBNull.Value), ("$s", status),
            ("$n", (object?)note ?? DBNull.Value), ("$p", (object?)pushedAt ?? DBNull.Value));

    public int Count(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Свести пропавшие ключи с найденными данными.
    /// Возвращает по каждому пропавшему: id транзакции, requestId, дату запроса и XML (если нашлись).
    /// </summary>
    public IEnumerable<MissingJoin> SelectMissingJoined()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.transaction_id, l.request_id, l.request_date_time, r.request_xml
            FROM missing m
            LEFT JOIN links    l ON l.transaction_id = m.transaction_id
            LEFT JOIN requests r ON r.request_id     = l.request_id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new MissingJoin(
                TransactionId: reader.GetString(0),
                RequestId: reader.IsDBNull(1) ? null : reader.GetString(1),
                RequestDateTime: reader.IsDBNull(2) ? null : reader.GetString(2),
                RequestXml: reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    /// <summary>Выполнить пакет операций в одной транзакции (ускоряет массовую загрузку из логов).</summary>
    public void RunInTransaction(Action body)
    {
        using var tx = _connection.BeginTransaction();
        body();
        tx.Commit();
    }

    private void Execute(string sql, params (string name, object value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Строка сведения пропавшего ключа с найденными данными.</summary>
public sealed record MissingJoin(string TransactionId, string? RequestId, string? RequestDateTime, string? RequestXml);
