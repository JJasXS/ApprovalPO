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
  var height = 180;
  var pad = { top: 12, right: 12, bottom: 28, left: 28 };
  var plotW = width - pad.left - pad.right;
  var plotH = height - pad.top - pad.bottom;
  var maxVal = 0;

  series.forEach(function (s) {
    (s.values || []).forEach(function (v) {
      if (v > maxVal) maxVal = v;
    });
  });
  if (maxVal < 1) maxVal = 1;

  var yMax = Math.ceil(maxVal);
  var ySteps = Math.min(4, yMax);
  var ns = 'http://www.w3.org/2000/svg';

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

  var svg = svgEl('svg', {
    viewBox: '0 0 ' + width + ' ' + height,
    class: 'dash-graph-svg',
    role: 'img',
    'aria-label': 'Purchase, sales and goods volume over the last 7 days'
  });

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
    var tick = svgEl('text', {
      x: pad.left - 6,
      y: y + 3,
      class: 'dash-graph-axis',
      'text-anchor': 'end'
    });
    tick.textContent = String(Math.round(value));
    svg.appendChild(tick);
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

  series.forEach(function (s) {
    var values = s.values || [];
    var points = values.map(function (v, i) {
      return xAt(i) + ',' + yAt(v);
    }).join(' ');

    svg.appendChild(svgEl('polyline', {
      points: points,
      fill: 'none',
      stroke: s.color,
      'stroke-width': 2.5,
      'stroke-linecap': 'round',
      'stroke-linejoin': 'round',
      class: 'dash-graph-line'
    }));

    values.forEach(function (v, i) {
      svg.appendChild(svgEl('circle', {
        cx: xAt(i),
        cy: yAt(v),
        r: 3.2,
        fill: s.color,
        stroke: '#fff',
        'stroke-width': 1.5
      }));
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
