# Scan PO improvements — status

| # | Improvement | Done |
|---|-------------|:----:|
| 2 | Align session vs submit (server draft + load on reopen; updated hint) | ✓ |
| 3 | Submit rules and feedback (confirm, block zero scans, toasts, optional all-lines warn) | ✓ |
| 5 | Rename tabs: **To scan** / **Submitted** (not ERP Approved/Not approved) | ✓ |
| 6 | Reopen for scanning (clears submit, returns to detail) | ✓ |
| 7 | Faster scanning (camera stays open, beep/vibrate, no scroll jump) | ✓ |
| 8 | Manual item code entry (beside Scan) | ✓ |
| 9 | HTTPS on LAN messaging (banner + QR photo hint with `https://…:5058`) | ✓ |
| 10 | Resolver resilience (server memory cache + client sessionStorage cache, 15 min) | ✓ |
| 11 | Offline queue (localStorage queue, flush when online, sync on submit clear) | ✓ |
| — | Line table **Status** column with ✓ when line scanned | ✓ |

## Not in this batch (from earlier suggestions)

| Improvement | Done |
|-------------|:----:|
| Persist submit in Firebird / ERP database | |
| Audit trail (user, timestamp on submit / reopen / draft) | ✓ |
| Qty validation (over/under vs PO line qty) | |
| PWA / Add to Home Screen | |

## How it works now

- **Draft**: Scan counts auto-save to `Data/scan-po-submits-{tenant}.json` (draft section) and `sessionStorage` on the device.
- **Submit**: Moves PO to **Submitted** tab; counts stored in submissions section.
- **Reopen**: Removes submission so PO returns to **To scan**.
- **Audit**: Logged-in user (OTP email + display name) recorded on draft save, submit, and reopen; **Activity log** on detail page; **Submitted** tab shows who/when under PO #.
- **Offline**: Failed resolves while offline are queued in `localStorage` and retried when online.

## Phone URL (live camera)

Use **`https://<your-pc-ip>:5058/ScanPO`** on the same Wi‑Fi (accept certificate warning if needed). HTTP port 5057 uses photo-of-QR mode only.
