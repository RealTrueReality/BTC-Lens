const {chromium}=require('playwright');
const assert=require('node:assert/strict');const fs=require('node:fs');
const base=process.env.BTC_LENS_URL||'http://127.0.0.1:5081';
(async()=>{
 const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||(process.platform==='win32'?'msedge':'chromium'),headless:true});
 const context=await browser.newContext({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai'});
 const page=await context.newPage(),errors=[],checks=[];page.on('pageerror',e=>errors.push(e.message));
 const form=page.locator('[aria-label="成交量选区编辑"]');const stats=async()=> (await(await context.request.get(base+'/test/history-stats')).json()).requests;
 fs.mkdirSync('test-results/profile-history',{recursive:true});
 try{
  const init=await context.request.post(base+'/test/live-history');assert.ok(init.ok());
  await page.goto(base);await page.waitForSelector('.chart-host canvas');
  // Remove only prior profiles in this explicitly isolated test host.
  for(const item of await page.locator('.profile-item').all())await item.getByRole('button',{name:'删除',exact:true}).click();
  await page.locator('.timeframes').getByRole('button',{name:'4H',exact:true}).click();
  await page.getByRole('button',{name:'填写时间范围',exact:true}).click();await form.waitFor();
  await page.getByLabel('选区名称',{exact:true}).fill('七八月历史区间（测试）');
  await page.getByLabel('开始时间（北京时间，包含）').fill('2026-07-01T00:00');
  await page.getByLabel('结束时间（北京时间，不含）').fill('2026-08-21T00:00');
  const before=await stats();await page.getByRole('button',{name:'预览计算',exact:true}).click();
  // Fresh hosts cancel mid-download; repeated runs may already have the full range cached.
  if(before===0){
   await page.getByTestId('profile-load').waitFor();
   await page.waitForFunction(async()=> (await(await fetch('/test/history-stats')).json()).requests>=2);
   await page.getByRole('button',{name:'取消加载',exact:true}).click();
   await page.getByText('已取消加载，已下载数据仍在本地；再次预览会继续补取。',{exact:true}).waitFor();
   await page.getByRole('button',{name:'预览计算',exact:true}).click();
  }
  await page.getByTestId('profile-preview').waitFor({timeout:60000});
  assert.match(await page.getByTestId('profile-preview').innerText(),/覆盖 14688 \/ 14688/);
  assert.match(await page.getByTestId('profile-preview').innerText(),/选区覆盖完整/);
  checks.push('51-day range / progress and cancellation / automatic resume to 14688 closed 5m bars');
  await page.getByRole('button',{name:'保存选区',exact:true}).click();await form.waitFor({state:'hidden'});
  const item=page.locator('.profile-item').filter({hasText:'七八月历史区间（测试）'});await item.waitFor();
  await item.getByRole('button',{name:'定位',exact:true}).click();
  await page.waitForFunction(()=>Number(document.querySelector('.chart-host')?.dataset.candleStart)<=Date.parse('2026-07-01T00:00:00+08:00')/1000);
  assert.equal(await page.locator('.timeframes .active').innerText(),'4H');
  await page.waitForFunction(()=>{
   const host=document.querySelector('.chart-host'),label=host?.querySelector('.chart-object-label[data-kind="profile"]');
   return label && Number(label.dataset.x)>host.clientWidth*.25;
  });
  await page.screenshot({path:'test-results/profile-history/historical-4h.png',fullPage:true});
  await page.reload();await item.waitFor();await page.waitForSelector('.chart-host canvas');
  await page.locator('.timeframes').getByRole('button',{name:'4H',exact:true}).click();await item.getByRole('button',{name:'定位',exact:true}).click();
  await page.waitForFunction(()=>Number(document.querySelector('.chart-host')?.dataset.candleStart)<=Date.parse('2026-07-01T00:00:00+08:00')/1000);
  checks.push('save / historical 4H candles and range location / refresh persistence');
  const loaded=await stats();await page.getByRole('button',{name:'填写时间范围',exact:true}).click();
  await page.getByLabel('开始时间（北京时间，包含）').fill('2026-07-01T00:00');await page.getByLabel('结束时间（北京时间，不含）').fill('2026-08-21T00:00');
  await page.getByRole('button',{name:'预览计算',exact:true}).click();await page.getByTestId('profile-preview').waitFor();assert.equal(await stats(),loaded);
  await page.getByLabel('结束时间（北京时间，不含）').fill('2026-08-20T00:00');await page.getByLabel('结束时间（北京时间，不含）').press('Tab');assert.equal(await page.getByTestId('profile-preview').count(),0);
  await page.getByRole('button',{name:'预览计算',exact:true}).click();await page.getByTestId('profile-preview').waitFor();assert.match(await page.getByTestId('profile-preview').innerText(),/覆盖 14400 \/ 14400/);assert.equal(await stats(),loaded);
  checks.push('warm-cache preview makes no requests / changing range clears the old preview');
  await page.getByRole('button',{name:'关闭成交量选区',exact:true}).click();assert.deepEqual(errors,[]);
  console.log(JSON.stringify({passed:checks.length,checks,newRequests:loaded-before,errors},null,2));
 }catch(e){await page.screenshot({path:'test-results/profile-history/failure.png',fullPage:true});throw e;}
 finally{await context.close();await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
