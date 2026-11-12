param([switch]$SelfContained, [switch]$AppOnly)
$ErrorActionPreference = 'Stop'
$basePath = $PSScriptRoot
$outputPath = Join-Path $basePath 'dist'
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
if (-not $AppOnly) { & (Join-Path $basePath 'setup-native.ps1') }
& (Join-Path $basePath 'setup-modder.ps1')
& (Join-Path $basePath 'setup-ghidra.ps1')
if ($AppOnly) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'worker/Reaper.Worker.exe'))) { throw '-AppOnly requires the existing dist/worker directory. A full backend build requires the Cpp2IL and Il2CppDumper sources.' }
    Write-Host 'Reusing the existing native worker; rebuilding the application and asset editor.'
} else {
dotnet publish (Join-Path $basePath 'src/Reaper.Worker/Reaper.Worker.csproj') -c Release -r win-x64 --self-contained $selfContainedValue -m:1 -p:UseSharedCompilation=false -p:GeneratePackageOnBuild=false -p:PublishTrimmed=false -o (Join-Path $outputPath 'worker')
if ($LASTEXITCODE -ne 0) { throw 'Worker build failed.' }
}

dotnet publish (Join-Path $basePath 'src/Reaper.App/Reaper.App.csproj') -c Release -r win-x64 --self-contained $selfContainedValue -m:1 -p:UseSharedCompilation=false -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
$distDocs = Join-Path $outputPath 'docs'
New-Item -ItemType Directory -Path $distDocs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $basePath 'docs') -Filter '*.md' -File | Copy-Item -Destination $distDocs -Force
Copy-Item -LiteralPath (Join-Path $basePath 'licenses') -Destination $outputPath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $basePath 'LICENSE') -Destination $outputPath -Force
$enginePath = Join-Path $basePath 'engines/AssetRipper'
if (-not (Test-Path -LiteralPath (Join-Path $enginePath 'AssetRipper.GUI.Free.exe'))) { throw 'Run setup.ps1 to download the project engine first.' }
$distEnginePath = Join-Path $outputPath 'engines/AssetRipper'
New-Item -ItemType Directory -Path $distEnginePath -Force | Out-Null
Get-ChildItem -LiteralPath $enginePath -File | Where-Object { $_.Extension -in '.exe', '.dll', '.json', '.txt' } | Copy-Item -Destination $distEnginePath -Force
foreach ($engine in @('Ghidra','Java','Modder')) {
    $destination = Join-Path $outputPath ('engines/'+$engine)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $basePath ('engines/'+$engine)) -Force | Copy-Item -Destination $destination -Recurse -Force
}
# The primary engine is linked into worker; the old standalone copy is obsolete.
$resolvedDist = [IO.Path]::GetFullPath($outputPath)
$legacy = [IO.Path]::GetFullPath((Join-Path $resolvedDist 'engines/Cpp2IL'))
if (-not $legacy.StartsWith($resolvedDist+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid legacy directory.' }
if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Recurse -Force }
Write-Host "Ready: $(Join-Path $outputPath 'Liquid.exe')"
# Include the illustrated project README in the portable distribution.
$sourceReadme = Join-Path $basePath 'README.md'
if (Test-Path -LiteralPath $sourceReadme) {
    $readme = [IO.File]::ReadAllText($sourceReadme).Replace('src="assets/','src="ui/assets/')
    [IO.File]::WriteAllText((Join-Path $outputPath 'README.md'), $readme)
}
foreach ($name in @('Il2cppLiquid.exe','Il2cppLiquid.dll','Il2cppLiquid.deps.json','Il2cppLiquid.runtimeconfig.json','Il2cppLiquid.pdb')) {
    $obsolete = [IO.Path]::GetFullPath((Join-Path $resolvedDist $name))
    if (-not $obsolete.StartsWith($resolvedDist+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid obsolete artifact.' }
    if (Test-Path -LiteralPath $obsolete) { Remove-Item -LiteralPath $obsolete -Force }
}
