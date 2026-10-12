[CmdletBinding()]
param(
    [string]$StateDirectory = "$env:ProgramData\QSurfer\QIndexer"
)

$ErrorActionPreference = "Stop"
$config = Join-Path $StateDirectory "config.yaml"
$ocrmyPdf = Join-Path $StateDirectory "sidecars\ocrmypdf\ocrmypdf.exe"
$tesseract = @(
    (Get-Command tesseract.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
    "$env:ProgramFiles\Tesseract-OCR\tesseract.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not (Test-Path -LiteralPath $config)) { throw "Managed QIndexer config was not found: $config" }
if (-not $tesseract) { throw "Tesseract is not installed or is not available to the machine." }
if (-not (Test-Path -LiteralPath $ocrmyPdf)) { throw "OCRmyPDF sidecar was not found: $ocrmyPdf" }

& $tesseract --version
& $ocrmyPdf --version
Write-Host "QIndexer OCR sidecars are available."
