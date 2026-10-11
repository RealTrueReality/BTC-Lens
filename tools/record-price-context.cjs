const {chromium}=require('playwright');const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||'msedge',headless:true});
 const context=await browser.newContext({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai',recordVideo:{dir:'test-results/price-context-video',size:{width:1440,height:1000}}});
 const page=await context.newPage();const base=process.env.BTC_LENS_URL||'http://127.0.0.1:5081';
 const caption=async text=>{await page.evaluate(text=>{let c=document.getElementById('demo-caption');if(!c){c=document.createElement('div');c.id='demo-caption';c.style.cssText='position:fixed;bottom:22px;left:50%;transform:translateX(-50%);padding:13px 22px;background:#0c1319ed;border:1px solid #52615d;border-radius:7px;color:#ece8dc;font:16px Microsoft YaHei,sans-serif;z-index:20000;pointer-events:none;white-space:nowrap';document.body.appendChild(c);}c.textContent=text;},text);};
 try{
  await page.goto(base);await page.getByRole('button',{name:'演示',exact:true}).click();await page.getByRole('button',{name:'新回放',exact:true}).click();await page.waitForTimeout(1000);
  await caption('BTC Lens V0.2.1 · 成交量分布与日／周／月 VWAP');await page.waitForTimeout(3000);
  await page.getByTestId('price-context').scrollIntoViewIfNeeded();await caption('当前日、周、月的位置：同一份 5 分钟数据，覆盖范围可见');await page.waitForTimeout(3500);
  await page.getByLabel('VWAP 重置时区',{exact:true}).selectOption('0');await page.waitForTimeout(2500);await page.getByLabel('VWAP 重置时区',{exact:true}).selectOption('8');
  await page.locator('.chart-host').scrollIntoViewIfNeeded();await page.getByLabel('月 VWAP',{exact:true}).uncheck();
  await caption('圈选盘整区间：依次点击起始和结束 K 线');await page.getByRole('button',{name:'成交量选区',exact:true}).click();await page.waitForTimeout(1000);
  const b=await page.locator('.chart-host').boundingBox();await page.mouse.click(b.x+b.width*.28,b.y+b.height*.3);await page.waitForTimeout(1000);await page.mouse.click(b.x+b.width*.57,b.y+b.height*.4);
  const modal=page.getByRole('dialog',{name:'成交量选区编辑',exact:true});await modal.waitFor();await page.getByLabel('选区名称',{exact:true}).fill('盘整区间 · 09/21');await modal.getByRole('button',{name:'预览计算',exact:true}).click();
  await caption('预览 VAH／POC／VAL，检查已覆盖的成交量与 K 线');await page.waitForTimeout(4000);await modal.getByRole('button',{name:'保存选区',exact:true}).click();await modal.waitFor({state:'hidden'});
  await page.locator('.chart-host').scrollIntoViewIfNeeded();await caption('保存选区后，价格分布和关键位置显示在主图');await page.waitForTimeout(4000);
  await page.getByTestId('profile-detail').scrollIntoViewIfNeeded();await page.getByTestId('profile-detail').locator('.profile-levels>div').filter({hasText:'POC'}).getByRole('button',{name:'＋ 关注',exact:true}).click();await page.getByRole('dialog',{name:'关注区编辑',exact:true}).waitFor();
  await page.getByLabel('名称',{exact:true}).fill('盘整区间 POC');await caption('将位置加入自己的关注区，按判断调整宽度与备注');await page.waitForTimeout(3500);await page.getByRole('button',{name:'保存并启用',exact:true}).click();await page.waitForTimeout(1000);
  await page.getByRole('button',{name:'生成报告 ↗',exact:true}).click();await page.getByRole('dialog',{name:'观察报告',exact:true}).waitFor();await caption('观察报告同时引用选区、VWAP 与订单流，保留缺失证据');await page.waitForTimeout(4500);await page.getByRole('button',{name:'关闭报告',exact:true}).click();
  await page.getByRole('button',{name:'推进 5 根',exact:true}).click();await page.waitForSelector('.event-row');await caption('到区时保存当时的证据，后续调整保留原始记录');await page.waitForTimeout(3000);
  await page.evaluate(()=>document.getElementById('demo-caption')?.remove());await page.waitForTimeout(5000);await page.evaluate(()=>scrollTo(0,0));
  fs.mkdirSync('docs/screenshots',{recursive:true});await page.screenshot({path:'docs/screenshots/radar.png',fullPage:true});await page.getByTestId('price-context').screenshot({path:'docs/screenshots/price-context.png'});
 }finally{const video=page.video();await context.close();await video.saveAs('docs/price-context-demo.webm');await browser.close();}
 console.log('Saved V0.2.1 screenshots and video.');
})().catch(e=>{console.error(e);process.exitCode=1;});
