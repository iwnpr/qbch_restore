using System.Text;
using System.Text.RegularExpressions;
using QbchRequestRestore.Data;

namespace QbchRequestRestore.Stages;

/// <summary>
/// Этап 2. Разбор логов qbch_api. Извлекаем две вещи:
///   1) связку TransactionId (dlrequest) -> RequestId (ИдентификаторЗапроса);
///   2) запросный XML (ЗапросСведений) по RequestId.
///
/// Связка лежит в двух соседних строках обработчика QBCHProcessingHandlerV3:
///   ...Handle начало: TransactionId="&lt;guid&gt;"
///   ...QBCHProcessingHandlerV3: RequestId=&lt;guid&gt;
/// Строки могут быть не строго подряд (между ними вклинивается лог БД), поэтому
/// связываем "начало" со СЛЕДУЮЩЕЙ строкой RequestId.
///
/// XML логируется записью с маркером "(fip_search_for_api_3_0): " и занимает несколько строк
/// (одно событие Serilog пишется атомарно, поэтому блок XML не разрывается чужими строками).
/// </summary>
public static class Stage2ParseApiLogs
{
    // Начало новой записи лога: [2026-06-26 21:38:57.522 DBG]
    private static readonly Regex RecordStart = new(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \w{3}\]", RegexOptions.Compiled);

    private static readonly Regex TxStartRegex = new(@"Handle начало: TransactionId=""(?<tx>[0-9a-fA-F-]+)""", RegexOptions.Compiled);
    private static readonly Regex RequestIdRegex = new(@"QBCHProcessingHandlerV3: RequestId=(?<rid>[0-9a-fA-F-]+)", RegexOptions.Compiled);
    private static readonly Regex RequestDateRegex = new(@"QBCHIIIController ""(?<tx>[0-9a-fA-F-]+)"" Request: (?<dt>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}:\d+)", RegexOptions.Compiled);
    private static readonly Regex RequestIdAttrRegex = new(@"ИдентификаторЗапроса=""(?<rid>[0-9a-fA-F-]+)""", RegexOptions.Compiled);

    // Маркер строки, после которого начинается XML запроса
    private const string XmlMarker = "(fip_search_for_api_3_0): ";
    private const string XmlEndTag = "</ЗапросСведений>";

    public static void Run(string apiLogsDir, RestoreDb db)
    {
        if (!Directory.Exists(apiLogsDir))
        {
            Console.WriteLine($"[stage2] Папка логов qbch_api не найдена: {apiLogsDir}");
            return;
        }

        var logFiles = Directory.GetFiles(apiLogsDir, "*.log");
        Console.WriteLine($"[stage2] Файлов логов qbch_api: {logFiles.Length}");

        var links = 0;
        var requests = 0;

        foreach (var file in logFiles)
        {
            var result = (links: 0, requests: 0);
            db.RunInTransaction(() => result = ParseFile(file, db));
            links += result.links;
            requests += result.requests;
        }

        Console.WriteLine($"[stage2] Связок (TransactionId->RequestId): {links}");
        Console.WriteLine($"[stage2] Запросных XML: {requests}");
        Console.WriteLine($"[stage2] В БД сейчас: links={db.Count("links")}, requests={db.Count("requests")}");
    }

    private static (int links, int requests) ParseFile(string file, RestoreDb db)
    {
        var links = 0;
        var requests = 0;

        // Дата запроса по TransactionId (строка "Request:" идёт раньше блока обработчика)
        var txDate = new Dictionary<string, string>();
        // TransactionId, ждущий свою строку RequestId
        string? pendingTx = null;

        // Состояние сбора многострочного XML
        var collectingXml = false;
        var xml = new StringBuilder();

        foreach (var line in File.ReadLines(file))
        {
            if (collectingXml)
            {
                xml.AppendLine(line);
                if (line.Contains(XmlEndTag, StringComparison.Ordinal))
                {
                    SaveXml(xml.ToString(), db, ref requests);
                    collectingXml = false;
                    xml.Clear();
                }
                continue;
            }

            // Начало XML-записи: всё после маркера на этой строке — первая строка XML
            var markerIdx = line.IndexOf(XmlMarker, StringComparison.Ordinal);
            if (markerIdx >= 0)
            {
                var firstLine = line[(markerIdx + XmlMarker.Length)..];
                xml.Clear();
                xml.AppendLine(firstLine);
                // Иногда XML может уместиться в одну строку
                if (firstLine.Contains(XmlEndTag, StringComparison.Ordinal))
                {
                    SaveXml(xml.ToString(), db, ref requests);
                    xml.Clear();
                }
                else
                {
                    collectingXml = true;
                }
                continue;
            }

            // Дата запроса
            var dateMatch = RequestDateRegex.Match(line);
            if (dateMatch.Success)
            {
                txDate[dateMatch.Groups["tx"].Value] = dateMatch.Groups["dt"].Value;
                continue;
            }

            // Начало обработки: запоминаем TransactionId
            var txMatch = TxStartRegex.Match(line);
            if (txMatch.Success)
            {
                pendingTx = txMatch.Groups["tx"].Value;
                continue;
            }

            // Строка с RequestId: связываем с последним TransactionId
            var ridMatch = RequestIdRegex.Match(line);
            if (ridMatch.Success && pendingTx is not null)
            {
                var requestId = ridMatch.Groups["rid"].Value;
                txDate.TryGetValue(pendingTx, out var date);
                db.UpsertLink(pendingTx, requestId, date);
                links++;
                pendingTx = null;
            }
        }

        return (links, requests);
    }

    private static void SaveXml(string xmlText, RestoreDb db, ref int requests)
    {
        var match = RequestIdAttrRegex.Match(xmlText);
        if (!match.Success)
            return; // не нашли ИдентификаторЗапроса — пропускаем

        db.UpsertRequest(match.Groups["rid"].Value, xmlText.Trim());
        requests++;
    }
}
