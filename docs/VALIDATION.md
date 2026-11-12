# Validation — 12 November 2026

The self-contained Windows x64 build is at dist/Liquid.exe. The entire dist folder is required.

- 84 core/integration checks passed against the final published worker and actual project-export engine: native metadata import, typed scalar/branch/loop/call/field recovery, CIL execution, C# output, lighting staging/filtering, Unity Assets/ProjectSettings/meta export, Both mode, useful liquid.log output and cancellation. Evidence: test-output/liquid-engine-checks.log.
- 14 backend checks passed: actual Ghidra headless evidence, P-code-to-CIL execution against known a+2*b for 1000 signed input pairs, rejection of unsupported/mismatched-width operations, cache reuse, ILSpy C#, Roslyn semantic compilation, checked/native body fusion, Cpp2IL body import, signature conflict isolation and equivalent multidimensional-array signatures. Evidence: test-output/liquid-final-backend-checks.log and final-backend-checks/backend-verification.json.
- UI checks passed: English default and four complete dictionaries; Language directly above Project export engine; black surfaces, bold text, no horizontal overflow, horizontal paths, game EXE icon, automatic folder/profile detection and start gating. test-output/liquid-final-ui/ui-check.json. Video readyState=4 and playback advances; no grayscale filter. Log opening moves content up and progress stays monotonic.
- Frame check: caption style absent, nonclient activation handled, client covers frame, 20 activation-message cycles passed. This is a repeatable native message regression check, not an unlimited interactive Windows compatibility test.
- Actual supplied game exported through the published folder-only pipeline: 7832 primary C# files. 4035 game bodies instruction-mapped; 31474 game bodies partial through Cpp2IL. Roslyn found 341 syntax errors in 31 files. These errors are preserved and reported; the result is not claimed compilable or playable. See SAMPLE_CHECK.md.
- Source recovery on the actual game's malformed CIL retained the four formerly omitted classes. 13 bodies remain unavailable as C#; all 109 managed DLL hashes stayed unchanged. test-output/liquid-source-integrity-check.json.

Controlled Unity fixture source includes arithmetic, branches, loops, arrays, generics, virtual dispatch, delegates, exceptions, fields and properties. Native round-trip of that fixture could not run: the installed Unity editors lack Windows IL2CPP build support. The Unity build log records this; those cases are not declared passed through Cpp2IL merely because handwritten native/CIL tests passed.

Known limitations: protected metadata adapter matches only the supplied SHA256 pair; custom attributes, parameter defaults, string literals and exported forwarding remain omitted. Ghidra batches are bounded, with unprocessed methods explicitly deferred. Unknown native helpers, unsafe memory, generics and obfuscation can leave partial or unsupported bodies. Recovered scripts remain separate from UnityProject to preserve scene GUIDs.

Build warnings are upstream source-link information missing from source archives and existing Dumper CA2022 reads; no build errors. Tests may emit nullable-flow warnings. No production game executable was launched for analysis.

## Specific Asset Only and transitions — 2026-11-12

