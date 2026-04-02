[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version,

    [Parameter()]
    [string]$Configuration = "Release",

    [Parameter()]
    [string]$RuntimeIdentifier = "win-x64",

    [Parameter()]
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$appProjectPath = Join-Path $repoRoot "src\OptiScalerInstaller.App\OptiScalerInstaller.App.csproj"
$publishProfile = "WinX64SelfContained"
$releaseRoot = Join-Path $repoRoot "artifacts\release"
$publishRoot = Join-Path $releaseRoot "publish\$RuntimeIdentifier"
$portableRoot = Join-Path $releaseRoot "portable"
$installerRoot = Join-Path $releaseRoot "installer"
$appExecutableName = "OptiScalerInstaller.exe"
$portableFileName = $null
$installerFileName = $null

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    $projectXml = [xml](Get-Content -Path $ProjectPath)
    $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "Could not determine the app version from '$ProjectPath'."
    }

    return [string]$versionNode
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion -ProjectPath $appProjectPath
}

$projectVersion = Get-ProjectVersion -ProjectPath $appProjectPath
if ($Version -ne $projectVersion) {
    throw "Requested version '$Version' does not match the project version '$projectVersion'."
}

if (Test-Path $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot | Out-Null
New-Item -ItemType Directory -Path $portableRoot | Out-Null
New-Item -ItemType Directory -Path $installerRoot | Out-Null

Write-Host "Publishing OptiScaler Installer $Version for $RuntimeIdentifier..."
dotnet publish $appProjectPath `
    -c $Configuration `
    /p:PublishProfile=$publishProfile `
    /p:PublishDir="$publishRoot\" | Out-Host

$publishedExePath = Join-Path $publishRoot $appExecutableName
if (-not (Test-Path $publishedExePath)) {
    throw "Expected published executable was not found at '$publishedExePath'."
}

$portableFileName = "OptiScalerInstaller-portable-$RuntimeIdentifier-v$Version.zip"
$portableArchivePath = Join-Path $portableRoot $portableFileName
Write-Host "Creating portable archive $portableFileName..."
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $portableArchivePath -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $isccCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -eq $isccCommand) {
        throw "ISCC.exe was not found on PATH. Install Inno Setup or use -SkipInstaller."
    }

    $installerFileName = "OptiScalerInstaller-setup-$RuntimeIdentifier-v$Version"
    $installerScriptPath = Join-Path $repoRoot "installer\OptiScalerInstaller.iss"
    Write-Host "Creating installer package $installerFileName.exe..."
    & $isccCommand.Source `
        "/DAppVersion=$Version" `
        "/DPublishDir=$publishRoot" `
        "/DOutputDir=$installerRoot" `
        "/DOutputBaseFilename=$installerFileName" `
        "/DAppExeName=$appExecutableName" `
        $installerScriptPath | Out-Host
}

$hashLines = New-Object System.Collections.Generic.List[string]
foreach ($artifactPath in Get-ChildItem -Path $portableRoot, $installerRoot -File | Sort-Object Name) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $artifactPath.FullName
    $hashLines.Add(("{0} *{1}" -f $hash.Hash.ToLowerInvariant(), $artifactPath.Name))
}

$hashFilePath = Join-Path $releaseRoot "SHA256SUMS.txt"
Set-Content -Path $hashFilePath -Value $hashLines

Write-Host ""
Write-Host "Release artifacts written to $releaseRoot"
Get-ChildItem -Path $releaseRoot -Recurse -File | Select-Object FullName, Length
