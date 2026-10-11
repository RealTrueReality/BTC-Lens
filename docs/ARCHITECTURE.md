# 架构与数据模型

~~~mermaid
flowchart LR
    A[OKX REST / WebSocket] --> B[MarketEngine]
    D[仅测试主机 / 确定性数据] -.-> B
    B --> C[指标与关注规则]
    C --> S[SqlSugar / SQLite]
    B --> W[Blazor / 图表]
    W --> S
    S --> J[交易与复盘 / 统计]
    B --> R[结构化规则报告]
    A --> F[OrderFlowEngine / 主动成交与 OI]
    F --> S
    F --> W
    F --> R
~~~

## 运行方式

单进程、单用户。生产实例始终从 live scope 启动，不播种示例交易和关注区；旧示例数据保留在旧 scope。BackgroundService 管理行情订阅、重试与 REST 兜底；MarketEngine 串行处理行情、绘图、关注区与成交量选区变更。页面通过事件订阅刷新，各页面和图表在销毁时取消订阅与释放 JavaScript 对象。

行情样本处理使用内存中的上次观测，避免每个 WebSocket 样本都写数据库。关注区变更、首次触发、交易与回放进度才需要持久化。触发事件与关注区的 Triggered 状态在同一数据库事务提交。

SQLite 连接禁用连接池，并自动关闭连接，保证本地文件可以在应用退出后释放。JSON 中使用 decimal 保留精确输入，JavaScript 的数值只负责图表显示。

`chart_drawings` 保存绘图类型、时间与价格端点、版本和删除标记。拖动保存校验 scope 与预期版本；矩形/水平线转关注时，在同一事务写入关注区并删除源绘图，保留 `ChartAnchor` 来源版本和端点。成交量选区继续冻结计算证据，只允许单独修改 Visible / DeletedAt；删除不清除历史事件或交易引用。

确定性回放仅用于显式 Testing + OfflineFixture 主机；测试端点不在生产环境注册，界面没有模式切换。

订单流后台任务与价格任务分别订阅公共 WebSocket。OrderFlowEngine 校验主动成交、换算 BTC 数量，按成交 ID 去重。原始消息、分钟聚合与段检查点每秒在一个事务提交，再发布给页面；OI 每 10 秒采样。网络中断、模式暂停、进程停止或校验失败时结束段，恢复时另起零基线。异常退出留下的开放段在启动时关闭，不跨越停机时间续算。

## 表

| 表 | 键及查询列 | 领域数据 |
|---|---|---|
| settings | Key | 数据模式、回放会话和游标 |
| watch_zones | Id、Scope | 当前版本、历史版本、启用与触发状态 |
| alert_events | 唯一事件 Id、Scope、Timestamp | 原关注版本、价格、来源、证据 JSON |
| trades | Id、Mode、Timestamp | 交易、初始风险、成本、复盘、图片、关联事件 |
| candle_cache | Scope + Instrument + Timeframe + OpenTime | OHLCV、确认状态 |
| flow_segments | Id、Timestamp=连接时间 | 首笔与最后成交时间、累计主动买卖 BTC、计数、合约元数据、开始/结束原因 |
| captured_trades | Instrument + TradeId、SegmentId、Timestamp | 聚合原始消息、接收时间、BTC 换算结果 |
| cvd_points | SegmentId + Minute、Timestamp=最后成交时间 | 段内累计 CVD、该分钟主动买卖量及消息数 |
| open_interest | Instrument + Timestamp | 张数、BTC、USD、接收时间与采样来源 |

Scope=live 用于真实行情关注区；每个演示回放使用独立 demo:会话 ID。交易按 live/demo 分开；新回放不会删除之前保存的事件或演示交易。通过交易关联 ID 可以继续找到旧事件。

## 关键状态

- FVG：Active → Touched → Filled。
- 参考 OB：Active → Touched → Invalidated。
- 已形成区域的后续数据断档：Unknown。
- 关注区：Enabled 与 Triggered 分别保存；暂停不改写历史；编辑建立新版本并重新启用。
- 关注区删除使用 DeletedAt 标记，查询与监控排除已删除对象；按 Scope 与版本校验删除，拒绝滞后编辑或提醒重新写入。演示关注区按会话只初始化一次，删除最后一个后重启也不补建；旧事件及交易快照不改写。
- 首次到区事件键：Scope + WatchId + Revision + RuleVersion。
- CSV 交易键：SHA-256(Mode + external_id)，不同模式分别去重。

## 快照

到区快照包含该截止点前的已确认 K 线、产品参数、关键价格、来源区域、规则版本和数据完整性状态。之后编辑关注区或推进回放不会改变已保存快照。

V0.2 同时保存已提交的订单流快照与段 ID。报告只引用新鲜实盘数据；完整目标资格与个人确认规则仍单独审核。

V0.2.1 使用独立 `PriceContextWorker` 补充当前周/月起点的已收盘 5m 历史，保存在 `candle_cache` 的 `live:analysis` scope；行情与订单流任务继续各自运行。`PriceContextRules` 执行固定范围近似分配、当前日/周/月 HLC3 VWAP 与覆盖检查。更换可视图表周期不更换计算源。

`volume_profiles` 保存范围、版本、合约元数据、结果与原始 K 线的冻结证据。关注区引用当时的位置；报告、到区事件及交易记录保存汇总快照。SQLite 增量建表兼容已有交易与事件。历史完整性与策略确认资格分别处理，未来数值及事后选区不能进入历史截止点的报告。

成交导出从同一个持久化检查点读取段汇总与成交明细，不使用可能落后一批的页面汇总标注较新的明细。

手动交易保存「记录时快照」。其捕获时间与交易时间分别存在；补录过去的交易时，不把当前快照冒充历史开仓证据。CSV 导入不补造历史快照。

## 边界

缓存服务于存储与审查；实时启动时重新获取行情，不把缓存视为新鲜报价。首版不使用私有账户 API、自动交易、外部通知、Redis 或消息队列。多用户部署、分批成交与数据库切换是独立扩展任务。
