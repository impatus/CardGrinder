$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskInstaller = Join-Path $taskRoot 'artifacts/release/CardGrinder-Setup.exe'
$taskRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{D7F46D04-CF21-47D4-B864-9498DC9E0FA2}_is1'
if (Test-Path -LiteralPath $taskRegistry) { throw 'CardGrinder is already installed. Refusing to replace an existing installation during verification.' }
$taskId = [Guid]::NewGuid().ToString('N')
$taskTestRoot = Join-Path $taskRoot ('.build/installer-verification/' + $taskId)
$taskInstallDir = Join-Path $taskTestRoot 'CardGrinder'
$taskGroup = 'CardGrinder'
$taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($taskGroup + '/CardGrinder.lnk')
if (Test-Path -LiteralPath $taskShortcut) { throw 'A CardGrinder Start-menu shortcut already exists. Refusing to replace it during verification.' }
New-Item -ItemType Directory -Force -Path $taskTestRoot | Out-Null
$taskDataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CardGrinder'
$taskDataBefore = @{}
foreach ($taskName in @('session.bin', 'settings.json')) {
    $taskDataFile = Join-Path $taskDataDir $taskName
    if (Test-Path -LiteralPath $taskDataFile) {
        $taskItem = Get-Item -LiteralPath $taskDataFile
        $taskDataBefore[$taskName] = "$($taskItem.Length):$($taskItem.LastWriteTimeUtc.Ticks)"
    }
}
try {
    foreach ($taskStage in @('install', 'upgrade')) {
        $taskArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=""', ('/DIR="' + $taskInstallDir + '"'), ('/LOG="' + (Join-Path $taskTestRoot ($taskStage + '.log')) + '"'))
        $taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList $taskArguments -WindowStyle Hidden -Wait -PassThru
        if ($taskProcess.ExitCode -ne 0) { throw "Installer $taskStage failed: exit $($taskProcess.ExitCode)" }
        $taskRegistration = Get-ItemProperty -LiteralPath $taskRegistry
        if ($taskRegistration.InstallLocation.TrimEnd('\') -ne $taskInstallDir.TrimEnd('\')) { throw 'The installer registered the wrong installation directory.' }
        if (-not (Test-Path -LiteralPath $taskShortcut)) { throw 'Start-menu shortcut was not created.' }
    }
    $taskExe = Join-Path $taskInstallDir 'CardGrinder.exe'
    $taskSmokeDir = Join-Path $taskTestRoot 'ui-smoke'
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--smoke-test', ('"' + $taskSmokeDir + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskProcess.ExitCode -ne 0) { throw 'The installed application did not pass its offline startup check.' }
    $taskSmokeResult = Get-Content -LiteralPath (Join-Path $taskSmokeDir 'smoke-test.txt') -Raw
    if (-not $taskSmokeResult.StartsWith('PASS:')) { throw 'The installed application reported UI binding errors.' }
    if ($taskSmokeResult -notmatch 'Update edition: Installed') { throw 'The updater did not recognize the installed edition.' }
} finally {
    $taskUninstaller = Join-Path $taskInstallDir 'unins000.exe'
    if (Test-Path -LiteralPath $taskUninstaller) {
        $taskProcess = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $taskTestRoot 'uninstall.log') + '"')) -WindowStyle Hidden -Wait -PassThru
        if ($taskProcess.ExitCode -ne 0) { throw "Uninstall failed: exit $($taskProcess.ExitCode)" }
    }
}
if (Test-Path -LiteralPath $taskRegistry) { throw 'Uninstall registration was not removed.' }
if (Test-Path -LiteralPath $taskShortcut) { throw 'Start-menu shortcut was not removed.' }
if (Test-Path -LiteralPath (Join-Path $taskInstallDir 'CardGrinder.exe')) { throw 'Application executable was not removed.' }
foreach ($taskName in $taskDataBefore.Keys) {
    $taskItem = Get-Item -LiteralPath (Join-Path $taskDataDir $taskName)
    if ("$($taskItem.Length):$($taskItem.LastWriteTimeUtc.Ticks)" -ne $taskDataBefore[$taskName]) { throw 'An existing user-data file was modified during installer verification.' }
}
Write-Host 'PASS: Installer, upgrade, Start-menu shortcut, offline app startup and uninstall verified; existing user data preserved.'
