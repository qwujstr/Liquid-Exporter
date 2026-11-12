# Cleaning the workspace

For running or sharing Liquid, keep the complete dist folder. Start dist/Liquid.exe. Source files and tests are not needed on the recipient's PC.

The following paths are relative to the application directory containing build.ps1. Deleting generated files does not remove the published dist build.

| Path | Can be removed? | Effect |
| --- | --- | --- |
| test-output/ | Yes, if its reports and fixture copies are no longer needed | Removes test results, screenshots and test runners. |
| src/**/bin and src/**/obj | Yes | Recreated on the next build. |
| tests/ | Optional | Production build does not use it; automated checks and Unity fixture sources are lost. |
| .packages/ | Yes, with network access available later | NuGet must restore dependencies on the next build. |
| tools/research/, tools/NativeInspect/, tools/cpp2il-research.zip | Optional | Research sources and a diagnostic utility; production build does not use them. |
| tools/ghidra/LiquidAnalyze.java | Keep for rebuilding | Copied into the worker and used by native reconstruction. Do not delete the whole tools folder. |
| engines/*.zip | Yes, after the corresponding engines are extracted | Existing builds use engines/Ghidra and engines/Java, not these archives. |
| dist/ | Keep to run/share the existing build | Can only be removed if you intend to rebuild it. |
| src/, assets/, engines/, licenses/, build.ps1, setup*.ps1, Directory.Build.props, NuGet.Config | Keep for rebuilding | Source, visual assets, dependencies and build inputs. |

In the outer workspace, supplied AssetRipper-master and ghidra-master are research checkouts and are not used by the current build. Supplied Cpp2IL-development and Il2CppDumper-master are direct worker build dependencies: keep them to rebuild. Test With This contains your game and previous exports, not application dependencies; keep or archive it according to your own needs.

Do not remove dist/ui, dist/worker, dist/engines or individual DLL/runtime files from the portable distribution. The setup scripts can download missing supported engines, but preserving engines allows rebuilding without those downloads.
