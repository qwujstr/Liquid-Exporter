# Разбор локальных Il2CppDumper и AssetRipper

Дата: 11 ноября 2026 года. Основание: исходники именно этих локальных копий.

## Область проверки

Проведены инвентаризация проектов и зависимостей, чтение ключевых реализаций и прослеживание основных путей выполнения. Подробно разобраны входные точки, IL2CPP metadata/registration, чтение контейнеров и объектов Unity, загрузка сборок, MonoBehaviour, обработка объектов и экспорт. Вспомогательные генераторы, расширения, интерфейс и тесты рассмотрены по структуре и выбранным реализациям. Это архитектурный разбор, а не заявление о построчной проверке каждого файла и каждой ветки. Исходники внешних NuGet-зависимостей в этих папках не представлены полностью; их внутреннее устройство не следует считать проверенным.

Сборка, тесты и обработка настоящего игрового билда не выполнялись. Установлен SDK 10.0.401; основной TargetFramework AssetRipper — net11.0. Для проверки выполнения нужна подходящая среда и игровые входные данные. Код проектов не изменён.

Все пути ниже указаны относительно корня соответствующего проекта:

- `Il2CppDumper-master/Il2CppDumper-master/`
- `AssetRipper-master/AssetRipper-master/`

## 1. Назначение и границы

| Вопрос | Il2CppDumper | AssetRipper |
|---|---|---|
| Главный вход | IL2CPP binary + global-metadata.dat | Файлы/директории Unity-билда, bundles и serialized files |
| Основная задача | Связать metadata с нативными таблицами и адресами | Прочитать граф Unity-объектов и преобразовать его для экспорта |
| Скрипты | Сигнатуры, поля, атрибуты, DLL-заглушки | Mono-сборки либо сборки, созданные Cpp2IL |
| Текстуры/сцены/модели | Не основная функция | Чтение, преобразование и экспорт |
| Результат | dump.cs, DummyDll, il2cpp.h, JSON для дизассемблеров | Unity-проект либо отдельный контент |
| Декомпиляция исходной логики IL2CPP | Не реализована | В стандартном пути пустые методы; расширения восстановления подключаются отдельно |

AssetRipper не использует Il2CppDumper как свой IL2CPP backend. Его `IL2CppManager` вызывает Cpp2IL/LibCpp2IL. Поэтому последовательный запуск этих программ сам по себе не означает автоматическую интеграцию их результатов.

## 2. Il2CppDumper: цепочка выполнения

```text
Program.Main
  -> config.json и пути
  -> Init
     -> Metadata(global-metadata.dat)
     -> выбор ExecutableFormats по magic
     -> SetProperties(version, metadataUsagesCount)
     -> обработка признаков memory dump
     -> PlusSearch -> Search -> SymbolSearch -> ручные registration addresses
     -> Il2Cpp.Init
  -> Dump
     -> Il2CppExecutor
     -> Il2CppDecompiler.Decompile
     -> StructGenerator.WriteScript
     -> DummyAssemblyExporter.Export
```

### Метаданные

`Il2Cpp/Metadata.cs` проверяет magic `0xFAB11BAF` и версию; локальный код принимает версии 16–31. Это проверка версии формата, а не гарантия совместимости со всеми Unity-билдами этого диапазона. Для 24.x есть уточнение подверсий по структуре заголовка и размерам таблиц.

Из metadata читаются images, assemblies, types, methods, parameters, fields, properties, events, generics, interfaces, nested types, default values и строки. Для разных версий различается хранение metadata usages и custom attributes; с версии 29 используется сериализованный поток данных атрибутов.

`Il2Cpp/MetadataClass.cs` и `Il2Cpp/Il2CppClass.cs` описывают структуры. `IO/BinaryStream.cs` читает их через reflection с учётом `VersionAttribute`, `ArrayLengthAttribute` и разрядности указателей. В этом reader поля Int64/UInt64 при чтении структур обрабатываются как pointer-sized значения — важная особенность при изменении описаний бинарных структур.

### Нативные файлы

