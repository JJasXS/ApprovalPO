#!/usr/bin/env python3
"""Capture ApprovalPO user-guide screenshots into docs/images/user-guide/."""

from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "docs" / "images" / "user-guide"


def _capture(page, base: str, path: str, filename: str, created: list[str], after=None) -> None:
    page.goto(f"{base}{path}", wait_until="domcontentloaded")
    page.wait_for_timeout(1500)
    if after:
        after(page)
    dest = OUT_DIR / filename
    page.screenshot(path=str(dest), full_page=True)
    created.append(filename)
    print(f"  OK {filename} ({dest.stat().st_size} bytes)")


def _seed(page, base: str) -> dict:
    r = page.request.post(
        f"{base}/api/guide_capture/login",
        headers={"Content-Type": "application/json"},
        data="{}",
    )
    if r.status != 200:
        raise RuntimeError(f"guide login failed: {r.status} {r.text()}")
    return r.json()


def _click_first(page, selectors: list[str], wait_ms: int = 1200) -> bool:
    for sel in selectors:
        loc = page.locator(sel).first
        try:
            if loc.count() and loc.is_visible():
                loc.click(timeout=2500)
                page.wait_for_timeout(wait_ms)
                return True
        except Exception:
            continue
    return False


def _first_scan_dockey(page, base: str) -> str | None:
    # Prefer OrdersJson from ScanPO page config after navigation
    try:
        cfg = page.locator("#scanPageConfig").first
        if cfg.count():
            url = cfg.get_attribute("data-orders-json-url") or ""
            if url:
                if url.startswith("/"):
                    url = base + url
                r = page.request.get(url)
                if r.status == 200:
                    data = r.json()
                    items = data if isinstance(data, list) else data.get("orders") or data.get("items") or data.get("data") or []
                    if isinstance(items, list) and items:
                        row = items[0]
                        for key in ("docKey", "DocKey", "dockey", "id", "Id"):
                            if key in row and row[key]:
                                return str(row[key])
    except Exception as ex:
        print(f"  WARN scan dockey: {ex}")
    # Fallback: first detail link
    href = page.locator('a[href*="ScanPODetail"]').first.get_attribute("href") if page.locator('a[href*="ScanPODetail"]').count() else None
    if href:
        m = re.search(r"docKey=(\d+)", href, re.I)
        if m:
            return m.group(1)
    return None


