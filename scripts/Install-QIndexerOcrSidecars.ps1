[CmdletBinding()]
param(
    [string]$StateDirectory = "$env:ProgramData\QSurfer\QIndexer",
    [switch]$ConfigureManagedQIndexer
)

$ErrorActionPreference = "Stop"

function Require-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell window. The QIndexer service runs as LocalSystem, so its OCR sidecars must be machine available."
    }
}

function Invoke-WingetInstall([string]$PackageId) {
    $winget = Get-Command winget.exe -ErrorAction Stop
    & $winget.Source install --id $PackageId --exact --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        throw "winget could not install $PackageId (exit code $LASTEXITCODE)."
    }
}

function Find-Tesseract {
    $candidates = @(
        (Get-Command tesseract.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
        "$env:ProgramFiles\Tesseract-OCR\tesseract.exe",
        "${env:ProgramFiles(x86)}\Tesseract-OCR\tesseract.exe"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    return $candidates | Select-Object -First 1
}

function Find-Uv {
    $candidate = Get-Command uv.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if ($candidate) { return $candidate }
    $knownLocations = @(
        "$env:ProgramFiles\uv\uv.exe",
        "$env:LOCALAPPDATA\uv\bin\uv.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ }
    return $knownLocations | Select-Object -First 1
}

Require-Administrator
Invoke-WingetInstall "tesseract-ocr.tesseract"
Invoke-WingetInstall "astral-sh.uv"

$tesseract = Find-Tesseract
if (-not $tesseract) {
    throw "Tesseract was installed but tesseract.exe could not be located."
}

$uv = Find-Uv
if (-not $uv) {
    throw "uv was installed but uv.exe could not be located. Open a new elevated PowerShell window and run this script again."
}

$toolDirectory = Join-Path $StateDirectory "sidecars\ocrmypdf"
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
& $uv tool install --tool-dir $toolDirectory --upgrade ocrmypdf
if ($LASTEXITCODE -ne 0) {
    throw "uv could not install OCRmyPDF (exit code $LASTEXITCODE)."
}

$ocrmyPdf = Join-Path $toolDirectory "ocrmypdf.exe"
if (-not (Test-Path -LiteralPath $ocrmyPdf)) {
    throw "OCRmyPDF was installed but its executable was not found at $ocrmyPdf."
}

& $tesseract --version
& $ocrmyPdf --version

if ($ConfigureManagedQIndexer) {
    $config = Join-Path $StateDirectory "config.yaml"
    if (-not (Test-Path -LiteralPath $config)) {
        throw "Managed QIndexer config was not found: $config"
    }
    $yaml = Get-Content -LiteralPath $config -Raw
    $yaml = [regex]::Replace($yaml, '(?m)^(\s*tesseract_command:\s*).*$', "`$1'$tesseract'")
    $yaml = [regex]::Replace($yaml, '(?m)^(\s*ocrmypdf_command:\s*).*$', "`$1'$ocrmyPdf'")
    Set-Content -LiteralPath $config -Value $yaml -NoNewline -Encoding utf8
}

Write-Host "OCR sidecars installed."
Write-Host "  Tesseract: $tesseract"
Write-Host "  OCRmyPDF:  $ocrmyPdf"
if (-not $ConfigureManagedQIndexer) {
    Write-Host "Use QIndexer's advanced administration to select these commands and enable OCR/content extraction when you want it."
}
