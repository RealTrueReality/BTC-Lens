const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
(async () => {
  const prior = JSON.parse(await fs.readFile('test-results/order-flow-live-state.json','utf8'));
  const oldIds = prior.orderFlow.instruments.map(s => s.id);
  const base = process.env.BTC_LENS_URL || 'http://127.0.0.1:5081';
  const browser = await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page = await browser.newPage({viewport:{width:1440,height:1000}});
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.goto(base); await page.waitForSelector('.flow-chart canvas');
    let state;
    for(let i=0; i<120; i++) {
      state = await (await page.request.get(base+'/healthz')).json();
      if(state.mode==='live' && state.orderFlow?.instruments?.length===2 && state.orderFlow.instruments.every(s => s.messages>0 && !oldIds.includes(s.id) && !s.endedAt)) break;
      await page.waitForTimeout(500);
    }
    assert.equal(state.mode,'live');
    assert.ok(state.orderFlow.instruments.every(s => s.messages>0 && !oldIds.includes(s.id) && !s.endedAt));
    await page.locator('.flow-records summary').click();
    for(const id of oldIds) {
      const row = page.locator('.flow-record').filter({hasText:id});
      assert.equal(await row.count(),1); assert.match(await row.innerText(),/已结束/);
      const event = page.waitForEvent('download');
      await row.getByRole('button',{name:/导出此段/}).click();
      const download = await event;
      const packet = JSON.parse(await fs.readFile(await download.path(),'utf8'));
      assert.equal(packet.segment.id,id); assert.ok(packet.segment.endedAt); assert.ok(packet.exportedMessages>0);
    }
    assert.deepEqual(errors,[]);
    console.log(JSON.stringify({verified:true,checks:['saved live mode resumes after process restart','old raw messages survive restart','old segments are closed','new segments have separate zero anchors'],oldIds,newIds:state.orderFlow.instruments.map(s=>s.id),consoleErrors:errors},null,2));
    // Capture the documentation screenshot using explicitly synthetic data.
    await page.getByRole('button',{name:'演示',exact:true}).click();
    await page.waitForTimeout(500);
    await page.getByRole('button',{name:'新回放',exact:true}).click();
    await page.waitForTimeout(4500);
    await page.locator('[data-testid="order-flow"]').screenshot({path:'docs/screenshots/order-flow.png'});
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
