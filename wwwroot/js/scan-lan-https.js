/* Redirect phone/LAN HTTP (5057) to HTTPS (5058) so camera APIs work. */
(function () {
  'use strict';
  const loc = window.location;
  if (loc.protocol !== 'http:') return;
  const host = loc.hostname;
  if (host === 'localhost' || host === '127.0.0.1' || !host.includes('.')) return;
  if (loc.port && loc.port !== '5057') return;
  loc.replace(
    'https://' + host + ':5058' + loc.pathname + loc.search + loc.hash
  );
})();