`Program.Init` выбирает PE, ELF32/64, Mach-O32/64, FAT Mach-O, NSO или WebAssembly. NSO предварительно распаковывается; WASM преобразуется в представление памяти. Форматные реализации обеспечивают перевод виртуальных адресов в смещения файла (`MapVATR`) и обратно (`MapRTVA`), сведения о секциях и способы поиска registration.

ELF/NSO содержат обработку релокаций и динамических таблиц; Mach-O имеет отдельные алгоритмы поиска. Не каждый формат реализует все варианты поиска: например, PE.Search и PE.SymbolSearch возвращают false.

`Utils/SectionHelper.cs` выполняет эвристический поиск. Для старых версий используются количества методов и типов, таблицы указателей и проверка принадлежности адресов секциям. В новых версиях поиск CodeRegistration опирается на строку `mscorlib.dll` и цепочки ссылок на модули; используются также количество images и версия структуры.

Если PlusSearch PE не сработал на Windows, `Utils/PELoader.cs` может загрузить библиотеку через Windows LoadLibrary и скопировать секции из памяти. Это существенно отличается от обычного чтения файла и имеет побочные эффекты загрузки DLL. В ходе этого исследования loader не запускался.

### Соединение metadata и binary

`Il2Cpp/Il2Cpp.cs::Init` читает CodeRegistration и MetadataRegistration, затем таблицы типов, generic instantiations, method specs, generic method pointers, invokers, field offsets и RGCTX. Для новых версий указатели методов сгруппированы по CodeGenModule.

`GetMethodPointer` до 24.2 использует methodIndex, начиная с 24.2 — имя image и младшие 24 бита method token с поправкой на нумерацию от единицы. Нельзя безусловно применять старую схему ко всем версиям.

`GetFieldOffsetFromIndex` учитывает хранение offsets непосредственно или через таблицы указателей. Для нестатических полей value type вычитает размер объектного заголовка: 8 либо 16 байт. Эти offsets относятся к расположению в памяти, а не к позиции поля в сериализованном Unity asset.

`Utils/Il2CppExecutor.cs` соединяет обе модели: разрешает имена, nested/generic типы, generic context, RGCTX, значения констант и ссылки на definitions.

### Выходные данные

- `Outputs/Il2CppDecompiler.cs`: текстовая декларация типов и членов в dump.cs, modifiers, offsets, attributes по настройкам. Название Decompiler не означает восстановление алгоритмов методов.
- `Outputs/StructGenerator.cs`: структуры runtime-типов и C-сигнатуры методов, generic/RGCTX сведения, привязка metadata и string literals к адресам; формирует il2cpp.h, script.json, stringliteral.json.
- `Utils/DummyAssemblyGenerator.cs`: многопроходное построение .NET-сборок через Mono.Cecil, включая типы, наследование, generics, поля, методы, свойства, события и доступные атрибуты. Добавляет вспомогательные address/offset/token атрибуты.
- Тела обычных методов создаются как заглушки: ret, null либо default value. Исходный машинный код не превращается здесь в C#.
- Python-скрипты и плагин Binary Ninja используют полученные описания для разметки бинарника в соответствующем инструменте.

Фактический флаг генерации структур в `Config.cs` — GenerateStruct; README местами использует прежнее название GenerateScript. При работе следует ориентироваться на код и config.json.

`DummyAssemblyExporter` меняет текущую рабочую директорию и пересоздаёт каталог DummyDll. При превращении программы в библиотеку этот глобальный побочный эффект нужно учитывать.

## 3. AssetRipper: слои решения

