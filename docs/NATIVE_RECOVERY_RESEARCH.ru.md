Архив исследования предыдущей реализации. Текущий конвейер описан в CPP2IL_INTEGRATION.ru.md: Cpp2IL.Core встроен напрямую, Ghidra включена, Source единый. Старые пути Experimental и старые числа ниже относятся к предыдущей версии.

# Восстановление тел IL2CPP: исследование и реализация

Проверка: 11 ноября 2026. Цель — исходный Windows x64 Pixel Worlds из `Test with this/Pixel Worlds`. Файлы игры анализируются статически; EXE и GameAssembly.dll не запускаются.

## Почему прежний экспорт был почти пустым

Il2CppDumper восстанавливает managed-описания классов по metadata и native registration, но не восстанавливает произвольные тела методов. Dummy DLL с правильными именами и полями ещё не содержит IL исходной игры. dnSpy или ILSpy могут декомпилировать только тот IL, который реально записан в DLL. В прежнем приложении собственный анализатор принимал максимум одну значимую x64-инструкцию перед ret. Это и было основной причиной заглушек после успешного чтения metadata.

Для этой сборки есть ещё и обфускация: нестандартный зашифрованный контейнер metadata, индексы переменной ширины, переименованные runtime API и идентификаторы, лишние параметры, статические поля вместо простых констант и запутанные переходы.

## Что изучено в предоставленной Ghidra

`Ghidra/Features/Decompiler/src/decompile/cpp/flow.hh` описывает обход достижимых инструкций и построение блоков. `funcdata.hh`, `heritage.hh`, `action.hh`, `coreaction.cc` относятся к представлению функции, SSA и проходам анализа. `printc.hh` — C-backend печати выражений, условий, циклов и вызовов. В Java `DecompileResults.getHighFunction()` возвращает высокоуровневую функцию, а `getDecompiledFunction().getC()` — C-текст. Это полезные компоненты, но печать C не восстанавливает автоматически ссылки managed-объектов, Unity API, generics, GC и семантику C#.

Применён принцип отдельного анализа потока данных и CFG; код Ghidra не копировался. Собранный движок Ghidra не входит в приложение.

Первичные источники:

