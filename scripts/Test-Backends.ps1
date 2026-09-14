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
    $plugin = Join-Path $build 'PluginHost.exe'
    $bundledPlugins = Join-Path $build 'BundledPlugins'
    $smoke = Join-Path $build 'SmokeTests.exe'
    $todoLayout = Join-Path $build 'TodoLayoutProbe.exe'
    $calendarLayout = Join-Path $build 'CalendarLayoutProbe.exe'
    $calendarRecurrence = Join-Path $build 'CalendarRecurrenceProbe.exe'
    $addressProviderProbe = Join-Path $build 'AddressProviderProbe.exe'
    $fakePlugin = Join-Path $build 'FakePlugin.exe'
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
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Plugin -OutputDirectory $build | Out-Null
    Set-Content -LiteralPath (Join-Path $build 'app-version.txt') -Value '2.0.0' -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Build-OfficialPlugins.ps1') -OutputDirectory $bundledPlugins -IncludePrivate | Out-Null
    & (Join-Path $tests 'Test-PluginInstaller.ps1') -Installer (Join-Path $PSScriptRoot 'Install-RwPlugin.ps1') -PluginSource (Join-Path $bundledPlugins 'calendar-to-todo')
    & $csc /nologo /target:exe /optimize+ /r:System.Web.Extensions.dll "/out:$smoke" (Join-Path $backend 'SmokeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed' }
    & $csc /nologo /target:exe /main:TodoLayoutProbe /optimize+ @refs "/out:$todoLayout" @todoSources $dpiAssertions (Join-Path $tests 'TodoLayoutProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Todo layout probe compilation failed' }
    & $csc /nologo /target:exe /main:CalendarLayoutProbe /optimize+ @refs "/out:$calendarLayout" @calendarSources $dpiAssertions (Join-Path $tests 'CalendarLayoutProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Calendar layout probe compilation failed' }
    & $csc /nologo /target:exe /main:CalendarRecurrenceProbe /optimize+ @refs "/out:$calendarRecurrence" @calendarSources (Join-Path $tests 'CalendarRecurrenceProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Calendar recurrence probe compilation failed' }
    & $csc /nologo /target:exe /main:AddressProviderProbe /optimize+ @refs "/out:$addressProviderProbe" @calendarSources (Join-Path $tests 'AddressProviderProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Address provider probe compilation failed' }
    & $csc /nologo /target:exe /optimize+ /r:System.Web.Extensions.dll "/out:$fakePlugin" (Join-Path $tests 'FakePlugin.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fake plugin compilation failed' }
    $previousCommandDisable = $env:RAINMETER_COMMANDS_DISABLED
    $previousPluginRoot = $env:RAINMETER_PLUGIN_ROOT
    try {
        $env:RAINMETER_COMMANDS_DISABLED = '1'
        $env:RAINMETER_PLUGIN_ROOT = Join-Path $build 'PluginState'
        & $smoke $todo $calendar
        if ($LASTEXITCODE -ne 0) { throw 'Backend smoke tests failed' }
        & $plugin SelfTest
        if ($LASTEXITCODE -ne 0) { throw "PluginHost self-tests failed with exit code $LASTEXITCODE" }
        & $plugin Bootstrap
        if ($LASTEXITCODE -ne 0) { throw "Bundled plugin bootstrap failed with exit code $LASTEXITCODE" }
        $ssdpRoot=Join-Path $env:RAINMETER_PLUGIN_ROOT 'Plugins\io.github.kevendai.ssdp-server-ip';$ssdpCurrentPath=Join-Path $ssdpRoot 'current.json';$ssdpCurrent=Get-Content $ssdpCurrentPath -Raw -Encoding UTF8|ConvertFrom-Json;$ssdpCurrent.enabled=$true;$ssdpCurrent|ConvertTo-Json|Set-Content $ssdpCurrentPath -Encoding UTF8
        @{entries=@{Plugin_io_github_kevendai_ssdp_server_ip_server_ip=@{value='203.0.113.7';stale=$false;updated_at=[DateTimeOffset]::Now.ToString('o')}};providers=@{}}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $env:RAINMETER_PLUGIN_ROOT 'PluginValues.json') -Encoding UTF8
        & $addressProviderProbe 'calendar.caldav' 'io.github.kevendai.ssdp-server-ip' '100' '203.0.113.7';if($LASTEXITCODE-ne 0){throw 'Enabled address provider binding failed'}
        & $addressProviderProbe 'arxiv.file_server' 'io.github.kevendai.ssdp-server-ip' '100' '203.0.113.7';if($LASTEXITCODE-ne 0){throw 'arXiv file-server address binding failed'}
        $highRoot=Join-Path $env:RAINMETER_PLUGIN_ROOT 'Plugins\io.github.test.high-address';New-Item (Join-Path $highRoot 'versions\1.0.0') -ItemType Directory -Force|Out-Null;@{version='1.0.0';enabled=$true}|ConvertTo-Json|Set-Content (Join-Path $highRoot 'current.json') -Encoding UTF8;@{id='io.github.test.high-address';name='High address';address_provider=@{priority=200;value='server_ip';targets=@('calendar.caldav')}}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $highRoot 'versions\1.0.0\plugin.json') -Encoding UTF8
        $values=Get-Content (Join-Path $env:RAINMETER_PLUGIN_ROOT 'PluginValues.json') -Raw -Encoding UTF8|ConvertFrom-Json;$values.entries|Add-Member Plugin_io_github_test_high_address_server_ip @{value='203.0.113.8';stale=$false} -Force;$values|ConvertTo-Json -Depth 8|Set-Content (Join-Path $env:RAINMETER_PLUGIN_ROOT 'PluginValues.json') -Encoding UTF8
        & $addressProviderProbe 'calendar.caldav' 'io.github.test.high-address' '200' '203.0.113.8';if($LASTEXITCODE-ne 0){throw 'Address provider priority selection failed'}
        $highCurrent=Get-Content (Join-Path $highRoot 'current.json') -Raw -Encoding UTF8|ConvertFrom-Json;$highCurrent.enabled=$false;$highCurrent|ConvertTo-Json|Set-Content (Join-Path $highRoot 'current.json') -Encoding UTF8;$ssdpCurrent.enabled=$false;$ssdpCurrent|ConvertTo-Json|Set-Content $ssdpCurrentPath -Encoding UTF8
        & $addressProviderProbe 'calendar.caldav' 'none' '0' '0.0.0.0';if($LASTEXITCODE-ne 0){throw 'Disabled address provider affected unconfigured host'};Write-Host 'Address provider enablement, priority and disabled-host isolation passed'
        $arxivPlugin=Join-Path $bundledPlugins 'arxiv\bin\ArxivPlugin.exe';& $arxivPlugin PaperRssSelfTest;if($LASTEXITCODE -ne 0){throw "arXiv plugin RSS tests failed with exit code $LASTEXITCODE"};Write-Host 'arXiv plugin RSS selection, filtering, dates and XML escaping passed'
        @{version=3;meta=@{};tasks=@()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $build 'tasks.json') -Encoding UTF8
        $eventPath=Join-Path $build 'calendar-event.json';$firstResult=Join-Path $build 'calendar-result-1.json';$secondResult=Join-Path $build 'calendar-result-2.json'
        @{uid='meeting';occurrence_key='meeting#one';title='组会';start_at='2026-09-13T09:00:00+08:00';end_at='2026-09-13T10:00:00+08:00';reminder_at='2026-09-13T08:45:00+08:00';source='caldav';all_day=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $eventPath -Encoding UTF8
        & $plugin Transform io.github.kevendai.calendar-to-todo $eventPath $firstResult;if($LASTEXITCODE -ne 0){throw 'Calendar transform plugin failed'}
        $env:RAINMETER_UI_SMOKE='1';& $plugin PluginAction io.github.kevendai.ssdp-server-ip configure_discovery;$wizardExit=$LASTEXITCODE;Remove-Item Env:RAINMETER_UI_SMOKE
        if($wizardExit -ne 0){throw 'Disabled SSDP plugin setup window could not be opened'}
        $ssdpCurrent=Get-Content (Join-Path $env:RAINMETER_PLUGIN_ROOT 'Plugins\io.github.kevendai.ssdp-server-ip\current.json') -Raw -Encoding UTF8|ConvertFrom-Json
        if($ssdpCurrent.enabled){throw 'SSDP plugin setup window unexpectedly enabled the plugin'}
        & $plugin Transform io.github.kevendai.calendar-to-todo $eventPath $secondResult;if($LASTEXITCODE -ne 0){throw 'Calendar transform plugin repeat failed'}
        $one=Get-Content -LiteralPath $firstResult -Raw -Encoding UTF8|ConvertFrom-Json;$two=Get-Content -LiteralPath $secondResult -Raw -Encoding UTF8|ConvertFrom-Json;$imported=Get-Content -LiteralPath (Join-Path $build 'tasks.json') -Raw -Encoding UTF8|ConvertFrom-Json
        if($one.import.created -ne 1 -or $two.import.skipped -ne 1 -or $imported.tasks.Count -ne 1){throw 'Calendar transform import/dedup assertion failed'}
        Write-Host 'Calendar plugin transform, import, UTF-8 and origin dedup passed'
        $fakeId='io.github.test.protocol';$fakeVersion=Join-Path $env:RAINMETER_PLUGIN_ROOT 'Plugins\io.github.test.protocol\versions\1.0.0';New-Item -ItemType Directory -Path (Join-Path $fakeVersion 'bin') -Force|Out-Null;Copy-Item -LiteralPath $fakePlugin -Destination (Join-Path $fakeVersion 'bin\FakePlugin.exe')
        @{id=$fakeId;name='Protocol probe';version='1.0.0';api_version=1;min_host_version='2.0.0';entry='bin/FakePlugin.exe';capabilities=@('value_provider');permissions=@()}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $fakeVersion 'plugin.json') -Encoding UTF8
        @{version='1.0.0';enabled=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $env:RAINMETER_PLUGIN_ROOT 'Plugins\io.github.test.protocol\current.json') -Encoding UTF8
        $progressHost=Start-Process -FilePath $plugin -ArgumentList @('PluginAction',$fakeId,'progress') -WindowStyle Hidden -PassThru
        $progressJob=Join-Path $env:RAINMETER_PLUGIN_ROOT ('PluginJobs\'+$fakeId+'.json');$sawLiveProgress=$false
        for($attempt=0;$attempt -lt 20;$attempt++){if(Test-Path -LiteralPath $progressJob){try{$progressState=Get-Content -LiteralPath $progressJob -Raw -Encoding UTF8|ConvertFrom-Json;if($progressState.state -eq 'running' -and [int]$progressState.current -eq 1){$sawLiveProgress=$true;break}}catch{}};Start-Sleep -Milliseconds 100}
        if(-not $sawLiveProgress){try{$progressHost.Kill()}catch{};throw 'Progress was not published while the plugin was still running'}
        if(-not $progressHost.WaitForExit(10000) -or $progressHost.ExitCode -ne 0){throw 'Valid progress/result protocol failed'}
        foreach($bad in @('invalid_json','duplicate_result','no_result','crash')){& $plugin PluginAction $fakeId $bad;if($LASTEXITCODE -eq 0){throw "Malformed plugin protocol was accepted: $bad"}}
        & $plugin Values $fakeId;if($LASTEXITCODE -ne 0){throw 'Value provider success failed'}
        $values=Get-Content (Join-Path $env:RAINMETER_PLUGIN_ROOT 'PluginValues.json') -Raw -Encoding UTF8|ConvertFrom-Json;$inc=Get-Content (Join-Path $build 'PluginValues.inc') -Raw -Encoding Unicode
        if($values.providers.$fakeId.ttl -ne 60 -or $inc -notmatch 'Plugin_io_github_test_protocol_wan_ip=测试地址'){throw 'Value provider TTL/INC materialization failed'}
        $fakeData=Join-Path $env:RAINMETER_PLUGIN_ROOT 'PluginData\io.github.test.protocol';New-Item -ItemType Directory -Path $fakeData -Force|Out-Null;@{emit_secret=$true}|ConvertTo-Json|Set-Content (Join-Path $fakeData 'config.json') -Encoding UTF8
        $secretResponse=& $plugin Values $fakeId|ConvertFrom-Json;if($LASTEXITCODE -ne 0){throw 'Plugin secret update request failed'}
        Add-Type -AssemblyName System.Security;$cipher=[Convert]::FromBase64String((Get-Content (Join-Path $fakeData 'secret.dat') -Raw));$plain=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($cipher,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))|ConvertFrom-Json
        if($plain.session_token.value -ne 'encrypted-by-host'){throw 'Plugin secret update was not DPAPI persisted'}
        if($secretResponse.payload.PSObject.Properties.Name -contains 'secret_updates'){throw 'Plugin secret update leaked into host response'}
        $updatedConfig=Get-Content (Join-Path $fakeData 'config.json') -Raw -Encoding UTF8|ConvertFrom-Json;if($updatedConfig.selected_did -ne 'test-device'){throw 'Plugin config update was not persisted'}
        if($secretResponse.payload.PSObject.Properties.Name -contains 'config_updates'){throw 'Plugin config update leaked into host response'}
        Remove-Item (Join-Path $fakeData 'config.json') -Force
        @{fail=$true}|ConvertTo-Json|Set-Content (Join-Path $fakeData 'config.json') -Encoding UTF8
        & $plugin Values $fakeId;if($LASTEXITCODE -eq 0){throw 'Failing value provider reported success'};$inc=Get-Content (Join-Path $build 'PluginValues.inc') -Raw -Encoding Unicode
        if($inc -notmatch 'Plugin_io_github_test_protocol_wan_ip_Stale=1' -or $inc -notmatch 'Plugin_io_github_test_protocol_wan_ip=测试地址'){throw 'Value provider stale last-success behavior failed'}
        Remove-Item (Join-Path $fakeData 'config.json') -Force
        $longHost=Start-Process -FilePath $plugin -ArgumentList @('PluginAction',$fakeId,'sleep') -WindowStyle Hidden -PassThru
        $jobPath=Join-Path $env:RAINMETER_PLUGIN_ROOT 'PluginJobs\io.github.test.protocol.json';$job=$null
        for($attempt=0;$attempt -lt 50;$attempt++){
            if(Test-Path $jobPath){try{$job=Get-Content $jobPath -Raw -Encoding UTF8|ConvertFrom-Json;if($job.state -eq 'running' -and $job.pid){break}}catch [IO.IOException]{$job=$null}}
            Start-Sleep -Milliseconds 100
        }
        if(-not $job.pid){try{$longHost.Kill()}catch{};throw 'Long-running plugin PID was not recorded'}
        & $plugin Cancel $fakeId;if($LASTEXITCODE -ne 0){throw 'Plugin cancellation command failed'}
        if(-not $longHost.WaitForExit(10000)){try{$longHost.Kill()}catch{};throw 'Plugin host did not settle after cancellation'}
        $job=$null;for($attempt=0;$attempt -lt 20;$attempt++){try{$job=Get-Content $jobPath -Raw -Encoding UTF8|ConvertFrom-Json;break}catch [IO.IOException]{Start-Sleep -Milliseconds 50}}
        if($null-eq $job-or $job.state -ne 'cancelled'){throw ('Cancelled job state was overwritten: '+$job.state+' / '+$job.message)}
        Write-Host 'Plugin progress, invalid JSON, duplicate/missing result and crash isolation passed'
        $backupProcess=Start-Process -FilePath $todo -ArgumentList 'BackupSelfTest' -WindowStyle Hidden -PassThru -Wait
        if ($backupProcess.ExitCode -ne 0) { throw "Encrypted backup self-tests failed with exit code $($backupProcess.ExitCode)" }
        Write-Host 'Encrypted portable backup config v2.0 round-trip, v1.0 compatibility, DPAPI rewrap, rollback, wrong-password, and tamper tests passed'
