(function () {
  var root = document.getElementById('dashTrend');
  if (!root) return;

  var host = root.querySelector('#dashTrendGraph');
  var titleEl = root.querySelector('[data-dash-title]');
  var raw = root.getAttribute('data-trend');
  if (!host || !raw) return;

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
  var width = 360;
  var height = 188;
  var pad = { top: 16, right: 14, bottom: 30, left: 30 };
  var plotW = width - pad.left - pad.right;
  var plotH = height - pad.top - pad.bottom;
  var ns = 'http://www.w3.org/2000/svg';

  function svgEl(name, attrs) {
    var el = document.createElementNS(ns, name);
    Object.keys(attrs || {}).forEach(function (k) {
      el.setAttribute(k, attrs[k]);
    });
    return el;
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
          values: (s.values || []).slice(start)
        };
      }).filter(function (s) {
        return !hidden[s.key];
      })
    };
  }

  function formatLabel(rawLabel, days) {
    if (!rawLabel) return '';
    if (days <= 7) {
      var dt = new Date(rawLabel + 'T00:00:00');
      if (!isNaN(dt.getTime())) {
        return ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][dt.getDay()];
      }
    }
    if (/^\d{4}-\d{2}-\d{2}$/.test(rawLabel)) {
      return rawLabel.slice(5).replace('-', '/');
    }
    return rawLabel;
  }

  function labelStep(count) {
    if (count <= 7) return 1;
    if (count <= 30) return 5;
    if (count <= 90) return 15;
    return 20;
  }

  function smoothLine(points) {
    if (!points.length) return '';
    if (points.length === 1) return 'M ' + points[0].x + ' ' + points[0].y;

    var d = 'M ' + points[0].x + ' ' + points[0].y;
    for (var i = 0; i < points.length - 1; i++) {
      var p0 = points[i === 0 ? 0 : i - 1];
      var p1 = points[i];
      var p2 = points[i + 1];
      var p3 = points[i + 2] || p2;
      var cp1x = p1.x + (p2.x - p0.x) / 6;
      var cp1y = p1.y + (p2.y - p0.y) / 6;
      var cp2x = p2.x - (p3.x - p1.x) / 6;
      var cp2y = p2.y - (p3.y - p1.y) / 6;
      cp1y = Math.min(pad.top + plotH, Math.max(pad.top, cp1y));
      cp2y = Math.min(pad.top + plotH, Math.max(pad.top, cp2y));
      d += ' C ' + cp1x + ' ' + cp1y + ', ' + cp2x + ' ' + cp2y + ', ' + p2.x + ' ' + p2.y;
    }
    return d;
  }

  function areaPath(points) {
    if (!points.length) return '';
    var base = pad.top + plotH;
    return smoothLine(points)
      + ' L ' + points[points.length - 1].x + ' ' + base
      + ' L ' + points[0].x + ' ' + base
      + ' Z';
  }

  function render() {
    var sliced = sliceTrend(range);
    var labels = sliced.labels;
    var series = sliced.series;
    var maxVal = 0;

    series.forEach(function (s) {
      (s.values || []).forEach(function (v) {
        if (v > maxVal) maxVal = v;
      });
    });
    if (maxVal < 1) maxVal = 1;
    var yMax = Math.max(1, Math.ceil(maxVal * 1.15));

    function xAt(i) {
      if (labels.length === 1) return pad.left + plotW / 2;
      return pad.left + (plotW * i) / (labels.length - 1);
    }

    function yAt(v) {
      return pad.top + plotH - (plotH * v) / yMax;
    }

    var svg = svgEl('svg', {
      viewBox: '0 0 ' + width + ' ' + height,
      class: 'dash-graph-svg',
      role: 'img',
      'aria-label': 'Volume trend for the selected range'
    });

    var defs = svgEl('defs');
    series.forEach(function (s, idx) {
      var grad = svgEl('linearGradient', {
        id: 'dashFill' + idx,
        x1: '0',
        y1: '0',
        x2: '0',
        y2: '1'
      });
      grad.appendChild(svgEl('stop', { offset: '0%', 'stop-color': s.color, 'stop-opacity': '0.28' }));
      grad.appendChild(svgEl('stop', { offset: '100%', 'stop-color': s.color, 'stop-opacity': '0.02' }));
      defs.appendChild(grad);
    });
    svg.appendChild(defs);

    for (var step = 0; step <= 4; step++) {
      var value = (yMax * step) / 4;
      var y = yAt(value);
      svg.appendChild(svgEl('line', {
        x1: pad.left,
        x2: width - pad.right,
        y1: y,
        y2: y,
        class: 'dash-graph-grid'
      }));
      if (step === 0 || step === 4 || step === 2) {
        var tick = svgEl('text', {
          x: pad.left - 8,
          y: y + 3,
          class: 'dash-graph-axis',
          'text-anchor': 'end'
        });
        tick.textContent = String(Math.round(value));
        svg.appendChild(tick);
      }
    }

    var stepLabel = labelStep(labels.length);
    labels.forEach(function (label, i) {
      var show = i === 0 || i === labels.length - 1 || i % stepLabel === 0;
      if (!show) return;
      var text = svgEl('text', {
        x: xAt(i),
        y: height - 8,
        class: 'dash-graph-axis',
        'text-anchor': 'middle'
      });
      text.textContent = formatLabel(label, labels.length);
      svg.appendChild(text);
    });

    var showDots = labels.length <= 14;
    series.forEach(function (s, idx) {
      var values = s.values || [];
      var points = values.map(function (v, i) {
        return { x: xAt(i), y: yAt(v) };
      });
      var delay = (0.06 * idx) + 's';

      svg.appendChild(svgEl('path', {
        d: areaPath(points),
        fill: 'url(#dashFill' + idx + ')',
        class: 'dash-graph-area',
        style: '--dash-delay:' + delay
      }));

      svg.appendChild(svgEl('path', {
        d: smoothLine(points),
        fill: 'none',
        stroke: s.color,
        'stroke-width': labels.length > 40 ? 2 : 2.75,
        'stroke-linecap': 'round',
        'stroke-linejoin': 'round',
        class: 'dash-graph-line',
        pathLength: '1',
        style: '--dash-delay:' + delay
      }));

      if (!showDots) return;
      values.forEach(function (v, i) {
        if (v <= 0 && maxVal > 0) return;
        svg.appendChild(svgEl('circle', {
          cx: xAt(i),
          cy: yAt(v),
          r: 7,
          fill: s.color,
          class: 'dash-graph-glow',
          style: '--dash-delay:' + delay
        }));
        svg.appendChild(svgEl('circle', {
          cx: xAt(i),
          cy: yAt(v),
          r: 3.4,
          fill: '#fff',
          stroke: s.color,
          'stroke-width': 2.25,
          class: 'dash-graph-dot',
          style: '--dash-delay:' + delay
        }));
      });
    });

    host.innerHTML = '';
    host.appendChild(svg);

    if (titleEl) titleEl.textContent = 'Last ' + range + ' days';

    var legend = root.querySelector('.dash-graph-legend');
    if (!legend) return;
    legend.innerHTML = '';
    allSeries.forEach(function (s) {
      var item = document.createElement('button');
      item.type = 'button';
      item.className = 'dash-graph-legend-item' + (hidden[s.key] ? ' is-off' : '');
      item.setAttribute('data-series', s.key);
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

  Array.prototype.forEach.call(root.querySelectorAll('[data-range]'), function (btn) {
    if (!btn.classList.contains('dash-range-btn')) return;
    btn.addEventListener('click', function () {
      range = parseInt(btn.getAttribute('data-range') || '7', 10) || 7;
      root.setAttribute('data-range', String(range));
      Array.prototype.forEach.call(root.querySelectorAll('.dash-range-btn'), function (b) {
        b.classList.toggle('is-active', b === btn);
      });
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
})();