- 47 new asset integration checks passed using a real Unity 6000.0.55f1 bundle: actual class inventory, Texture2D PNG, two cropped Sprite PNGs with verified pixel colors/alpha, multilingual TextAsset, single-type and combined selections, manifest paths, no script/project output and scan cancellation. Evidence: test-output/liquid-assets-checks.log; fixture source: tests/UnityRecoveryFixture/Assets/Editor/LiquidAssetFixtureBuild.cs.
- All 84 existing core/engine/worker integration checks passed after adding the Assets mode. Evidence: test-output/liquid-engine-checks-assets.log.
- Actual supplied Pixel Worlds folder: 35 matched TextAsset objects, all 35 exported. Output: test-output/pixel-assets-export/Liquid_20261112_082124_3612b9d7/Assets. The engine reported an unreadable Unity class 129 in globalgamemanagers; the job preserves that warning. Initial scene processing remains slow. This test does not assert that all Unity types in this newer game version can be read.
- Published UI checks: real inventory/counts from fixture, asset selection gating, no requirement for IL2CPP metadata in asset mode, card placement to the right of Unity project, no horizontal overflow at normal/minimum window size, fade out/in, rapid navigation, toast replacement/dismissal, reduced-motion behavior, four translation dictionaries, video playback, log layout and native frame activation. Evidence: test-output/liquid-assets-final-ui/*.json and screenshots.
- StbImageSharp 2.30.16 and StbImageWriteSharp 1.16.7 provide per-sprite PNG processing. Notices are distributed in licenses/StbSharp.txt.

The actual Pixel Worlds folder also completed with Skip lighting data enabled: 8 lighting objects removed from the private staging copy; all 35 selected TextAsset objects exported. Evidence: test-output/liquid-pixel-assets-filtered.log. This does not establish a speed improvement; scene processing is still expensive.

## Integrated asset editor and expanded export — 2026-11-12

- UABEA reader/writer/plugin flows were studied; AssetModder uses AssetsTools.NET 3.0.5 with a SHA256-pinned Unity type package.
- 49 uncompressed and 52 LZ4/serialized-file round-trip checks passed: game discovery, 64-bit IDs, name/type search, texture and cropped sprite preview/import, color/alpha integrity, isolation of adjacent sprites, UTF-8/binary TextAsset, typed JSON, malformed raw rejection, Undo, discard, exact original backups, same-file save/reopen, streamed audio preservation, original compression and external-change conflicts. Evidence: test-output/liquid-modder-roundtrip.log and liquid-modder-lz4.log. Test writes affected private fixture copies only.
- 27 expanded-export checks passed on a Unity-generated LZ4 bundle: all real categories discovered; playable WAV, actual GLB, Material YAML, complete local GameObject hierarchy including child/components, resources.assets selection, editor audio preview, parsed WAV sample data, and Scripts category invoking actual IL2CPP reconstruction with a controlled matching native pair. Evidence: test-output/liquid-expanded-assets-checks.log.
- Original 47 image/text export checks and 84 native/project/worker integration checks passed. Evidence: test-output/liquid-original-asset-regression.log and liquid-modder-regression.log.
- Published UI checks: exactly three export cards, Both absent, Export label and visible button, nine discovered categories, real file tabs, search, decoded texture preview, four complete language dictionaries, black/bold styling, fade transitions, reduced motion, colored video playback and 20 native-frame activation cycles. Evidence: test-output/liquid-modder-final-ui.
- Read-only inspection of supplied Pixel Worlds: resources.assets and sharedassets0.assets textures decoded, individual resource sprites decoded, and packed sharedassets sprites resolved through SpriteAtlas render-data keys. The UI opened ten actual game data files and previewed an atlas. No game files were changed or game executable launched.
- Public application, executable and project directory are now Liquid; dist/Liquid.exe is the launcher. Root/project README files use provided assets and actual UI screenshots.

Limits: PNG replacement uses RGBA32 and one mip level; independent sprite replacement requires matching displayed dimensions. Unsupported platforms/layouts/codecs remain possible. Generic data uses Liquid typed JSON or raw serialized bytes. Audio decoding/export works; direct WAV-to-FSB import is not implemented. Prefab JSON and material YAML retain references and are inspection formats, not a complete Unity project. Catalog CRC/hash rewriting for Addressables is not implemented. Native C# reconstruction limits from earlier validation remain unchanged.

## Asset Editor preview update — 2026-11-12

- Published Windows UI: colored placeholder video plays in an empty editor and pauses for valid content; status text is centered. English navigation and page title read Asset Editor.
- Native-size image preview starts at 100%; wheel input increases zoom, real WebView mouse input pans by the requested 35 × 20 CSS pixels, and Fit keeps the full texture inside a visible viewport. No horizontal overflow; all four translation dictionaries remain complete. Evidence: test-output/liquid-preview-ui/preview-assertions.json and screenshots.
- Read-only validation of the supplied Pixel Worlds resources.assets: all 35 TextAsset previews match their decoded bytes; the actual UI shows 35 entries and nonempty text. sharedassets0.assets contains no TextAsset entries. Evidence: test-output/liquid-preview-readonly.log and test-output/liquid-preview-ui/editor-text-check.json. Game files were not modified.
- Build paths now support the project being one or two directories below its supplied Cpp2IL and Il2CppDumper sources.

## Typography, pixel previews and cleanup — 2026-11-12

The published UI uses Arial Bold throughout, including text previews and logs. Asset previews use pixelated rendering; a constant-width light-gray outline tracks the image dimensions while panning and zooming. Fit/reset buttons and the zoom percentage are removed. The folder picker reads Open Folder and Open File includes a document icon.

Published WebView checks passed for fonts, image rendering, outline alignment, removed controls, file-picker labels/icons, real mouse panning, wheel zoom, viewport visibility, all translations and the actual game's 35 TextAssets. A compact result is retained in UI-VALIDATION.json. Test output directories, test sources and optional research tools were subsequently removed at the owner's request; earlier historical evidence paths may no longer exist. The NuGet package cache, source dependencies, production engines and portable distribution were preserved.

## Inline text editing, continuous list and languages — 2026-11-12

- Save no longer creates permanent backup files or a .liquid-backups directory. Atomic replacement retains original file handles only for transaction rollback; these handles close after success/failure. Existing backups are left untouched.
- 21 checks on private copies of the supplied game's serialized resources passed: uncapped asset enumeration, UTF-8/UTF-16 LE/BE with preserved BOM, Unicode content, Undo restoring exact bytes, binary-text rejection, full previews longer than 100,000 characters, deliberate second-file write failure with first-file rollback, saving two changed files, no backup/temp leftovers and reopening saved text.
- Published WebView UI: inline text Save and Undo succeeded; 686 Texture2D entries are accessible by continuous scrolling to the last item while only 21 rows render at the end. Width dragging, height resizing, Exports navigation, seven complete dictionaries and language persistence passed. Source game files were read only; writes affected temporary copies.
- TextAsset inline editing supports valid UTF-8 and BOM-marked UTF-16 up to 8 MB. Binary or larger TextAssets retain file export/import. Save or Ctrl+S commits pending edits. Changes remain visible when switching assets and participate in the existing close/save flow.
- Evidence is retained in liquid-editor-validation.json; test runners and game copies lived in Windows Temp.
- This workspace no longer contains the supplied native backend source checkouts. build.ps1 -SelfContained -AppOnly rebuilds the app/core against the preserved dist/worker; a complete native worker rebuild requires those sources.
