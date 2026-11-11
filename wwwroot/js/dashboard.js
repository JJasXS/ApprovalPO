(function () {
  var root = document.getElementById('dashTrend');
  if (!root) return;

  var host = root.querySelector('#dashTrendGraph');
  var raw = root.getAttribute('data-trend');
  if (!host || !raw) return;

  var trend;
  try {
    trend = JSON.parse(raw);
  } catch (e) {
    return;
  }

  var labels = Array.isArray(trend.labels) ? trend.labels : [];
  var series = Array.isArray(trend.series) ? trend.series : [];
  if (!labels.length || !series.length) return;

  var width = 360;
  var height = 188;
  var pad = { top: 16, right: 14, bottom: 30, left: 30 };
  var plotW = width - pad.left - pad.right;
  var plotH = height - pad.top - pad.bottom;
  var maxVal = 0;
  var ns = 'http://www.w3.org/2000/svg';

  series.forEach(function (s) {
    (s.values || []).forEach(function (v) {
      if (v > maxVal) maxVal = v;
    });
  });
  if (maxVal < 1) maxVal = 1;

  var yMax = Math.max(1, Math.ceil(maxVal * 1.15));

  function svgEl(name, attrs) {
    var el = document.createElementNS(ns, name);
    Object.keys(attrs || {}).forEach(function (k) {
      el.setAttribute(k, attrs[k]);
    });
    return el;
  }

  function xAt(i) {
    if (labels.length === 1) return pad.left + plotW / 2;
    return pad.left + (plotW * i) / (labels.length - 1);
  }

  function yAt(v) {
    return pad.top + plotH - (plotH * v) / yMax;
  }

  function pointList(values) {
    return values.map(function (v, i) {
      return { x: xAt(i), y: yAt(v) };
    });
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

  var svg = svgEl('svg', {
    viewBox: '0 0 ' + width + ' ' + height,
    class: 'dash-graph-svg',
    role: 'img',
    'aria-label': 'Purchase, sales and goods volume over the last 7 days'
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
    var stopA = svgEl('stop', { offset: '0%', 'stop-color': s.color, 'stop-opacity': '0.28' });
    var stopB = svgEl('stop', { offset: '100%', 'stop-color': s.color, 'stop-opacity': '0.02' });
    grad.appendChild(stopA);
    grad.appendChild(stopB);
    defs.appendChild(grad);
  });
  svg.appendChild(defs);

  var ySteps = 4;
  for (var step = 0; step <= ySteps; step++) {
    var value = (yMax * step) / ySteps;
    var y = yAt(value);
    svg.appendChild(svgEl('line', {
      x1: pad.left,
      x2: width - pad.right,
      y1: y,
      y2: y,
      class: 'dash-graph-grid'
    }));
    if (step === 0 || step === ySteps || step === Math.round(ySteps / 2)) {
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

  labels.forEach(function (label, i) {
    var text = svgEl('text', {
      x: xAt(i),
      y: height - 8,
      class: 'dash-graph-axis',
      'text-anchor': 'middle'
    });
    text.textContent = label;
    svg.appendChild(text);
  });

  series.forEach(function (s, idx) {
    var values = s.values || [];
    var points = pointList(values);
    var delay = (0.08 * idx) + 's';

    var area = svgEl('path', {
      d: areaPath(points),
      fill: 'url(#dashFill' + idx + ')',
      class: 'dash-graph-area',
      style: '--dash-delay:' + delay
    });
    svg.appendChild(area);

    var line = svgEl('path', {
      d: smoothLine(points),
      fill: 'none',
      stroke: s.color,
      'stroke-width': 2.75,
      'stroke-linecap': 'round',
      'stroke-linejoin': 'round',
      class: 'dash-graph-line',
      pathLength: '1',
      style: '--dash-delay:' + delay
    });
    svg.appendChild(line);

    values.forEach(function (v, i) {
      if (v <= 0 && maxVal > 0) return;
      var glow = svgEl('circle', {
        cx: xAt(i),
        cy: yAt(v),
        r: 7,
        fill: s.color,
        class: 'dash-graph-glow',
        style: '--dash-delay:' + delay
      });
      var dot = svgEl('circle', {
        cx: xAt(i),
        cy: yAt(v),
        r: 3.4,
        fill: '#fff',
        stroke: s.color,
        'stroke-width': 2.25,
        class: 'dash-graph-dot',
        style: '--dash-delay:' + delay
      });
      svg.appendChild(glow);
      svg.appendChild(dot);
    });
  });

  host.innerHTML = '';
  host.appendChild(svg);

  var legend = root.querySelector('.dash-graph-legend');
  if (!legend) return;
  legend.innerHTML = '';
  series.forEach(function (s) {
    var item = document.createElement('span');
    item.className = 'dash-graph-legend-item';
    item.innerHTML = '<i style="background:' + s.color + '"></i>' + s.label;
    legend.appendChild(item);
  });
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
