const {chromium}=require('playwright');
const assert=require('node:assert/strict');const fs=require('node:fs');
const base=process.env.BTC_LENS_URL||'http://127.0.0.1:5081';
(async()=>{
 const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||(process.platform==='win32'?'msedge':'chromium'),headless:true});
 const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai',acceptDownloads:true});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));fs.mkdirSync('test-results/price-context',{recursive:true});
 try {
  await page.goto(base);await page.getByRole('button',{name:'实时 OKX',exact:true}).click();
  let state;const started=Date.now();
  while(Date.now()-started<180000){
    state=await (await page.request.get(base+'/healthz')).json();const c=state.priceContext;
    if(c.candles>300&&c.vwaps.length===3&&c.vwaps.every(v=>v.value>0&&v.coverage.complete))break;
    console.log(JSON.stringify({loaded:c.candles,status:c.status,coverage:c.vwaps.map(v=>[v.period,v.coverage.observed,v.coverage.expected])}));
    await page.waitForTimeout(5000);
  }
  assert.ok(state.priceContext.vwaps.every(v=>v.coverage.complete));assert.ok(state.priceContext.candles>300);
  const price=state.priceContext;await page.getByLabel('VWAP 重置时区',{exact:true}).selectOption('0');await page.waitForTimeout(500);
  const utc=await (await page.request.get(base+'/healthz')).json();assert.equal(utc.priceContext.offsetHours,0);assert.ok(utc.priceContext.vwaps.every(v=>v.coverage.complete));
  await page.getByLabel('VWAP 重置时区',{exact:true}).selectOption('8');
  await page.getByRole('button',{name:'填写时间范围',exact:true}).click();const modal=page.getByRole('dialog',{name:'成交量选区编辑',exact:true});await modal.waitFor();
  await page.getByLabel('选区名称',{exact:true}).fill('OKX 实际历史 · 最近六小时');
  await modal.getByRole('button',{name:'预览计算',exact:true}).click();await page.getByTestId('profile-preview').waitFor();assert.match(await page.getByTestId('profile-preview').innerText(),/覆盖完整/);
  await modal.getByRole('button',{name:'保存选区',exact:true}).click();await modal.waitFor({state:'hidden'});
  const downloaded=page.waitForEvent('download');await page.getByRole('button',{name:'导出选区证据 ↗',exact:true}).click();await(await downloaded).saveAs('test-results/price-context/live-profile.json');
  await page.getByRole('button',{name:'生成报告 ↗',exact:true}).click();await page.getByRole('dialog',{name:'观察报告',exact:true}).waitFor();
  const analysisDownload=page.waitForEvent('download');await page.getByRole('button',{name:'导出分析 JSON',exact:true}).click();await(await analysisDownload).saveAs('test-results/price-context/live-analysis.json');
  await page.getByRole('button',{name:'关闭报告',exact:true}).click();
  await page.reload();await page.getByTestId('profile-detail').waitFor();assert.match(await page.getByTestId('profile-detail').innerText(),/选区覆盖完整/);
  assert.deepEqual(errors,[]);fs.writeFileSync('test-results/price-context/live-verification.json',JSON.stringify({passed:4,checks:['cold OKX backfill / all anchors covered','both timezone anchors','actual profile export','refresh persistence'],priceContext:price,utcContext:utc.priceContext,errors},null,2));
  console.log(JSON.stringify({passed:4,candles:price.candles,vwaps:price.vwaps,errors},null,2));
 } finally {await page.getByRole('button',{name:'演示',exact:true}).click().catch(()=>{});await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
