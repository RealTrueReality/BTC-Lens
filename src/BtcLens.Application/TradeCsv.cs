using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BtcLens.Core;
using Microsoft.VisualBasic.FileIO;

namespace BtcLens.Application;

public static partial class TradeCsv
{
    public const string Header = "external_id,side,entry_time,exit_time,entry_price,exit_price,quantity_btc,initial_stop,target,fees,funding,reason,review,tags";
    public static string Template => Header + "\nexample-001,Long,2026-10-01T09:00:00+08:00,2026-10-01T10:00:00+08:00,67000,67300,0.01,66900,67300,0.6,0,manual example,review example,planned\n";
    public static ImportPreview Preview(string content, string mode)
    {
        if (content.Length > 1_000_000) return new([], ["CSV 最大 1 MB。"]);
        var trades = new List<Trade>(); var errors = new List<string>(); var ids = new HashSet<string>();
        using var reader = new StringReader(content.TrimStart('\uFEFF'));
        using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        try
        {
            var header = parser.ReadFields();
            if (header is null || !header.SequenceEqual(Header.Split(','))) return new([], ["列名或顺序不匹配，请下载模板。"]);
            var row = 1;
            while (!parser.EndOfData)
            {
                row++;
                if (row > 1001) { errors.Add("一次最多导入 1000 条记录。"); break; }
                try
                {
                    var f = parser.ReadFields()!;
                    if (f.Length != 14) throw new ArgumentException("需要 14 列。");
                    var externalId = f[0].Trim();
                    if (externalId.Length is < 1 or > 100 || !ids.Add(externalId)) throw new ArgumentException("external_id 缺失、过长或在文件内重复。");
                    if (f[1] is not ("Long" or "Short")) throw new ArgumentException("side 必须为 Long 或 Short。");
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mode + ":csv:" + externalId))).ToLowerInvariant();
                    var t = new Trade { Id = "csv-" + key, ImportKey = externalId, Mode = mode,
                        Side = Enum.Parse<TradeSide>(f[1]), EntryTime = Date(f[2])!.Value, ExitTime = Date(f[3]),
                        EntryPrice = Number(f[4])!.Value, ExitPrice = Number(f[5]), QuantityBtc = Number(f[6])!.Value,
                        InitialStop = Number(f[7]), Target = Number(f[8]), Fees = Number(f[9]), Funding = Number(f[10]),
                        Reason = f[11], Review = f[12], Tags = f[13] };
                    TradeMath.Validate(t); trades.Add(t);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or MalformedLineException)
                { errors.Add($"第 {row} 行：{ex.Message}"); }
            }
        }
        catch (MalformedLineException ex) { errors.Add($"CSV 格式错误：{ex.Message}"); }
        return new(trades.ToArray(), errors.ToArray());
    }
    private static decimal? Number(string s) => string.IsNullOrWhiteSpace(s) ? null : decimal.Parse(s.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    private static DateTimeOffset? Date(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!ExplicitOffset().IsMatch(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new ArgumentException("时间必须为带 Z 或 +08:00 等时区的 ISO 8601 格式。");
        return d;
    }
    [GeneratedRegex(@"T.+(?:Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ExplicitOffset();
}
