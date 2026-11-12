# Liquid

Основная инструкция со скриншотами: [README](../README.md).

- **Unity project** — основные папки Unity-проекта.
- **Specific Asset Only** — Texture2D, Sprite, TextAsset, Audio, Prefabs, Mesh, Material, Resources и Scripts из фактических файлов игры. Scripts требует подходящих IL2CPP binary и metadata.
- **Scripts only** — восстановление IL2CPP в C# с отчётом о полноте методов.
- **Asset Editor** — вкладки data.unity3d/sharedassets/resources/bundle, поиск, предпросмотр и экспорт, замена, Undo и сохранение изменённых файлов обратно в папку игры. До Save изменения остаются в памяти. Постоянные бекапы не создаются. Панель предпросмотра изменяется разделителями по ширине и высоте; список прокручивается без страниц. Текстовые TextAsset можно редактировать и сохранять через Save или Ctrl+S.

PNG-замены сохраняются как RGBA32; отдельный спрайт заменяет свой участок атласа с проверкой размера. Текстовые и бинарные TextAsset, typed JSON и raw поддерживаются. Аудио можно прослушать и экспортировать; прямой импорт WAV в FSB не реализован. Prefabs экспортируются как JSON иерархии GameObject, Material — YAML; это исследовательские экспорты со ссылками. Для Unity-проекта используйте соответствующий режим. Каталоги и CRC Addressables автоматически не переписываются.

Сборка из папки Liquid:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
```

Нужен .NET SDK 10 и соседние исходники Cpp2IL-development/Il2CppDumper-master. При первом запуске сборки нужен интернет для загрузки компонентов. Результат: dist/Liquid.exe. Для передачи приложения отправляйте всю папку dist, удобно ZIP-архивом. Получателю нужен Windows x64 с WebView2 Runtime.

Языки интерфейса: английский, русский, немецкий, испанский, украинский, китайский (упрощённый) и французский. Вкладка истории экспорта называется Exports.

Если исходники Cpp2IL/Il2CppDumper отсутствуют, сохраните готовый dist/worker и собирайте редактор командой `powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained -AppOnly`.
