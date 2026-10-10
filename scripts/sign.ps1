param(
  [Parameter(Mandatory)][string]$Path,
  [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint,
  [Parameter(Mandatory)][string]$SignToolPath,
  [uri]$TimestampServer = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$target = (Resolve-Path -LiteralPath $Path).ProviderPath
if (!$target.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Only project build files may be signed.' }
if ($TimestampServer.Scheme -ne 'https') { throw 'Use an HTTPS RFC3161 timestamp service.' }
$tool = (Resolve-Path -LiteralPath $SignToolPath).ProviderPath
if ([IO.Path]::GetFileName($tool) -ne 'signtool.exe') { throw 'Pass the Windows SDK signtool.exe.' }
$file = [IO.File]::OpenRead($target)
try { if ($file.ReadByte() -ne 77 -or $file.ReadByte() -ne 90) { throw 'Signing target must be a Windows executable.' } } finally { $file.Dispose() }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (!$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'A current code-signing certificate with an accessible private key is required.' }
if ($certificate.Subject -eq $certificate.Issuer) { throw 'Self-signed certificates are not supported for public builds.' }
if (!($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })) { throw 'Certificate must permit code signing.' }
$chain = New-Object Security.Cryptography.X509Certificates.X509Chain
try { if (!$chain.Build($certificate)) { throw 'Certificate chain is not trusted or revocation validation failed.' } } finally { $chain.Dispose() }
& $tool sign /sha1 $CertificateThumbprint /s My /fd SHA256 /td SHA256 /tr $TimestampServer.AbsoluteUri /d 'Clippboard by Shard Apps' $target
if ($LASTEXITCODE -ne 0) { throw 'Code signing or timestamping failed.' }
& $tool verify /pa /all /tw $target
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $target
if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) { throw 'Expected a valid, timestamped signature from the selected publisher.' }
