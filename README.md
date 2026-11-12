<p align="center"><img src="assets/appicon.png" width="88" alt="Liquid"></p>
<h1 align="center">Liquid</h1>
<p align="center">Export Unity assets and scripts. Edit game files in one app.</p>
<p align="center"><a href="https://github.com/qwujstr/Liquid-Exporter/releases/latest"><b>Download for Windows x64</b></a></p>

## Asset Editor

- **Replace a single sprite.** Export it as PNG, edit it, then replace it. Liquid updates that sprite's region in the atlas, so you don't have to edit a huge sprite sheet. Keep the original dimensions.
- **Edit TextAssets directly.** Change text in the preview and press **Save** or **Ctrl+S**. UTF-8 and BOM-marked UTF-16 are supported.
- Preview textures, sprites and audio. Zoom with the wheel, drag to pan, and resize the preview panel.
- Search by name, type or Path ID. Browse the full asset list without pages and switch between Unity files in tabs.
- Export or replace PNGs, text, typed JSON and raw asset data. **Undo** changes before saving.

<img src="assets/screenshots/asset-editor.png" width="100%" alt="Liquid Asset Editor with a resizable TextAsset editor and searchable asset list">

## Export

| Mode | Output |
| --- | --- |
| **Unity project** | Assets, Packages and ProjectSettings. |
| **Specific Asset Only** | Textures, individual sprites, text, audio, meshes, materials, prefab hierarchies, resources or scripts. |
| **Scripts only** | Reconstructed C# types and supported method bodies, managed assemblies and a recovery report. |

<img src="assets/screenshots/export.png" width="100%" alt="Liquid export modes">

## Run

Download a release ZIP, extract **everything** and run **Liquid.exe**. Requires Windows x64 and WebView2; .NET is included.

In Asset Editor, choose **Open Folder** or **Open File**. **Save** writes changes back to the game files without creating backups. Audio can be previewed/exported; direct WAV replacement is not supported. Script recovery can be incomplete—check `script-report.json`.

English · Русский · Deutsch · Español · Українська · 简体中文 · Français

[Build instructions](docs/BUILD.md) · [Third-party credits](docs/THIRD-PARTY-NOTICES.md) · [License](LICENSE)
