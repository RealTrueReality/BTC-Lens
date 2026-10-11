using BtcLens.Application;
using BtcLens.Core;
using SqlSugar;

namespace BtcLens.Infrastructure;

public sealed class LensStore : ILensStore, IDisposable
{
    private readonly SqlSugarClient db;
    private readonly object sync = new();
    public LensStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new(new ConnectionConfig { ConnectionString = $"Data Source={path};Pooling=False", DbType = DbType.Sqlite,
            IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute });
        db.CodeFirst.InitTables<SettingRow, WatchRow, AlertRow, TradeRow, CandleRow>();
        db.CodeFirst.InitTables<FlowSegmentRow, CvdRow, CapturedTradeRow, OpenInterestRow>();
        db.CodeFirst.InitTables<ProfileRowEntity>();
        db.CodeFirst.InitTables<DrawingRowEntity>();
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_drawing_scope ON chart_drawings(Scope,Timestamp)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_profile_scope ON volume_profiles(Scope,Timestamp)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_candle_scope ON candle_cache(Scope)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_watch_scope ON watch_zones(Scope)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_alert_scope ON alert_events(Scope)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_trade_mode ON trades(Mode)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_flow_time ON flow_segments(Timestamp)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_cvd_time ON cvd_points(Timestamp)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_capture_segment ON captured_trades(SegmentId,Timestamp)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_oi_time ON open_interest(Timestamp)");
    }
    public string? GetSetting(string key) { lock (sync) return db.Queryable<SettingRow>().InSingle(key)?.Value; }
    public void SetSetting(string key, string value) { lock (sync) db.Storageable(new SettingRow { Key = key, Value = value }).ExecuteCommand(); }
    public WatchZone[] Watches(string scope, bool includeDeleted = false) { lock (sync) return db.Queryable<WatchRow>().Where(x => x.Scope == scope).ToList().Select(x => JsonCodec.Read<WatchZone>(x.Json)).Where(x => includeDeleted || x.DeletedAt is null).OrderByDescending(x => x.Current.CreatedAt).ToArray(); }
    public void SaveWatch(WatchZone zone, int? promotedDrawingRevision = null)
    {
        lock (sync)
        {
            if (db.Queryable<WatchRow>().InSingle(zone.Id) is { } row && JsonCodec.Read<WatchZone>(row.Json).DeletedAt is not null)
                throw new ArgumentException("关注区已删除，请重新建立关注区。");
            if (promotedDrawingRevision is null) { db.Storageable(Watch(zone)).ExecuteCommand(); return; }
            var sourceId = zone.Current.ChartAnchor?.DrawingId;
            var source = sourceId is null ? null : db.Queryable<DrawingRowEntity>().InSingle(sourceId);
            var drawing = source is null ? null : JsonCodec.Read<ChartDrawing>(source.Json);
            if (drawing is null || drawing.Scope != zone.Scope || drawing.DeletedAt is not null || drawing.Revision != promotedDrawingRevision)
                throw new ArgumentException("原绘图已变化或已删除，请重新选择后加入关注。");
            db.Ado.BeginTran();
            try
            {
                db.Storageable(Watch(zone)).ExecuteCommand();
                source!.Json = JsonCodec.Write(drawing with { DeletedAt = zone.Current.CreatedAt.ToUnixTimeMilliseconds() });
                db.Updateable(source).ExecuteCommand(); db.Ado.CommitTran();
            }
            catch { db.Ado.RollbackTran(); throw; }
        }
    }
    public bool DeleteWatch(string scope, string id, int expectedRevision, DateTimeOffset deletedAt)
    {
        lock (sync)
        {
            var row = db.Queryable<WatchRow>().InSingle(id);
            if (row is null || row.Scope != scope) return false;
            var zone = JsonCodec.Read<WatchZone>(row.Json);
            if (zone.DeletedAt is not null || zone.Current.Revision != expectedRevision) return false;
            db.Updateable(Watch(zone with { Enabled = false, LastSample = null, DeletedAt = deletedAt })).ExecuteCommand();
            return true;
        }
    }
    public AlertEvent[] Alerts(string scope) { lock (sync) return db.Queryable<AlertRow>().Where(x => x.Scope == scope).OrderByDescending(x => x.Timestamp).Take(200).ToList().Select(x => JsonCodec.Read<AlertEvent>(x.Json)).ToArray(); }
    public AlertEvent? FindAlert(string id) { lock (sync) { var row = db.Queryable<AlertRow>().InSingle(id); return row is null ? null : JsonCodec.Read<AlertEvent>(row.Json); } }
    public bool RecordAlert(WatchZone updated, AlertEvent alert)
    {
        lock (sync)
        {
            db.Ado.BeginTran();
            try
            {
                if (db.Queryable<AlertRow>().InSingle(alert.Id) is not null ||
                    db.Queryable<WatchRow>().InSingle(updated.Id) is { } row && JsonCodec.Read<WatchZone>(row.Json).DeletedAt is not null)
                { db.Ado.CommitTran(); return false; }
                db.Insertable(new AlertRow { Id = alert.Id, Scope = alert.Scope, Timestamp = alert.CreatedAt.ToUnixTimeMilliseconds(), Json = JsonCodec.Write(alert) }).ExecuteCommand();
                db.Storageable(Watch(updated)).ExecuteCommand();
                db.Ado.CommitTran(); return true;
            }
            catch { db.Ado.RollbackTran(); throw; }
        }
    }
    public Trade[] Trades(string mode) { lock (sync) return db.Queryable<TradeRow>().Where(x => x.Mode == mode).OrderByDescending(x => x.Timestamp).ToList().Select(x => JsonCodec.Read<Trade>(x.Json)).ToArray(); }
    public Trade? FindTrade(string id) { lock (sync) { var row = db.Queryable<TradeRow>().InSingle(id); return row is null ? null : JsonCodec.Read<Trade>(row.Json); } }
    public void SaveTrade(Trade trade)
    {
        TradeMath.Validate(trade);
        lock (sync)
        {
            var existing = FindTrade(trade.Id);
            if (existing is not null && (existing.InitialStop != trade.InitialStop || existing.EntryPrice != trade.EntryPrice || existing.QuantityBtc != trade.QuantityBtc || existing.Side != trade.Side || existing.EntryTime != trade.EntryTime || existing.Mode != trade.Mode || existing.EntrySnapshotJson != trade.EntrySnapshotJson || existing.AlertId != trade.AlertId))
                throw new ArgumentException("已保存的入场和初始风险保持原样；可以补充平仓、费用和复盘。");
            db.Storageable(TradeRowOf(trade)).ExecuteCommand();
        }
    }
    public int ImportTrades(IReadOnlyList<Trade> trades)
    {
        foreach (var t in trades) TradeMath.Validate(t);
        if (trades.Select(t => t.Id).Distinct().Count() != trades.Count) throw new ArgumentException("导入记录 ID 重复。");
        lock (sync)
        {
            db.Ado.BeginTran();
            try
            {
                var inserted = 0;
                foreach (var t in trades)
                {
                    var old = db.Queryable<TradeRow>().InSingle(t.Id);
                    if (old is not null)
                    {
                        if (old.Json != JsonCodec.Write(t)) throw new ArgumentException($"external_id「{t.ImportKey}」已经存在且内容不同。请通过交易编辑更新。");
                        continue;
                    }
                    db.Insertable(TradeRowOf(t)).ExecuteCommand(); inserted++;
                }
                db.Ado.CommitTran(); return inserted;
            }
            catch { db.Ado.RollbackTran(); throw; }
        }
    }
    public void SaveCandles(string scope, IEnumerable<Candle> candles)
    {
        var rows = candles.Select(c => new CandleRow { Id = $"{scope}:{c.Instrument}:{c.Timeframe}:{c.OpenTime}", Scope = scope, Json = JsonCodec.Write(c) }).ToArray();
        if (rows.Length == 0) return;
        lock (sync) db.Storageable(rows).ExecuteCommand();
    }
    private static WatchRow Watch(WatchZone w) => new() { Id = w.Id, Scope = w.Scope, Json = JsonCodec.Write(w) };
    public Candle[] ReadCandles(string scope)
    { lock (sync) return db.Queryable<CandleRow>().Where(x => x.Scope == scope).ToList().Select(x => JsonCodec.Read<Candle>(x.Json)).OrderBy(x => x.OpenTime).ToArray(); }
    public SavedProfile[] Profiles(string scope)
    { lock (sync) return db.Queryable<ProfileRowEntity>().Where(x => x.Scope == scope).OrderByDescending(x => x.Timestamp).ToList().Select(x => JsonCodec.Read<SavedProfile>(x.Json)).Where(p => p.DeletedAt is null).ToArray(); }
    public void SaveProfile(SavedProfile profile)
    {
        lock (sync)
        {
            var existing = db.Queryable<ProfileRowEntity>().InSingle(profile.Id);
            if (existing is not null)
            {
                if (existing.Json != JsonCodec.Write(profile)) throw new ArgumentException("已保存的固定范围计算保持原样；调整范围请新建选区。");
                return;
            }
            db.Insertable(new ProfileRowEntity { Id = profile.Id, Scope = profile.Scope, Timestamp = profile.CreatedAt, Json = JsonCodec.Write(profile) }).ExecuteCommand();
        }
    }
    public bool SetProfileState(string scope, string id, bool visible, long? deletedAt)
    {
        lock (sync)
        {
            var row = db.Queryable<ProfileRowEntity>().InSingle(id);
            if (row is null || row.Scope != scope) return false;
            var profile = JsonCodec.Read<SavedProfile>(row.Json);
            if (profile.DeletedAt is not null) return false;
            row.Json = JsonCodec.Write(profile with { Visible = visible, DeletedAt = deletedAt });
            db.Updateable(row).ExecuteCommand(); return true;
        }
    }
    public ChartDrawing[] Drawings(string scope)
    { lock (sync) return db.Queryable<DrawingRowEntity>().Where(x => x.Scope == scope).OrderByDescending(x => x.Timestamp).ToList().Select(x => JsonCodec.Read<ChartDrawing>(x.Json)).Where(d => d.DeletedAt is null).ToArray(); }
    public void SaveDrawing(ChartDrawing drawing, int? expectedRevision)
    {
        DrawingRules.Validate(drawing);
        lock (sync)
        {
            var row = db.Queryable<DrawingRowEntity>().InSingle(drawing.Id);
            var old = row is null ? null : JsonCodec.Read<ChartDrawing>(row.Json);
            if (old is null ? expectedRevision is not null || drawing.Revision != 1
                : old.DeletedAt is not null || old.Scope != drawing.Scope || old.Revision != expectedRevision || drawing.Revision != old.Revision + 1 || old.Kind != drawing.Kind || old.CreatedAt != drawing.CreatedAt)
                throw new ArgumentException("绘图已变化或已删除，请重新选择。");
            db.Storageable(new DrawingRowEntity { Id = drawing.Id, Scope = drawing.Scope, Timestamp = drawing.CreatedAt, Json = JsonCodec.Write(drawing) }).ExecuteCommand();
        }
    }
    public bool DeleteDrawing(string scope, string id, int expectedRevision, long deletedAt)
    {
        lock (sync)
        {
            var row = db.Queryable<DrawingRowEntity>().InSingle(id);
            if (row is null || row.Scope != scope) return false;
            var drawing = JsonCodec.Read<ChartDrawing>(row.Json);
            if (drawing.DeletedAt is not null || drawing.Revision != expectedRevision) return false;
            row.Json = JsonCodec.Write(drawing with { DeletedAt = deletedAt }); db.Updateable(row).ExecuteCommand(); return true;
        }
    }
    public FlowSegment[] FlowSegments(int limit = 40)
    { lock (sync) return db.Queryable<FlowSegmentRow>().OrderByDescending(x => x.Timestamp).Take(Math.Clamp(limit, 1, 100)).ToList().Select(x => JsonCodec.Read<FlowSegment>(x.Json)).ToArray(); }
    public CvdPoint[] CvdPoints(long since, int limit = 3000)
    { lock (sync) return db.Queryable<CvdRow>().Where(x => x.Timestamp >= since).OrderByDescending(x => x.Timestamp).Take(Math.Clamp(limit, 1, 10000)).ToList().Select(x => JsonCodec.Read<CvdPoint>(x.Json)).OrderBy(x => x.Timestamp).ToArray(); }
    public OpenInterestSample[] OpenInterest(long since, int limit = 3000)
    { lock (sync) return db.Queryable<OpenInterestRow>().Where(x => x.Timestamp >= since).OrderByDescending(x => x.Timestamp).Take(Math.Clamp(limit, 1, 10000)).ToList().Select(x => JsonCodec.Read<OpenInterestSample>(x.Json)).OrderBy(x => x.Timestamp).ToArray(); }
    public CapturedTrade? FindCapturedTrade(string id)
    { lock (sync) { var row = db.Queryable<CapturedTradeRow>().InSingle(id); return row is null ? null : JsonCodec.Read<CapturedTrade>(row.Json); } }
    public CapturedTrade[] CapturedTrades(string segmentId, int limit = 500)
    { lock (sync) return db.Queryable<CapturedTradeRow>().Where(x => x.SegmentId == segmentId).OrderByDescending(x => x.Timestamp).Take(Math.Clamp(limit, 1, 2000)).ToList().Select(x => JsonCodec.Read<CapturedTrade>(x.Json)).OrderBy(x => x.Trade.Timestamp).ThenBy(x => x.Trade.TradeId).ToArray(); }
    public FlowCapture? CaptureFlow(string segmentId, int limit = 500)
    {
        lock (sync)
        {
            // Read the persisted checkpoint and detail under the same store lock. A chart-render
            // snapshot may already be one batch behind; it must not label newer raw messages.
            var row = db.Queryable<FlowSegmentRow>().InSingle(segmentId);
            return row is null ? null : new(JsonCodec.Read<FlowSegment>(row.Json), CapturedTrades(segmentId, limit));
        }
    }
    public void SaveFlowBatch(FlowSegment[] segments, CvdPoint[] points, CapturedTrade[] trades)
    {
        lock (sync)
        {
            db.Ado.BeginTran();
            try
            {
                var ids = trades.Select(t => t.Id).ToArray();
                if (ids.Distinct().Count() != ids.Length) throw new ArgumentException("Duplicate captured trade in batch.");
                // A raw message is immutable; duplicate writes must not silently update its attribution.
                var existing = ids.Length == 0 ? [] : db.Queryable<CapturedTradeRow>().Where(x => ids.Contains(x.Id)).ToList();
                if (existing.Count > 0) throw new ArgumentException("Captured trade already exists; batch rolled back.");
                if (trades.Length > 0) db.Insertable(trades.Select(t => new CapturedTradeRow { Id = t.Id, SegmentId = t.SegmentId, Timestamp = t.Trade.Timestamp, Json = JsonCodec.Write(t) }).ToArray()).ExecuteCommand();
                if (points.Length > 0) db.Storageable(points.Select(p => new CvdRow { Id = p.Id, Timestamp = p.Timestamp, Json = JsonCodec.Write(p) }).ToArray()).ExecuteCommand();
                if (segments.Length > 0) db.Storageable(segments.Select(s => new FlowSegmentRow { Id = s.Id, Timestamp = s.ConnectedAt, Json = JsonCodec.Write(s) }).ToArray()).ExecuteCommand();
                db.Ado.CommitTran();
            }
            catch { db.Ado.RollbackTran(); throw; }
        }
    }
    public void SaveOpenInterest(OpenInterestSample sample)
    {
        if (!sample.Valid) throw new ArgumentException("Invalid OI sample.");
        lock (sync)
        {
            var row = db.Queryable<OpenInterestRow>().InSingle(sample.Id);
            if (row is not null) return;
            db.Insertable(new OpenInterestRow { Id = sample.Id, Timestamp = sample.Timestamp, Json = JsonCodec.Write(sample) }).ExecuteCommand();
        }
    }
    private static TradeRow TradeRowOf(Trade t) => new() { Id = t.Id, Mode = t.Mode, Timestamp = t.EntryTime.ToUnixTimeMilliseconds(), Json = JsonCodec.Write(t) };
    public void Dispose() { lock (sync) db.Dispose(); }
}

