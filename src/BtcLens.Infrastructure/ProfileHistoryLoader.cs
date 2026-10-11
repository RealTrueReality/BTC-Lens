using BtcLens.Application;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

/// <summary>Loads only a requested closed 5m range; does not hold the live-market gate.</summary>
public sealed class ProfileHistoryLoader(ILensStore store, OkxClient okx)
{
    private readonly SemaphoreSlim loadGate = new(1, 1);
    public async Task<Candle[]> LoadAsync(long start, long end, long cutoff, IEnumerable<Candle> seed,
        IProgress<ProfileLoadProgress>? progress, CancellationToken ct)
    {
        var expected = checked((int)((end - start) / PriceContextRules.Step));
        progress?.Report(new(0, expected, 0, "正在检查本地缓存"));
        await loadGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var cached = store.ReadCandles("live:analysis", start, end);
            var bars = PriceContextRules.ClosedBars(cached.Concat(seed), start, end, cutoff).ToDictionary(c => c.OpenTime);
            var gaps = new List<(long Start, long End)>();
            var next = start;
            foreach (var time in bars.Keys.Order())
            {
                if (time > next) gaps.Add((next, time));
                next = time + PriceContextRules.Step;
            }
            if (next < end) gaps.Add((next, end));
            var pages = 0;
            progress?.Report(new(bars.Count, expected, pages, "已复用本地缓存，正在补取缺失 K 线"));
            foreach (var gap in gaps.AsEnumerable().Reverse())
            {
                var cursor = gap.End;
                while (cursor > gap.Start)
                {
                    while (cursor > gap.Start && bars.ContainsKey(cursor - PriceContextRules.Step)) cursor -= PriceContextRules.Step;
                    if (cursor <= gap.Start) break;
                    ct.ThrowIfCancellationRequested();
                    var incoming = await FetchAsync(cursor, ct); pages++;
                    if (incoming.Length == 0) break;
                    var older = incoming.Where(c => c.OpenTime < cursor).ToArray();
                    if (older.Length == 0) throw new InvalidDataException("历史分页没有向前推进，请重试。");
                    var groups = older.Where(c => c.OpenTime >= start && c.OpenTime < end && c.CloseTime <= cutoff && c.Confirmed)
                        .GroupBy(c => c.OpenTime).ToArray();
                    if (groups.Any(g => g.Distinct().Count() > 1 || bars.TryGetValue(g.Key, out var old) && g.Any(c => c != old)))
                        throw new InvalidDataException("历史 K 线存在冲突，已停止计算，请重试。");
                    var closed = PriceContextRules.ClosedBars(groups.Select(g => g.First()), start, end, cutoff);
                    ct.ThrowIfCancellationRequested();
                    store.SaveCandles("live:analysis", closed);
                    foreach (var c in closed) bars[c.OpenTime] = c;
                    progress?.Report(new(bars.Count, expected, pages, "正在补取历史 K 线"));
                    cursor = older.Min(c => c.OpenTime);
                }
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(bars.Count, expected, pages, bars.Count == expected ? "选区历史数据已补齐" : "历史补取结束，仍有缺失 K 线"));
            return bars.Values.OrderBy(c => c.OpenTime).ToArray();
        }
        finally { loadGate.Release(); }
    }
    private async Task<Candle[]> FetchAsync(long cursor, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await okx.HistoryCandlesAsync(cursor, ct); }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            { await Task.Delay(500 * (attempt + 1), ct); }
        }
    }
}
