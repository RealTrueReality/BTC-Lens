export function create(host, dotnet) {
    host.replaceChildren();
    const L = window.LightweightCharts;
    let focusBounds = null,evidenceFocus=null;
    const chart = L.createChart(host, {
        autoSize: true,
        layout: { background: { color: '#13191d' }, textColor: '#86939b', fontFamily: 'Segoe UI, sans-serif', fontSize: 11, attributionLogo: true },
        grid: { vertLines: { color: '#20282d' }, horzLines: { color: '#20282d' } },
        rightPriceScale: { borderColor: '#283238', scaleMargins: { top: .12, bottom: .22 } },
        timeScale: { borderColor: '#283238', timeVisible: true, secondsVisible: false, rightOffset: 12, minBarSpacing: .02,
            tickMarkFormatter: (t, kind) => new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai',
                ...(kind === L.TickMarkType.Year ? { year: 'numeric' } : kind === L.TickMarkType.Month ? { month: 'short' } :
                    kind === L.TickMarkType.DayOfMonth ? { day: 'numeric' } : { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }) }).format(new Date(t * 1000)) },
        crosshair: { mode: L.CrosshairMode.Normal, vertLine: { color: '#657078', labelBackgroundColor: '#34434a' }, horzLine: { color: '#657078', labelBackgroundColor: '#34434a' } },
        localization: { locale: 'zh-CN', timeFormatter: t => new Date(t * 1000).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }) }
    });
    const series = chart.addSeries(L.CandlestickSeries, { upColor: '#59b99e', downColor: '#dd787b', borderVisible: false,
        wickUpColor: '#59b99e', wickDownColor: '#dd787b', priceFormat: { type: 'price', precision: 1, minMove: .1 },
        autoscaleInfoProvider: original => {
            const info = original(); if (!info || !focusBounds) return info;
            return { ...info, priceRange: { minValue: Math.min(info.priceRange.minValue, focusBounds.lower), maxValue: Math.max(info.priceRange.maxValue, focusBounds.upper) } };
        } });
    const volumes = chart.addSeries(L.HistogramSeries, { priceScaleId: 'volume', priceFormat: { type: 'volume' }, lastValueVisible: false, priceLineVisible: false });
    chart.priceScale('volume').applyOptions({ scaleMargins: { top: .83, bottom: 0 }, visible: false });
    const labels = document.createElement('div'); labels.className = 'chart-object-labels'; host.append(labels);
    const buttons = new Map(), labelPositions = new Map(), palette = ['#6da3b8', '#a594cf', '#d8b369', '#64a99c', '#bd879e'];
    let candles = [], times = [], zones = [], watches = [], profiles = [], drawings = [], objects = [],
        tool = 'select', selection = null, scope = '', first = null, preview = null, drag = null, pointerStart = null,
        suppressClick = false, frame = 0, disposed = false, priceLines = [], vwapSeries = [], initialized = false;
    const invoke = (method, ...args) => dotnet.invokeMethodAsync(method, ...args).catch(error => { if (!disposed) console.error(error); });
    const plot = () => ({ width: host.clientWidth - chart.priceScale('right').width(), height: host.clientHeight - chart.timeScale().height() });
    const step = () => candles[0] ? candles[0].closeTime - candles[0].time : 300;
    const logicalAt = time => {
        if (!times.length) return null;
        if (time <= times[0]) return (time - times[0]) / step();
        if (time >= times.at(-1)) return times.length - 1 + (time - times.at(-1)) / step();
        let a = 0, b = times.length - 1;
        while (b - a > 1) { const m = (a + b) >> 1; if (times[m] <= time) a = m; else b = m; }
        return a + (time - times[a]) / (times[b] - times[a]);
    };
    const xAt = time => {
        const logical = logicalAt(time); if(logical===null) return null;
        // A range endpoint can fall between chart bars; interpolate integer bar coordinates.
        const index=Math.floor(logical),fraction=logical-index;
        const a=chart.timeScale().logicalToCoordinate(index),b=chart.timeScale().logicalToCoordinate(index+1);
        return a===null||b===null?null:a+(b-a)*fraction;
    };
    const pointAt = (x, y) => {
        const logical = chart.timeScale().coordinateToLogical(x), price = series.coordinateToPrice(y);
        if (logical === null || price === null || !times.length || price <= 0) return null;
        const index = Math.round(logical);
        const time = index < 0 ? times[0] + index * step() : index >= times.length ? times.at(-1) + (index - times.length + 1) * step() : times[index];
        return { time, price: Math.round(price * 10) / 10, x, y };
    };
    const geometry = obj => {
        const size = plot(), x1 = xAt(obj.start), x2 = obj.extend ? size.width : xAt(obj.end);
        let lastPrice = obj.price2 ?? obj.lower;
        if(obj.drawingKind === 'TrendLine' && obj.extend) {
            const right = pointAt(size.width, size.height / 2);
            if(!right || obj.end === obj.start) return null;
            lastPrice = obj.price1 + (obj.price2 - obj.price1) * (right.time - obj.start) / (obj.end - obj.start);
        }
        const y1 = series.priceToCoordinate(obj.price1 ?? obj.upper), y2 = series.priceToCoordinate(lastPrice);
        if ([x1,x2,y1,y2].some(x => x === null)) return null;
        return { x1, x2, y1, y2, left: Math.min(x1,x2), right: Math.max(x1,x2), top: Math.min(y1,y2), bottom: Math.max(y1,y2) };
    };
    const keyOf = obj => obj.kind + ':' + obj.id;
    const isSelected = obj => selection?.kind === obj.kind && selection?.id === obj.id;
    const editable = obj => obj.kind === 'drawing' || obj.kind === 'watch';
    const select = obj => { selection = obj ? {kind:obj.kind,id:obj.id} : null; host.focus({preventScroll:true}); primitive.refresh?.(); invoke('Selected', obj?.kind || '', obj?.id || ''); };
    const buildObjects = () => {
        objects = [
            ...(evidenceFocus?[{...evidenceFocus,kind:'evidence',color:'#f0c76b',extend:false,drawingKind:evidenceFocus.lower===evidenceFocus.upper?'HorizontalLine':null,price1:evidenceFocus.upper,price2:evidenceFocus.lower}]:[]),
            ...zones.map(z => ({...z,kind:'zone',extend:true,color:z.bias === 'Bullish' ? '#59b99e' : '#dd787b'})),
            ...profiles.map(p => ({...p,kind:'profile',color:palette[[...p.id].reduce((n,c)=>n+c.charCodeAt(0),0) % palette.length],lower:p.rows[0]?.lower,upper:p.rows.at(-1)?.upper})),
            ...drawings.map(d => ({...d,kind:'drawing',color:'#91b7db',lower:Math.min(d.price1,d.price2),upper:Math.max(d.price1,d.price2),extend:d.drawingKind === 'HorizontalLine'})),
            ...watches.map(w => ({...w,kind:'watch',extend:true,color:'#efb85d'}))
        ];
        if (drag) objects = objects.map(o => keyOf(o) === keyOf(drag.original) ? drag.changed : o);
    };
    const syncLabels = () => {
        frame = 0; if (disposed) return;
        const size = plot(), keep = new Set(), slots = []; let moved = false;
        for (const obj of objects) {
            const g = geometry(obj);
            if (!g || g.right < 0 || g.left > size.width || g.bottom < 0 || g.top > size.height) continue;
            const key = keyOf(obj); keep.add(key); let button = buttons.get(key);
            if (!button) { button = document.createElement('button'); button.type = 'button'; button.className = 'chart-object-label';
                button.addEventListener('click', e => { if (tool !== 'select') return; e.stopPropagation(); select(objects.find(o => keyOf(o) === key)); });
                buttons.set(key,button); labels.append(button); }
            button.dataset.objectId = obj.id; button.dataset.kind = obj.kind;
            button.textContent = obj.label; button.title = `${obj.label} · ${obj.lower?.toFixed(1)} — ${obj.upper?.toFixed(1)} · 点击选择`;
            button.setAttribute('aria-label', '选择 ' + obj.label); button.style.color = obj.color;
            button.classList.toggle('selected',isSelected(obj));
            const left = Math.min(Math.max(4,g.left + 5),Math.max(4,size.width - 118));
            let top = Math.min(Math.max(3,g.top - 20),Math.max(3,size.height - 22));
            for (let attempt = 0; attempt < 8 && slots.some(s => left < s.left + 118 && left + 118 > s.left && top < s.top + 20 && top + 20 > s.top); attempt++) top = Math.min(size.height - 22, top + 21);
            slots.push({left,top}); button.style.left = left + 'px'; button.style.top = top + 'px';
            const previous = labelPositions.get(key); if (!previous || Math.abs(previous.left-left)>.5 || Math.abs(previous.top-top)>.5) moved=true;
            labelPositions.set(key,{left,top});
            button.style.pointerEvents = tool === 'select' ? 'auto' : 'none';
            button.dataset.x = String((Math.max(0,g.left) + Math.min(size.width,g.right))/2);
            button.dataset.y = String((Math.max(0,g.top) + Math.min(size.height,g.bottom))/2);
        }
        for (const [key,button] of buttons) if (!keep.has(key)) { button.remove(); buttons.delete(key); labelPositions.delete(key); }
        host.dataset.profiles = JSON.stringify(profiles.map(p=>p.id)); host.dataset.drawings = JSON.stringify(drawings.map(d=>d.id));
        host.dataset.watches = JSON.stringify(watches.map(w=>w.id)); host.dataset.zones = JSON.stringify(zones.map(z=>z.id));
        host.dataset.candleStart = String(times[0] || ''); host.dataset.candleCount = String(times.length);
        if(moved) primitive.refresh?.();
    };
    const scheduleLabels = () => { if (!frame && !disposed) frame = requestAnimationFrame(syncLabels); };
    const drawObject = (ctx, size, obj) => {
        const g = geometry(obj); if (!g || g.right < 0 || g.left > size.width || g.bottom < 0 || g.top > size.height) return;
        const label = labelPositions.get(keyOf(obj));
        if(label && Math.abs(label.top+20-g.top)>10) {
            ctx.strokeStyle=obj.color+'80';ctx.lineWidth=.7;ctx.setLineDash([2,3]);ctx.beginPath();
            ctx.moveTo(label.left+6,label.top+10);ctx.lineTo(Math.max(4,Math.min(size.width-4,g.left+10)),Math.max(0,g.top));ctx.stroke();ctx.setLineDash([]);
        }
        ctx.strokeStyle = obj.color; ctx.lineWidth = isSelected(obj) ? 2 : 1;
        ctx.setLineDash(obj.kind === 'watch' || obj.kind === 'profile' ? [5,4] : []);
        if (obj.drawingKind === 'HorizontalLine' || obj.drawingKind === 'TrendLine') {
            if(obj.tolerance) {
                const offset=Math.abs(series.priceToCoordinate(obj.price1 + obj.tolerance)-g.y1);
                ctx.fillStyle=obj.color+'18'; ctx.beginPath();ctx.moveTo(g.x1,g.y1-offset);ctx.lineTo(g.x2,g.y2-offset);
                ctx.lineTo(g.x2,g.y2+offset);ctx.lineTo(g.x1,g.y1+offset);ctx.closePath();ctx.fill();
            }
            ctx.beginPath(); ctx.moveTo(obj.drawingKind === 'HorizontalLine' ? 0 : g.x1,g.y1); ctx.lineTo(g.x2,g.y2); ctx.stroke();
        } else {
            ctx.fillStyle = obj.color + (isSelected(obj) ? '22' : '12');
            ctx.fillRect(g.left,g.top,g.right-g.left,Math.max(1,g.bottom-g.top));
            ctx.strokeRect(g.left,g.top,g.right-g.left,Math.max(1,g.bottom-g.top));
        }
        ctx.setLineDash([]);
        if (obj.kind === 'profile' && obj.rows?.length) {
            const maxVolume = Math.max(...obj.rows.map(r=>r.volumeBtc));
            const width = Math.min(130,Math.max(20,(g.right-g.left)*.45)), edge = Math.min(size.width,g.right);
            for (let i=0;i<obj.rows.length;i++) { const r=obj.rows[i], y1=series.priceToCoordinate(r.upper), y2=series.priceToCoordinate(r.lower);
                if(y1===null || y2===null || maxVolume<=0) continue;
                const w=width*r.volumeBtc/maxVolume; ctx.fillStyle=obj.color+(i===obj.pocRow?'a0':i>=obj.vaLowRow && i<=obj.vaHighRow?'65':'30');
                ctx.fillRect(edge-w,y1,w,Math.max(1,y2-y1)); }
            for(const [name,price] of [['VAH',obj.vah],['POC',obj.poc],['VAL',obj.val]]) { if(price===null) continue;
                const y=series.priceToCoordinate(price); if(y===null) continue;
                ctx.strokeStyle=obj.color; ctx.lineWidth=name==='POC'?1.5:.8; ctx.setLineDash(name==='POC'?[]:[4,3]);
                ctx.beginPath(); ctx.moveTo(Math.max(0,g.left),y); ctx.lineTo(edge,y); ctx.stroke(); ctx.setLineDash([]);
                ctx.fillStyle=obj.color; ctx.font='10px Segoe UI, sans-serif'; ctx.fillText(name,Math.max(4,edge-33),y-3); }
        }
        if (isSelected(obj) && editable(obj)) {
            const handles = obj.drawingKind==='HorizontalLine' ? [[Math.max(12,g.x1),g.y1]] : [[g.x1,g.y1],[obj.drawingKind==='TrendLine'?xAt(obj.end):g.x2,obj.drawingKind==='TrendLine'?series.priceToCoordinate(obj.price2):g.y2]];
            ctx.fillStyle='#13191d'; ctx.strokeStyle=obj.color;
            for (const [x,y] of handles) { ctx.fillRect(x-4,y-4,8,8); ctx.strokeRect(x-4,y-4,8,8); }
        }
    };
    const primitive = {
        attached({requestUpdate}) { this.refresh=requestUpdate; },
        paneViews() { return [{ zOrder:()=> 'top', renderer:()=> ({ draw: target=>target.useMediaCoordinateSpace(({context:ctx,mediaSize:size})=> {
            for(const obj of objects) if(!isSelected(obj)) drawObject(ctx,size,obj);
            for(const obj of objects) if(isSelected(obj)) drawObject(ctx,size,obj);
            if(preview) drawObject(ctx,size,preview);
            if(first && tool==='profile') { const x=xAt(first.time); if(x!==null) { ctx.strokeStyle='#efb85d'; ctx.setLineDash([3,3]); ctx.beginPath(); ctx.moveTo(x,0); ctx.lineTo(x,size.height); ctx.stroke(); ctx.setLineDash([]); } }
            scheduleLabels();
        }) }) }]; }
    };
    series.attachPrimitive(primitive);
    const distanceToLine = (p,g) => { const dx=g.x2-g.x1,dy=g.y2-g.y1,t=Math.max(0,Math.min(1,((p.x-g.x1)*dx+(p.y-g.y1)*dy)/(dx*dx+dy*dy || 1)));
        return Math.hypot(p.x-(g.x1+t*dx),p.y-(g.y1+t*dy)); };
    const hit = (p,selectedOnly=false) => {
        const ordered=[...objects].sort((a,b)=>Number(isSelected(b))-Number(isSelected(a)) || (a.kind==='zone'?1:0)-(b.kind==='zone'?1:0));
        for(const obj of ordered) { if(selectedOnly && !isSelected(obj)) continue; const g=geometry(obj); if(!g) continue;
            if(obj.drawingKind==='HorizontalLine') { if(Math.abs(p.y-g.y1)<=6) return {obj,handle:'move'}; continue; }
            if(isSelected(obj) && editable(obj)) {
                if(Math.hypot(p.x-g.x1,p.y-g.y1)<=9) return {obj,handle:'first'};
                const lastX=obj.drawingKind==='TrendLine'?xAt(obj.end):g.x2,lastY=obj.drawingKind==='TrendLine'?series.priceToCoordinate(obj.price2):g.y2;
                if(Math.hypot(p.x-lastX,p.y-lastY)<=9) return {obj,handle:'second'};
            }
            if(obj.drawingKind==='TrendLine') { if(distanceToLine(p,g)<=Math.max(6,obj.tolerance?Math.abs(series.priceToCoordinate(obj.price1+obj.tolerance)-g.y1):0)) return {obj,handle:'move'}; }
            else if(p.x>=Math.max(0,g.left)-3 && p.x<=g.right+3 && p.y>=g.top-4 && p.y<=g.bottom+4) return {obj,handle:'move'};
        } return null;
    };
    const coords = event => { const r=host.getBoundingClientRect(),p={x:event.clientX-r.left,y:event.clientY-r.top},size=plot();
        return p.x>=0 && p.x<=size.width && p.y>=0 && p.y<size.height ? p : null; };
    const setInteraction = () => { chart.applyOptions({handleScroll:tool==='select'&&!drag,handleScale:tool==='select'&&!drag}); host.style.cursor=tool==='select'?'':'crosshair'; };
    const cancel = () => { first=preview=drag=null; tool='select'; setInteraction(); primitive.refresh?.(); invoke('CancelTool'); };
    const changePayload = obj => ({id:obj.id||null,revision:obj.revision||null,kind:obj.kind==='watch'?'Watch':obj.drawingKind,
        start:Math.round(obj.start*1000),end:Math.round(obj.end*1000),price1:Math.round((obj.price1??obj.upper)*10)/10,
        price2:Math.round((obj.price2??obj.lower)*10)/10,scope});
    const pick = event => {
        if(suppressClick) {suppressClick=false;return;} const p=coords(event); if(!p) return; host.focus({preventScroll:true});
        if(tool==='select') { select(hit(p)?.obj); return; }
        const point=pointAt(p.x,p.y); if(!point) return;
        if(tool==='HorizontalLine') { const obj={drawingKind:tool,start:point.time,end:point.time,price1:point.price,price2:point.price}; cancel(); invoke('SaveObject',changePayload(obj)); return; }
        if(!first) { first=point; primitive.refresh?.(); return; }
        if(tool==='profile') {
            const a=Math.max(0,Math.min(times.length-1,Math.round(logicalAt(first.time)))), b=Math.max(0,Math.min(times.length-1,Math.round(logicalAt(point.time))));
            const closed=candles.slice(Math.min(a,b),Math.max(a,b)+1).filter(c=>c.confirmed);
            cancel(); if(closed.length) invoke('PickedRange',closed[0].time,closed.at(-1).closeTime); return;
        }
        const a=first.time<=point.time?first:point,b=first.time<=point.time?point:first;
        const obj={drawingKind:tool,start:a.time,end:b.time,price1:a.price,price2:b.price}; cancel(); invoke('SaveObject',changePayload(obj));
    };
    const down = event => {
        if(event.button!==0 || tool!=='select') return; const p=coords(event); if(!p) return; pointerStart=p; suppressClick=false;
        if(event.target.closest('.chart-object-label')) return;
        const h=hit(p,true); if(!h || !editable(h.obj)) return; const point=pointAt(p.x,p.y); if(!point) return;
        drag={original:{...h.obj},changed:{...h.obj},anchor:point,handle:h.handle};
        host.setPointerCapture(event.pointerId); event.preventDefault(); event.stopImmediatePropagation(); setInteraction();
    };
    const move = event => {
        const p=coords(event); if(!p) return;
        if(pointerStart && Math.hypot(p.x-pointerStart.x,p.y-pointerStart.y)>4) suppressClick=true;
        const point=pointAt(p.x,p.y); if(!point) return;
        if(drag) {
            const o=drag.original, p1=o.price1??o.upper,p2=o.price2??o.lower; let a=o.start,b=o.end,v1=p1,v2=p2;
            if(drag.handle==='first') {a=point.time;v1=point.price;} else if(drag.handle==='second') {b=point.time;v2=point.price;}
            else { const dt=point.time-drag.anchor.time,dp=point.price-drag.anchor.price; a+=dt;b+=dt;v1+=dp;v2+=dp; }
            if(a>b) { [a,b]=[b,a]; [v1,v2]=[v2,v1]; } if(v1<=0 || v2<=0) return;
            drag.changed={...o,start:a,end:b,price1:v1,price2:v2,lower:Math.min(v1,v2),upper:Math.max(v1,v2)};
            buildObjects(); primitive.refresh?.(); event.preventDefault(); return;
        }
        if(first && tool!=='select' && tool!=='profile') {
            preview={kind:'drawing',id:'preview',drawingKind:tool,label:'',color:'#91b7db',start:Math.min(first.time,point.time),end:Math.max(first.time,point.time),
                price1:first.time<=point.time?first.price:point.price,price2:first.time<=point.time?point.price:first.price}; primitive.refresh?.();
        }
        if(tool==='select') { const h=hit(p); host.style.cursor=h && isSelected(h.obj)&&editable(h.obj)?(h.handle==='move'?'move':'nwse-resize'):h?'pointer':''; }
    };
    const up = event => {
        pointerStart=null; if(!drag) return; const changed=drag.changed,original=drag.original; drag=null;
        if(host.hasPointerCapture(event.pointerId)) host.releasePointerCapture(event.pointerId); setInteraction();
        if(suppressClick && (changed.start!==original.start || changed.end!==original.end || (changed.price1??changed.upper)!==(original.price1??original.upper) || (changed.price2??changed.lower)!==(original.price2??original.lower))) invoke('SaveObject',changePayload(changed));
        else {buildObjects();primitive.refresh?.();}
    };
    const keys = event => {
        if(event.target.closest('input,textarea,select,[contenteditable=true]')) return;
        if(event.key==='Escape' && (tool!=='select'||first||drag)) {event.preventDefault();cancel();}
        else if((event.key==='Delete'||event.key==='Backspace') && host.contains(document.activeElement) && selection && selection.kind!=='zone') {event.preventDefault();invoke('DeleteObject',selection.kind,selection.id);}
    };
    const abortPointer = () => {pointerStart=null;drag=null;buildObjects();setInteraction();primitive.refresh?.();};
    host.addEventListener('click',pick); host.addEventListener('pointerdown',down,true); host.addEventListener('pointermove',move,true);
    host.addEventListener('pointerup',up,true); host.addEventListener('pointercancel',abortPointer); document.addEventListener('keydown',keys);
    return {
        update(nextCandles,nextZones,nextWatches,levels,nextTool,reset,nextProfiles,vwaps,nextDrawings,nextSelection,nextScope,nextEvidence=null) {
            const range=initialized&&!reset?chart.timeScale().getVisibleLogicalRange():null;
            const oldTimes=times;
            let shift=0;
            if(range && oldTimes.length) {
                const anchor=Math.max(0,Math.min(oldTimes.length-1,Math.round((range.from+range.to)/2)));
                const target=nextCandles.findIndex(c=>c.time===oldTimes[anchor]);
                if(target>=0)shift=target-anchor;
            }
            candles=nextCandles;times=candles.map(c=>c.time);zones=nextZones;watches=nextWatches;profiles=nextProfiles||[];drawings=nextDrawings||[];evidenceFocus=nextEvidence;
            if(reset || tool!==nextTool) {first=preview=drag=null;focusBounds=null;}
            tool=nextTool;selection=nextSelection;scope=nextScope;buildObjects();setInteraction();
            series.setData(candles.map(c=>({time:c.time,open:c.open,high:c.high,low:c.low,close:c.close})));
            volumes.setData(candles.map(c=>({time:c.time,value:c.volume,color:c.close>=c.open?'rgba(89,185,158,.23)':'rgba(221,120,123,.23)'})));
            priceLines.forEach(p=>series.removePriceLine(p));priceLines=levels.map(l=>series.createPriceLine({price:l.price,color:'#71828d',lineWidth:1,lineStyle:L.LineStyle.Dashed,axisLabelVisible:false,title:l.name}));
            vwapSeries.forEach(s=>chart.removeSeries(s));vwapSeries=[];
            for(const v of vwaps||[]) {const groups=new Map();for(const p of v.points) {if(!groups.has(p.segment))groups.set(p.segment,[]);groups.get(p.segment).push({time:p.time,value:p.value});}
                for(const points of groups.values()) { const line=chart.addSeries(L.LineSeries,{color:v.period==='day'?'#59b99e':v.period==='week'?'#d8b369':'#a594cf',lineWidth:1,
                    lineStyle:v.complete?L.LineStyle.Solid:L.LineStyle.Dashed,priceLineVisible:false,lastValueVisible:false,crosshairMarkerVisible:true,title:`${v.period} VWAP`,priceFormat:{type:'price',precision:1,minMove:.1}});
                    line.setData(points);vwapSeries.push(line); } }
            host.dataset.vwapSeries=String(vwapSeries.length);host.dataset.profileId=profiles[0]?.id||'';
            if(range)chart.timeScale().setVisibleLogicalRange({from:range.from+shift,to:range.to+shift});else if(candles.length)chart.timeScale().setVisibleLogicalRange({from:Math.max(0,candles.length-115),to:candles.length+12});
            initialized=true;primitive.refresh?.();scheduleLabels();
            host.dataset.evidenceId=evidenceFocus?.id||'';
        },
        focus(kind,id) {
            const obj=objects.find(o=>o.kind===kind&&o.id===id);if(!obj)return false; focusBounds={lower:obj.lower,upper:obj.upper};
            const a=logicalAt(obj.start),b=obj.extend?Math.max(a+10,times.length-1):logicalAt(obj.end),padding=Math.max(8,(b-a)*.15);
            chart.timeScale().setVisibleLogicalRange({from:a-padding,to:b+padding});primitive.refresh?.();scheduleLabels();return true;
        },
        dispose() {
            disposed=true;if(frame)cancelAnimationFrame(frame);
            host.removeEventListener('click',pick);host.removeEventListener('pointerdown',down,true);host.removeEventListener('pointermove',move,true);
            host.removeEventListener('pointerup',up,true);host.removeEventListener('pointercancel',abortPointer);document.removeEventListener('keydown',keys);chart.remove();labels.remove();buttons.clear();
        }
    };
}
