export function create(host){
 host.replaceChildren();const L=window.LightweightCharts;
 const chart=L.createChart(host,{autoSize:true,layout:{background:{color:'#13191d'},textColor:'#86939b',fontSize:11,attributionLogo:true,panes:{separatorColor:'#34434a'}},grid:{vertLines:{color:'#20282d'},horzLines:{color:'#20282d'}},rightPriceScale:{minimumWidth:78,borderColor:'#283238',scaleMargins:{top:.20,bottom:.08}},timeScale:{borderColor:'#283238',timeVisible:true,rightOffset:2,tickMarkFormatter:t=>new Date(t*1000).toLocaleTimeString('zh-CN',{timeZone:'Asia/Shanghai',hour:'2-digit',minute:'2-digit'})},localization:{locale:'zh-CN',timeFormatter:t=>new Date(t*1000).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai'})},crosshair:{mode:L.CrosshairMode.Normal}});
 const series=new Map(),names=new Map(),headers=[],latest=new Map();let unit='BTC',titles=[],hadData=false,hovered=null;
 const compact=n=>new Intl.NumberFormat('en-US',{notation:'compact',maximumFractionDigits:Math.abs(n)<10?4:3}).format(n);
 for(let pane=0;pane<4;pane++){const label=document.createElement('div');label.className='flow-pane-label indicator-pane-label';label.dataset.pane=pane;host.append(label);headers.push(label);add('empty-'+pane,pane,'line').setData([]);}
 for(const pane of chart.panes())if(typeof pane.setStretchFactor==='function')pane.setStretchFactor(1);
 function add(id,pane,type){if(!series.has(id)){const options={priceLineVisible:false,lastValueVisible:true,priceFormat:{type:'custom',minMove:.0001,formatter:compact}};const value=type==='candle'?chart.addSeries(L.CandlestickSeries,{...options,upColor:'#32b5a0',downColor:'#ef5964',borderVisible:false,wickUpColor:'#32b5a0',wickDownColor:'#ef5964'},pane):type==='delta'?chart.addSeries(L.HistogramSeries,{...options,priceScaleId:'delta-'+pane,lastValueVisible:false,priceFormat:{type:'volume'}},pane):chart.addSeries(L.LineSeries,{...options,color:pane===2?'#e9ad62':pane===3?'#7b9fd6':'#32b5a0',lineWidth:1,pointMarkersVisible:false},pane);series.set(id,value);names.set(id,{pane,type});}return series.get(id);}
 function header(pane,item){const label=headers[pane];label.replaceChildren();const title=document.createElement('span');title.textContent=titles[pane]||'';label.append(title);if(item){const value=document.createElement('strong');value.className=item.close!==undefined&&item.close<item.open?'negative':'positive';value.textContent=item.close===undefined?' '+compact(item.value)+' '+(pane===3?'BTC':unit):' 开 '+compact(item.open)+' 高 '+compact(item.high)+' 低 '+compact(item.low)+' 收 '+compact(item.close);label.append(value);}}
 chart.subscribeCrosshairMove(event=>{const found=new Map();for(const [id,s] of series){const meta=names.get(id),row=event.seriesData.get(s);if(row && meta.type!=='delta' && !id.startsWith('empty-'))found.set(meta.pane,row);}hovered=event.time?found:null;for(let p=0;p<4;p++)header(p,hovered?hovered.get(p):latest.get(p));});
 function sampled(rows,minutes){const map=new Map();for(const row of rows)map.set(Math.floor((row.time-1)/(minutes*60)),{time:row.time,value:row.value});return [...map.values()].sort((a,b)=>a.time-b.time);}
 function lineGroups(rows,maxGap){const groups=[];let group=[];for(const row of rows){if(group.length && row.time-group.at(-1).time>maxGap){groups.push(group);group=[];}group.push(row);}if(group.length)groups.push(group);return groups;}
 return{
  update(points,oi,interval,reset,maxOiGap=30,candles=[],nextUnit='BTC',oiSource='OKX',options={style:'price',showDelta:true,bars:[],main:[]}){
   unit=nextUnit;const used=new Set(['empty-0','empty-1','empty-2','empty-3']),bars=options.bars||[];latest.clear();
   titles=[options.style==='price'?'价格 · OKX 永续 · USDT':'<OKX> 永续 CVD · '+unit,'聚合现货 CVD · '+unit,'聚合合约 CVD · '+unit,'<'+oiSource+'> 持仓 OI · BTC'];
   const paneData=[options.main||[],points.filter(p=>p.instrument==='BTC-USDT'),points.filter(p=>p.instrument==='BTC-USDT-SWAP'),oi];
   let barCount=0,cvdGroups=0;
   for(let pane=0;pane<4;pane++){
    if(pane===0 && options.style==='price'){const id='price';used.add(id);add(id,0,'candle').setData(candles);latest.set(0,candles.at(-1));continue;}
    const groups=new Map();for(const row of bars.filter(b=>b.pane===pane)){if(!groups.has(row.group))groups.set(row.group,[]);groups.get(row.group).push(row);}
    const useCandles=options.style==='candles' && groups.size>0;
    if(useCandles){for(const [group,rows] of [...groups].slice(-6)){const id='candle:'+pane+':'+group;used.add(id);add(id,pane,'candle').setData(rows);barCount+=rows.length;latest.set(pane,rows.at(-1));if(pane<3)cvdGroups++;}const basis=[...new Set(bars.filter(b=>b.pane===pane).map(b=>b.basis))];titles[pane]+=' · '+(basis.every(b=>b==='observed_trades')?'观测成交 K线':'采样 K线');}
    else{
     const rows=paneData[pane];const bySegment=pane===3?lineGroups(rows,maxOiGap):[...Map.groupBy(rows,r=>r.segmentId).values()];
     for(const group of bySegment.slice(-6)){if(!group.length)continue;const id='line:'+pane+':'+(group[0].segmentId||group[0].time);used.add(id);const samples=sampled(group,interval);add(id,pane,'line').setData(samples);latest.set(pane,samples.at(-1));if(pane<3)cvdGroups++;}
     titles[pane]+=' · '+(rows.length===0?'暂无数据':options.style==='candles'?'高低点不足，显示折线':'折线');
    }
    if(options.showDelta && (pane===1 || pane===2)){const buckets=new Map();for(const row of paneData[pane]){if(row.delta===null || row.delta===undefined)continue;const k=Math.floor((row.time-1)/(interval*60));const old=buckets.get(k);buckets.set(k,{time:row.time,value:(old?.value||0)+row.delta});}const id='delta:'+pane;used.add(id);add(id,pane,'delta').setData([...buckets.values()].sort((a,b)=>a.time-b.time).map(r=>({...r,color:r.value>=0?'#32b5a045':'#ef596445'})));}
   }
   for(const [id,s] of series)if(!used.has(id)){chart.removeSeries(s);series.delete(id);names.delete(id);}
   const hasData=points.length>0 || oi.length>0 || (options.main||[]).length>0;if(hasData && (reset || !hadData))chart.timeScale().fitContent();hadData=hasData;
   if(reset)hovered=null;let top=7;for(const [i,pane] of chart.panes().entries()){headers[i].style.top=top+'px';header(i,hovered?hovered.get(i):latest.get(i));top+=pane.getHeight();}
   Object.assign(host.dataset,{cvdPoints:String(points.length),mainCvdPoints:String((options.main||[]).length),oiSamples:String(oi.length),segments:String(cvdGroups),oiSegments:String(lineGroups(oi,maxOiGap).length),priceBars:String(options.style==='price'?candles.length:0),unit,panes:String(chart.panes().length),indicatorBars:String(barCount),style:options.style,delta:String(!!options.showDelta)});
  },
  dispose(){chart.remove();headers.forEach(h=>h.remove());}
 };
}
