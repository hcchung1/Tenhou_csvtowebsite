param(
  [string]$Configuration = "Release",
  [string]$ProductVersion = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "TenhouCsvReader\TenhouCsvReader.csproj"
$installerProject = Join-Path $repoRoot "Installer\TenhouCsvReader.Installer.wixproj"

$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishDir = Join-Path $artifactsRoot "publish\win-x64"
$portableZip = Join-Path $artifactsRoot "TenhouCsvReader-portable-win-x64.zip"
$msiOutputDir = Join-Path $artifactsRoot "msi"
$portableDir = Join-Path $artifactsRoot "portable"

function Get-ProjectVersion {
  param([string]$ProjectPath)

  try {
    [xml]$projectXml = Get-Content -Path $ProjectPath -Raw
    foreach ($propertyGroup in $projectXml.Project.PropertyGroup) {
      if ($propertyGroup.Version -and -not [string]::IsNullOrWhiteSpace($propertyGroup.Version)) {
        return $propertyGroup.Version.Trim()
      }
    }
  }
  catch {
  }

  return "1.0.0"
}

function Convert-ToMsiVersion {
  param([string]$VersionText)

  if ([string]::IsNullOrWhiteSpace($VersionText)) {
    throw "ProductVersion cannot be empty."
  }

  try {
    $parsedVersion = [version]$VersionText
  }
  catch {
    throw "Invalid ProductVersion '$VersionText'. Use a numeric version like 1.0.1."
  }

  $major = $parsedVersion.Major
  $minor = $parsedVersion.Minor
  $build = if ($parsedVersion.Build -ge 0) { $parsedVersion.Build } else { 0 }

  if ($major -gt 255 -or $minor -gt 255 -or $build -gt 65535) {
    throw "Invalid ProductVersion '$VersionText'. MSI requires Major<=255, Minor<=255, Build<=65535."
  }

  return "$major.$minor.$build"
}

if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
  $ProductVersion = Get-ProjectVersion -ProjectPath $appProject
}

$ProductVersion = Convert-ToMsiVersion -VersionText $ProductVersion
Write-Host "Using product version: $ProductVersion"

Write-Host "[1/5] Cleaning artifact folders..."
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $msiOutputDir -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $portableDir -ErrorAction SilentlyContinue
Remove-Item -Force $portableZip -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force $publishDir | Out-Null
New-Item -ItemType Directory -Force $msiOutputDir | Out-Null
New-Item -ItemType Directory -Force $portableDir | Out-Null

Write-Host "[2/5] Publishing app (self-contained win-x64)..."
& dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true -o $publishDir

$webViewDataDir = Join-Path $publishDir "TenhouCsvReader.exe.WebView2"
if (Test-Path $webViewDataDir) {
  Remove-Item -Recurse -Force $webViewDataDir
}

Write-Host "[3/5] Building MSI installer..."
& dotnet build $installerProject -t:Rebuild -c $Configuration -p:PublishDir=$publishDir -p:OutputPath=$msiOutputDir -p:ProductVersion=$ProductVersion

$msi = Get-ChildItem -Path $msiOutputDir -Filter *.msi -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) {
  throw "MSI build completed but no .msi file was found under: $msiOutputDir"
}

$minimumExpectedMsiSizeBytes = 5MB
if ($msi.Length -lt $minimumExpectedMsiSizeBytes) {
  throw "Generated MSI is unexpectedly small ($($msi.Length) bytes). Stop release to avoid shipping an empty installer."
}

$finalMsiPath = Join-Path $artifactsRoot "TenhouCsvReader-setup-x64.msi"
Copy-Item $msi.FullName $finalMsiPath -Force

Write-Host "[4/5] Building portable ZIP..."
Copy-Item -Path (Join-Path $publishDir "*") -Destination $portableDir -Recurse -Force
Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $portableZip -Force

Write-Host "[5/5] Writing checksums..."
$checksumFile = Join-Path $artifactsRoot "SHA256SUMS.txt"
$msiHash = Get-FileHash -Algorithm SHA256 $finalMsiPath
$zipHash = Get-FileHash -Algorithm SHA256 $portableZip

@(
  "$($msiHash.Hash)  $(Split-Path $finalMsiPath -Leaf)",
  "$($zipHash.Hash)  $(Split-Path $portableZip -Leaf)"
) | Set-Content $checksumFile

Write-Host ""
Write-Host "Artifacts ready:" -ForegroundColor Green
Write-Host " MSI: $finalMsiPath"
Write-Host " ZIP: $portableZip"
Write-Host " SHA: $checksumFile"
