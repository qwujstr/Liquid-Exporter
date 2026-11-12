$ErrorActionPreference = 'Stop'
$engineRoot = Join-Path $PSScriptRoot 'engines'
New-Item -ItemType Directory -Path $engineRoot -Force | Out-Null
function Fetch-Verified([string]$Url,[string]$Path,[string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) {
        & curl.exe -L --fail --retry 3 --silent --show-error -o $Path $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) { throw "Checksum mismatch: $Path" }
}
$ghidraRoot = Join-Path $engineRoot 'Ghidra'
if (-not (Test-Path -LiteralPath (Join-Path $ghidraRoot 'ghidra_12.2_PUBLIC/Ghidra/Framework/Utility/lib/Utility.jar'))) {
    $archive = Join-Path $engineRoot 'ghidra_12.2_PUBLIC_20261016.zip'
    Fetch-Verified 'https://github.com/NationalSecurityAgency/ghidra/releases/download/Ghidra_12.2_build/ghidra_12.2_PUBLIC_20261016.zip' $archive '2C2CD776DB3C1F6A097BA418922378AC3649CADD5E844A6F5F088AE628A7BE67'
    Expand-Archive -LiteralPath $archive -DestinationPath $ghidraRoot -Force
}
$javaRoot = Join-Path $engineRoot 'Java'
if (-not (Test-Path -LiteralPath (Join-Path $javaRoot 'bin/java.exe'))) {
    $archive = Join-Path $engineRoot 'OpenJDK25U-jdk_x64_windows_hotspot_25.0.5_8.zip'
    Fetch-Verified 'https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.5%2B8/OpenJDK25U-jdk_x64_windows_hotspot_25.0.5_8.zip' $archive '636BDC13DC649DDC7D5921C5C7D23CCA6E44FD19D662B45CC9C02135A6C96C42'
    $unpack = Join-Path $engineRoot 'Java-unpack'
    Expand-Archive -LiteralPath $archive -DestinationPath $unpack -Force
    $package = Get-ChildItem -LiteralPath $unpack -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin/java.exe') } | Select-Object -First 1
    if (-not $package) { throw 'Portable JDK package is incomplete.' }
    New-Item -ItemType Directory -Path $javaRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $package.FullName -Force | Copy-Item -Destination $javaRoot -Recurse -Force
}
Write-Host 'Ghidra 12.2 and portable Temurin JDK 25 are ready.'