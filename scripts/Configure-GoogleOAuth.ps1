param(
    [Parameter(Mandatory = $true)]
    [string]$ClientJson
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePath = (Resolve-Path -LiteralPath $ClientJson).Path
$configuration = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json

if ($null -eq $configuration.installed -or
    [string]::IsNullOrWhiteSpace($configuration.installed.client_id) -or
    [string]::IsNullOrWhiteSpace($configuration.installed.client_secret)) {
    throw 'Expected a Google OAuth client JSON with application type Desktop app.'
}

$targetDirectory = Join-Path $repositoryRoot 'src\Gomail.App\Private'
$targetPath = Join-Path $targetDirectory 'GoogleOAuthClient.json'
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force

Write-Host 'Google OAuth is configured for local Inboxwell builds.' -ForegroundColor Green
Write-Host 'The private JSON is ignored by Git and will be included in release packages.'
