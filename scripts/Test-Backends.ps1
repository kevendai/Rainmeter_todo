$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$backend = Join-Path $projectRoot 'backend'
$tests = Join-Path $projectRoot 'tests'
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
    $todoLayout = Join-Path $build 'TodoLayoutProbe.exe'
    $calendarLayout = Join-Path $build 'CalendarLayoutProbe.exe'
    $calendarRecurrence = Join-Path $build 'CalendarRecurrenceProbe.exe'
    $dpiAssertions = Join-Path $tests 'DpiLayoutAssertions.cs'
    function Get-ProjectSources([string]$projectPath) {
        [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
        $namespace = [Xml.XmlNamespaceManager]::new($projectXml.NameTable)
        $namespace.AddNamespace('msb', 'http://schemas.microsoft.com/developer/msbuild/2003')
        @($projectXml.SelectNodes('//msb:Compile', $namespace) | ForEach-Object { Join-Path $backend $_.Include })
    }
    $todoSources = Get-ProjectSources (Join-Path $backend 'TodoHost.csproj')
    $calendarSources = Get-ProjectSources (Join-Path $backend 'CalendarHost.csproj')
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Todo -OutputDirectory $build | Out-Null
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Calendar -OutputDirectory $build | Out-Null
    & $csc /nologo /target:exe /optimize+ /r:System.Web.Extensions.dll "/out:$smoke" (Join-Path $backend 'SmokeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed' }
    & $csc /nologo /target:exe /main:TodoLayoutProbe /optimize+ @refs "/out:$todoLayout" @todoSources $dpiAssertions (Join-Path $tests 'TodoLayoutProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Todo layout probe compilation failed' }
    & $csc /nologo /target:exe /main:CalendarLayoutProbe /optimize+ @refs "/out:$calendarLayout" @calendarSources $dpiAssertions (Join-Path $tests 'CalendarLayoutProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Calendar layout probe compilation failed' }
    & $csc /nologo /target:exe /main:CalendarRecurrenceProbe /optimize+ @refs "/out:$calendarRecurrence" @calendarSources (Join-Path $tests 'CalendarRecurrenceProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Calendar recurrence probe compilation failed' }
    $previousCommandDisable = $env:RAINMETER_COMMANDS_DISABLED
    try {
        $env:RAINMETER_COMMANDS_DISABLED = '1'
        & $smoke $todo $calendar
        if ($LASTEXITCODE -ne 0) { throw 'Backend smoke tests failed' }
        & $todo BackupSelfTest
        if ($LASTEXITCODE -ne 0) { throw "Encrypted backup self-tests failed with exit code $LASTEXITCODE" }
        Write-Host 'Encrypted portable backup config v1.0 round-trip, DPAPI rewrap, rollback, wrong-password, and tamper tests passed'
        & $todo PaperRssSelfTest
        if ($LASTEXITCODE -ne 0) { throw "Paper RSS self-tests failed with exit code $LASTEXITCODE" }
        Write-Host 'Paper RSS selection, completion filtering, score filtering, RFC 822 dates, empty feed, and XML escaping passed'
        & $calendarRecurrence
        if ($LASTEXITCODE -ne 0) { throw "Calendar recurrence tests failed with exit code $LASTEXITCODE" }
    } finally {
        if ($null -eq $previousCommandDisable) { Remove-Item Env:RAINMETER_COMMANDS_DISABLED -ErrorAction SilentlyContinue }
        else { $env:RAINMETER_COMMANDS_DISABLED = $previousCommandDisable }
    }
    $previousScaleOverride = $env:RAINMETER_UI_SCALE_OVERRIDE
    $previousDpiOverride = $env:RAINMETER_UI_DPI_OVERRIDE
    try {
        foreach ($scale in @('0.70','0.75','0.80','0.90','1.00','1.10','1.25')) {
            $env:RAINMETER_UI_SCALE_OVERRIDE = $scale
            foreach ($hostExe in @($todo, $calendar)) {
                $render = Start-Process -FilePath $hostExe -ArgumentList 'Render' -WindowStyle Hidden -PassThru
                if (-not $render.WaitForExit(20000)) {
                    try { $render.Kill() } catch {}
                    throw "Rainmeter tile render timed out at $scale"
                }
                if ($render.ExitCode -ne 0) { throw "Rainmeter tile render failed at $scale with exit code $($render.ExitCode)" }
            }
            $expectedPanelWidth = (518 * [double]$scale).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
            foreach ($generated in @(
                (Join-Path (Split-Path $todo -Parent) 'Generated.inc'),
                (Join-Path (Split-Path $calendar -Parent) 'Generated.inc')
            )) {
                $generatedText = [IO.File]::ReadAllText($generated, [Text.Encoding]::Unicode)
                if ($generatedText -notmatch ('(?m)^Shape=Rectangle [^,]+,[^,]+,' + [regex]::Escape($expectedPanelWidth) + ',')) {
                    throw "Rainmeter tile width did not scale to $expectedPanelWidth at $scale in $generated"
                }
            }
            foreach ($probe in @(
                @{ File = $todoLayout; Argument = 'editor'; Name = 'Todo editor' },
                @{ File = $todoLayout; Argument = 'manager'; Name = 'Todo manager' },
                @{ File = $todoLayout; Argument = 'settings'; Name = 'Todo settings' },
                @{ File = $calendarLayout; Argument = 'manager'; Name = 'Calendar manager' },
                @{ File = $calendarLayout; Argument = 'settings'; Name = 'Calendar settings' },
                @{ File = $calendarLayout; Argument = 'detail-restore'; Name = 'Calendar restore-rule detail' },
                @{ File = $calendarLayout; Argument = 'editor-recurrence'; Name = 'Calendar editor recurrence' },
                @{ File = $calendarLayout; Argument = 'recurrence-dialog'; Name = 'Calendar recurrence dialog' }
            )) {
                $process = Start-Process -FilePath $probe.File -ArgumentList $probe.Argument -WindowStyle Hidden -PassThru
                if (-not $process.WaitForExit(20000)) {
                    try { $process.Kill() } catch {}
                    throw "$($probe.Name) layout probe timed out at $scale"
                }
                if ($process.ExitCode -ne 0) { throw "$($probe.Name) layout probe failed at $scale with exit code $($process.ExitCode)" }
            }
            Write-Host "Tile and window scale probes passed at $([int]([double]$scale * 100))%"
        }
        $env:RAINMETER_UI_SCALE_OVERRIDE = '0.75'
        $env:RAINMETER_UI_DPI_OVERRIDE = '192'
        foreach ($probe in @(
            @{ File = $todoLayout; Argument = 'editor'; Name = 'Todo editor' },
            @{ File = $todoLayout; Argument = 'manager'; Name = 'Todo manager' },
            @{ File = $todoLayout; Argument = 'settings'; Name = 'Todo settings' },
            @{ File = $calendarLayout; Argument = 'manager'; Name = 'Calendar manager' },
            @{ File = $calendarLayout; Argument = 'settings'; Name = 'Calendar settings' },
            @{ File = $calendarLayout; Argument = 'detail-restore'; Name = 'Calendar restore-rule detail' },
            @{ File = $calendarLayout; Argument = 'editor-recurrence'; Name = 'Calendar editor recurrence' },
            @{ File = $calendarLayout; Argument = 'recurrence-dialog'; Name = 'Calendar recurrence dialog' }
        )) {
            $process = Start-Process -FilePath $probe.File -ArgumentList $probe.Argument -WindowStyle Hidden -PassThru
            if (-not $process.WaitForExit(20000)) {
                try { $process.Kill() } catch {}
                throw "$($probe.Name) high-DPI layout probe timed out"
            }
            if ($process.ExitCode -ne 0) { throw "$($probe.Name) high-DPI layout probe failed with exit code $($process.ExitCode)" }
        }
        Write-Host 'Window DPI compensation probe passed at UI 75% / Windows 200% (effective 120%)'
    } finally {
        if ($null -eq $previousScaleOverride) { Remove-Item Env:RAINMETER_UI_SCALE_OVERRIDE -ErrorAction SilentlyContinue }
        else { $env:RAINMETER_UI_SCALE_OVERRIDE = $previousScaleOverride }
        if ($null -eq $previousDpiOverride) { Remove-Item Env:RAINMETER_UI_DPI_OVERRIDE -ErrorAction SilentlyContinue }
        else { $env:RAINMETER_UI_DPI_OVERRIDE = $previousDpiOverride }
    }
} finally {
    Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue
}
