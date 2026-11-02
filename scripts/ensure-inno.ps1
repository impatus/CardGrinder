$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskConfig = Get-Content -LiteralPath (Join-Path $taskRoot 'build.json') -Raw | ConvertFrom-Json
if ($env:ISCC_PATH) {
    if (-not (Test-Path -LiteralPath $env:ISCC_PATH -PathType Leaf)) { throw 'ISCC_PATH does not point to a compiler.' }
    return $env:ISCC_PATH
}
$taskTools = Join-Path $taskRoot '.build/tools'
$taskToolDir = Join-Path $taskTools ('inno-' + $taskConfig.innoSetupVersion)
$taskCompiler = Join-Path $taskToolDir 'ISCC.exe'
if (Test-Path -LiteralPath $taskCompiler) { return $taskCompiler }
New-Item -ItemType Directory -Force -Path $taskTools | Out-Null
$taskInstallerName = 'innosetup-' + $taskConfig.innoSetupVersion + '-x64.exe'
$taskInstaller = Join-Path $taskTools $taskInstallerName
if (-not (Test-Path -LiteralPath $taskInstaller)) {
    $taskTag = 'is-' + $taskConfig.innoSetupVersion.Replace('.', '_')
    Invoke-WebRequest "https://github.com/jrsoftware/issrc/releases/download/$taskTag/$taskInstallerName" -OutFile $taskInstaller
}
if ((Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash -ne $taskConfig.innoSetupSha256) { throw 'Inno Setup download checksum mismatch.' }
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskInstaller
if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Inno Setup publisher signature could not be verified.' }
$taskArguments = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=""', ('/DIR="' + $taskToolDir + '"'))
$taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList $taskArguments -WindowStyle Hidden -Wait -PassThru
if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskCompiler)) { throw 'Inno Setup compiler installation failed.' }
return $taskCompiler
