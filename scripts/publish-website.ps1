#Requires -Version 7.0
[CmdletBinding()]
param([string]$AdminUsername = 'impatus')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot\publish-site-assets.ps1"
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$entryPath = Join-Path $root "artifacts\website\$version\website-entry.json"
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.CookieContainer = [Net.CookieContainer]::new()
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(60)
function Send-Json([string]$path, [string]$json) {
    $content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $client.PostAsync("https://impatus.no$path", $content).GetAwaiter().GetResult()
        try {
            if (!$response.IsSuccessStatusCode) { throw "Website request failed: HTTP $([int]$response.StatusCode) ($path)." }
            return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        } finally { $response.Dispose() }
    } finally { $content.Dispose() }
}
try {
    $password = Read-Host "impatus.no admin password for $AdminUsername" -AsSecureString
    $credential = [PSCredential]::new($AdminUsername, $password)
    $login = @{ username = $AdminUsername; password = $credential.GetNetworkCredential().Password } | ConvertTo-Json -Compress
    try { $null = Send-Json '/api/admin/login' $login }
    finally { $login = $null; $credential = $null; $password.Dispose() }
    # The normal admin endpoint upserts this one ID and rebuilds the public catalog.
    $saved = Send-Json '/api/admin/entries' ([IO.File]::ReadAllText($entryPath))
    if (!$saved.ok -or $saved.id -ne 'cardgrinder' -or !$saved.published) {
        throw 'The catalog entry was not confirmed published. Check the website admin before retrying.'
    }
    $page = Invoke-WebRequest 'https://impatus.no/programs/cardgrinder' -MaximumRedirection 0
    if ($page.Content -notmatch [regex]::Escape("CardGrinder-$version-Setup-x64.exe")) {
        throw 'Catalog saved, but the public page does not yet show the expected download.'
    }
    Write-Host "CardGrinder $version is live: https://impatus.no/programs/cardgrinder"
} finally { $client.Dispose(); $handler.Dispose() }

