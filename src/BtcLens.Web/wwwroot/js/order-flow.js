export function create(host) {
    host.replaceChildren();
    const L = window.LightweightCharts;
    const chart = L.createChart(host, {
        autoSize: true,
        layout: { background: { color: '#13191d' }, textColor: '#86939b', fontSize: 11,
            attributionLogo: true, panes: { separatorColor: '#34434a', separatorHoverColor: '#53616a' } },
        grid: { vertLines: { color: '#20282d' }, horzLines: { color: '#20282d' } },
        rightPriceScale: { borderColor: '#283238', minimumWidth: 78 },
        timeScale: { borderColor: '#283238', timeVisible: true, secondsVisible: true, rightOffset: 2,
            tickMarkFormatter: (t, kind) => new Date(t * 1000).toLocaleTimeString('zh-CN', { timeZone: 'Asia/Shanghai', hour: '2-digit', minute: '2-digit', hourCycle: 'h23', ...(kind === L.TickMarkType.TimeWithSeconds ? {second:'2-digit'} : {}) }) },
        localization: { locale: 'zh-CN', timeFormatter: t => new Date(t * 1000).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }) }
    });
    const lines = new Map();
    const histograms = new Map();
    const price = chart.addSeries(L.CandlestickSeries,{upColor:'#59b99e',downColor:'#dd787b',borderVisible:false,wickUpColor:'#59b99e',wickDownColor:'#dd787b',priceLineVisible:false,lastValueVisible:true},0);
    const readout=document.createElement('div');readout.className='flow-readout';host.append(readout);
    const paneLabels=['价格 · OKX 永续 · USDT','现货 CVD / Delta','永续 CVD / Delta','OI · OKX 永续 · BTC'].map(name=>{
        const element=document.createElement('div');element.className='flow-pane-label';element.textContent=name;host.append(element);return element;
    });
    const values=new Map();let currentUnit='BTC';
    chart.subscribeCrosshairMove(event=>{
        if(!event.time){readout.textContent='移动光标查看同一时刻的价格、CVD 与 OI';return;}
        const parts=[new Date(event.time*1000).toLocaleTimeString('zh-CN',{timeZone:'Asia/Shanghai'})];
        const candle=event.seriesData.get(price);if(candle)parts.push('价格 '+candle.close.toFixed(1)+' USDT');
        for(const [id,line] of lines){const item=event.seriesData.get(line);if(item && !id.startsWith('empty-'))parts.push((id.startsWith('oi-')?'OKX OI':values.get(id)||'CVD')+' '+item.value.toFixed(4)+' '+(id.startsWith('oi-')?'BTC':currentUnit));}
        for(const [id,line] of histograms){const item=event.seriesData.get(line);if(item)parts.push((values.get(id)||'')+' Delta '+item.value.toFixed(4)+' '+currentUnit);}
        readout.textContent=parts.join(' · ');
    });
    let hadData = false;
    function getLine(id, pane, color) {
        if (!lines.has(id)) lines.set(id, chart.addSeries(L.LineSeries, {
            color, lineWidth: 2, priceLineVisible: false, lastValueVisible: true, pointMarkersVisible: true, pointMarkersRadius: 2,
            priceFormat: { type: 'price', precision: pane === 3 ? 2 : 4, minMove: pane === 3 ? .01 : .0001 }
        }, pane));
        return lines.get(id);
    }
    // Shared price / spot / perpetual / OI time axis, including empty collectors.
    getLine('empty-spot', 1, '#59b99e'); getLine('empty-perp', 2, '#e9ad62'); getLine('empty-oi', 3, '#7b9fd6');
    function deltaLine(id,pane){
        if(!histograms.has(id))histograms.set(id,chart.addSeries(L.HistogramSeries,{priceScaleId:'delta-'+pane,priceLineVisible:false,lastValueVisible:false,priceFormat:{type:'volume'}},pane));
        return histograms.get(id);
    }
    function sample(rows, minutes) {
        const buckets = new Map();
        for (const row of rows) buckets.set(Math.floor((row.time - 1) / (minutes * 60)), { time: row.time, value: row.value });
        return [...buckets.values()].sort((a, b) => a.time - b.time);
    }
    return {
        update(points, oi, interval, reset, maxOiGap = 30, candles = [], unit = 'BTC') {
            currentUnit=unit;price.setData(candles);
            const used = new Set(['empty-spot', 'empty-perp', 'empty-oi']);
            for (const instrument of ['BTC-USDT', 'BTC-USDT-SWAP']) {
                const groups = new Map();
                for (const point of points.filter(p => p.instrument === instrument)) {
                    if (!groups.has(point.segmentId)) groups.set(point.segmentId, []);
                    groups.get(point.segmentId).push(point);
                }
                for (const [id, rows] of [...groups.entries()].slice(-6)) {
                    const key = 'cvd-' + id; used.add(key);
                    const pane=instrument === 'BTC-USDT'?1:2;
                    const line=getLine(key,pane,instrument === 'BTC-USDT'?'#59b99e':'#e9ad62');
                    line.applyOptions({priceFormat:{type:'price',precision:unit==='BTC'?4:2,minMove:unit==='BTC'?.0001:.01}});
                    line.setData(sample(rows, interval));values.set(key,(rows[0]?.label||'')+' '+(instrument === 'BTC-USDT'?'现货 CVD':'永续 CVD'));
                    const buckets=new Map();for(const row of rows){if(row.delta===null || row.delta===undefined)continue;const k=Math.floor((row.time-1)/(interval*60));const old=buckets.get(k);buckets.set(k,{time:row.time,value:(old?.value||0)+row.delta});}
                    deltaLine(key,pane).setData([...buckets.values()].sort((a,b)=>a.time-b.time).map(r=>({...r,color:r.value>=0?'#59b99e45':'#dd787b45'})));
                }
            }
            let oiGroups = [], group = [];
            for (const row of oi) {
                if (group.length && row.time - group.at(-1).time > maxOiGap) { oiGroups.push(group); group = []; }
                group.push(row);
            }
            if (group.length) oiGroups.push(group);
            for (const rows of oiGroups.slice(-6)) {
                const key = 'oi-' + rows[0].time; used.add(key);
                getLine(key, 3, '#7b9fd6').setData(sample(rows, interval));
            }
            for (const [key, line] of lines) if (!used.has(key)) { chart.removeSeries(line); lines.delete(key); }
            for (const [key, line] of histograms) if (!used.has(key)) { chart.removeSeries(line); histograms.delete(key);values.delete(key); }
            const hasData = points.length > 0 || oi.length > 0;
            if (hasData && (reset || !hadData)) chart.timeScale().fitContent();
            hadData = hasData;
            host.dataset.cvdPoints = String(points.length);
            host.dataset.oiSamples = String(oi.length);
            host.dataset.segments = String([...used].filter(k => k.startsWith('cvd-')).length);
            host.dataset.oiSegments = String(oiGroups.slice(-6).length);
            host.dataset.priceBars=String(candles.length);host.dataset.unit=unit;host.dataset.panes=String(chart.panes().length);
            let top=18;for(const [index,pane] of chart.panes().entries()){if(paneLabels[index]){paneLabels[index].style.top=top+'px';if(index===1 || index===2)paneLabels[index].textContent=(index===1?'现货':'永续')+' CVD / Delta · '+unit;}top+=pane.getHeight();}
        },
        dispose() { chart.remove();readout.remove();paneLabels.forEach(e=>e.remove()); }
    };
}
