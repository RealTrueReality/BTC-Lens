const {chromium}=require('playwright');const assert=require('node:assert/strict');const fs=require('node:fs');
const base=process.env.BTC_LENS_URL||'http://127.0.0.1:5081';
(async()=>{
 const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||(process.platform==='win32'?'msedge':'chromium'),headless:true});
 const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai'});const errors=[],checks=[];
 page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});fs.mkdirSync('test-results/market-reading',{recursive:true});
 try{
  assert.ok((await page.request.post(base+'/test/live-history')).ok());await page.goto(base);await page.waitForSelector('.chart-host canvas');await page.getByTestId('market-reading').waitFor();
  const panel=page.getByTestId('market-reading');await panel.getByRole('button',{name:'分析当前行情',exact:true}).click();
  await panel.locator('.reading-summary').waitFor();assert.match(await panel.innerText(),/OKX \+ Binance 公共成交/);await page.getByLabel('行情分析窗口',{exact:true}).selectOption('60');await panel.getByRole('button',{name:'分析当前行情',exact:true}).click();
  assert.match(await panel.innerText(),/主目标/);checks.push('aligned public-data interpretation / 15m and 1h / conditional evidence');
  await page.goto(base+'/settings');const settings=page.getByTestId('coinalyze-settings');await page.waitForSelector('[data-testid="coinalyze-settings"][data-ready="true"]');await settings.getByLabel('Coinalyze API Key',{exact:true}).fill('invalid-test-key');
  await settings.getByRole('button',{name:'验证并保存连接',exact:true}).click();await page.getByRole('status').filter({hasText:'无效'}).waitFor();
  await settings.getByLabel('Coinalyze API Key',{exact:true}).fill('offline-coinalyze-test-key');await settings.getByRole('button',{name:'验证并保存连接',exact:true}).click();
  await settings.getByRole('status').filter({hasText:'已连接'}).waitFor();assert.equal(await settings.getByLabel('Coinalyze API Key',{exact:true}).inputValue(),'');
  const stats=await (await page.request.get(base+'/test/coinalyze-stats')).json();assert.equal(stats.protectedKey,true);assert.equal(stats.snapshotContainsKey,false);
  await page.reload();assert.match(await settings.innerText(),/已保存连接/);assert.doesNotMatch(await settings.innerText(),/offline-coinalyze-test-key/);checks.push('invalid credentials / encrypted local key / no credential echo / reload');
  await page.goto(base);await page.waitForFunction(()=>Number(document.querySelector('.flow-chart')?.dataset.cvdPoints)>100);
  assert.match(await page.locator('.flow-caption').innerText(),/Coinalyze/);assert.equal(await page.locator('.flow-chart').getAttribute('data-panes'),'4');
  await panel.getByRole('button',{name:'分析当前行情',exact:true}).click();await panel.locator('.reading-summary').waitFor();assert.match(await panel.innerText(),/Coinalyze/);
  const downloading=page.waitForEvent('download');await panel.getByRole('button',{name:'导出这次分析 ↗',exact:true}).click();const download=await downloading;await download.saveAs('test-results/market-reading/reading.json');
  const json=fs.readFileSync('test-results/market-reading/reading.json','utf8');assert.doesNotMatch(json,/offline-coinalyze-test-key/);assert.equal(JSON.parse(json).reading.window.source,'Coinalyze');
  await page.getByLabel('订单流数据源',{exact:true}).selectOption('public');await page.getByLabel('订单流交易所',{exact:true}).waitFor();
  await page.getByLabel('订单流数据源',{exact:true}).selectOption('auto');await page.waitForFunction(()=>Number(document.querySelector('.flow-chart')?.dataset.cvdPoints)>100);
  await page.screenshot({path:'test-results/market-reading/coinalyze-reading.png',fullPage:true});checks.push('Coinalyze history / four panes / provider selection / frozen analysis export');
  await page.setViewportSize({width:390,height:900});await page.waitForTimeout(200);assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false);
  await page.goto(base+'/settings');await page.waitForSelector('[data-testid="coinalyze-settings"][data-ready="true"]');await settings.getByRole('button',{name:'移除连接',exact:true}).click();await page.getByRole('status').filter({hasText:'已移除'}).waitFor();
  assert.equal((await (await page.request.get(base+'/healthz')).json()).coinalyze.configured,false);await page.goto(base);await page.waitForSelector('.chart-host canvas');
  await panel.getByRole('button',{name:'分析当前行情',exact:true}).click();await panel.locator('.reading-summary').waitFor();assert.match(await panel.innerText(),/OKX \+ Binance 公共成交/);checks.push('mobile layout / disconnect clears preferred provider');
  assert.deepEqual(errors,[]);console.log(JSON.stringify({passed:checks.length,checks,errors},null,2));
 }catch(e){await page.screenshot({path:'test-results/market-reading/failure.png',fullPage:true});throw e;}finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
