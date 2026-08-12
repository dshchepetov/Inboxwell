param(
    [string]$Version = '1.3.2',
    [ValidateSet('x64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$msixRoot = Join-Path $artifactsRoot 'msix'
$installerSource = Join-Path $PSScriptRoot 'installer'
$installerName = "Inboxwell-$Version-win-$Architecture-installer"
$installerRoot = Join-Path $artifactsRoot $installerName
$zipPath = Join-Path $artifactsRoot "$installerName.zip"
$projectPath = Join-Path $repositoryRoot 'src\Gomail.App\Gomail.App.csproj'
$localDotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

function Assert-ChildPath([string]$Parent, [string]$Child) {
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childPath = [IO.Path]::GetFullPath($Child)
    if (-not $childPath.StartsWith($parentPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe output path: $childPath"
    }
}

Assert-ChildPath $artifactsRoot $installerRoot
Assert-ChildPath $artifactsRoot $zipPath

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $msixRoot -Force | Out-Null

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=Denis Shchepetov' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddMonths(6) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if ($null -eq $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject 'CN=Denis Shchepetov' `
        -FriendlyName 'Inboxwell local development signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("inboxwell-build-" + [Guid]::NewGuid().ToString('N'))
Assert-ChildPath ([IO.Path]::GetTempPath()) $temporaryRoot
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$pfxPath = Join-Path $temporaryRoot 'Inboxwell-Signing.pfx'
$passwordBytes = New-Object byte[] 24
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$random.GetBytes($passwordBytes)
$random.Dispose()
$passwordText = [Convert]::ToBase64String($passwordBytes)
$password = ConvertTo-SecureString -String $passwordText -AsPlainText -Force

try {
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password | Out-Null

    & $dotnet publish $projectPath `
        -c Release `
        -r "win-$Architecture" `
        --self-contained false `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxBundle=Never `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageDir="$msixRoot\" `
        -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateThumbprint="$($certificate.Thumbprint)" `
        -p:AppxPackageVersion="$Version.0"
    if ($LASTEXITCODE -ne 0) { throw "MSIX build failed with exit code $LASTEXITCODE." }

    $packageDirectory = Get-ChildItem -LiteralPath $msixRoot -Directory |
        Where-Object Name -like "Gomail.App_$Version.0_${Architecture}_Test" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $packageDirectory) { throw 'The generated MSIX package directory was not found.' }
    $builtPackage = Get-ChildItem -LiteralPath $packageDirectory.FullName -File -Filter '*.msix' | Select-Object -First 1
    $signature = Get-AuthenticodeSignature -FilePath $builtPackage.FullName
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'The generated MSIX does not have the expected Inboxwell signature.'
    }

    if (Test-Path -LiteralPath $installerRoot) { Remove-Item -LiteralPath $installerRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $installerRoot | Out-Null
    Copy-Item -LiteralPath $packageDirectory.FullName -Destination $installerRoot -Recurse
    $copiedPackage = Join-Path $installerRoot $packageDirectory.Name
    foreach ($dependencyName in @('arm64', 'x86', 'win32')) {
        $unusedDependency = Join-Path $copiedPackage "Dependencies\$dependencyName"
        Assert-ChildPath $installerRoot $unusedDependency
        if (Test-Path -LiteralPath $unusedDependency) { Remove-Item -LiteralPath $unusedDependency -Recurse -Force }
    }
    Copy-Item -LiteralPath (Join-Path $installerSource 'Install-Inboxwell.ps1') -Destination $installerRoot
    Copy-Item -LiteralPath (Join-Path $installerSource 'INSTALL.md') -Destination $installerRoot
    Export-Certificate -Cert $certificate -FilePath (Join-Path $installerRoot 'Inboxwell-Development.cer') | Out-Null

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath $installerRoot -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Created $zipPath" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