def main() -> int:
    base = (os.getenv("APPROVAL_GUIDE_BASE_URL") or "http://127.0.0.1:2097").rstrip("/")
    only = {x.strip() for x in (os.getenv("APPROVAL_GUIDE_ONLY") or "").split(",") if x.strip()}
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        print("ERROR: install playwright", file=sys.stderr)
        return 1

    print(f"Base URL: {base}")
    print(f"Output:   {OUT_DIR}")
    created: list[str] = []

    def want(name: str) -> bool:
        return (not only) or (name in only)

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        context = browser.new_context(viewport={"width": 1280, "height": 900})
        page = context.new_page()
        page.set_default_timeout(60000)

        if want("01-login.png"):
            _capture(page, base, "/Login", "01-login.png", created)

        if want("02-otp.png"):

            def show_otp(pg):
                pg.evaluate(
                    """() => {
                      const id = document.getElementById('idGroup');
                      const otp = document.getElementById('otpGroup');
                      if (id) id.style.display = 'none';
                      if (otp) otp.style.display = 'block';
                      const h = document.getElementById('loginIdHidden');
                      if (h) h.value = 'staff@example.com';
                    }"""
                )
                pg.wait_for_timeout(300)

            _capture(page, base, "/Login", "02-otp.png", created, after=show_otp)

        auth_needed = any(
            want(n)
            for n in (
                "03-dashboard.png",
                "04-purchase-orders.png",
                "05-purchase-review.png",
                "06-sales-orders.png",
                "07-scan-po.png",
                "08-scan-po-detail.png",
                "09-received-goods.png",
                "10-maintenance-scanner.png",
                "11-maintenance-details.png",
            )
        )
        if auth_needed:
            info = _seed(page, base)
            print(f"  Seeded email={info.get('email')} roles={info.get('roles')}")

        if want("03-dashboard.png"):
            _capture(page, base, "/Dashboard", "03-dashboard.png", created)

        if want("04-purchase-orders.png"):

            def after_po(pg):
                pg.wait_for_timeout(2500)
                _click_first(pg, ["button:has-text('Pending')", "[data-tab='pending']", ".po-tab:has-text('Pending')"], 800)

            _capture(page, base, "/PurchaseOrders", "04-purchase-orders.png", created, after=after_po)

        if want("05-purchase-review.png"):

            def open_review(pg):
                try:
                    pg.wait_for_selector("button.btn-review", timeout=20000)
                except Exception:
                    print("  WARN: Review buttons never appeared")
                # Prefer a Pending row Review button
                opened = pg.evaluate(
                    """() => {
                      const pendingTab = document.getElementById('tabPending');
                      if (pendingTab) pendingTab.click();
                      const rows = [...document.querySelectorAll('tr.po-row')];
                      const pending = rows.find(r => (r.dataset.status || '').toLowerCase() === 'pending')
                        || rows.find(r => (r.innerText || '').includes('PENDING'));
                      const btn = (pending || rows[0] || document).querySelector('button.btn-review, button[data-action=\"review\"]');
                      if (btn) { btn.click(); return true; }
                      return false;
                    }"""
                )
                print(f"  review click={opened}")
                pg.wait_for_timeout(2000)
                try:
                    pg.wait_for_selector("#modalApproveBtn, #reviewBody, .review-sheet", timeout=8000)
                except Exception:
                    pass
                pg.wait_for_timeout(800)

            _capture(page, base, "/PurchaseOrders", "05-purchase-review.png", created, after=open_review)

        if want("06-sales-orders.png"):

            def after_so(pg):
                pg.wait_for_timeout(2500)

            _capture(page, base, "/SalesOrders", "06-sales-orders.png", created, after=after_so)

        scan_dockey = None
        if want("07-scan-po.png") or want("08-scan-po-detail.png"):

            def after_scan_list(pg):
                nonlocal scan_dockey
                pg.wait_for_timeout(2500)
                scan_dockey = _first_scan_dockey(pg, base)
                print(f"  Sample scan docKey={scan_dockey}")

            if want("07-scan-po.png"):
                _capture(page, base, "/ScanPO", "07-scan-po.png", created, after=after_scan_list)
            else:
                page.goto(f"{base}/ScanPO", wait_until="domcontentloaded")
                page.wait_for_timeout(2500)
                scan_dockey = _first_scan_dockey(page, base)

        if want("08-scan-po-detail.png"):
            path = f"/ScanPODetail?docKey={scan_dockey}" if scan_dockey else "/ScanPO"
            _capture(page, base, path, "08-scan-po-detail.png", created, after=lambda pg: pg.wait_for_timeout(2000))

        if want("09-received-goods.png"):
            _capture(page, base, "/ReceivedGoods", "09-received-goods.png", created, after=lambda pg: pg.wait_for_timeout(2000))

        if want("10-maintenance-scanner.png") or want("11-maintenance-details.png"):

            def dismiss_prompt(pg):
                pg.evaluate(
                    """() => {
                      try {
                        const keys = Object.keys(localStorage);
                        for (const k of keys) {
                          if (k.toLowerCase().includes('operator') || k.toLowerCase().includes('scanner')) {
                            /* keep */
                          }
                        }
                        localStorage.setItem('scanner.operatorName.guide', 'Guide Operator');
                      } catch (e) {}
                    }"""
                )
                for sel in [
                    "button:has-text('Use system account name only')",
                    "button:has-text('Save and continue')",
                    "button:has-text('Continue')",
                ]:
                    loc = pg.locator(sel).first
                    try:
                        if loc.count() and loc.is_visible():
                            loc.click(timeout=1500)
                            pg.wait_for_timeout(400)
                            break
                    except Exception:
                        pass

            if want("10-maintenance-scanner.png"):
                _capture(
                    page,
                    base,
                    "/MaintenanceScanner",
                    "10-maintenance-scanner.png",
                    created,
                    after=dismiss_prompt,
                )

            if want("11-maintenance-details.png"):

                def fill_maint(pg):
                    dismiss_prompt(pg)
                    pg.evaluate(
                        """() => {
                          const set = (id, v) => {
                            const el = document.getElementById(id);
                            if (el) { el.value = v; el.dispatchEvent(new Event('input', {bubbles:true})); }
                          };
                          set('ms-location', 'WH-A / RACK-12');
                          set('ms-project', 'MAINTENANCE');
                          set('ms-last-scanned', '2026-08-01 14:30');
                          set('ms-remark1', 'Location verified');
                          const result = document.getElementById('ms-scan-result');
                          if (result) result.textContent = 'Scanned: ITEM-10045';
                          const upd = document.getElementById('ms-update');
                          if (upd) upd.disabled = false;
                          const code = document.getElementById('ms-manual-code');
                          if (code) code.value = 'ITEM-10045';
                        }"""
                    )
                    pg.wait_for_timeout(500)

                _capture(
                    page,
                    base,
                    "/MaintenanceScanner",
                    "11-maintenance-details.png",
                    created,
                    after=fill_maint,
                )

        browser.close()

    print(f"Created {len(created)} PNG(s)")
    for n in created:
        print(f"  - {n}")
    return 0 if created else 1


if __name__ == "__main__":
    raise SystemExit(main())
