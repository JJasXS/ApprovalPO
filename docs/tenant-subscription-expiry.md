# Tenant subscription expiry (ApprovalPO)

Full guide (DynamoDB `license.expiryDate`, WhatsApp copy, test dates):

[DeployCommands/tenant-subscription-expiry.md](../../DeployCommands/tenant-subscription-expiry.md)

## ApprovalPO specifics

- Login page only shows the countdown (1–10 days) or expired lockout. No banners inside PO/SO modules after login.
- Tenant JSON is fetched on service start and again by `TenantConfigRefreshWorker`.
- Default refresh interval: **`Approval:TenantConfigRefreshSeconds` = 86400** (24 hours from service start), clamped 60–86400. Set `0` to disable the worker (startup fetch still runs).
- This is **not** a daily 08:00 cron. The timer starts when the Windows service starts (same as eQuotation).
- WhatsApp number: `Support:WhatsAppNumber` in `appsettings.json`, or env `SUPPORT_WHATSAPP_NUMBER`.
