/* Left drawer for header hamburgers. The panel is moved to document.body so it
   stays above the page and is not tinted by the dim layer. */
function bindAppDrawer(btn, panel) {
  if (!btn || !panel) return null;

  document.body.appendChild(panel);
  panel.removeAttribute('hidden');

  const backdrop = document.createElement('button');
  backdrop.type = 'button';
  backdrop.className = 'scan-menu-backdrop';
  backdrop.setAttribute('aria-label', 'Close menu');
  document.body.appendChild(backdrop);

  const setOpen = (open) => {
    panel.classList.toggle('is-open', open);
    backdrop.classList.toggle('is-open', open);
    panel.setAttribute('aria-hidden', open ? 'false' : 'true');
    btn.setAttribute('aria-expanded', String(open));
    document.body.classList.toggle('scan-menu-open', open);
    if (open) document.dispatchEvent(new CustomEvent('app-drawer-open'));
  };

  btn.addEventListener('click', (e) => {
    e.stopPropagation();
    setOpen(!panel.classList.contains('is-open'));
  });
  const pressOutside = (e) => {
    if (!panel.classList.contains('is-open')) return;
    const t = e.target;
    if (!(t instanceof Node)) return;
    if (panel.contains(t) || btn.contains(t)) return;
    setOpen(false);
    e.preventDefault();
    e.stopPropagation();
  };
  document.addEventListener('pointerdown', pressOutside, true);
  backdrop.addEventListener('pointerdown', pressOutside);

  return { close: () => setOpen(false) };
}

(function () {
  const scan = bindAppDrawer(
    document.getElementById('scanMenuBtn'),
    document.getElementById('scanMenuPanel'));
  const po = bindAppDrawer(
    document.getElementById('menuBtn'),
    document.getElementById('menuPanel'));

  window.ScanMenu = scan || { close() {} };
  window.AppMenu = po || { close() {} };

  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    window.ScanMenu.close();
    window.AppMenu.close();
  });
})();
