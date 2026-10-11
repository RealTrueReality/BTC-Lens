# 公共订单流与免费数据来源

V0.2.4 同时采集 OKX BTC-USDT 现货 / BTC-USDT-SWAP 永续与 Binance BTCUSDT 现货 / USDT 永续。无需账户密钥，只有所列交易对，不代表全市场或 CoinGlass 全量覆盖。

## 图表操作

订单流面板有交易所、共同起点汇总 / 各采集段、最近 1h / 4h / 24h、BTC / USDT、1m / 5m / 15m 采样控制。价格、现货 CVD + Delta、永续 CVD + Delta、OKX OI 共用时间轴和光标；各窗格内显示名称与单位。

共同汇总使用 `Closed-Common-Minute-CVD/1.0`。每个交易所先按分钟保存真实主动买卖量，再对所选交易所共同覆盖的已收盘分钟求和，累计 Delta。首次接入的部分分钟排除，当前分钟尚未计入；缺少任一来源的分钟不拿剩余来源替代。断线或来源采集段变化后从零另起一段。窗口选择只决定所请求显示范围，实际起点取已有共同观测；选择 24h 不会补造停机期间历史。

BTC 数量：OKX 现货原始 `sz`；OKX 永续 `sz × ctVal`；Binance BTCUSDT 的 `q` 已是 BTC 数量。USDT 是每笔真实 `BTC 数量 × 成交价`，不拿最新价转换旧 CVD。旧记录没有 USDT 证据时留空。OI 当前仍只采集 OKX，保存张数、BTC 和 USD，图表为 BTC。

Binance `m=true` 表示买方为 maker，因此主动方为卖；否则为主动买。`f/l` 保留聚合撮合范围，数量 `q` 不重复乘撮合数；`a` 检查聚合消息连续性。原始消息和交易所名一起保留。交易所、市场和成交 ID 联合去重；原 OKX 记录的 ID 格式不变。

每个连接、市场和交易所独立分段。某个连接中断不会关闭其他连接；超过 30 秒未观测成交、乱序或数据冲突重新分段。公共接口也可能中断或受网络影响，界面显示各来源状态。每次批量事务保存后才发布快照；最近最多 10,000 个 CVD 点与 3,000 个 OI 样本进入界面，最多 100 个原始采集段，列表显示最近 12 段；历史采集继续保存在本地数据库。

如本机需要已有的网络代理，可设置 `Binance__ProxyUrl` 环境变量或 `--Binance:ProxyUrl` 启动参数；没有配置时直接连接。不要把私人代理凭据写入 Git 仓库。

## 免费 API 的选择

- **Binance / OKX 公共成交**：当前接入路径，无需账户或付费 Key，应用自行计算与汇总 CVD。Binance 永续 `aggTrade` 使用新的 `/market` 路由。
- **Coinalyze**：官方 API 免费，需要注册领取 Key。可查询现货/合约市场、OHLCV 与买入量、OI 等；仅对 `has_buy_sell_data=true` 的市场计算 `Delta = 2 × bv − v`，统一各市场原生数量口径后才能汇总。限制为每分钟 40 次调用，批量每个 symbol 单独计次数；日内保留约 1,500–2,000 点，日线历史不按此规则清理。当前未接入，适合以后补充已有历史。
- **CoinGlass**：仅参考操作与指标分类；未抓取网页数据或接入付费服务。官方汇总 CVD 接口需要有权限的 Key。

来源：[OKX 官方 API](https://app.okx.com/docs-v5/en/)、[Binance 现货公共流](https://developers.binance.com/docs/binance-spot-api-docs/web-socket-streams)、[Binance 永续公共流与新路由](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Connect)、[Coinalyze 免费 API 文档](https://api.coinalyze.net/v1/doc/)、[CoinGlass 汇总 CVD](https://docs.coinglass.com/reference/futures-aggregated-cvd-history)。
