using Microsoft.Extensions.Configuration;
using QbchRequestRestore;
using QbchRequestRestore.Data;
using QbchRequestRestore.Stages;

// Загрузка настроек (appsettings.json лежит рядом с exe)
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var config = AppConfig.Load(configuration);

// Разбор аргументов: <stage> [--push]
var stage = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var push = args.Contains("--push", StringComparer.OrdinalIgnoreCase);

using var db = new RestoreDb(config.DbPath);

switch (stage)
{
    case "stage1":
        // Логи saver -> список пропавших ключей
        Stage1Missing.Run(config.SaverLogsDir, config.MissingTxt, db);
        break;

    case "stage2":
        // Логи qbch_api -> связки и запросный XML
        Stage2ParseApiLogs.Run(config.ApiLogsDir, db);
        break;

    case "stage3":
        // Свод + dry-run/запись в Redis и Kafka
        await Stage3BuildAndPush.Run(config, db, push);
        break;

    default:
        PrintHelp();
        break;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Восстановление пропавших dlrequest из логов.

        Использование:
          qbch_request_restore stage1            Собрать пропавшие ключи из логов saver -> missing.txt + БД
          qbch_request_restore stage2            Распарсить логи qbch_api (связки + запросный XML) -> БД
          qbch_request_restore stage3            Свод и отчёт (dry-run, без записи)
          qbch_request_restore stage3 --push     Записать в Redis и отправить событие в Kafka

        Настройки — в appsettings.json (пути логов, подключения Redis/Kafka).
        """);
}
