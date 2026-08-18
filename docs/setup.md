# Setup

## Requirements

- .NET 8 SDK
- Access to a SQL Accounting Firebird company file
- Tenant-config HTTP API URL and tenant code
- SMTP reachable for OTP (or Development OTP-to-console flags — not for production)

## Local run

1. Copy `.env.example` to `.env` in the project root.
2. Set `TENANT_CODE`, `TENANT_BOOTSTRAP_API_URL` (or `TenantBootstrap__AwsApiBaseUrl`), and `FIREBIRD_PASSWORD`.
3. Optional: AWS credentials that can `GetSecretValue` for SMTP / OpenAI refs.
4. TLS: put an operator-supplied PFX at `tls/site.pfx` (or set `Approval:TlsPfxPath`). Do not commit `.pfx` files.
5. Run:

```powershell
dotnet run --project ApprovalPO.csproj
```

HTTP/HTTPS ports come from `Approval:PublicHttpPort` and `Approval:PublicHttpsPort` (defaults 2095 / 2096).

## LAN testing

`run-lan.ps1` sets `APPROVALPO_LISTEN_LAN=true` so Kestrel binds beyond loopback. PEM paths: `APPROVALPO_TLS_PEM` / `APPROVALPO_TLS_KEY`.

## Smoke tests

```powershell
dotnet run --project tests/RolesParserSmoke/RolesParserSmoke.csproj
dotnet run --project tests/ScanPoMatchSmoke/ScanPoMatchSmoke.csproj
```
