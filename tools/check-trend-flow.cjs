const {chromium}=require('playwright');const assert=require('node:assert/strict');const fs=require('node:fs');
const base=process.env.BTC_LENS_URL||'http://127.0.0.1:5081';
(async()=>{
 const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||(process.platform==='win32'?'msedge':'chromium'),headless:true});
 const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai'});const errors=[],checks=[];
 page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});fs.mkdirSync('test-results/trend-flow',{recursive:true});
 try {
  assert.ok((await page.request.post(base+'/test/replay')).ok());await page.goto(base);await page.waitForSelector('.chart-host canvas');
  const host=page.locator('.chart-host');await page.locator('[aria-label="图表绘图工具"]').getByRole('button',{name:/趋势线/}).click();
  await page.waitForFunction(()=>document.querySelector('.chart-host')?.style.cursor==='crosshair');const box=await host.boundingBox();
  await page.mouse.click(box.x+box.width*.30,box.y+box.height*.30);await page.mouse.click(box.x+box.width*.70,box.y+box.height*.55);
  await page.getByTestId('chart-selection').getByRole('button',{name:'加入关注',exact:true}).click();
  const form=page.getByRole('dialog',{name:'关注区编辑'});await form.waitFor();await form.getByLabel('名称',{exact:true}).fill('动态趋势线（测试）');
  await form.getByLabel('关注距离（上下各 USDT）').fill('35');await form.getByRole('button',{name:'保存并启用',exact:true}).click();await form.waitFor({state:'hidden'});
  const card=page.locator('.watch-card').filter({hasText:'动态趋势线（测试）'});await card.waitFor();assert.match(await card.innerText(),/趋势线动态关注/);assert.match(await card.innerText(),/35.0 USDT/);
  await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host')?.dataset.drawings||'[]').length===0);
  await card.getByRole('button',{name:'编辑',exact:true}).click();assert.equal(await form.getByLabel('关注距离（上下各 USDT）').inputValue(),'35');
  await form.getByLabel('关注距离（上下各 USDT）').fill('10');await form.getByRole('button',{name:'保存并启用',exact:true}).click();await form.waitFor({state:'hidden'});
  await page.reload();await card.waitFor();assert.match(await card.innerText(),/10.0 USDT/);assert.match(await card.innerText(),/v2/);
  await card.getByRole('button',{name:'定位',exact:true}).click();await page.screenshot({path:'test-results/trend-flow/trend-watch.png',fullPage:true});
  await card.getByRole('button',{name:'删除',exact:true}).click();await card.waitFor({state:'hidden'});checks.push('trend drawing promotion / moving band / distance edit / version and refresh / deletion');
  assert.ok((await page.request.post(base+'/test/flow-fixture')).ok());await page.reload();await page.waitForSelector('.flow-chart canvas');
  await page.waitForFunction(()=>Number(document.querySelector('.flow-chart')?.dataset.cvdPoints)>0);
  assert.equal(await page.locator('.flow-chart').getAttribute('data-panes'),'4');assert.ok(Number(await page.locator('.flow-chart').getAttribute('data-price-bars'))>0);
  assert.match(await page.locator('.flow-source-status').innerText(),/Binance/);assert.match(await page.locator('.flow-caption').innerText(),/共同分钟/);
  await page.getByLabel('CVD 单位',{exact:true}).selectOption('USDT');await page.waitForFunction(()=>document.querySelector('.flow-chart')?.dataset.unit==='USDT');
  await page.getByLabel('订单流交易所',{exact:true}).selectOption('OKX');await page.getByLabel('订单流显示周期',{exact:true}).selectOption('5');
  await page.getByLabel('订单流时间范围',{exact:true}).selectOption('1');await page.getByLabel('订单流视图',{exact:true}).selectOption('segments');
  await page.waitForFunction(()=>Number(document.querySelector('.flow-chart')?.dataset.segments)>=2);
  await page.getByLabel('订单流视图',{exact:true}).selectOption('aggregate');await page.getByLabel('订单流交易所',{exact:true}).selectOption('OKX+Binance');
  await page.screenshot({path:'test-results/trend-flow/multi-exchange.png',fullPage:true});checks.push('two exchange common-minute CVD / exact USDT / delta / price and OI panes / controls');
  await page.setViewportSize({width:390,height:900});await page.waitForTimeout(300);assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false);checks.push('mobile layout');
  assert.deepEqual(errors,[]);console.log(JSON.stringify({passed:checks.length,checks,errors},null,2));
 } catch(e){await page.screenshot({path:'test-results/trend-flow/failure.png',fullPage:true});throw e;}finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
