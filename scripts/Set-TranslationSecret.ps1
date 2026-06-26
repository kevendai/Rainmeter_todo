param(
    [string]$RainmeterRoot = 'D:\Program Files (x86)\Rainmeter'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$secretId = (Read-Host 'Tencent Cloud SecretId').Trim()
$secureKey = Read-Host 'Tencent Cloud SecretKey (hidden)' -AsSecureString
if (-not $secretId) { throw 'SecretId cannot be empty.' }

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
try {
    $secretKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if (-not $secretKey) { throw 'SecretKey cannot be empty.' }
    $json = @{ SecretId = $secretId; SecretKey = $secretKey } | ConvertTo-Json -Compress
    $plain = [Text.Encoding]::UTF8.GetBytes($json)
    $cipher = [Security.Cryptography.ProtectedData]::Protect(
        $plain, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $destination = Join-Path $RainmeterRoot 'Skins\Todo\@Resources\translation.secret'
    $directory = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $directory)) { throw "Todo skin not found: $directory" }
    [IO.File]::WriteAllText($destination, [Convert]::ToBase64String($cipher), [Text.UTF8Encoding]::new($false))
    Write-Host "Encrypted translation credential updated: $destination"
} finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $secretKey = $null
}