| Группа | Роль |
|---|---|
| GUI.Free, GUI.Web, Web | Запуск локального ASP.NET интерфейса/API, просмотр и команды |
| Configuration, Import.Configuration, Processing.Configuration, Export.Configuration | Настройки этапов и хранилище сериализованных данных |
| IO.Files | Определение форматов, потоки, распаковка, serialized file metadata |
| Assets | Базовые объекты, коллекции, bundles, PPtr, traversal, cloning |
| Import | Распознавание платформы, загрузка сборок, создание объектов и пользовательских структур |
| SerializationLogic | Правила сериализуемых C#-полей Unity |
| SourceGenerated.Extensions | Расширения version-specific классов, работа с ресурсами, mesh/texture/animation и прочими данными |
| Processing | Восстановление редакторских представлений и группировка объектов |
| Export | Общая конфигурация, стек обработчиков и инфраструктура ILSpy |
| Export.UnityProjects | Unity YAML, importers, .meta, сцены, prefabs, scripts и post-export |
| Export.PrimaryContent | Отдельные изображения, аудио, GLB, тексты, scripts и JSON |
| Export.Modules.Textures/Audio/Models | Преобразование соответствующих ресурсов |
| Yaml, Numerics | YAML-модель и математические операции |
| AssemblyDumper и утилиты | Генерация библиотеки схем Unity по TypeTree, подготовка данных и пакетов |
| DocExtraction | Извлечение сведений для документации/генерации |
| SourceGenerator-проекты | Генерация кода при сборке: файловая система, расширения, processing, UI, локализации, лицензии |
| Tools.* | Извлечение файлов, raw textures, JSON, TypeTree, графа зависимостей, CAB map, системные проверки |
| *.Tests | Проверки потоков, сериализации, разрешения ссылок, экспорта, YAML, математики и генераторов |
| UnityEngine | Вспомогательные определения UnityEngine внутри решения |

### Главная цепочка

```text
GUI.Free/Program.cs
  -> WebApplicationLauncher.Launch
  -> GameFileLoader.LoadAndProcess
  -> ExportHandler.LoadAndProcess
     -> GameStructure.Load
        -> ZipExtractor / PlatformChecker
        -> PlatformGameStructure.CollectFiles
        -> MonoManager / IL2CppManager / BaseManager
        -> GameBundle.InitializeFromPaths
     -> GameData.FromGameStructure
     -> ExportHandler.Process
  -> ExportHandler.Export
     -> ProjectExporter: экспортные коллекции и ссылки
     -> экспорт ресурсов / YAML / .meta
     -> post-export
```

`ExportHandler.LoadProcessAndExport` предоставляет прямой программный вход без взаимодействия с веб-интерфейсом. Для экспорта отдельного контента используется другой путь — `PrimaryContentExporter`.

### Распознавание и чтение

`Import/Platforms` содержит отдельные структуры Windows, Linux, macOS, Android, iOS, WebGL и других платформ. Они определяют каталоги и файлы ассетов, managed assemblies, native binary, metadata и ресурсы. Например, WindowsGameStructure ищет GameAssembly.dll, UnityPlayer.dll и `<Game>_Data/il2cpp_data/Metadata/global-metadata.dat`.

`IO.Files/SchemeReader.cs` распознаёт формат по содержимому: UnityFS, Raw/Web bundles, Archive bundle, WebFile, GZip, Brotli, SerializedFile. Нераспознанные данные сохраняются как ResourceFile. `FileContainer` позволяет читать вложенные файлы рекурсивно.

В UnityFS отдельно читаются header, blocks info и directory nodes. `BundleFileBlockReader` извлекает диапазоны нужных блоков; поддерживает none/LZMA/LZ4/LZ4HC, а в отдельной fallback-ветке проверяет Zstd. LZHAM помечен как неподдерживаемый. Есть кэш последнего блока, pooled buffers, частичные потоки и временные файлы для больших данных.

`SerializedFile` представляет header и metadata: UnityVersion, BuildTarget, endian, types, objects, external dependencies и reference types. Это ещё не семантические GameObject или Texture2D. `SerializedAssetCollection.FromSerializedFile` создаёт AssetInfo для каждого объекта и передаёт его bytes фабрике.

`GameBundle.InitializeFromPaths` сначала загружает файлы и внешние зависимости, затем создаёт коллекции, затем инициализирует dependency lists. Hooks `GameInitializer` добавляют встроенные engine resources и при настройке меняют версии коллекций.

### Модель объектов и ссылки

Иерархия — GameBundle/Bundle -> AssetCollection -> IUnityObjectBase. AssetInfo связывает объект с коллекцией, PathID и ClassID. Вложенные сериализуемые структуры представлены IUnityAssetBase.

PPtr хранит FileID и PathID. FileID 0 означает текущий файл; другие значения выбирают внешнюю зависимость. PathID 0 — null. PathID не является нативным адресом, metadata token либо готовым экспортным fileID.

