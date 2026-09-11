function Resolve-RainmeterEnvironment {
    param([string]$RainmeterRoot)

    $root = if ($null -eq $RainmeterRoot) { '' } else { $RainmeterRoot.Trim().Trim('"') }
    if ([string]::IsNullOrWhiteSpace($root)) {
        $running = Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path } | Select-Object -First 1
        if ($null -ne $running) { $root = Split-Path $running.Path -Parent }
    }
    if ([string]::IsNullOrWhiteSpace($root)) {
        foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe')) {
            $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if ($null -ne $item -and -not [string]::IsNullOrWhiteSpace($item.'(default)')) { $root = Split-Path $item.'(default)' -Parent; break }
        }
    }
    if ([string]::IsNullOrWhiteSpace($root)) { throw 'Rainmeter was not found. Pass -RainmeterRoot explicitly.' }

    $exe = Join-Path $root 'Rainmeter.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Rainmeter not found: $exe" }
    $skins = Join-Path $root 'Skins'
    $ini = Join-Path $env:APPDATA 'Rainmeter\Rainmeter.ini'
    if (Test-Path -LiteralPath $ini) {
        $match = [regex]::Match([IO.File]::ReadAllText($ini), '(?m)^SkinPath=(.+)$')
        if ($match.Success) { $skins = $match.Groups[1].Value.Trim() }
    }
    [pscustomobject]@{ RainmeterRoot = $root; RainmeterExe = $exe; SkinsRoot = $skins }
}
