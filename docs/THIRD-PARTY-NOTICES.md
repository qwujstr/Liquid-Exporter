# Third-party components

- Il2CppDumper, Perfare and contributors — MIT. Source: https://github.com/Perfare/Il2CppDumper . Worker compiles the adjacent supplied source; its original license is in licenses/Il2CppDumper.txt. Original source remains unchanged.
- AssetRipper 2.0.0, AssetRipper contributors — GNU GPL v3. Distributed as a separate executable. License: licenses/AssetRipper.txt. Corresponding release source: https://github.com/AssetRipper/AssetRipper/tree/2.0.0 ; release and source archives: https://github.com/AssetRipper/AssetRipper/releases/tag/2.0.0 . Its own embedded dependency notices also apply.
- Mono.Cecil 0.11.4 — MIT, https://github.com/jbevain/cecil .
- ICSharpCode.Decompiler / ILSpy 9.1.0.7988 — MIT, https://github.com/icsharpcode/ILSpy .
- Iced 1.21.0 — MIT, https://github.com/icedland/iced .
- Microsoft.Web.WebView2 1.0.2903.40 — Microsoft license terms, https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2903.40 .
- Microsoft .NET runtime — MIT and third-party notices; runtime notices are included in self-contained distributions.

NuGet package license files are copied into licenses when available. See the linked projects and package metadata for transitive dependencies and notices. Assets in assets/ were provided by the workspace owner.

Visual inspiration: Argent, https://github.com/seangeng/argent . No Argent implementation is bundled.

- Cpp2IL / LibCpp2IL / StableNameDotNet / WasmDisassembler, Sam Byass and contributors — MIT. Source: https://github.com/SamboyCoding/Cpp2IL/tree/b5ad444b82267cb1e4b88b8b373c008105bdea52 . Built directly from the supplied adjacent source; license in licenses/Cpp2IL.txt. One local patch makes empty native analysis an explicit failure; setup-native.ps1 records it. Native analysis libraries are linked into worker, not distributed as a separate Cpp2IL CLI.
- Ghidra 12.2, NSA and contributors — Apache License 2.0 plus third-party notices. Official release: https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.2_build . Full LICENSE and dependency notices remain under engines/Ghidra/ghidra_12.2_PUBLIC. LiquidAnalyze.java uses its supported headless/decompiler APIs; upstream engine binaries are not modified.
- Eclipse Temurin OpenJDK 25.0.5+8 — GNU GPL v2 with Classpath Exception and component notices. Distributed portable under engines/Java, including LICENSE, NOTICE and legal. Binary and corresponding source release: https://github.com/adoptium/temurin25-binaries/releases/tag/jdk-25.0.5%2B8 . OpenJDK source project: https://github.com/adoptium/jdk25u . No Oracle JDK is bundled.

Cpp2IL NuGet transitives include AsmResolver, AssetRipper.CIL, Disarm, Capstone and other packages. Their package/release license files and notices apply. Ghidra and Temurin release archives are pinned and SHA256-verified by setup-ghidra.ps1. The .NET runtime retains its own notices. Application docs are in docs; third-party documentation and legal files remain in their distributions.
- AssetsTools.NET 3.0.5 and AssetsTools.NET.Texture 3.0.2, nesrak1 and contributors — MIT. https://github.com/nesrak1/AssetsTools.NET . License: licenses/AssetsTools.NET.txt.
- UABEA, nesrak1 and contributors — MIT. https://github.com/nesrak1/UABEA . Reader/writer and plugin workflows were studied; Liquid uses its own interface. Class database pinned to commit 057e2f6ae67ebc94a38faca9f946f8577162fa99, SHA256 verified by setup-modder.ps1. License: licenses/UABEA.txt.
- AssetRipper.TextureDecoder 1.3.0 — MIT, https://github.com/AssetRipper/TextureDecoder . License: licenses/TextureDecoder.txt.
- Fmod5Sharp 3.0.1 — MIT, https://github.com/SamboyCoding/Fmod5Sharp . License: licenses/Fmod5Sharp.txt. Transitives: NAudio.Core 2.1.0, OggVorbisEncoder 1.2.0 and IndexRange 1.0.2, all MIT; respective notices are included in licenses.
- StbImageSharp 2.30.16 / StbImageWriteSharp 1.16.7 — public-domain/MIT options as described in licenses/StbSharp.txt.
