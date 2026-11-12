# Build Liquid

Install .NET SDK 10. From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
```

Output: dist/Liquid.exe. Share the complete dist folder.

A full native worker build also needs Cpp2IL-development/Cpp2IL-development and Il2CppDumper-master/Il2CppDumper-master in the parent workspace. setup-native.ps1 applies the recovery diagnostic patch to the supplied Cpp2IL source; preserve your matching checkout when changing the worker.

To rebuild the UI and asset editor with the released worker, extract a portable release into dist first, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained -AppOnly
```

This rebuilds the application/core and preserves dist/worker. Setup scripts download pinned dependencies when missing; .packages is the local NuGet cache.
