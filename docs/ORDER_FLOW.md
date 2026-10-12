# 公共订单流与免费数据来源

V0.2.4 同时采集 OKX BTC-USDT 现货 / BTC-USDT-SWAP 永续与 Binance BTCUSDT 现货 / USDT 永续。无需账户密钥，只有所列交易对，不代表全市场或 CoinGlass 全量覆盖。

## 图表操作

V0.3.0 默认四个指标窗格：OKX BTC 永续单市场 CVD、聚合现货 CVD、聚合合约 CVD、OI。共用时间轴和光标，各自显示单位、OHLC 与最新值。可切换指标 K 线、曲线或旧版价格视图，Delta 柱单独开关。交易所、共同起点汇总 / 各采集段、最近 1h / 4h / 24h、BTC / USDT、1m / 5m / 15m 控制继续保留。第一栏只使用本机实际采集的 OKX 永续成交，不用提供方聚合曲线替代。

单市场的新采集记录保留每分钟实际观测的 CVD 开高低收。多市场分钟历史只有主动买卖量，因此 5m / 15m 指标 K 线的高低取已知分钟累计路径，属于分钟采样近似，不能还原分钟内部极值；1m 缺少真实 OHLC 时显示曲线。不能把不同交易所的 High / Low 直接相加冒充汇总极值。OI K 线也是实际采样值的 OHLC，缺失起点、终点或中间样本时不生成完整柱。

共同汇总使用 `Closed-Common-Minute-CVD/1.0`。每个交易所先按分钟保存真实主动买卖量，再对所选交易所共同覆盖的已收盘分钟求和，累计 Delta。首次接入的部分分钟排除，当前分钟尚未计入；缺少任一来源的分钟不拿剩余来源替代。断线或来源采集段变化后从零另起一段。窗口选择只决定所请求显示范围，实际起点取已有共同观测；选择 24h 不会补造停机期间历史。

BTC 数量：OKX 现货原始 `sz`；OKX 永续 `sz × ctVal`；Binance BTCUSDT 的 `q` 已是 BTC 数量。USDT 是每笔真实 `BTC 数量 × 成交价`，不拿最新价转换旧 CVD。旧记录没有 USDT 证据时留空。OI 当前仍只采集 OKX，保存张数、BTC 和 USD，图表为 BTC。

Binance `m=true` 表示买方为 maker，因此主动方为卖；否则为主动买。`f/l` 保留聚合撮合范围，数量 `q` 不重复乘撮合数；`a` 检查聚合消息连续性。原始消息和交易所名一起保留。交易所、市场和成交 ID 联合去重；原 OKX 记录的 ID 格式不变。

每个连接、市场和交易所独立分段。某个连接中断不会关闭其他连接；超过 30 秒未观测成交、乱序或数据冲突重新分段。公共接口也可能中断或受网络影响，界面显示各来源状态。每次批量事务保存后才发布快照；最近最多 10,000 个 CVD 点与 3,000 个 OI 样本进入界面，最多 100 个原始采集段，列表显示最近 12 段；历史采集继续保存在本地数据库。

如本机需要已有的网络代理，可设置 `Binance__ProxyUrl` 环境变量或 `--Binance:ProxyUrl` 启动参数；没有配置时直接连接。不要把私人代理凭据写入 Git 仓库。

## 免费 API 的选择

- **Binance / OKX 公共成交**：当前接入路径，无需账户或付费 Key，应用自行计算与汇总 CVD。Binance 永续 `aggTrade` 使用新的 `/market` 路由。
- **Coinalyze（V0.2.5 起可选接入）**：官方 API 免费，需要注册领取 Key，在本地「数据连接」填写。查询现货/合约市场、OHLCV 与主动买入量、历史 OI；仅对 `has_buy_sell_data=true` 的市场计算 `Delta = 2 × bv − v`。支持 BTC/USD、USDT、USDC 现货，以及原生量为 BTC 的稳定币保证金永续；不混入币本位、交割或未知单位。限制为每分钟 40 个调用额度，批量每个 symbol 单独计数；日内保留约 1,500–2,000 点，日线历史不按此规则清理。本地真实连接已验证，Key 不随仓库分发；其他使用者需要自行配置。

Coinalyze 默认选择 Binance / Coinbase / Bybit / Kraken / Bitfinex 现货，以及 Binance / OKX / Bybit 线性永续中的可用市场。真实联调时这八个市场均有完整 24h 分钟历史，其他部分市场为空或稀疏；这不保证未来每分钟都完整。通过官方元数据显示名称，Coinbase / Kraken / Bitfinex 现货优先 USD，其他现货优先 USDT。其他支持市场仍可手动选择；元数据支持不保证接口实际返回历史，完全没有数据的来源会提示，程序不会自动移除已选市场。每次刷新最多 28 个额度，现货每个 1、永续每个 2（成交量 + OI）；服务端滑动分钟限额 35，按 `Retry-After` 延后请求。每 90 秒查询最近 24h 的 1m 数据，只请求已收盘时间。默认覆盖列表是支持列表的子集，不能称作全市场。BTC 原生量直接汇总，OI 使用 `convert_to_usd=false`；不以收盘价估算历史美元主动量。

所选现货与所选永续各自要求所有市场完整覆盖该分钟，缺少一个则该分钟不显示；CVD 缺口后从零开始。OI 只在所有所选永续都返回同一收盘分钟时相加。配置变更清除当前计算视图，不跨市场组合拼接基线。快照保存提供方原始分钟数据、市场元数据、选择和获取时间，不含 Key。断线保留旧历史；已配置却不可用时不自动用公共采集代替 Coinalyze 确认。
- **CoinGlass**：仅参考操作与指标分类；未抓取网页数据或接入付费服务。官方汇总 CVD 接口需要有权限的 Key。

来源：[OKX 官方 API](https://app.okx.com/docs-v5/en/)、[Binance 现货公共流](https://developers.binance.com/docs/binance-spot-api-docs/web-socket-streams)、[Binance 永续公共流与新路由](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Connect)、[Coinalyze 免费 API 文档](https://api.coinalyze.net/v1/doc/)、[CoinGlass 汇总 CVD](https://docs.coinglass.com/reference/futures-aggregated-cvd-history)。
