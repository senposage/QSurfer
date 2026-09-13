param(
    [string]$Query = 'a'
)

$ErrorActionPreference = 'Stop'

# This opt-in probe only authenticates and sends a bounded Qsirch name search.
# It never browses, mounts, restores, creates, changes, or deletes NAS content.
$env:QSURFER_LIVE_NAS = '1'
$env:QSURFER_LIVE_QUERY = $Query

try {
    dotnet test tests/QSurfer.Core.Tests/QSurfer.Core.Tests.csproj -c Release --no-restore --filter 'Category=LiveNas'
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-Host 'QSurfer read-only live NAS smoke check passed.'
}
finally {
    Remove-Item Env:QSURFER_LIVE_NAS -ErrorAction SilentlyContinue
    Remove-Item Env:QSURFER_LIVE_QUERY -ErrorAction SilentlyContinue
}
