using ClosedXML.Excel;
using System.Text.Json;

namespace DataConverter.Converters;

public enum OutputFormat
{
    DictByFirstColumn, // 첫 번째 열이 키 → { "Bat": { "maxHp": 10, ... } }
    Array,             // 모든 열 포함 배열 → [ { "waveNumber": 1, ... } ]
    KeyValueDict,      // 두 열짜리 키-값 → { "waveinterval": 8.0 }
}

public record TableSpec(string FileName, string OutputName, OutputFormat Format, string Label);

/// <summary>
/// 헤더 행을 읽어 Excel → JSON을 자동 변환한다.
/// DTO 정의 없이 Excel 파일 구조만으로 동작한다.
/// </summary>
public static class TableConverter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly TableSpec[] DefaultSpecs =
    [
        new("monsters.xlsx", "monsters.json", OutputFormat.DictByFirstColumn, "Monster"),
        new("weapons.xlsx",  "weapons.json",  OutputFormat.DictByFirstColumn, "Weapon"),
        new("waves.xlsx",    "waves.json",    OutputFormat.Array,              "Wave"),
        new("config.xlsx",   "config.json",   OutputFormat.KeyValueDict,       "Config"),
    ];

    public static void RunAll(string excelDir, string outputDir)
    {
        foreach (var spec in DefaultSpecs)
            Convert(Path.Combine(excelDir, spec.FileName),
                    Path.Combine(outputDir, spec.OutputName),
                    spec);
    }

    public static void Convert(string excelPath, string outputPath, TableSpec spec)
    {
        using var wb     = new XLWorkbook(excelPath);
        var ws           = wb.Worksheet(1);
        var usedRows     = ws.RowsUsed().ToList();

        if (usedRows.Count < 2)
        {
            Console.WriteLine($"[{spec.Label}] 데이터 없음 — {excelPath}");
            return;
        }

        var headerRow = usedRows[0];
        int colCount  = headerRow.LastCellUsed()!.Address.ColumnNumber;
        var headers   = Enumerable.Range(1, colCount)
                                  .Select(i => headerRow.Cell(i).GetString().Trim())
                                  .ToArray();
        var dataRows  = usedRows.Skip(1);

        string json;
        int    count;

        switch (spec.Format)
        {
            case OutputFormat.DictByFirstColumn:
                var dict = BuildDict(dataRows, headers);
                count = dict.Count;
                json  = JsonSerializer.Serialize(dict, JsonOpts);
                break;

            case OutputFormat.Array:
                var arr = BuildArray(dataRows, headers);
                count = arr.Count;
                json  = JsonSerializer.Serialize(arr, JsonOpts);
                break;

            case OutputFormat.KeyValueDict:
                var kv = BuildKeyValue(dataRows);
                count = kv.Count;
                json  = JsonSerializer.Serialize(kv, JsonOpts);
                break;

            default:
                throw new ArgumentException($"알 수 없는 OutputFormat: {spec.Format}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"[{spec.Label}] {count}건 → {outputPath}");
    }

    // ── 출력 형식별 빌더 ─────────────────────────────────────────────────────

    private static Dictionary<string, Dictionary<string, object?>> BuildDict(
        IEnumerable<IXLRow> rows, string[] headers)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var key = row.Cell(1).GetString().Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;

            var obj = new Dictionary<string, object?>();
            for (int i = 1; i < headers.Length; i++)          // i=0(키 열) 제외
                obj[ToCamelCase(headers[i])] = ReadCell(row.Cell(i + 1));

            result[key] = obj;
        }
        return result;
    }

    private static List<Dictionary<string, object?>> BuildArray(
        IEnumerable<IXLRow> rows, string[] headers)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            if (row.IsEmpty()) continue;
            var obj = new Dictionary<string, object?>();
            for (int i = 0; i < headers.Length; i++)
                obj[ToCamelCase(headers[i])] = ReadCell(row.Cell(i + 1));
            result.Add(obj);
        }
        return result;
    }

    private static Dictionary<string, object?> BuildKeyValue(IEnumerable<IXLRow> rows)
    {
        var result = new Dictionary<string, object?>();
        foreach (var row in rows)
        {
            var key = row.Cell(1).GetString().Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key)) continue;
            result[key] = ReadCell(row.Cell(2));
        }
        return result;
    }

    // ── 셀 값 읽기 ───────────────────────────────────────────────────────────

    private static object? ReadCell(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        return cell.DataType switch
        {
            XLDataType.Number  => IsWholeNumber(cell.GetDouble())
                                  ? (object)(long)cell.GetDouble()
                                  : cell.GetDouble(),
            XLDataType.Boolean => cell.GetBoolean(),
            _                  => cell.GetString(),
        };
    }

    private static bool IsWholeNumber(double d)
        => !double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Floor(d);

    private static string ToCamelCase(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