`GameAssetFactory` создаёт engine-объекты через AssetRipper.SourceGenerated; при необходимости использует TPK TypeTree. Неизвестные объекты сохраняются как UnknownObject, нечитаемые — как UnreadableObject. Проверяется потребление ожидаемого количества bytes, предусмотрены отдельные совместимые исключения.

### Скрипты и MonoBehaviour

`MonoManager` загружает реальные managed assemblies через AsmResolver. `IL2CppManager` инициализирует LibCpp2IL, выполняет Cpp2IL processing layers и строит AsmResolver assemblies. Стандартные layers — анализ атрибутов и исправление имён overrides.

ScriptContentLevel:

- Level0: отключение загрузки scripts через конфигурацию импорта.
- Level1: методы заменяются заглушками при processing.
- Level2: стандартный режим; реальные методы для Mono, пустые методы для IL2CPP.
- Level3: выбор RecoveryProcessingLayers/RecoveryOutputFormat, если они установлены. В противном случае используются стандартные layers/output; само переключение уровня не гарантирует восстановления логики.

`GameAssetFactory.ReadMonoBehaviour` сначала читает базовую Unity-часть. Если есть TypeTree — создаёт SerializableStructure по нему. Иначе сохраняет UnloadedStructure для отложенного чтения после загрузки MonoScript и зависимостей.

`UnloadedStructure.LoadStructure` разрешает тип через MonoScript/AssemblyManager, создаёт его сериализуемое описание и читает оставшиеся bytes. `SerializationLogic.FieldSerializer` учитывает наследование, generics, enum, arrays/List, SerializeField, SerializeReference, fixed buffers, UnityEngine.Object и ограничения конкретной версии Unity. Static/const/readonly/NonSerialized не входят в обычный набор сериализуемых полей. SerializeReference использует отдельный managed reference registry.

Для чтения значений пользовательских полей нужны их корректные типы и порядок сериализации, а не исходные тела методов. Поэтому DLL-заглушки полезны для ассетов, хотя не восстанавливают поведение игры.

### Processing

Порядок задаёт `ExportHandler.GetProcessors` и он является частью алгоритма.

Сначала выполняется подготовка сборок: polyfills, исправления свойств/имён, forwarding assemblies, optional method stubbing, обработка ref returns, unmanaged constraints, nullable/publicizing и некоторых атрибутов.

Затем: SceneDefinition -> OriginalPath -> MainAsset -> AnimatorController -> AudioMixer -> EditorFormat -> LightingData -> Prefab -> Sprite -> ScriptableObject.

EditorFormatProcessor восполняет отсутствующие в player build editor-only поля: часть вычисляет из оставшихся данных, часть задаёт default values. Примеры — Euler hints, порядок Transform, flags/settings. Это реконструкция пригодного редакторского представления, а не точное возвращение всех исходных редакторских данных.

PrefabProcessor создаёт описания иерархий сцен и prefabs, при необходимости генерирует PrefabInstance и отсутствующие Transform. AnimatorControllerProcessor строит редакторские структуры controller/state machine. LightingDataProcessor собирает связанные данные освещения. SpriteProcessor связывает sprites, textures и atlases.

### Экспорт Unity-проекта

ProjectExporter использует ObjectHandlerStack: для runtime-типа перебирает подходящие exporters, пока один не создаст IExportCollection. Порядок регистрации и финальные overrides значимы.

Экспортная коллекция — группа объектов, попадающих в общий файл/набор связанных файлов: например, сцена или texture со sprites. Сначала создаются все коллекции с устранением повторного включения объектов; затем ProjectAssetContainer строит отображение объектов в коллекции.

При YAML traversal ProjectYamlWalker разрешает исходный PPtr и вызывает CreateExportPointer. Внутри одной коллекции формируется локальный fileID; между коллекциями — fileID/GUID/type. .meta содержит GUID и importer. Отсутствующие зависимости становятся missing references; одной распаковки bundle для восстановления проекта недостаточно.

Unity YAML exporter является общим fallback. Для отдельных типов предусмотрены textures, audio, fonts, text/video, terrain, scripts, scenes, shader exporters и другие обработчики. Не следует переносить список форматов PrimaryContent в список форматов UnityProject: например, обычный путь IMesh в UnityProject использует streamed YAML, тогда как PrimaryContent умеет GLB.

