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
    let hadData = false;
    function getLine(id, pane, color) {
        if (!lines.has(id)) lines.set(id, chart.addSeries(L.LineSeries, {
            color, lineWidth: 2, priceLineVisible: false, lastValueVisible: true, pointMarkersVisible: true, pointMarkersRadius: 2,
            priceFormat: { type: 'price', precision: pane === 2 ? 2 : 4, minMove: pane === 2 ? .01 : .0001 }
        }, pane));
        return lines.get(id);
    }
    // Keep all three panes mounted even while the collector has no historical samples.
    getLine('empty-spot', 0, '#59b99e'); getLine('empty-perp', 1, '#e9ad62'); getLine('empty-oi', 2, '#7b9fd6');
    function sample(rows, minutes) {
        const buckets = new Map();
        for (const row of rows) buckets.set(Math.floor(row.time / (minutes * 60)), { time: row.time, value: row.value });
        return [...buckets.values()].sort((a, b) => a.time - b.time);
    }
    return {
        update(points, oi, interval, reset, maxOiGap = 30) {
            const used = new Set(['empty-spot', 'empty-perp', 'empty-oi']);
            for (const instrument of ['BTC-USDT', 'BTC-USDT-SWAP']) {
                const groups = new Map();
                for (const point of points.filter(p => p.instrument === instrument)) {
                    if (!groups.has(point.segmentId)) groups.set(point.segmentId, []);
                    groups.get(point.segmentId).push(point);
                }
                for (const [id, rows] of [...groups.entries()].slice(-6)) {
                    const key = 'cvd-' + id; used.add(key);
                    getLine(key, instrument === 'BTC-USDT' ? 0 : 1, instrument === 'BTC-USDT' ? '#59b99e' : '#e9ad62').setData(sample(rows, interval));
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
                getLine(key, 2, '#7b9fd6').setData(sample(rows, interval));
            }
            for (const [key, line] of lines) if (!used.has(key)) { chart.removeSeries(line); lines.delete(key); }
            const hasData = points.length > 0 || oi.length > 0;
            if (hasData && (reset || !hadData)) chart.timeScale().fitContent();
            hadData = hasData;
            host.dataset.cvdPoints = String(points.length);
            host.dataset.oiSamples = String(oi.length);
            host.dataset.segments = String([...used].filter(k => k.startsWith('cvd-')).length);
            host.dataset.oiSegments = String(oiGroups.slice(-6).length);
        },
        dispose() { chart.remove(); }
    };
}
