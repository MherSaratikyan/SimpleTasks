// Charts for the statistics page. Colours come from CSS variables on .viz-root, validated
// for both themes, and every chart is redrawn when the theme changes.
(function () {
    const source = document.getElementById('stats-data');
    const root = document.querySelector('.viz-root');
    if (!source || !root || !window.Chart) {
        return;
    }
    const data = JSON.parse(source.textContent);
    const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
    let charts = [];

    function tokens() {
        const style = getComputedStyle(root);
        const read = name => style.getPropertyValue(name).trim();
        return {
            surface: read('--chart-surface'),
            text: read('--chart-text'),
            muted: read('--chart-muted'),
            grid: read('--chart-grid'),
            series: [1, 2, 3, 4].map(slot => read(`--series-${slot}`))
        };
    }

    function base(t) {
        return {
            responsive: true,
            maintainAspectRatio: false,
            animation: reduceMotion ? false : { duration: 300 },
            font: { family: 'Inter, system-ui, sans-serif' },
            plugins: {
                legend: {
                    position: 'top',
                    align: 'start',
                    labels: { color: t.text, usePointStyle: true, boxWidth: 10, boxHeight: 10, padding: 16 }
                },
                tooltip: {
                    backgroundColor: t.surface,
                    titleColor: t.muted,
                    bodyColor: t.text,
                    borderColor: t.grid,
                    borderWidth: 1,
                    padding: 10,
                    usePointStyle: true,
                    boxPadding: 6,
                    // Value first, series second: the reader already knows the series.
                    callbacks: { label: item => ` ${item.formattedValue}  ${item.dataset.label ?? ''}` }
                }
            },
            scales: {
                x: { grid: { display: false }, ticks: { color: t.muted, maxRotation: 0, autoSkipPadding: 12 }, border: { color: t.grid } },
                y: { beginAtZero: true, grid: { color: t.grid }, ticks: { color: t.muted, precision: 0 }, border: { display: false } }
            }
        };
    }

    // Writes each bar's value at its end when it fits, so values never depend on colour or hover alone.
    const barValues = {
        id: 'barValues',
        afterDatasetsDraw(chart) {
            const { ctx } = chart;
            const t = tokens();
            ctx.save();
            ctx.fillStyle = t.text;
            ctx.font = '600 12px Inter, system-ui, sans-serif';
            ctx.textBaseline = 'middle';
            chart.getDatasetMeta(0).data.forEach((bar, index) => {
                const value = chart.data.datasets[0].data[index];
                ctx.fillText(String(value), bar.x + 8, bar.y);
            });
            ctx.restore();
        }
    };

    function draw() {
        charts.forEach(chart => chart.destroy());
        const t = tokens();
        const [blue, orange, aqua, yellow] = t.series;

        const flow = base(t);
        flow.interaction = { mode: 'index', intersect: false };
        charts.push(new Chart(document.getElementById('chart-flow'), {
            type: 'line',
            data: {
                labels: data.days,
                datasets: [
                    { label: 'Opened', data: data.opened, borderColor: blue, backgroundColor: blue, pointStyle: 'line' },
                    { label: 'Closed', data: data.closed, borderColor: orange, backgroundColor: orange, pointStyle: 'line' }
                ].map(series => ({ ...series, borderWidth: 2, tension: 0, pointRadius: 0, pointHoverRadius: 5, pointHitRadius: 12 }))
            },
            options: flow
        }));

        const moves = base(t);
        moves.interaction = { mode: 'index', intersect: false };
        moves.scales.x.stacked = true;
        moves.scales.y.stacked = true;
        const statuses = [
            ['To Do', data.toDo, blue], ['In Progress', data.inProgress, orange],
            ['In Review', data.inReview, aqua], ['Done', data.done, yellow]
        ];
        charts.push(new Chart(document.getElementById('chart-moves'), {
            type: 'bar',
            data: {
                labels: data.days,
                datasets: statuses.map(([label, values, colour]) => ({
                    label, data: values, backgroundColor: colour, pointStyle: 'rect',
                    // A 2px surface gap between stacked segments instead of an outline.
                    borderColor: t.surface, borderWidth: { top: 2 }, borderSkipped: 'bottom',
                    borderRadius: 4, maxBarThickness: 28
                }))
            },
            options: moves
        }));

        const current = base(t);
        current.indexAxis = 'y';
        current.plugins.legend.display = false;
        current.layout = { padding: { right: 32 } };
        current.scales.x.grid = { color: t.grid };
        current.scales.y.grid = { display: false };
        current.scales.y.ticks = { color: t.text };
        current.scales.x.ticks.precision = 0;
        charts.push(new Chart(document.getElementById('chart-current'), {
            type: 'bar',
            data: {
                labels: data.current.map(item => item.status),
                datasets: [{
                    label: 'Tickets',
                    data: data.current.map(item => item.count),
                    backgroundColor: [blue, orange, aqua, yellow],
                    borderRadius: 4,
                    borderSkipped: 'start',
                    maxBarThickness: 26
                }]
            },
            options: current,
            plugins: [barValues]
        }));
    }

    draw();
    // Redraw with the other theme's colours when the toggle or the device setting changes.
    new MutationObserver(draw).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    matchMedia('(prefers-color-scheme: dark)').addEventListener('change', draw);
})();