[SugarTable("settings")]
public sealed class SettingRow { [SugarColumn(IsPrimaryKey = true)] public string Key { get; set; } = ""; [SugarColumn(ColumnDataType = "text")] public string Value { get; set; } = ""; }
[SugarTable("watch_zones")]
public sealed class WatchRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public string Scope { get; set; } = ""; [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }
[SugarTable("alert_events")]
public sealed class AlertRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public string Scope { get; set; } = ""; public long Timestamp { get; set; } [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }
[SugarTable("trades")]
public sealed class TradeRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public string Mode { get; set; } = ""; public long Timestamp { get; set; } [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }
[SugarTable("candle_cache")]
public sealed class CandleRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public string Scope { get; set; } = ""; [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }

[SugarTable("flow_segments")]
public sealed class FlowSegmentRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public long Timestamp { get; set; } [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }
[SugarTable("cvd_points")]
public sealed class CvdRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public long Timestamp { get; set; } [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }
[SugarTable("captured_trades")]
public sealed class CapturedTradeRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public string SegmentId { get; set; } = ""; public long Timestamp { get; set; } [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }
[SugarTable("open_interest")]
public sealed class OpenInterestRow { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public long Timestamp { get; set; } [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }

[SugarTable("volume_profiles")]
public sealed class ProfileRowEntity { [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = ""; public string Scope { get; set; } = ""; public long Timestamp { get; set; } [SugarColumn(ColumnDataType = "text")] public string Json { get; set; } = ""; }
