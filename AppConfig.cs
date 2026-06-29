using Microsoft.Extensions.Configuration;

namespace QbchRequestRestore;

/// <summary>Настройки приложения из appsettings.json.</summary>
public sealed class AppConfig
{
    public string ApiLogsDir { get; init; } = "logs";
    public string SaverLogsDir { get; init; } = "logs_saver";
    public string MissingTxt { get; init; } = "missing.txt";
    public string DbPath { get; init; } = "restore.db";

    public string RedisConnectionString { get; init; } = "localhost:6379";
    public int RedisDbIndex { get; init; }
    public int RedisTtlMinutes { get; init; } = 480;

    public string KafkaBootstrapServers { get; init; } = "localhost:9092";
    public string KafkaTopic { get; init; } = "QBCHMessagesTopic_V3_27";

    public static AppConfig Load(IConfiguration c) => new()
    {
        ApiLogsDir = c["Paths:ApiLogsDir"] ?? "logs",
        SaverLogsDir = c["Paths:SaverLogsDir"] ?? "logs_saver",
        MissingTxt = c["Paths:MissingTxt"] ?? "missing.txt",
        DbPath = c["Paths:Db"] ?? "restore.db",
        RedisConnectionString = c["Redis:ConnectionString"] ?? "localhost:6379",
        RedisDbIndex = c.GetValue<int?>("Redis:DbIndex") ?? 0,
        RedisTtlMinutes = c.GetValue<int?>("Redis:TtlMinutes") ?? 480,
        KafkaBootstrapServers = c["Kafka:BootstrapServers"] ?? "localhost:9092",
        KafkaTopic = c["Kafka:Topic"] ?? "QBCHMessagesTopic_V3_27",
    };
}
