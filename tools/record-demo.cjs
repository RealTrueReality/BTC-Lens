const { chromium } = require('playwright');
const fs = require('node:fs');
(async()=>{
  const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
  const context=await browser.newContext({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai',
    recordVideo:{dir:'artifacts/demo-recording',size:{width:1440,height:1000}}});
  const page=await context.newPage();
  const video=page.video();
  const base=process.env.BTC_LENS_URL || 'http://127.0.0.1:5081';
  const started=Date.now();
  async function caption(text,seconds=10){
    await page.evaluate(text=>{
      let box=document.getElementById('demo-caption');
      if(!box){box=document.createElement('div');box.id='demo-caption';document.body.append(box);
        box.style.cssText='position:fixed;bottom:22px;left:250px;right:32px;background:#101a22ed;border:1px solid #675335;border-radius:8px;padding:15px 20px;z-index:9999;pointer-events:none;color:#f1c47a;font:15px/1.8 Microsoft YaHei, sans-serif;box-shadow:0 8px 35px #0009;white-space:pre-wrap';}
      box.innerText=text;
    },text);
    console.log(text.split('\n')[0]);
    await page.waitForTimeout(seconds*1000);
  }
  try{
    await page.goto(base);await page.waitForSelector('.chart-host canvas');
    await page.getByRole('button',{name:'演示',exact:true}).click();
    await page.getByRole('button',{name:'新回放',exact:true}).click();
    await caption('BTC Lens · BTC 盯盘与交易复盘\n合成行情演示：自动参考区域 → 手动关注 → 到区证据 → 交易与复盘',13);
    await page.locator('.timeframes').getByRole('button',{name:'4H',exact:true}).click();
    await caption('01 / 多周期观察\n先看大周期背景，再回到低周期观察反应。自动标记采用明确版本的参考算法。',10);
    await page.locator('.timeframes').getByRole('button',{name:'5m',exact:true}).click();
    await page.getByRole('button',{name:'添加关注区',exact:false}).click();
    await page.getByLabel('名称',{exact:true}).fill('手动选择 · 回踩观察');
    await page.getByLabel('下边界',{exact:true}).fill('66900');
    await page.getByLabel('上边界',{exact:true}).fill('67000');
    await page.getByLabel('关注原因',{exact:true}).fill('等待价格回访后，继续观察局部反应；目标与确认单独判断。');
    await caption('02 / 你选择关注什么\n边界、周期和关注原因由你确认；自动区域与手动关注区各自保存。',11);
    await page.getByRole('button',{name:'保存并启用',exact:true}).click();
    await page.waitForSelector('[aria-label="关注区编辑"]',{state:'hidden'});
    await caption('保存关注区后持续监控\n每次编辑形成新版本，历史事件保留触发时的边界。',7);
    await page.getByRole('button',{name:'推进 5 根',exact:true}).click();
    await page.waitForSelector('.event-row');
    await page.locator('.event-row').first().scrollIntoViewIfNeeded();
    await caption('03 / 首次到区记录\n回放只揭示当前截止点之前的数据。同一关注区版本只记录一次首次进入。',10);
    await page.locator('.event-row').first().click();
    await caption('价格、时间、区域版本和证据一起保存\n触及区域后，局部确认与主目标仍需要判断。',10);
    await page.getByRole('link',{name:'关联交易记录 →',exact:true}).click();
    await page.waitForSelector('[aria-label="交易编辑"]');
    const entry=Number(await page.getByLabel('实际开仓价',{exact:true}).inputValue());
    await page.getByLabel('初始止损（可留空）',{exact:true}).fill(String(entry-100));
    await page.getByLabel('原定目标（可留空）',{exact:true}).fill(String(entry+300));
    await caption('04 / 记录实际交易\n保存成交价、BTC 数量、初始止损和原定目标。初始风险保存后固定。',10);
    await page.getByLabel('这笔交易已平仓',{exact:true}).check();
    await page.getByLabel('实际平仓价',{exact:true}).fill(String(entry+200));
    const time=await page.getByLabel('开仓时间（北京时间）',{exact:true}).inputValue();
    const exit=new Date(time+'Z');exit.setUTCHours(exit.getUTCHours()+1);
    await page.getByLabel('平仓时间（北京时间）',{exact:true}).fill(exit.toISOString().slice(0,16));
    await page.getByLabel('总手续费（USDT）',{exact:true}).fill('0.6');
    await page.getByLabel('资金费（收入为正，USDT）',{exact:true}).fill('0');
    await page.getByLabel('复盘记录',{exact:true}).fill('演示记录：保留到区证据，记录实际执行与原计划之间的差异。');
    await page.getByLabel('标签（用逗号分隔）',{exact:true}).fill('演示视频,按计划执行');
    await caption('补齐成本与复盘\n费用未知时留空；确定没有资金费时填 0。复盘记录让判断和执行可以回看。',10);
    await page.getByRole('button',{name:'保存交易',exact:true}).click();
    await page.waitForSelector('[aria-label="交易编辑"]',{state:'hidden'});
    await caption('05 / 从证据到复盘\n这笔演示交易的净盈亏为 +1.40 USDT，关联的原始到区快照继续保留。',10);
    await page.goto(base+'/statistics');
    await caption('06 / 从记录看执行\n净盈亏、R、成本覆盖与复盘标签。这里是合成演示数据，不代表真实交易业绩。',12);
    await page.goto(base+'/journal');await page.getByRole('button',{name:'导入 CSV',exact:true}).click();
    const csv=fs.readFileSync('examples/trades.csv');
    await page.locator('[aria-label="CSV 导入"] input[type=file]').setInputFiles({name:'trades.csv',mimeType:'text/csv',buffer:csv});
    await page.waitForSelector('.import-summary');
    await caption('07 / CSV 预览与导入\n固定模板、带时区时间、整批事务与 external_id 去重。相同 ID 内容冲突时整批回滚。',12);
    await page.getByRole('button',{name:'关闭导入',exact:true}).click();
    await page.goto(base);await page.waitForSelector('.chart-host canvas');
    await page.getByRole('button',{name:'生成报告 ↗',exact:true}).click();
    await caption('08 / 整理当前观察\n报告保留事实、来源和缺失证据，可以导出分析 JSON 给智能体。当前是规则报告。',13);
    await page.getByRole('button',{name:'关闭报告',exact:true}).click();
    await page.goto(base+'/guide');
    await caption('下一步：现货/合约 CVD、OI、固定范围成交量分布、VWAP 与个人规则\n.NET 10 / Blazor / SqlSugar / SQLite · 公共行情 · 本地个人工作区',12);
    const remaining=Math.max(0,180000-(Date.now()-started));
    if(remaining)await page.waitForTimeout(remaining);
    await context.close();
    fs.mkdirSync('docs',{recursive:true});await video.saveAs('docs/demo.webm');
    console.log('Saved docs/demo.webm');
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