$updaterPath = Join-Path $projectRoot 'scripts\RainmeterDesktopWidgetsUpdater.ps1'
$updaterText = [IO.File]::ReadAllText($updaterPath)
if ($updaterText -notmatch '\$checksumContent -is \[byte\[\]\]' -or $updaterText -notmatch "\(\?i\)\^\(\[0-9a-f\]\{64\}\)") {
    throw 'Updater checksum parser must support byte[] responses and Windows PowerShell 5.1 regex semantics.'
}
Write-Host 'Updater SHA256 response parsing compatibility guard passed'
        & $calendarRecurrence
        if ($LASTEXITCODE -ne 0) { throw "Calendar recurrence tests failed with exit code $LASTEXITCODE" }
    } finally {
        if ($null -eq $previousPluginRoot) { Remove-Item Env:RAINMETER_PLUGIN_ROOT -ErrorAction SilentlyContinue } else { $env:RAINMETER_PLUGIN_ROOT = $previousPluginRoot }
        if ($null -eq $previousCommandDisable) { Remove-Item Env:RAINMETER_COMMANDS_DISABLED -ErrorAction SilentlyContinue }
        else { $env:RAINMETER_COMMANDS_DISABLED = $previousCommandDisable }
    }
    & $todoLayout 'scale-config'
    if ($LASTEXITCODE -ne 0) { throw 'Independent tile/window scale configuration probe failed' }
    Write-Host 'Independent tile/window scale persistence and legacy fallback passed'
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
                @{ File = $todoLayout; Argument = 'appearance-settings'; Name = 'Todo appearance settings' },
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
            @{ File = $todoLayout; Argument = 'appearance-settings'; Name = 'Todo appearance settings' },
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
