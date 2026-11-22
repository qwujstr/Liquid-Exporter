# Build Liquid

Install .NET SDK 10 and Rust. Run `rustup target add wasm32-unknown-unknown` once to enable the Sprite Editor's Rust pixel engine. From the repository root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
```

Launch `build/Liquid Exporter/Liquid Exporter.exe`. To share the portable build, keep `Liquid Exporter.exe` and its `app` folder together. Windows x64 and the WebView2 Runtime are required.

The native worker build needs matching Cpp2IL and Il2CppDumper source checkouts in the parent workspace. To rebuild the app with an existing worker, place its complete worker directory in `dist/worker`, `dist/app/worker`, or `build/Liquid Exporter/app/worker` and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained -AppOnly
```

Add `-IncludeGhidra` to bundle the optional Ghidra and Java fallback for script recovery. The standard build includes AssetRipper for project and asset export, the native scripts worker, and the Unity type database for Asset Editor.
