$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$backend = Join-Path $projectRoot 'backend'
$build = Join-Path ([IO.Path]::GetTempPath()) ('RainmeterBackendBuild-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $build | Out-Null
try {
    $csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path -LiteralPath $csc)) { throw 'C# compiler not found' }

    $refs = @('/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Security.dll')
    $todo = Join-Path $build 'TodoHost.exe'
    $calendar = Join-Path $build 'CalendarHost.exe'
    $smoke = Join-Path $build 'SmokeTests.exe'
    & $csc /nologo /target:winexe /optimize+ @refs "/out:$todo" (Join-Path $backend 'Common.cs') (Join-Path $backend 'TodoApp.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Todo backend compilation failed' }
    & $csc /nologo /target:winexe /optimize+ @refs "/out:$calendar" (Join-Path $backend 'Common.cs') (Join-Path $backend 'CalendarApp.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Calendar backend compilation failed' }
    & $csc /nologo /target:exe /optimize+ /r:System.Web.Extensions.dll "/out:$smoke" (Join-Path $backend 'SmokeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed' }
    & $smoke $todo $calendar
    if ($LASTEXITCODE -ne 0) { throw 'Backend smoke tests failed' }
} finally {
    Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue
}
