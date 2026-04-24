window.TradingChart = {
    _charts: {},

    createChart: function (elementId) {
        const container = document.getElementById(elementId);
        if (!container) return;

        if (this._charts[elementId]) {
            this._charts[elementId].chart.remove();
            delete this._charts[elementId];
        }

        const chart = LightweightCharts.createChart(container, {
            width: container.clientWidth,
            height: 420,
            layout: {
                background: { color: '#1a1a2e' },
                textColor: '#e0e0e0'
            },
            grid: {
                vertLines: { color: '#2a2a3e' },
                horzLines: { color: '#2a2a3e' }
            },
            crosshair: {
                mode: LightweightCharts.CrosshairMode.Normal
            },
            rightPriceScale: {
                borderColor: '#3a3a4e'
            },
            timeScale: {
                borderColor: '#3a3a4e',
                timeVisible: true,
                secondsVisible: false
            }
        });

        const candleSeries = chart.addCandlestickSeries({
            upColor: '#26a69a',
            downColor: '#ef5350',
            borderDownColor: '#ef5350',
            borderUpColor: '#26a69a',
            wickDownColor: '#ef5350',
            wickUpColor: '#26a69a'
        });

        this._charts[elementId] = { chart: chart, series: candleSeries };

        const resizeObserver = new ResizeObserver(entries => {
            for (const entry of entries) {
                chart.applyOptions({ width: entry.contentRect.width });
            }
        });
        resizeObserver.observe(container);
    },

    setKlineData: function (elementId, data) {
        const entry = this._charts[elementId];
        if (!entry || !data) return;

        const mapped = data.map(d => ({
            time: Math.floor(new Date(d.openTime).getTime() / 1000),
            open: d.open,
            high: d.high,
            low: d.low,
            close: d.close
        }));

        entry.series.setData(mapped);
        entry.chart.timeScale().fitContent();
    },

    addMarkers: function (elementId, markers) {
        const entry = this._charts[elementId];
        if (!entry || !markers || markers.length === 0) return;

        const mapped = markers.map(m => ({
            time: Math.floor(new Date(m.createdAt).getTime() / 1000),
            position: m.side === 'Buy' ? 'belowBar' : 'aboveBar',
            color: m.side === 'Buy' ? '#26a69a' : '#ef5350',
            shape: m.side === 'Buy' ? 'arrowUp' : 'arrowDown',
            text: m.side === 'Buy' ? 'BUY' : 'SELL'
        })).sort((a, b) => a.time - b.time);

        entry.series.setMarkers(mapped);
    },

    destroyChart: function (elementId) {
        const entry = this._charts[elementId];
        if (!entry) return;
        entry.chart.remove();
        delete this._charts[elementId];
    }
};
