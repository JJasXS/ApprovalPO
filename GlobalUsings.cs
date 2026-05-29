// Feature-grouped service namespaces are surfaced globally so existing
// consumers (Pages, Program.cs, other services) resolve them without
// per-file using churn. Folder layout matches each namespace.
global using ApprovalPO.Services;
global using ApprovalPO.Services.Auth;
global using ApprovalPO.Services.Email;
global using ApprovalPO.Services.Orders;
global using ApprovalPO.Services.Scan;
global using ApprovalPO.Services.Push;
global using ApprovalPO.Services.Ocr;
global using ApprovalPO.Services.MaintenanceScanner;
