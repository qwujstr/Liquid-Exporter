<p align="center">
  <img src="Liquid/Liquid%20Exporter/assets/appicon.png" width="92" alt="Liquid chrome icon">
</p>
<h1 align="center">Liquid</h1>
<p align="center"><b>Explore, export and modify Unity games.</b><br>Unity project export · IL2CPP to C# · an integrated asset editor.</p>
<p align="center">
  <img alt="Windows x64" src="https://img.shields.io/badge/Windows-x64-111111?style=flat-square">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-252525?style=flat-square">
  <img alt="Local processing" src="https://img.shields.io/badge/processing-local-383838?style=flat-square">
</p>
<p align="center"><a href="#get-started">Get started</a> · <a href="#export">Export</a> · <a href="#asset-editor">Asset editor</a> · <a href="#build">Build</a></p>

<img src="Liquid/Liquid%20Exporter/assets/screenshots/export.png" alt="Liquid selective export with nine Unity asset categories" width="100%">

## Get started

Extract the **entire `dist` folder** and run **`Liquid.exe`**. Keep `worker`, `engines`, `ui` and the supporting DLLs beside it. Windows x64 and Microsoft Edge WebView2 Runtime are required; .NET is included in self-contained builds.

To share Liquid, ZIP the complete `dist` folder. Source repositories and game files are not required by the recipient.

## Export

Select a game folder or file, choose a separate output folder, then click **Export**. Each run gets its own `Liquid_*` directory.

| Mode | Result |
| :--- | :--- |
| **Unity project** | Essential `Assets`, `Packages` and `ProjectSettings` folders. |
| **Specific Asset Only** | Select categories discovered in the game's actual files. |
| **Scripts only** | IL2CPP reconstruction into `Scripts/Source`, managed assemblies and a method recovery report. |

| Category | Output |
| :--- | :--- |
| Texture2D / Sprite | PNG textures and individual sprite images. |
| TextAsset | UTF-8 text. The editor supports byte-preserving TextAsset export. |
| Audio | Decoded WAV, OGG or MP3, depending on the source codec. |
| Prefabs | GameObject hierarchy JSON with child objects and components. |
| Mesh / Material | GLB geometry / serialized Material YAML. |
| Resources | Objects stored in `resources.assets`. |
| Scripts | MonoScript descriptors plus reconstructed C#; matching IL2CPP binary and metadata are required. |

`GameAssembly.dll` / `libil2cpp.so` and `global-metadata.dat` are detected automatically when available. Asset reports record individual decoding failures. **Skip lighting data** filters supported loose serialized files before import; compressed bundles require this option to be off.

## Asset editor

<img src="Liquid/Liquid%20Exporter/assets/screenshots/asset-editor.png" alt="Liquid asset editor with Unity file tabs, search, type filtering and a live texture preview from Pixel Worlds" width="100%">

1. Choose **Asset editor → Open game**, or open a Unity data file directly.
2. Switch between `data.unity3d`, `sharedassets*.assets`, `resources.assets` and bundle tabs. Search loaded assets by name, type or Path ID.
3. Preview textures, sprites and supported audio. **Export** an image, TextAsset, decoded audio, typed JSON dump or raw serialized bytes. **Replace** imports PNG/text, matching JSON dumps or raw asset data.
4. Review the changed-file indicators, use **Undo** if needed, then **Save**.

**Save writes the modified files back to the game folder.** Replacements remain in memory until then. Liquid validates temporary output, preserves bundle compression and creates original-file backups under `.liquid-backups/<timestamp>/`. Stop the game before saving; the folder must be writable.

PNG imports use RGBA32. Replacing a sprite updates its atlas region; the replacement must match the displayed dimensions. Audio replacement uses serialized JSON/raw data, not direct WAV-to-FSB encoding. Typed dumps retain 64-bit values and binary TextAsset bytes; changing an array layout requires raw import.

## Important limits

- IL2CPP reconstruction can be partial or unsupported. Read `script-report.json`; exported C# is not a guaranteed compilable or playable Mono rebuild. The supplied Pixel Worlds build has a specific metadata decoder; other protected versions are not automatically supported.
- Prefab JSON and Material YAML are inspection exports with references. Use **Unity project** for a Unity project layout and its asset metadata.
- The editor supports Unity serialized files and UnityFS bundles. Unknown layouts/codecs can fall back to raw export; Addressables catalog hashes/CRCs are not rewritten automatically.

## Build

Install **.NET SDK 10**. Keep the supplied `Cpp2IL-development` and `Il2CppDumper-master` source folders beside the Liquid project directory.

From the Liquid project directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
```

Output: **`dist/Liquid.exe`**. Subsequent builds normally need only the second command. Initial setup downloads pinned engines/type data and restores NuGet packages; Unity Editor is not required to build Liquid.

<details>
<summary><b>Engines & credits</b></summary>

Asset editing uses [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) and [Fmod5Sharp](https://github.com/SamboyCoding/Fmod5Sharp), with [UABEA](https://github.com/nesrak1/UABEA) as the reader/writer workflow reference. Export and reconstruction use [AssetRipper](https://github.com/AssetRipper/AssetRipper), [Il2CppDumper](https://github.com/Perfare/Il2CppDumper), [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL), [ILSpy](https://github.com/icsharpcode/ILSpy) and [Ghidra](https://github.com/NationalSecurityAgency/ghidra).

See [third-party notices](Liquid/Liquid%20Exporter/docs/THIRD-PARTY-NOTICES.md) and [validation details](Liquid/Liquid%20Exporter/docs/VALIDATION.md). Branding and animation assets were supplied by the project owner.

</details>
