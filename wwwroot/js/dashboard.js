(function () {
  var root = document.getElementById('dashTrend');
  if (!root) return;

  var host = root.querySelector('#dashTrendGraph');
  var titleEl = root.querySelector('[data-dash-title]');
  var raw = root.getAttribute('data-trend');
  if (!host || !raw || typeof Chart === 'undefined') return;

  var trend;
  try {
    trend = JSON.parse(raw);
  } catch (e) {
    return;
  }

  var allLabels = Array.isArray(trend.labels) ? trend.labels : [];
  var allSeries = Array.isArray(trend.series) ? trend.series : [];
  if (!allLabels.length || !allSeries.length) return;

  var hidden = {};
  var range = parseInt(root.getAttribute('data-range') || '7', 10) || 7;
  var chart = null;

  function hexToRgba(hex, alpha) {
    var h = (hex || '#64748b').replace('#', '');
    if (h.length === 3) {
      h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    }
    var n = parseInt(h, 16);
    var r = (n >> 16) & 255;
    var g = (n >> 8) & 255;
    var b = n & 255;
    return 'rgba(' + r + ',' + g + ',' + b + ',' + alpha + ')';
  }

  function formatLabel(rawLabel, days) {
    if (!rawLabel) return '';
    var dt = new Date(rawLabel + 'T00:00:00');
    if (isNaN(dt.getTime())) return rawLabel;
    if (days <= 7) {
      return ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][dt.getDay()];
    }
    var mm = String(dt.getMonth() + 1).padStart(2, '0');
    var dd = String(dt.getDate()).padStart(2, '0');
    return mm + '/' + dd;
  }

  function sliceTrend(days) {
    var take = Math.min(Math.max(days, 1), allLabels.length);
    var start = allLabels.length - take;
    return {
      labels: allLabels.slice(start),
      series: allSeries.map(function (s) {
        return {
          key: s.key,
          label: s.label,
          color: s.color,
          values: (s.values || []).slice(start),
          hidden: !!hidden[s.key]
        };
      })
    };
  }

  function ensureCanvas() {
    var canvas = host.querySelector('canvas');
    if (canvas) return canvas;
    host.innerHTML = '';
    canvas = document.createElement('canvas');
    canvas.setAttribute('aria-label', 'Volume trend for the selected range');
    host.appendChild(canvas);
    return canvas;
  }

  function renderLegend(series) {
    var legend = root.querySelector('.dash-graph-legend');
    if (!legend) return;
    legend.innerHTML = '';
    series.forEach(function (s) {
      var item = document.createElement('button');
      item.type = 'button';
      item.className = 'dash-graph-legend-item' + (s.hidden ? ' is-off' : '');
      item.innerHTML = '<i style="background:' + s.color + '"></i>' + s.label;
      item.addEventListener('click', function () {
        hidden[s.key] = !hidden[s.key];
        var visible = allSeries.some(function (row) { return !hidden[row.key]; });
        if (!visible) hidden[s.key] = false;
        render();
      });
      legend.appendChild(item);
    });
  }

  function render() {
    var sliced = sliceTrend(range);
    var labels = sliced.labels.map(function (label) {
      return formatLabel(label, sliced.labels.length);
    });
    var pointRadius = sliced.labels.length <= 14 ? 3 : 0;
    var pointHover = sliced.labels.length <= 40 ? 4 : 3;

    var datasets = sliced.series.map(function (s) {
      return {
        label: s.label,
        data: s.values,
        borderColor: s.color,
        backgroundColor: hexToRgba(s.color, 0.12),
        borderWidth: 2.25,
        tension: 0.35,
        cubicInterpolationMode: 'monotone',
        fill: true,
        pointRadius: pointRadius,
        pointHoverRadius: pointHover,
        pointBackgroundColor: '#fff',
        pointBorderColor: s.color,
        pointBorderWidth: 2,
        hidden: s.hidden
      };
    });

    if (titleEl) titleEl.textContent = 'Last ' + range + ' days';
    renderLegend(sliced.series);

    var canvas = ensureCanvas();
    if (chart) {
      chart.data.labels = labels;
      chart.data.datasets = datasets;
      chart.update('none');
      return;
    }

    chart = new Chart(canvas.getContext('2d'), {
      type: 'line',
      data: { labels: labels, datasets: datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: '#0f172a',
            titleFont: { size: 12, weight: '600' },
            bodyFont: { size: 12 },
            padding: 10,
            cornerRadius: 8,
            displayColors: true,
            boxPadding: 4
          }
        },
        scales: {
          x: {
            grid: { display: false },
            ticks: {
              color: '#94a3b8',
              font: { size: 11 },
              maxRotation: 0,
              autoSkip: true,
              maxTicksLimit: sliced.labels.length <= 7 ? 7 : 6
            },
            border: { display: false }
          },
          y: {
            beginAtZero: true,
            grace: '8%',
            ticks: {
              precision: 0,
              color: '#94a3b8',
              font: { size: 11 }
            },
            grid: { color: '#eef2f7' },
            border: { display: false }
          }
        }
      }
    });
  }

  Array.prototype.forEach.call(root.querySelectorAll('.dash-range-btn'), function (btn) {
    btn.addEventListener('click', function () {
      range = parseInt(btn.getAttribute('data-range') || '7', 10) || 7;
      root.setAttribute('data-range', String(range));
      Array.prototype.forEach.call(root.querySelectorAll('.dash-range-btn'), function (b) {
        b.classList.toggle('is-active', b === btn);
      });
      if (chart) {
        chart.destroy();
        chart = null;
      }
      render();
    });
  });

  render();
})();

(function () {
  var tabs = document.querySelector('[data-dash-tabs]');
  if (!tabs) return;

  var buttons = Array.prototype.slice.call(tabs.querySelectorAll('[data-dash-tab]'));
  var panels = Array.prototype.slice.call(tabs.querySelectorAll('[data-dash-panel]'));

  function activate(name) {
    if (!name) return;
    buttons.forEach(function (btn) {
      var on = btn.getAttribute('data-dash-tab') === name;
      btn.classList.toggle('is-active', on);
      btn.setAttribute('aria-selected', on ? 'true' : 'false');
    });
    panels.forEach(function (panel) {
      var on = panel.getAttribute('data-dash-panel') === name;
      panel.classList.toggle('is-active', on);
      panel.hidden = !on;
    });
  }

  buttons.forEach(function (btn) {
    btn.addEventListener('click', function () {
      activate(btn.getAttribute('data-dash-tab'));
    });
  });

  var initial = tabs.getAttribute('data-default-tab')
    || (buttons[0] && buttons[0].getAttribute('data-dash-tab'));
  activate(initial);
})();
