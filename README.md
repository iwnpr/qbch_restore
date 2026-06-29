# qbch_request_restore

Консольная утилита (.NET 8) для восстановления пропавших `dlrequest`, которые qbch_db_saver_sdc
не успел сохранить до истечения TTL в Redis. Данные собираются заново из логов Serilog сервиса
qbch_api, кладутся в Redis и анонсируются событием в Kafka — так же, как это делал боевой qbch_api.

## Как это работает

Цепочка связывания:

```
пропавший ключ  QBCH:dlrequest:<TransactionId>      (из логов saver)
   └─ пара строк QBCHProcessingHandlerV3:  TransactionId ↔ RequestId(ИдентификаторЗапроса)
        └─ запись fip_search_for_api_3_0:  запросный XML (ЗапросСведений)
```

Промежуточные данные хранятся в локальной SQLite-базе `restore.db`
(таблицы `missing`, `links`, `requests`, `restore`). Этапы идемпотентны.

## Этапы

```
dotnet run -- stage1            # логи saver  -> пропавшие ключи (missing.txt + БД)
dotnet run -- stage2            # логи qbch_api -> связки и запросный XML (БД)
dotnet run -- stage3            # свод и отчёт (dry-run, БЕЗ записи)
dotnet run -- stage3 --push     # запись в Redis + событие в Kafka
```

По умолчанию stage3 работает в режиме dry-run и ничего не пишет — печатает отчёт
(сколько ключей можно восстановить) и превью первого хэша. Реальная запись — только с `--push`.

## Что восстанавливается

Только **запросная часть** dlrequest — то, что есть в логах qbch_api:

| Поле хэша | Источник |
|---|---|
| `request_xml` | XML `fip_search_for_api_3_0` из лога |
| `response_guid` | TransactionId |
| `request_id` | ИдентификаторЗапроса |
| `request_date_time` | строка `... Request: ...` из лога |
| `request_certificate_thumbprint` | `-` (отпечаток в логах не пишется) |
| `error_code` | `0` |
| `api_version`, `contract_version` | `3.0` |

Подписанный ответ ЦБ (`response_signed_data`, `response_xml`) и разбивка по КБКИ-задачам
в логах отсутствуют и не восстанавливаются. После записи saver сохранит сам dlrequest,
субъектов, запросы и пользователей; `AbonentId` будет пустым (нет отпечатка сертификата).

## Настройки — `appsettings.json`

Пути к логам, подключение к Redis (`ConnectionString`, `DbIndex`, `TtlMinutes`) и Kafka
(`BootstrapServers`, `Topic`). Перед `--push` укажите боевые адреса Redis/Kafka и тот же топик,
который читает saver (`QBCHMessagesTopic_V3_27`).
