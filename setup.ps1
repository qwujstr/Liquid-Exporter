param([switch]$SkipAssetRipper)
$ErrorActionPreference = 'Stop'
$basePath = $PSScriptRoot
& (Join-Path $basePath 'setup-native.ps1')
& (Join-Path $basePath 'setup-modder.ps1')
$engineDirectory = Join-Path $basePath 'engines/AssetRipper'
if (-not $SkipAssetRipper) {
    $archivePath = Join-Path $basePath 'engines/AssetRipper-2.0.0-win-x64.zip'
    $expectedHash = '9a7ef0e7c5c3ea5b90b4e6d855e2d98d5f7ec8c3f9e26fccbc194c6a7b01baf7'
    New-Item -ItemType Directory -Path (Split-Path $archivePath) -Force | Out-Null
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host 'Downloading official AssetRipper 2.0.0 (Windows x64)...'
        Invoke-WebRequest -Uri 'https://github.com/AssetRipper/AssetRipper/releases/download/2.0.0/AssetRipper_win_x64.zip' -OutFile $archivePath
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'AssetRipper archive checksum mismatch. No executable has been launched.'
    }
    if (-not (Test-Path -LiteralPath $engineDirectory)) {
        Expand-Archive -LiteralPath $archivePath -DestinationPath $engineDirectory
    }
    Write-Host "AssetRipper ready: $engineDirectory"
}
dotnet restore (Join-Path $basePath 'src/Reaper.Worker/Reaper.Worker.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Worker restore failed.' }
dotnet restore (Join-Path $basePath 'src/Reaper.App/Reaper.App.csproj')
if ($LASTEXITCODE -ne 0) { throw 'App restore failed.' }
