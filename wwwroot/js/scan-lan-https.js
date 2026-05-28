/* Redirect phone/LAN HTTP to HTTPS so camera APIs work. Ports from _Layout meta tags. */
(function () {
  var loc = window.location;
  if (loc.protocol !== 'http:') return;

  var httpMeta = document.querySelector('meta[name="approval-http-port"]');
  var httpsMeta = document.querySelector('meta[name="approval-https-port"]');
  var httpPort = (httpMeta && httpMeta.content) ? httpMeta.content : '2095';
  var httpsPort = (httpsMeta && httpsMeta.content) ? httpsMeta.content : '2096';

  if (loc.port && loc.port !== httpPort) return;
  var host = loc.hostname;
  if (host === 'localhost' || host.indexOf('127.') === 0) return;
  if (host.indexOf('.') === -1) return;
  window.location.replace(
    'https://' + host + ':' + httpsPort + loc.pathname + loc.search + loc.hash
  );
})();
