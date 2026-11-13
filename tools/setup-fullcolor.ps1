param(
    [Parameter(Mandatory=$true)][string]$AppDirectory,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$disAppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $disAppDirectory 'FullColorEngine\worker.py'))) {
    throw 'Publish or build the app before installing its Full Color runtime.'
}
$disRuntime = Join-Path $disAppDirectory 'FullColorRuntime'
& $Python -m venv $disRuntime
if ($LASTEXITCODE -ne 0) { throw 'Could not create Python runtime.' }
& (Join-Path $disRuntime 'Scripts\python.exe') -m pip install -r (Join-Path $disAppDirectory 'FullColorEngine\requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not install Full Color dependencies.' }
Write-Output ('Full Color runtime ready: ' + $disRuntime)
