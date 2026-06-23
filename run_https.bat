@echo off
cd /d C:\Users\sqlsupport\ApprovalPO
set ASPNETCORE_Kestrel__Certificates__Default__Path=C:\Users\sqlsupport\ApprovalPO\cert2.pfx
set ASPNETCORE_Kestrel__Certificates__Default__Password=changeit
dotnet run --project ApprovalPO.csproj --urls "https://0.0.0.0:2096"
