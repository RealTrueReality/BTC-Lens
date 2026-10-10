const { chromium } = require('playwright');
const fs = require('fs');
(async () => {
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if(m.type()==='error') errors.push(m.text()); });
  await page.goto(process.env.BTC_LENS_URL || 'http://127.0.0.1:5081');
  await page.waitForSelector('.chart-host canvas', { timeout: 20000 });
  await page.waitForTimeout(1200);
  fs.mkdirSync('test-results', { recursive:true });
  await page.screenshot({ path: 'test-results/radar-initial.png', fullPage:true });
  console.log(JSON.stringify({ title:await page.title(), canvas:await page.locator('canvas').count(), errors, headings:await page.locator('h1').allTextContents(), bodyWidth:await page.evaluate(()=>document.body.scrollWidth) }));
  await browser.close();
  if(errors.length) process.exitCode=1;
})().catch(e=>{console.error(e);process.exit(1);});
