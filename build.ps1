param(
    [switch]$SelfContained,
    [switch]$AppOnly,
    [switch]$IncludeGhidra,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 _-]*[A-Za-z0-9]$')]
    [string]$OutputName = 'Liquid Exporter'
)
$ErrorActionPreference = 'Stop'
$basePath = $PSScriptRoot
$buildRoot = Join-Path $basePath 'build'
$output = Join-Path $buildRoot $OutputName
$staging = Join-Path $buildRoot ('Liquid-stage-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $staging 'app'
$worker = Join-Path $app 'worker'
$launcher = Join-Path $staging 'Liquid Exporter.exe'
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
function Remove-BuildDirectory([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (!$resolved.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid build directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
& (Join-Path $basePath 'setup-modder.ps1')
if ($IncludeGhidra) { & (Join-Path $basePath 'setup-ghidra.ps1') }
New-Item -ItemType Directory -Path $worker -Force | Out-Null
try {
    if ($AppOnly) {
        $existingWorker = @((Join-Path $output 'app/worker'), (Join-Path $buildRoot 'Liquid Exporter/app/worker'), (Join-Path $buildRoot 'Liquid/app/worker'), (Join-Path $basePath 'dist/app/worker'), (Join-Path $basePath 'dist/worker')) |
            Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Reaper.Worker.exe') } | Select-Object -First 1
        if (!$existingWorker) { throw '-AppOnly needs an existing portable worker under build/Liquid Exporter/app/worker or dist/worker.' }
        Get-ChildItem -LiteralPath $existingWorker -Force | Copy-Item -Destination $worker -Recurse -Force
    } else {
        & (Join-Path $basePath 'setup-native.ps1')
        dotnet publish (Join-Path $basePath 'src/Reaper.Worker/Reaper.Worker.csproj') -c Release -r win-x64 --self-contained $selfContainedValue -m:1 -p:UseSharedCompilation=false -p:GeneratePackageOnBuild=false -p:PublishTrimmed=false -o $worker
        if ($LASTEXITCODE -ne 0) { throw 'Worker build failed.' }
    }

    $cargo = Get-Command cargo -ErrorAction SilentlyContinue
    if (!$cargo) { throw 'Install Rust and run rustup target add wasm32-unknown-unknown.' }
    $kernelTarget = Join-Path $basePath 'artifacts/rust-target'
    $kernelManifest = Join-Path $basePath 'src/pixel-kernel/Cargo.toml'
    & $cargo.Source build --locked --release --target wasm32-unknown-unknown --target-dir $kernelTarget --manifest-path $kernelManifest
    if ($LASTEXITCODE -ne 0) { throw 'Rust pixel engine build failed.' }

    dotnet publish (Join-Path $basePath 'src/Reaper.App/Reaper.App.csproj') -c Release -r win-x64 --self-contained $selfContainedValue -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false "-p:LiquidLauncherPath=$launcher" -o $app
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $launcher)) { throw 'Application build failed.' }

    $kernel = Join-Path $kernelTarget 'wasm32-unknown-unknown/release/liquid_pixel_kernel.wasm'
    Copy-Item -LiteralPath $kernel -Destination (Join-Path $app 'ui/pixel-kernel.wasm')
    $engines = @('AssetRipper','Modder')
    if ($IncludeGhidra) { $engines += 'Ghidra','Java' }
    foreach ($engine in $engines) {
        $destination = Join-Path $app "engines/$engine"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $source = Join-Path $basePath "engines/$engine"
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
    }
    if (!(Test-Path -LiteralPath (Join-Path $app 'engines/AssetRipper/AssetRipper.GUI.Free.exe'))) { throw 'Run setup.ps1 to install the project engine.' }
    Copy-Item -LiteralPath (Join-Path $basePath 'licenses') -Destination $app -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $basePath 'LICENSE') -Destination $app -Force
    Copy-Item -LiteralPath (Join-Path $basePath 'docs/THIRD-PARTY-NOTICES.md') -Destination $app -Force

    # All cleanup is confined to the fresh staging directory.
    Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object {
        $_.Extension -in '.pdb','.log' -or $_.Name -like 'Microsoft.Web.WebView2.*.xml'
    } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    if ($IncludeGhidra) {
        $scriptDirectory = Join-Path $worker 'ghidra'
        New-Item -ItemType Directory -Path $scriptDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $basePath 'tools/ghidra/LiquidAnalyze.java') -Destination $scriptDirectory -Force
    }
    if (!$IncludeGhidra -and (Test-Path -LiteralPath (Join-Path $worker 'ghidra'))) {
        $optionalScript = [IO.Path]::GetFullPath((Join-Path $worker 'ghidra'))
        if (!$optionalScript.StartsWith($staging + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
        Remove-Item -LiteralPath $optionalScript -Recurse -Force
    }

    $previous = Join-Path $buildRoot ('Liquid-previous-' + [Guid]::NewGuid().ToString('N'))
    $hadPrevious = Test-Path -LiteralPath $output
    if ($hadPrevious) { Move-Item -LiteralPath $output -Destination $previous }
    try { Move-Item -LiteralPath $staging -Destination $output }
    catch { if ($hadPrevious) { Move-Item -LiteralPath $previous -Destination $output }; throw }
    if ($hadPrevious) { Remove-BuildDirectory $previous }
    Write-Host "Ready: $(Join-Path $output 'Liquid Exporter.exe')"
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-BuildDirectory $staging }
}
