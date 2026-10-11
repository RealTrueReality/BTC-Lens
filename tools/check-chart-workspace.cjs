const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const base=process.env.BTC_LENS_URL||'http://127.0.0.1:5081';
const checks=[],errors=[];
(async()=>{
 const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||(process.platform==='win32'?'msedge':'chromium'),headless:true});
 const context=await browser.newContext({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai'});
 const page=await context.newPage();
 const host=page.locator('.chart-host'),selection=page.locator('[data-testid="chart-selection"]');
 const request=async path=>{const r=await context.request.post(base+path);assert.ok(r.ok(),await r.text());};
 const ids=async name=>JSON.parse(await host.getAttribute('data-'+name)||'[]');
 const ready=async()=>{await page.waitForSelector('.chart-host canvas');await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host')?.dataset.zones||'[]').length>0);};
 const draw=async(name,a,b)=>{
  await page.locator('[aria-label="图表绘图工具"]').getByRole('button',{name:new RegExp(name)}).click();
  await page.waitForFunction(()=>document.querySelector('.chart-host')?.style.cursor==='crosshair');
  const box=await host.boundingBox();
  await page.mouse.click(box.x+box.width*a[0],box.y+box.height*a[1]);
  if(b){await page.mouse.move(box.x+box.width*b[0],box.y+box.height*b[1]);await page.mouse.click(box.x+box.width*b[0],box.y+box.height*b[1]);}
 };
 page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
 fs.mkdirSync('test-results/chart-workspace',{recursive:true});
 try{
  await request('/test/replay');await page.goto(base);await ready();
  assert.equal(await page.getByRole('button',{name:'演示',exact:true}).count(),0);
  assert.equal(await page.locator('.demo-banner,.sidebar-note').count(),0);
  assert.equal(await page.locator('.lower-grid table').count(),0);
  assert.ok(!(await page.locator('body').innerText()).includes('PERSONAL TRADING WORKSPACE'));
  checks.push('realtime-only shell / no promotional note or numeric auto-zone table');
  await page.waitForSelector('.flow-chart canvas');
  await page.getByLabel('订单流显示周期').selectOption('15');
  assert.equal(await page.getByLabel('订单流显示周期').inputValue(),'15');
  checks.push('CVD / OI panes and sampling remain available');

  const zone=page.locator('.chart-object-label[data-kind="zone"]').last();await zone.waitFor();
  await zone.click();await selection.waitFor();
  await selection.getByRole('button',{name:'加入关注',exact:true}).click();
  const editor=page.locator('[aria-label="关注区编辑"]');await editor.waitFor();
  assert.ok(Number(await page.getByLabel('上边界',{exact:true}).inputValue())>Number(await page.getByLabel('下边界',{exact:true}).inputValue()));
  await page.getByLabel('名称',{exact:true}).fill('图上自动区域观察');
  await page.getByRole('button',{name:'保存并启用',exact:true}).click();await editor.waitFor({state:'hidden'});
  await page.locator('.watch-card').filter({hasText:'图上自动区域观察'}).waitFor();
  checks.push('click automatic OB/FVG inside chart → watch with exact zone boundaries');

  await draw('矩形',[.30,.22],[.58,.48]);
  await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.drawings||'[]').length===1);
  const drawingId=(await ids('drawings'))[0];
  const rectangle=page.locator('.chart-object-label[data-kind="drawing"][data-object-id="'+drawingId+'"]');
  await rectangle.waitFor();await rectangle.click();
  const rangeBefore=await selection.locator('.mono').innerText();
  const b=await host.boundingBox(),x=Number(await rectangle.getAttribute('data-x')),y=Number(await rectangle.getAttribute('data-y'));
  await page.mouse.move(b.x+x,b.y+y);await page.mouse.down();await page.mouse.move(b.x+x+28,b.y+y+18,{steps:8});await page.mouse.up();
  await page.waitForFunction(previous=>document.querySelector('[data-testid="chart-selection"] .mono')?.textContent!==previous,rangeBefore);
  const rangeAfter=await selection.locator('.mono').innerText();
  await page.reload();await ready();await rectangle.waitFor();await rectangle.click();assert.equal(await selection.locator('.mono').innerText(),rangeAfter);
  checks.push('rectangle drawing / drag geometry / refresh persistence');
  await selection.getByRole('button',{name:'加入关注',exact:true}).click();await editor.waitFor();
  await page.getByLabel('名称',{exact:true}).fill('矩形价格范围观察');
  await page.getByRole('button',{name:'保存并启用',exact:true}).click();await editor.waitFor({state:'hidden'});
  const rectangleWatch=page.locator('.watch-card').filter({hasText:'矩形价格范围观察'});await rectangleWatch.waitFor();
  await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.drawings||'[]').length===0);
  await rectangleWatch.getByRole('button',{name:'删除',exact:true}).click();await rectangleWatch.waitFor({state:'hidden'});
  checks.push('rectangle becomes watched range / one chart object / deletion stops watch');

  await draw('水平线',[.45,.32]);await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.drawings||'[]').length===1);
  const horizontal=(await ids('drawings'))[0];
  await draw('趋势线',[.25,.30],[.64,.43]);await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.drawings||'[]').length===2);
  await page.reload();await ready();assert.equal((await ids('drawings')).length,2);
  await page.locator('.chart-object-label[data-object-id="'+horizontal+'"]').click();await host.press('Delete');
  await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.drawings||'[]').length===1);
  assert.ok(!(await ids('drawings')).includes(horizontal));
  checks.push('horizontal / trend tools / multiple saved drawings / Delete key');
  await draw('矩形',[.22,.25],null);await host.press('Escape');
  await page.waitForFunction(()=>document.querySelector('.chart-host')?.style.cursor!=='crosshair');
  assert.equal((await ids('drawings')).length,1);
  checks.push('Escape cancels incomplete drawing without saving');

  const profileForm=page.locator('[aria-label="成交量选区编辑"]');
  for(const [name,a,b] of [['盘整区间 A',[.12,.38],[.40,.52]],['盘整区间 B',[.46,.38],[.76,.52]]]){
   await draw('成交量选区',a,b);await profileForm.waitFor();
   await page.getByLabel('选区名称',{exact:true}).fill(name);
   await page.getByRole('button',{name:'预览计算',exact:true}).click();await page.locator('[data-testid="profile-preview"]').waitFor();
   await page.getByRole('button',{name:'保存选区',exact:true}).click();await profileForm.waitFor({state:'hidden'});
  }
  const items=page.locator('.profile-item');await page.waitForFunction(()=>document.querySelectorAll('.profile-item').length===2);
  await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.profiles||'[]').length===2);
  const profileA=items.filter({hasText:'盘整区间 A'}),profileB=items.filter({hasText:'盘整区间 B'});
  const profileAId=await profileA.getAttribute('data-profile-id'),profileBId=await profileB.getAttribute('data-profile-id');
  assert.equal(new Set(await ids('profiles')).size,2);await page.reload();await ready();assert.equal((await ids('profiles')).length,2);
  checks.push('two independent on-chart fixed ranges / both displayed / both persist');
  await page.screenshot({path:'test-results/chart-workspace/multi-profile.png',fullPage:true});
  await profileA.getByRole('checkbox').uncheck();await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.profiles||'[]').length===1);
  await page.reload();await ready();assert.equal((await ids('profiles')).length,1);assert.equal(await profileA.getByRole('checkbox').isChecked(),false);
  await profileA.getByRole('checkbox').check();await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.profiles||'[]').length===2);
  checks.push('individual profile visibility survives refresh');

  await profileA.getByRole('button',{name:'定位',exact:true}).click();
  await page.locator('.chart-object-label[data-kind="profile"][data-object-id="'+profileAId+'"]').waitFor();
  await selection.getByRole('button',{name:'关注 POC',exact:true}).click();await editor.waitFor();
  await page.getByLabel('名称',{exact:true}).fill('选区 A POC 观察');
  await page.getByRole('button',{name:'保存并启用',exact:true}).click();await editor.waitFor({state:'hidden'});
  assert.match(await page.locator('.watch-card').filter({hasText:'选区 A POC 观察'}).innerText(),/参考来源/);
  await profileA.getByRole('button',{name:'删除',exact:true}).click();await profileA.waitFor({state:'hidden'});
  await page.reload();await ready();assert.deepEqual(await ids('profiles'),[profileBId]);assert.equal(await page.locator('.watch-card').filter({hasText:'选区 A POC 观察'}).count(),1);
  checks.push('profile location / POC watch / delete one keeps others and watch reference');
  await profileB.getByRole('button',{name:'定位',exact:true}).click();
  await page.locator('.chart-object-label[data-object-id="'+profileBId+'"]').waitFor();
  await selection.getByRole('button',{name:'删除所选',exact:true}).click();await items.waitFor({state:'hidden'});
  await page.waitForFunction(()=>JSON.parse(document.querySelector('.chart-host').dataset.profiles||'[]').length===0);
  checks.push('delete profile directly from chart selection');

  await request('/test/step/5');await page.waitForSelector('.event-row');await request('/test/step/5');
  await page.reload();await ready();assert.equal(await page.locator('.event-row').count(),1);
  await page.locator('.event-row').first().click();await page.getByText('查看保存的证据数据',{exact:true}).click();
  const snapshot=await page.locator('.json-block').innerText();assert.match(snapshot,/"cutoff"/);
  await page.getByRole('link',{name:'关联交易记录 →',exact:true}).click();
  await page.waitForSelector('[aria-label="交易编辑"]');
  const entry=Number(await page.getByLabel('实际开仓价',{exact:true}).inputValue());
  await page.getByLabel('初始止损（可留空）',{exact:true}).fill(String(entry-100));
  await page.getByLabel('这笔交易已平仓',{exact:true}).check();
  await page.getByLabel('实际平仓价',{exact:true}).fill(String(entry+200));
  const time=await page.getByLabel('开仓时间（北京时间）',{exact:true}).inputValue(),later=new Date(time+'Z');later.setUTCHours(later.getUTCHours()+1);
  await page.getByLabel('平仓时间（北京时间）',{exact:true}).fill(later.toISOString().slice(0,16));
  await page.getByLabel('总手续费（USDT）',{exact:true}).fill('.6');await page.getByLabel('资金费（收入为正，USDT）',{exact:true}).fill('0');
  await page.getByLabel('复盘记录',{exact:true}).fill('图表工作区浏览器验收');
  await page.getByRole('button',{name:'保存交易',exact:true}).click();await page.waitForSelector('[aria-label="交易编辑"]',{state:'hidden'});
  assert.match(await page.locator('.detail-pnl').innerText(),/\+1\.40/);
  const tradeId=await page.locator('tr.row-selected').getAttribute('data-trade-id');
  await page.goto(base);await ready();const original=page.locator('.watch-card').filter({hasText:'回踩观察'});
  await original.getByRole('button',{name:'删除',exact:true}).click();await original.waitFor({state:'hidden'});
  await page.locator('.event-row').first().click();await page.getByText('查看保存的证据数据',{exact:true}).click();assert.equal(await page.locator('.json-block').innerText(),snapshot);
  await page.goto(base+'/journal');await page.locator('tr[data-trade-id="'+tradeId+'"]').click();await page.getByText('关联到区证据',{exact:false}).click();
  assert.match(await page.locator('.review-content').innerText(),/回踩观察/);
  checks.push('entry deduplication / journal PnL / deleting watch preserves original event and linked trade');

  await page.getByRole('button',{name:'导入 CSV',exact:true}).click();
  const csv='external_id,side,entry_time,exit_time,entry_price,exit_price,quantity_btc,initial_stop,target,fees,funding,reason,review,tags\nworkspace-'+Date.now()+',Long,2026-09-20T09:00:00+08:00,2026-09-20T10:00:00+08:00,67000,67300,0.01,66900,67300,0.6,0,chart,review,test\n';
  const upload={name:'test.csv',mimeType:'text/csv',buffer:Buffer.from(csv)};
  for(let i=0;i<2;i++){if(i)await page.getByRole('button',{name:'导入 CSV',exact:true}).click();await page.locator('[aria-label="CSV 导入"] input[type=file]').setInputFiles(upload);await page.getByRole('button',{name:'确认导入',exact:true}).click();await page.waitForSelector('[aria-label="CSV 导入"]',{state:'hidden'});}
  checks.push('CSV import and repeated import');
  await page.goto(base);await ready();
  await page.getByRole('button',{name:'生成报告 ↗',exact:true}).click();await page.waitForSelector('[aria-label="观察报告"]');
  assert.match(await page.locator('[aria-label="观察报告"]').innerText(),/尚缺证据/);await page.getByRole('button',{name:'关闭报告',exact:true}).click();
  await page.locator('.timeframes').getByRole('button',{name:'4H',exact:true}).click();await page.waitForTimeout(200);await page.locator('.timeframes').getByRole('button',{name:'5m',exact:true}).click();
  checks.push('report and timeframe switching');
  await page.getByLabel('VWAP 重置时区',{exact:true}).selectOption('0');await page.reload();await ready();assert.equal(await page.getByLabel('VWAP 重置时区').inputValue(),'0');
  await page.getByLabel('VWAP 重置时区').selectOption('8');await page.waitForTimeout(250);
  checks.push('anchored VWAP timezone persistence');
  await page.screenshot({path:'test-results/chart-workspace/desktop.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});await page.waitForTimeout(300);assert.ok(await page.evaluate(()=>document.body.scrollWidth<=innerWidth));
  await page.screenshot({path:'test-results/chart-workspace/mobile.png',fullPage:true});checks.push('mobile layout without page overflow');
  assert.deepEqual(errors,[]);console.log(JSON.stringify({passed:checks.length,checks,errors},null,2));
 }catch(e){console.error({checks,errors});await page.screenshot({path:'test-results/chart-workspace/failure.png',fullPage:true});throw e;}
 finally{await context.close();await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
