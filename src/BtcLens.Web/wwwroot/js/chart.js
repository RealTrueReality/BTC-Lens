export function create(host, dotnet) {
    host.replaceChildren();
    const L = window.LightweightCharts;
    const chart = L.createChart(host, {
        autoSize: true,
        layout: { background: { color: '#13191d' }, textColor: '#86939b', fontFamily: 'Segoe UI, sans-serif', fontSize: 11, attributionLogo: true },
        grid: { vertLines: { color: '#20282d' }, horzLines: { color: '#20282d' } },
        rightPriceScale: { borderColor: '#283238', scaleMargins: { top: .12, bottom: .22 } },
        timeScale: { borderColor: '#283238', timeVisible: true, secondsVisible: false, rightOffset: 12,
            tickMarkFormatter: (t, kind) => {
                const options = kind === L.TickMarkType.Year ? { year: 'numeric' } : kind === L.TickMarkType.Month ? { month: 'short' } :
                    kind === L.TickMarkType.DayOfMonth ? { day: 'numeric' } : { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' };
                return new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', ...options }).format(new Date(t * 1000));
            } },
        crosshair: { mode: L.CrosshairMode.Normal, vertLine: { color: '#657078', labelBackgroundColor: '#34434a' }, horzLine: { color: '#657078', labelBackgroundColor: '#34434a' } },
        localization: { locale: 'zh-CN', timeFormatter: t => new Date(t * 1000).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }) }
    });
    const series = chart.addSeries(L.CandlestickSeries, { upColor: '#59b99e', downColor: '#dd787b', borderVisible: false, wickUpColor: '#59b99e', wickDownColor: '#dd787b', priceFormat: { type: 'price', precision: 1, minMove: .1 } });
    const volumes = chart.addSeries(L.HistogramSeries, { priceScaleId: 'volume', priceFormat: { type: 'volume' }, lastValueVisible: false, priceLineVisible: false });
    chart.priceScale('volume').applyOptions({ scaleMargins: { top: .83, bottom: 0 }, visible: false });
    let zones = [], watches = [], times = [], drawing = false, first = null, priceLines = [], initialized = false;
    const primitive = {
        attached({ requestUpdate }) { this.refresh = requestUpdate; },
        paneViews() {
            return [{ zOrder: () => 'bottom', renderer: () => ({
                draw: target => target.useMediaCoordinateSpace(({ context: ctx, mediaSize: size }) => {
                    const timeScale = chart.timeScale();
                    const drawZone = (z, manual) => {
                        const y1 = series.priceToCoordinate(z.upper), y2 = series.priceToCoordinate(z.lower);
                        if (y1 === null || y2 === null || y2 < 0 || y1 > size.height) return;
                        if (!times.length) return;
                        let logical = times.findIndex(t => t >= z.from);
                        if (logical < 0) logical = times.length - 1 + (z.from - times[times.length - 1]) / (times.length > 1 ? times[1] - times[0] : 300);
                        const coordinate = timeScale.logicalToCoordinate(logical);
                        if (coordinate === null) return;
                        const x = Math.max(0, coordinate);
                        if (x >= size.width) return;
                        const color = manual ? '#efb85d' : z.bias === 'Bullish' ? '#59b99e' : '#dd787b';
                        ctx.fillStyle = manual ? 'rgba(239,184,93,.085)' : z.bias === 'Bullish' ? 'rgba(89,185,158,.07)' : 'rgba(221,120,123,.07)';
                        ctx.fillRect(x, y1, size.width - x, Math.max(1, y2 - y1));
                        ctx.strokeStyle = color; ctx.lineWidth = manual ? 1.2 : .7;
                        ctx.setLineDash(manual ? [5, 4] : []);
                        ctx.strokeRect(x, y1, size.width - x, Math.max(1, y2 - y1)); ctx.setLineDash([]);
                        if (y2 - y1 > 12) { ctx.fillStyle = color; ctx.font = '10px Segoe UI, sans-serif'; ctx.fillText(z.label, x + 7, Math.min(y2 - 3, y1 + 14)); }
                    };
                    zones.forEach(z => drawZone(z, false)); watches.forEach(z => drawZone(z, true));
                    if (first !== null) { const y = series.priceToCoordinate(first); if (y !== null) { ctx.strokeStyle = '#efb85d'; ctx.setLineDash([3, 3]); ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(size.width, y); ctx.stroke(); ctx.setLineDash([]); } }
                })
            }) }];
        }
    };
    series.attachPrimitive(primitive);
    const pick = event => {
        if (!drawing) return;
        const rect = host.getBoundingClientRect();
        const x = event.clientX - rect.left, y = event.clientY - rect.top;
        if (x < 0 || x > rect.width - chart.priceScale('right').width() || y < 0 || y >= rect.height - chart.timeScale().height()) return;
        const value = series.coordinateToPrice(y); if (value === null) return;
        if (first === null) { first = value; primitive.refresh?.(); }
        else { const a = first; first = null; drawing = false; host.style.cursor = ''; primitive.refresh?.(); dotnet.invokeMethodAsync('Picked', a, value); }
    };
    // Receive both DOM clicks even when the chart library classifies a rapid pair as a double click.
    host.addEventListener('click', pick);
    return {
        update(candles, nextZones, nextWatches, levels, draw, reset) {
            times = candles.map(c => c.time);
            const range = initialized && !reset ? chart.timeScale().getVisibleLogicalRange() : null;
            series.setData(candles.map(c => ({ time: c.time, open: c.open, high: c.high, low: c.low, close: c.close })));
            volumes.setData(candles.map(c => ({ time: c.time, value: c.volume, color: c.close >= c.open ? 'rgba(89,185,158,.23)' : 'rgba(221,120,123,.23)' })));
            zones = nextZones; watches = nextWatches; drawing = draw;
            chart.applyOptions({ handleScroll: !draw, handleScale: !draw });
            if (!draw) first = null;
            host.style.cursor = draw ? 'crosshair' : '';
            priceLines.forEach(p => series.removePriceLine(p));
            priceLines = levels.map(l => series.createPriceLine({ price: l.price, color: '#71828d', lineWidth: 1, lineStyle: L.LineStyle.Dashed, axisLabelVisible: false, title: l.name }));
            if (range) chart.timeScale().setVisibleLogicalRange(range);
            else if (candles.length) chart.timeScale().setVisibleLogicalRange({ from: Math.max(0, candles.length - 115), to: candles.length + 12 });
            initialized = true; primitive.refresh?.();
        },
        dispose() { host.removeEventListener('click', pick); chart.remove(); }
    };
}
