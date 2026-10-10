const { chromium } = require('playwright');
const fs = require('node:fs');
(async()=>{
  const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai'});
    const base=process.env.BTC_LENS_URL || 'http://127.0.0.1:5080';
    fs.mkdirSync('docs/screenshots',{recursive:true});
    for(const [route,name] of [['/','radar'],['/journal','journal'],['/statistics','statistics']]){
      await page.goto(base+route); if(route==='/')await page.waitForSelector('.chart-host canvas');
      await page.waitForTimeout(400);
      await page.screenshot({path:'docs/screenshots/'+name+'.png',fullPage:true});
    }
    console.log('Captured three showcase screenshots.');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
