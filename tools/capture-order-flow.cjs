const { chromium } = require('playwright');
(async()=>{
  const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'zh-CN',timezoneId:'Asia/Shanghai'});
    const base=process.env.BTC_LENS_URL || 'http://127.0.0.1:5081';
    await page.goto(base); await page.waitForSelector('.flow-chart canvas');
    await page.getByRole('button',{name:'演示',exact:true}).click();
    await page.getByRole('button',{name:'新回放',exact:true}).click();
    await page.waitForTimeout(4500);
    await page.locator('[data-testid="order-flow"]').screenshot({path:'docs/screenshots/order-flow.png'});
    await page.screenshot({path:'docs/screenshots/radar.png',fullPage:true});
    console.log('Captured V0.2 screenshots from explicitly synthetic data.');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
