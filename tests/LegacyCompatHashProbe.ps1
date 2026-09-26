param(
    [Parameter(Mandatory=$true)][string]$UpdaterScript,
    [Parameter(Mandatory=$true)][string]$Fixture,
    [Parameter(Mandatory=$true)][string]$Expected,
    [string]$IntegrationRoot = ''
)
$ErrorActionPreference = 'Stop'
$errors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($UpdaterScript, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Compatibility updater does not parse in Windows PowerShell.' }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-FileSha256' }, $true)
if ($null -eq $function) { throw 'Self-contained file checksum function is missing.' }
Invoke-Expression $function.Extent.Text
function Get-FileHash { throw 'The host does not provide Get-FileHash.' }
$actual = Get-FileSha256 -Path $Fixture
if ($actual -ne $Expected) { throw "Checksum mismatch: $actual" }
Write-Output 'Windows PowerShell without Get-FileHash: PASS'
if (-not [string]::IsNullOrWhiteSpace($IntegrationRoot)) {
    $taskPath = Join-Path $IntegrationRoot 'Skins\Todo\@Resources\tasks.json'
    $originalTasks = [IO.File]::ReadAllBytes($taskPath)
    & $UpdaterScript -Mode CheckAndInstall -Repository 'kevendai/Rainmeter_todo' -CurrentVersion '1.3.5' -TargetVersion '2.2.0' -RainmeterRoot $IntegrationRoot -AssumeYes -VerifyOnly
    if (-not $?) { throw 'Compatibility installation returned an error.' }
    $version = [IO.File]::ReadAllText((Join-Path $IntegrationRoot 'Skins\Todo\@Resources\app-version.txt')).Trim()
    if ($version -ne '2.2.0') { throw "Compatibility installation did not finish: $version" }
    $updatedTasks = [IO.File]::ReadAllBytes($taskPath)
    if ($originalTasks.Length -ne $updatedTasks.Length) { throw 'Task data length changed.' }
    for ($index = 0; $index -lt $originalTasks.Length; $index++) {
        if ($originalTasks[$index] -ne $updatedTasks[$index]) { throw 'Task data changed.' }
    }
    Write-Output 'Full compatibility installation and task preservation: PASS'
}
