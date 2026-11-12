param()
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot 'engines/Modder/classdata.tpk'
$expected = '129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6'
if (-not (Test-Path -LiteralPath $target)) {
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Invoke-WebRequest 'https://raw.githubusercontent.com/nesrak1/UABEA/057e2f6ae67ebc94a38faca9f946f8577162fa99/ReleaseFiles/classdata.tpk' -OutFile $target
}
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw 'Unity type database checksum mismatch.' }
Write-Host 'Modder type database ready.'
