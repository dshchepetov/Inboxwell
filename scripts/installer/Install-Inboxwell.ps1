$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Administrator approval is required to trust the local MSIX certificate.'
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

$installerRoot = $PSScriptRoot
$certificatePath = Join-Path $installerRoot 'Inboxwell-Development.cer'
$packageRoot = Get-ChildItem -LiteralPath $installerRoot -Directory -Filter 'Gomail.App_*_x64_Test' | Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($packageRoot)) {
    throw 'The Inboxwell MSIX package folder is missing from this installer.'
}
$runtimePath = Join-Path $packageRoot 'Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix'
$packagePath = Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.msix' | Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($packagePath)) {
    throw 'The Inboxwell MSIX file is missing from this installer.'
}

$includedCertificate = Get-PfxCertificate -FilePath $certificatePath
$packageSignature = Get-AuthenticodeSignature -FilePath $packagePath
if ($null -eq $packageSignature.SignerCertificate -or $packageSignature.SignerCertificate.Thumbprint -ne $includedCertificate.Thumbprint) {
    throw 'The Inboxwell package signature does not match the included certificate. Do not install this package.'
}

Write-Host 'Trusting the Inboxwell development certificate for this computer...'
Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null

$requiredRuntimeVersion = [Version]'2.3.1.0'
$installedRuntime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' |
    Where-Object { $_.Architecture -eq 'X64' } |
    Sort-Object Version -Descending |
    Select-Object -First 1

if ($null -ne $installedRuntime -and [Version]$installedRuntime.Version -ge $requiredRuntimeVersion) {
    Write-Host "Windows App Runtime $($installedRuntime.Version) is already installed; skipping it."
}
else {
    Write-Host "Installing Windows App Runtime $requiredRuntimeVersion..."
    try {
        Add-AppxPackage -Path $runtimePath -ForceUpdateFromAnyVersion
    }
    catch {
        if ($_.Exception.Message -match '0x80073D02') {
            throw 'Windows App Runtime is currently in use. Close other Windows App SDK applications or restart Windows, then run this installer again.'
        }
        throw
    }
}

Write-Host 'Installing Inboxwell...'
Add-AppxPackage -Path $packagePath -ForceUpdateFromAnyVersion -ForceApplicationShutdown

Write-Host 'Inboxwell is installed. You can open it from the Start menu.' -ForegroundColor Green
