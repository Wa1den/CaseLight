# Собранные плагины

Готовые к установке плагины, чтобы не собирать их из исходников. В релизы программы они не
входят. Папка плагина копируется целиком в `%AppData%\CaseLight\plugins\` и включается в
разделе «Плагины».

| Папка | Исходники | Версия |
| --- | --- | --- |
| [plugins/NuPhy](plugins/NuPhy) | [plugins/NuPhy](../plugins/NuPhy) | 1.0.1, контракт 1 |
| [plugins/Audio](plugins/Audio) | [plugins/Audio](../plugins/Audio) | 1.0.2, контракт 1, нужен CaseLight 1.12.0 |
| [plugins/Equalizer](plugins/Equalizer) | [plugins/Equalizer](../plugins/Equalizer) | 1.0.0, контракт 1, нужен CaseLight 1.13.0 |
| [plugins/CapsLock](plugins/CapsLock) | [plugins/CapsLock](../plugins/CapsLock) | 1.0.0, контракт 1, нужен CaseLight 1.12.0 |

Сборка пересобирается после любой правки исходников плагина или контракта той же командой:

```
dotnet publish plugins/NuPhy -c Release -o prebuilt/plugins/NuPhy
```

Из результата в репозиторий идут `.dll` и `.deps.json`; `.pdb` остаётся вне его. У
плагинов Audio и Equalizer в папке лежат и библиотеки NAudio: без них они не запустятся.