`ScriptExporter` выбирает decompile/save/skip по режиму и reference assemblies. `ScriptDecompiler` использует ILSpy. Полезность C# определяется содержимым исходной managed assembly: декомпилятор не создаёт потерянную логику из пустого метода.

Post-export создаёт сведения о версии проекта и packages, переносит StreamingAssets по настройкам, сохраняет нужные DLL и карту PathID.

### Ресурсы и отдельный контент

- Textures: TextureConverter получает данные, выбирает TextureFormat/GraphicsFormat и decoder; учитывает crunch, normal maps, ориентацию изображения. Сам декодер ряда форматов — внешняя зависимость.
- Audio: AudioClipDecoder извлекает raw data, распознаёт старые форматы и FSB5, использует Fmod5Sharp для поддерживаемых кодеков. AudioConverter выполняет последующее преобразование.
- Models: GLB builders собирают mesh, submeshes, материалы и иерархии. Координаты переводятся из Unity в glTF с изменением handedness; tangents/quaternions также требуют преобразования.
- PrimaryContentExporter применяет собственный стек extractors и группировку; часть объектов пропускается, для остальных есть специализированный экспорт либо JSON fallback.

### Генерация схем Unity

`AssetRipper.AssemblyDumper/Program.cs` запускает последовательность passes над `type_tree.tpk`: нормализация деревьев и групп по версиям, выделение subclasses, interfaces, inheritance, fields, enums, constructors, read/write/dependency/reset/copy/traversal methods, PPtr properties, специальные исправления Unity-типов, factories, сохранение assembly и документации.

Рабочий importer использует пакет `AssetRipper.SourceGenerated` через проект расширений. Следовательно, значительная доля конкретных version-specific реализаций чтения поступает из пакета, а не лежит в папке решения в виде обычных .cs-файлов. AssemblyDumper объясняет происхождение этих реализаций, но не является дампером игровой Assembly-CSharp.

## 4. Практические выводы для дальнейшей разработки

1. Слой IL2CPP типов/адресов и слой сериализованных assets следует связывать явно через assembly/type identity; их индексы и offsets не взаимозаменяемы.
2. Для собственного чтения IL2CPP сначала нужны metadata parser, binary mapping и registration resolution, затем единая модель типов. Экспорт — следующий слой.
3. Для собственного asset pipeline нужны контейнеры, сериализованные объекты, внешние ресурсы, граф ссылок, version-specific схемы и editor reconstruction. Копирование только распаковщика не заменяет эти этапы.
4. В AssetRipper явные точки расширения — IAssemblyManager, AssetFactoryBase, IAssetProcessor, IAssetExporter/IExportCollection и ExportHandler; BeforeExport и recovery hooks позволяют подключать дополнительную функциональность.
5. Замена Cpp2IL на Il2CppDumper потребует адаптера между Mono.Cecil-моделью результата и AsmResolver/RuntimeContext либо загрузки созданных DLL с корректным разрешением зависимостей. Готовая прямая замена в рассмотренном пути не предусмотрена.
6. Ни результат DummyDll, ни наличие полного дерева Assets сами по себе не доказывают восстановление исполняемого исходного проекта игры.

## 5. Проверка и ограничения

Просмотрены примеры тестов SerializedFile round-trip, разрешения объектов, экспорта через VirtualFileSystem и карта остальных тестовых проектов. Наличие тестов не равнозначно их успешному прохождению в этой среде.

Для практической верификации необходимы отдельно: подходящий SDK, восстановление зависимостей, запуск существующих тестов и проверка на известном Unity-билде с последующим открытием экспортированного проекта в соответствующем Unity Editor. Эти действия в текущий статический разбор не входят.

README Il2CppDumper заявляет Unity 5.3–2022.2, README AssetRipper — 3.5.0–6000.4.X. Это сведения локальной документации, а не проведённая здесь проверка всех версий. Код metadata parser Il2CppDumper уже содержит поддержку номера 31; по одному номеру нельзя выводить полную поддержку конкретного релиза Unity.
