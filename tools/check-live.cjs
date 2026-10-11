const { chromium } = require('playwright');
const assert = require('node:assert/strict');
(async()=>{
  const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
  const page=await browser.newPage({viewport:{width:1440,height:1000}});
  const errors=[]; page.on('pageerror',e=>errors.push(e.message));
  const url=process.env.BTC_LENS_URL || 'http://127.0.0.1:5081';
  try {
    await page.goto(url);
    await page.waitForSelector('.chart-host canvas');
    let state;
    for(let i=0;i<50;i++){
      const response=await page.request.get(url+'/healthz'); state=await response.json();
      if(state.mode==='live' && state.instrument==='BTC-USDT-SWAP' && state.candleCounts?.['5m']>=200 && state.quoteTimestamp)break;
      await page.waitForTimeout(500);
    }
    assert.equal(state.mode,'live'); assert.equal(state.instrument,'BTC-USDT-SWAP');
    assert.ok(state.candleCounts['5m']>=200); assert.ok(state.quoteTimestamp);
    assert.ok(Date.now()-state.quoteTimestamp<30000);
    assert.deepEqual(errors,[]);
    await page.screenshot({path:'test-results/okx-live.png',fullPage:true});
    console.log(JSON.stringify({verified:true,...state,consoleErrors:errors},null,2));
  } finally {
    await browser.close();
  }
})().catch(e=>{console.error(e);process.exitCode=1;});
