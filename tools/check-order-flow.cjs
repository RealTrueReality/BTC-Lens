const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const base = process.env.BTC_LENS_URL || 'http://127.0.0.1:5081';
(async () => {
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width:1440, height:1000 }, locale:'zh-CN', timezoneId:'Asia/Shanghai' });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  const health = async () => (await page.request.get(base + '/healthz')).json();
  async function ready() {
    const deadline = Date.now() + 90_000;
    while (Date.now() < deadline) {
      const state = await health();
      if (state.mode === 'live' && state.orderFlow?.instruments?.length === 2 &&
          state.orderFlow.instruments.every(s => s.messages > 2 && !s.endedAt) && state.orderFlow.oiSamples > 0) return state;
      await page.waitForTimeout(500);
    }
    throw new Error('Order flow not ready: ' + JSON.stringify(await health()));
  }
  try {
    await page.goto(base); await page.waitForSelector('.flow-chart canvas');
    await page.getByRole('button', { name:'实时 OKX', exact:true }).click();
    const first = await ready();
    await page.waitForTimeout(3500);
    const next = await ready();
    assert.ok(next.orderFlow.instruments.every(s => s.messages >= first.orderFlow.instruments.find(p => p.instrument === s.instrument).messages));
    assert.ok(next.orderFlow.instruments.every(s => s.buyBtc >= 0 && s.sellBtc >= 0 && Math.abs(s.cvdBtc - (s.buyBtc - s.sellBtc)) < 1e-8));
    assert.ok(Date.now() - next.orderFlow.latestOi.timestamp < 30_000);
    assert.equal(next.orderFlow.latestOi.instrument, 'BTC-USDT-SWAP');
    const before = next.orderFlow.instruments.map(s => s.id);
    assert.ok(Number(await page.locator('.flow-chart').getAttribute('data-cvd-points')) > 0);
    await page.getByLabel('订单流显示周期').selectOption('5');
    await page.waitForTimeout(250);
    const records = page.locator('.flow-records');
    await records.locator('summary').click();
    const downloadEvent = page.waitForEvent('download');
    await records.getByRole('button', { name:/导出此段/ }).first().click();
    const download = await downloadEvent;
    const packet = JSON.parse(await fs.readFile(await download.path(), 'utf8'));
    assert.ok(packet.exportedMessages > 0 && packet.exportedMessages <= 500);
    assert.equal(packet.segment.algorithm, 'Observed-Taker-CVD/1.0');
    assert.equal(packet.quantityUnit, 'BTC');
    assert.ok(packet.trades.every(t => t.segmentId === packet.segment.id));
    assert.ok(packet.exportedMessages <= packet.segment.messages);
    assert.ok(packet.trades.every(t => t.trade.timestamp <= packet.segment.lastTradeAt));
    assert.equal(packet.completeDetail,packet.exportedMessages === packet.segment.messages);
    await page.getByRole('button',{ name:/生成报告/ }).click();
    const report = page.getByRole('dialog',{ name:'观察报告' });
    assert.match(await report.innerText(), /观测成交 CVD/);
    assert.match(await report.innerText(), /OI 本身不确定多空方向/);
    await page.getByRole('button',{ name:'关闭报告', exact:true }).click();
    await page.locator('[data-testid="order-flow"]').screenshot({ path:'test-results/order-flow-live.png' });
    await page.reload(); await page.waitForSelector('.flow-chart canvas');
    assert.deepEqual((await ready()).orderFlow.instruments.map(s => s.id).sort(), before.sort());
    await page.getByRole('button',{ name:'演示', exact:true }).click(); await page.waitForTimeout(1500);
    assert.match(await page.locator('[data-testid="order-flow"]').innerText(), /独立合成/);
    await page.getByRole('button',{ name:'实时 OKX', exact:true }).click();
    const resumed = await ready();
    assert.ok(resumed.orderFlow.instruments.every(s => !before.includes(s.id)));
    await page.locator('.flow-records summary').click();
    assert.match(await page.locator('.flow-records').innerText(), /未回补/);
    await page.setViewportSize({width:390,height:844}); await page.waitForTimeout(500);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    assert.deepEqual(errors, []);
    await fs.writeFile('test-results/order-flow-live-state.json', JSON.stringify(resumed, null, 2));
    console.log(JSON.stringify({ verified:true, checks:['spot/perp actual taker trades','native OI units','freshness','CVD arithmetic','JSON capture export','observation report','refresh persistence','mode gap and new segments','mobile layout'], orderFlow:resumed.orderFlow, consoleErrors:errors }, null, 2));
  } finally {
    if (process.env.KEEP_LIVE !== '1') await page.getByRole('button', {name:'演示',exact:true}).click().catch(()=>{});
    await browser.close();
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
