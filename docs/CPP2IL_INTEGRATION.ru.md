# Интеграция Cpp2IL и объединённый движок восстановления

Проверка исходников: 12 ноября 2026. Изучен предоставленный Cpp2IL-development, соответствующий b5ad444b82267cb1e4b88b8b373c008105bdea52, вместе с Il2CppDumper, Ghidra и существующим приложением. Параметры дизайна и существующие элементы управления сохранены; по запросу перенесена только строка выбора языка.

## Что выполняет Cpp2IL

LibCpp2IL читает native image и metadata, registration tables, generic instances и runtime-описания. Cpp2IL.Core создаёт ApplicationAnalysisContext и MethodAnalysisContext. Analyze переводит инструкции через Cpp2IlInstructionSet в ISIL, строит CFG и dominator tree, выполняет SSA и проходы восстановления условий, metadata, вызовов, массивов, интерфейсов, делегатов и типов. IlGenerator записывает CIL; AsmResolverDllOutputFormatIlRecovery строит managed DLL. Это настоящий анализ нативных тел, которого нет в dummy DLL от Dumper.

Исследованные точки входа: Cpp2IlApi.InitializeLibCpp2Il; MethodAnalysisContext.Analyze/ReleaseAnalysisData; AttributeInjectorProcessingLayer; AsmResolverDllOutputFormatIlRecovery.FillMethodBody/DoOutput; IlGenerator.GenerateIl/InjectHelpersType. Приложение вызывает поддержанные API напрямую через ProjectReference. CLI Cpp2IL.exe не запускается. Подключается только встроенный Core plugin; произвольные плагины не загружаются.

AttributeInjector сохраняет оригинальные токены/RVA. ReportingFormat собирает предупреждения и JSONL checkpoints после каждого метода. Локальная правка upstream: пустой ConvertedIsil считается явной неудачей, вместо выдачи успешного пустого/default тела. Проверка успешности процесса не подменяет оценку восстановления.

## Сопоставление и единый результат

Идентификатор метода: assembly name + оригинальный metadata token. Дополнительно сравниваются имя/declaring type, static flag, generic arity, return/parameter types и известный native VA. Многомерные массивы сравниваются по структуре, включая rank и vector flag: разные записи [,] и [0...,0...] не создают ложный конфликт. Неоднозначные или несовместимые методы не продвигаются автоматически.

Основной managed-моделью служат независимые metadata-описания Dumper. В них по отдельности импортируются Cpp2IL CIL bodies с перепривязкой типов, полей, вызовов, locals, branches и exception handlers. Checked x64 body имеет приоритет. Непереносимое тело оставляет явную заглушку и диагностику; единичная проблема не отбрасывает всю Assembly-CSharp. Поэтому результат один: Scripts/Source и соответствующие Assemblies, а не несколько несвязанных дампов.

У Cpp2IL-generated bodies статус partially-reconstructed даже без явных предупреждений: семантическая эквивалентность не доказана. NoteDecompilerIssue сохраняется в исходном месте и выбрасывает NotSupportedException при исполнении такого пути. Это не позволяет считать пропущенную операцию успешно выполненной. Native call site с unresolved callee также понижает статус проверенного метода до partial. Fully-reconstructed означает только полный перевод поддержанного подмножества инструкций, не восстановление всей игры.

## Что добавляет Ghidra

Используется официальный Ghidra 12.2 headless с переносимым Temurin JDK 25. Предоставленная копия ghidra-master неполна для самостоятельной сборки, поэтому интеграция работает через поддержанные Java API: AnalyzeHeadless, GhidraScript, DecompInterface, DecompileResults.getHighFunction, getDecompiledFunction, high P-code и basic blocks.

LiquidAnalyze.java получает адреса и сигнатуры автоматически из metadata, создаёт функции, задаёт Windows ABI параметры и экспортирует JSON: C, P-code, CFG, return-expression, завершённость/ошибки. Для сложных value types/ABI C-текст остаётся ориентировочной диагностикой. Его нельзя просто заменить расширением .cs: native pointers, GC, generics, virtual dispatch и Unity API требуют отдельного восстановления.

