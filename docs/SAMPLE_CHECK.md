# Pixel Worlds: final reconstruction check

Verified 12 November 2026 using the production folder-discovery/export pipeline, published worker, bundled Cpp2IL.Core, Ghidra and ILSpy. Input: ../Test With This/Pixel Worlds. Final output: C:/Users/nikit/OneDrive/Desktop/Folders/Code/Liquid/Test With This/Output/Liquid_20261112_024623_bb3e1007 .

| Result | Count |
|---|---:|
| Primary C# files | 7832 |
| All metadata methods tracked | 136459 |
| Instruction-mapped methods, all assemblies | 11172 |
| Partial methods, all assemblies | 95815 |
| Signature-only declarations | 6033 |
| Unsupported methods | 23439 |
| Game Assembly-CSharp instruction-mapped | 4035 |
| Game Assembly-CSharp partial Cpp2IL bodies | 31474 |
| Roslyn syntax errors | 341 |
| C# files with syntax errors | 31 |

These counts are different recovery categories and must not be added up as a percentage of working game behaviour. Partial native bodies are NOT verified runnable code. Some decompiled C# remains syntactically invalid; syntax-valid files can still have unresolved types, wrong approximations or incomplete behaviour. A full working Mono reconstruction has not been produced.

There is one primary Scripts/Source tree, not a separate Experimental dump. CharacterController2D retains the checked _skinWidth getter and native-derived partial bodies. WorldController, NetworkClient, the obfuscator class and SimpleRemoteInterpolation are retained even when individual CIL methods fail ILSpy. 13 methods could not be rendered as C#; their original CIL remains in Assemblies. Method-level retries and independent probes preserve the other readable implementations. SHA256 comparison confirms all 109 primary managed DLLs were unchanged by this source-repair pass.

Ghidra: 64 native addresses requested/attempted, 0 reused, 94210 deferred, timeout=false. For this game's batch no extra methods were promoted to the fully-reconstructed state by Ghidra; its structured evidence is still correlated in the method report. Its actual P-code-to-CIL subset was execution-tested separately on a known native fixture.

The first production export completed before the final source-repair improvement; --retry-source-errors regenerated the affected types from the same managed DLLs, without repeating or changing native reconstruction. --refresh-source-report synchronized per-method diagnostics. Fresh future exports perform this source recovery automatically.

Evidence in the output: Scripts/script-report.json, Scripts/Analysis/csharp-fallback-Assembly-CSharp.json, Scripts/Analysis/native-analysis-summary.json, source-syntax-check.txt, sample-verification.json. Application test logs are in test-output/liquid-*.log. Historical export folders are not overwritten.
