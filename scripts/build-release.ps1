param([string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $taskRoot
try {
    [xml]$taskProps = Get-Content -LiteralPath Directory.Build.props -Raw
    $taskVersion = [string]$taskProps.Project.PropertyGroup.Version
    if ($ExpectedVersion -and $ExpectedVersion.TrimStart('v') -ne $taskVersion) { throw 'Release tag does not match the application version.' }
    & (Join-Path $PSScriptRoot 'publish.ps1')
    $taskCompiler = & (Join-Path $PSScriptRoot 'ensure-inno.ps1')
    & $taskCompiler "/DAppVersion=$taskVersion" (Join-Path $taskRoot 'installer/CardGrinder.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $taskReleaseDir = Join-Path $taskRoot 'artifacts/release'
    Copy-Item -LiteralPath (Join-Path $taskRoot 'artifacts/CardGrinder-win-x64.zip') -Destination $taskReleaseDir -Force
    $taskNames = @('CardGrinder-Setup.exe', 'CardGrinder-win-x64.zip')
    $taskSums = foreach ($taskName in $taskNames) {
        $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskReleaseDir $taskName) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$taskHash  $taskName"
    }
    [IO.File]::WriteAllLines((Join-Path $taskReleaseDir 'SHA256SUMS.txt'), $taskSums)
    Write-Host "Release $taskVersion is ready in $taskReleaseDir"
} finally { Pop-Location }
