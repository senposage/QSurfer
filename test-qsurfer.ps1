param(
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

dotnet test QSurfer.slnx -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($Build) {
    dotnet build QSurfer.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Write-Host 'QSurfer automated regression checks passed.'