- [Ghidra FlowInfo](https://github.com/NationalSecurityAgency/ghidra/blob/master/Ghidra/Features/Decompiler/src/decompile/cpp/flow.hh)
- [Ghidra HighFunction](https://ghidra.re/ghidra_docs/api/ghidra/program/model/pcode/HighFunction.html)
- [Ghidra DecompileResults](https://ghidra.re/ghidra_docs/api/ghidra/app/decompiler/DecompileResults.html)
- [dnSpy](https://github.com/dnSpy/dnSpy)

## Новый собственный проход x64 → CIL → C#

`NativeImage` один раз разбирает PE. Диапазоны методов ограничиваются unwind-записями .pdata, следующими известными managed-адресами и границами исполняемых секций. Читать произвольные 64 байта после RVA больше не требуется.

`ScalarLifter` декодирует достижимые инструкции, строит граф переходов, отслеживает типизированные регистры и стек Windows x64. В точках слияния остаются только определённые на всех путях значения. Результат — CIL с типизированными локальными переменными и переходами, затем ILSpy печатает C#.

Поддержаны скалярные integer/float/double операции, чтения и записи распознанных полей, вложенные поля структур, условия, циклы, stack arguments, сохранение nonvolatile-регистров и прямые вызовы методов с единственным однозначным metadata-адресом. Вызов поддерживаемого тела может вести к ещё не восстановленному методу: восстановленная точка вызова не означает восстановление всего дерева вызовов. Записи ссылок, неоднозначные вызовы, неизвестные регистры/флаги, нативные указатели и неподдерживаемые инструкции отклоняются целиком.

Для проверенной сборки распознан null-throw helper `0x180911090`: цепочка приводит к созданию `System.NullReferenceException`, включая строковые аргументы по VA `0x185EB0B10` и `0x185EAF820`. Сопоставление применяется только к SHA256-проверенному профилю.

Неподдерживаемые тела остаются явными NotSupportedException. Их нельзя называть восстановленной логикой.

## Конкретно CharacterController2D

Проверены нативные методы:

| RVA | Нативное действие | Новый C# |
| --- | --- | --- |
| 0x29DFB60 | collisionState + флаг по смещению 0x1C, null-check | Чтение collisionState._0024UVle |
| 0x29DAC00 | collisionState + флаг 0x1A, null-check | Чтение collisionState._0024iQbD |
| 0x29D9EB0 | collisionState + флаг 0x10, null-check | Чтение collisionState._0024whLj |
| 0x29DFB40 | collisionState + флаг 0x11, null-check | Чтение collisionState._0024BRT |
| 0x29DB3C0 | movss из this+0x44 | Возврат _skinWidth |

Сложные методы движения используют статические поля обфускатора, непрямые вызовы, packed Vector2/Vector3 и runtime helpers. Они ещё не восстановлены собственным проверяемым проходом. Например, OnTriggerEnter2D по RVA 0x29DF470 содержит dispatcher с обратными переходами, проверку class initialization, статические значения и вызов через delegate pointer.

## Проверка нового Cpp2IL, а не только старого релиза

Исследована и собрана текущая development-ветка Cpp2IL, закреплённый commit `b5ad444b82267cb1e4b88b8b373c008105bdea52`. В ней присутствует `dll_il_recovery`: native → ISIL → CFG/analysis actions → CIL. Старые сообщения о полном отсутствии ISIL→CIL не описывают эту конкретную версию. См. [README закреплённой версии](https://github.com/SamboyCoding/Cpp2IL/blob/b5ad444b82267cb1e4b88b8b373c008105bdea52/README.md) и [IL recovery output](https://github.com/SamboyCoding/Cpp2IL/blob/b5ad444b82267cb1e4b88b8b373c008105bdea52/Cpp2IL.Core/OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs).

Первый запуск дошёл до модели типов, но не смог прочитать forwarding/vtable-диапазоны в нормализованном контейнере. По нативным функциям дополнительно восстановлены:

- vtableMethods: заголовок 0x310, добавка -0x770, 205512 encoded method references; GetMethodInfoFromVTableSlot по VA 0x1809748C0;
- interfaceOffsets: заголовок 0x19C, добавка +0x10AC, 18846 записей `UInt16 typeIndex + Int32 offset`, расширяемых до стандартных записей; GetInterfaceOffsetInfo по VA 0x180974FF0.

Все vtable-диапазоны и ссылки проверяются; пустые encoded entries 0/1 сохраняются. Отсутствующая таблица forwarding не выдаётся за заполненную: её диапазоны обнулены и пропуск указан в отчёте профиля.

После этого Cpp2IL завершил полный анализ примерно за 45 секунд. Но его итоговый счётчик успешных методов **не означает полноту**: он включает частичные результаты и пропуски. В CharacterController2D появились большие тела с присваиваниями, вызовами и ветвлениями, но также `Cpp2ILHelpers.NoteDecompilerIssue`, неразобранные native loads/calls и ILSpy diagnostics. Значения default рядом с такими пометками не являются восстановленными игровыми значениями.

Поэтому приложение сохраняет этот результат в отдельный `Scripts/Experimental/Source`. Отчёт помечает методы `experimental-incomplete` либо `experimental-unverified`; проверенных исполняемых методов у этого backend — 0. Он включён в Settings → Experimental native reconstruction. В `Scripts/Source` остаётся результат собственного ограниченного прохода. Эти две оценки нельзя складывать и объявлять полным восстановлением игры.

В поставляемой версии Cpp2IL изменены только target framework маленькой naming-библиотеки для SDK 10 и обработка пустого результата анализа: вместо молчаливого пустого тела записывается явное исключение. Изменения воспроизводятся `setup-native.ps1`; MIT-лицензия находится в `../licenses/Cpp2IL.txt`.

## Что остаётся для полного игрового кода

Необходимы восстановление строковых литералов/metadata usages, точное сопоставление runtime helper-функций и вызовов через указатели, обработка static constructors и констант обфускатора, packed value types, generic sharing, исключений и GC, затем проверка семантики, ссылок Unity и поведения. Нельзя получить эти гарантии простой заменой расширения .dll или удалением пометок о неразобранных операциях. Автоматический экспорт из папки теперь выполняет более глубокую реконструкцию, но полной работающей Mono-копии Pixel Worlds пока нет.