В CIL принимается только pure scalar subset: статический не-generic метод, максимум четыре Int32/UInt32/Int64/UInt64 аргумента, один basic block и return; никаких LOAD/STORE/CALL/INDIRECT. Проверяются размеры, параметры, return, глубина/число выражений и семантика shift counts. Ширины, неподдержанные операции и shared-address ambiguity не угадываются. Остальным методам прикрепляется native evidence; статус автоматически не повышается.

На известном PE-методе `lea eax,[rcx+rdx*2]; ret`, который собственный lifter отвергает, настоящий Ghidra headless восстановил a + 2*b. High P-code переведён в исполняемый CIL и читаемый C#; результат совпал с исходной формулой на 1000 signed input pairs. Это проверка ограниченного подмножества, а не всех Unity constructs.

## Производительность и повторный запуск

Cpp2IL checkpoints пишутся буферизованно. Полностью завершённый output кешируется по SHA256 native image, normalized metadata, revision и module identity Core. Manifest появляется только после завершения. Незавершённый анализ не выдаётся за complete cache. Cpp2IL caches находятся в LocalAppData/Il2cppLiquid/Cache; LIQUID_CACHE_DIRECTORY позволяет задать другой каталог.

Ghidra cache дополнительно привязан к версии и SHA скрипта. Повторные экспорты переиспользуют evidence. За один запуск анализируются 64 новых адреса, приоритет — игровым методам и поддержанным выражениям; LIQUID_GHIDRA_BUDGET допускает 0–512. Deferred и timeout отражаются в native-analysis-summary.json. Batch ограничен пятью минутами; завершённые checkpoints сохраняются. Отмена worker останавливает и дерево headless-процессов. Ошибка дополнительного backend не уничтожает результат предыдущего анализа.

## Ограничения конкретной игры

Pixel Worlds metadata имеют отдельный проверенный декодер. Custom attributes, parameter defaults, string literal tables и exported-type forwarding пока не нормализованы. Поэтому даже богатое восстановленное CIL может содержать unknown runtime helpers и недостающие значения. Обфускация, stripped symbols, оптимизация, unsafe память и generics ограничивают результат. Получить оригинальный исходник побайтно или гарантировать полную Mono-копию этим конвейером нельзя.

Результаты и честные syntax diagnostics см. SAMPLE_CHECK.md. Полный Unity fixture подготовлен, но его IL2CPP player не собрался: в установленных редакторах нет Windows IL2CPP build-support module. Поэтому массивы/virtual/generics/delegates/exception handling из этого fixture не объявлены прошедшими native round-trip.

## Первичные источники

- https://github.com/SamboyCoding/Cpp2IL/tree/b5ad444b82267cb1e4b88b8b373c008105bdea52
- https://github.com/SamboyCoding/Cpp2IL/blob/b5ad444b82267cb1e4b88b8b373c008105bdea52/Cpp2IL.Core/Model/Contexts/MethodAnalysisContext.cs
- https://github.com/SamboyCoding/Cpp2IL/blob/b5ad444b82267cb1e4b88b8b373c008105bdea52/Cpp2IL.Core/OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs
- https://github.com/SamboyCoding/Cpp2IL/blob/b5ad444b82267cb1e4b88b8b373c008105bdea52/Cpp2IL.Core/IlGenerator.cs
- https://github.com/Perfare/Il2CppDumper
- https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.2_build
- https://ghidra.re/ghidra_docs/api/ghidra/app/decompiler/DecompInterface.html
- https://ghidra.re/ghidra_docs/api/ghidra/program/model/pcode/HighFunction.html
- https://github.com/icsharpcode/ILSpy
- https://github.com/adoptium/temurin25-binaries/releases/tag/jdk-25.0.5%2B8