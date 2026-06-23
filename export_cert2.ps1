$pfx = (Resolve-Path .\\cert2.pfx).Path
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx,'changeit')
$derPath = Join-Path (Split-Path $pfx) 'cert2.der'
[System.IO.File]::WriteAllBytes($derPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
Write-Output "Exported to $derPath"
