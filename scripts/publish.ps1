param([string]$RuntimeVersion)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $taskRoot
try {
    if (-not $RuntimeVersion) { $RuntimeVersion = (Get-Content -LiteralPath build.json -Raw | ConvertFrom-Json).runtimeVersion }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.build'
    $env:NUGET_PACKAGES = Join-Path $taskRoot '.packages'
    $taskOutput = Join-Path $taskRoot 'artifacts/CardGrinder-win-x64'
    if (Test-Path -LiteralPath $taskOutput) {
        $taskExpected = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/CardGrinder-win-x64'))
        $taskExisting = Get-Item -LiteralPath $taskOutput
        if ($taskExisting.FullName -ne $taskExpected -or ($taskExisting.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing to clean an unexpected publish path.' }
        Remove-Item -LiteralPath $taskExpected -Recurse -Force
    }
    dotnet publish src/CardGrinder/CardGrinder.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -m:1 -o $taskOutput
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
    Copy-Item -LiteralPath README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination $taskOutput
    Copy-Item -LiteralPath licenses -Destination $taskOutput -Recurse -Force
    Copy-Item -LiteralPath docs -Destination $taskOutput -Recurse -Force
    $taskZip = Join-Path $taskRoot 'artifacts/CardGrinder-win-x64.zip'
    Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath $taskZip -Force
    Get-FileHash -LiteralPath (Join-Path $taskOutput 'CardGrinder.exe'),$taskZip -Algorithm SHA256 | Format-Table -AutoSize
} finally { Pop-Location }
