const { chromium } = require('playwright');
const assert = require('node:assert/strict');
(async()=>{
  const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai'});
    const errors=[]; page.on('pageerror',e=>errors.push(e.message));
    const base=process.env.BTC_LENS_URL || 'http://127.0.0.1:5080';
    await page.goto(base+'/guide');
    await page.waitForFunction(()=>document.body.innerText.includes('V0.2.1'));
    assert.match(await page.locator('main').innerText(),/现货与永续 CVD/);
    await page.goto(base); await page.waitForSelector('.flow-chart canvas');
    let state;
    for(let i=0;i<120;i++) {
      state=await (await page.request.get(base+'/healthz')).json();
      if(state.mode==='live' && state.orderFlow?.instruments?.length===2 && state.orderFlow.instruments.every(s=>s.messages>0 && !s.endedAt) && state.orderFlow.oiSamples>0) break;
      await page.waitForTimeout(500);
    }
    assert.equal(state.mode,'live'); assert.equal(state.orderFlow.instruments.length,2);
    assert.ok(state.orderFlow.instruments.every(s=>s.messages>0 && !s.endedAt));
    assert.ok(state.orderFlow.oiSamples>0); assert.ok(state.candleCounts['5m']>=200);
    assert.ok(state.priceContext.candles>300);assert.equal(state.priceContext.vwaps.length,3);
    assert.ok(state.priceContext.vwaps.every(v=>v.coverage.complete));assert.ok(await page.getByTestId('price-context').count()===1);
    assert.deepEqual(errors,[]);
    await page.locator('[data-testid="order-flow"]').screenshot({path:'test-results/v02-published-order-flow.png'});
    console.log(JSON.stringify({verified:true,version:'V0.2.1',mode:state.mode,transport:state.transport,historyBars:state.priceContext.candles,completeVwaps:3,consoleErrors:errors},null,2));
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
