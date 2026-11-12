$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $nativeRoot 'Cpp2IL-development/Cpp2IL-development'
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'Cpp2IL.Core/Cpp2IL.Core.csproj'))) {
    $nativeRoot = Split-Path $nativeRoot -Parent
    $sourceRoot = Join-Path $nativeRoot 'Cpp2IL-development/Cpp2IL-development'
}
$project = Join-Path $sourceRoot 'Cpp2IL.Core/Cpp2IL.Core.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw 'Cpp2IL-development/Cpp2IL-development source directory is required next to Liquid.' }
# Upstream silently emits an empty/default body when no ISIL could be recovered.
# Make that case an explicit unsupported-method diagnostic; no success is fabricated.
$file = Join-Path $sourceRoot 'Cpp2IL.Core/OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs'
$text = [IO.File]::ReadAllText($file)
$pattern = '(if\s*\(methodContext\.ConvertedIsil\.Count == 0\)\s*)FillMethodBodyWithStub\(methodDefinition\);'
$updated = [regex]::Replace($text,$pattern,'$1throw new DecompilerException("No native instructions reconstructed; analysis was unavailable or skipped.");')
if ($updated -ne $text) { [IO.File]::WriteAllText($file,$updated) }
Write-Host 'Cpp2IL.Core source integration ready.'