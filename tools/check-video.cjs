const { chromium } = require('playwright');
const { pathToFileURL } = require('node:url');
const path = require('node:path');
const assert = require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1000}});
  const file=process.argv[2] || 'docs/demo.webm';
  await page.goto(pathToFileURL(path.resolve(file)).href);
  await page.waitForFunction(()=>document.querySelector('video')?.readyState>=2);
  const info=await page.evaluate(async()=>{
   const video=document.querySelector('video');video.pause();
   const next=new Promise(resolve=>video.addEventListener('seeked',resolve,{once:true}));video.currentTime=Math.min(90,video.duration/2);await next;
   return {duration:video.duration,width:video.videoWidth,height:video.videoHeight,currentTime:video.currentTime};
  });
  console.log(JSON.stringify(info));
  assert.ok(file.includes('price-context') ? info.duration>=30 && info.duration<90 : info.duration>=170 && info.duration<210);assert.equal(info.width,1440);assert.equal(info.height,1000);
  await page.screenshot({path:'test-results/demo-frame.png'});
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
