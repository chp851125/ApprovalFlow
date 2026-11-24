// 儀表板圖表（ECharts，本機 wwwroot/lib/echarts，不使用 CDN）
(function () {
    'use strict';
    var dataEl = document.getElementById('dashboardData');
    if (!dataEl || !window.echarts) return;
    var data = JSON.parse(dataEl.textContent || '{}');

    var palette = ['#3d8bfd', '#20c997', '#fd7e14', '#6f42c1', '#dc3545', '#0dcaf0', '#ffc107', '#6c757d'];
    var font = 'system-ui, "Microsoft JhengHei", "Noto Sans TC", sans-serif';
    var base = { color: palette, textStyle: { fontFamily: font }, animationDuration: 500 };
    var fmt = new Intl.NumberFormat('zh-TW', { maximumFractionDigits: 1 });

    function isEmpty(d) {
        if (!d) return true;
        if (Array.isArray(d)) return d.length === 0 || d.every(function (x) { return !x.value; });
        return !d.series || d.series.every(function (s) { return s.data.every(function (v) { return !v; }); });
    }

    function categoryOption(d, horizontal, stacked) {
        var valueAxis = { type: 'value', axisLabel: { formatter: function (v) { return fmt.format(v); } }, splitLine: { lineStyle: { color: '#eef0f3' } } };
        var catAxis = { type: 'category', data: d.categories, axisTick: { show: false }, axisLabel: { interval: 0, hideOverlap: true } };
        return Object.assign({}, base, {
            tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' }, valueFormatter: function (v) { return fmt.format(v); } },
            legend: d.series.length > 1 ? { top: 0, type: 'scroll' } : undefined,
            grid: { left: 8, right: 16, top: d.series.length > 1 ? 36 : 12, bottom: 8, containLabel: true },
            xAxis: horizontal ? valueAxis : catAxis,
            yAxis: horizontal ? catAxis : valueAxis,
            series: d.series.map(function (s) {
                return { name: s.name, type: 'bar', stack: stacked ? 'total' : undefined, data: s.data, barMaxWidth: 36, emphasis: { focus: 'series' } };
            })
        });
    }

    function pieOption(list, donut) {
        return Object.assign({}, base, {
            tooltip: { trigger: 'item', formatter: function (p) { return p.name + '：' + fmt.format(p.value) + '（' + p.percent + '%）'; } },
            legend: { bottom: 0, type: 'scroll' },
            series: [{
                type: 'pie', radius: donut ? ['45%', '70%'] : '65%', center: ['50%', '45%'],
                itemStyle: { borderColor: '#fff', borderWidth: 2 },
                label: { formatter: '{b}\n{d}%' },
                data: list.map(function (x) { return { name: x.name, value: x.value }; })
            }]
        });
    }

    function hbarFromList(list) {
        return categoryOption({ categories: list.map(function (x) { return x.name; }), series: [{ name: '時數', data: list.map(function (x) { return x.value; }) }] }, true, false);
    }

    var charts = [];
    document.querySelectorAll('[data-chart]').forEach(function (el) {
        var d = data[el.getAttribute('data-chart')];
        if (isEmpty(d)) {
            el.classList.add('chart-empty');
            el.textContent = '目前沒有資料';
            return;
        }
        var kind = el.getAttribute('data-kind');
        var option =
            kind === 'pie' ? pieOption(d, false) :
            kind === 'donut' ? pieOption(d, true) :
            kind === 'hbar' ? hbarFromList(d) :
            kind === 'hstack' ? categoryOption(d, true, true) :
            kind === 'bar' ? categoryOption(d, false, false) :
            categoryOption(d, false, true);
        var chart = echarts.init(el, null, { renderer: 'canvas' });
        chart.setOption(option);
        charts.push(chart);
    });

    var timer;
    window.addEventListener('resize', function () {
        clearTimeout(timer);
        timer = setTimeout(function () { charts.forEach(function (c) { c.resize(); }); }, 150);
    });
})();
