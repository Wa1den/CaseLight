using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CaseLight.Core.Capture;

namespace CaseLight.Core.Text;

/// <summary>
/// Strings by key. Two languages are built in and written out as JSON next to the config
/// on first run; from then on the folder is what gets read. Any other file in that folder
/// joins the language list, so a translation needs no rebuild.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Bumped whenever the built-in strings change. Files on disk deliberately win over
    /// the built-ins so translations can be corrected - but that also meant an old file
    /// silently shadowed newly reworded labels, so a mismatched version rewrites it. Only
    /// the two built-in files are rewritten; added languages are left alone.
    /// </summary>
    const string Version = "3";

    /// <summary>
    /// Bookkeeping entries rather than translated text: the version a file was written
    /// from, and the name to show in the language list.
    /// </summary>
    const string VersionKey = "_version";
    const string NameKey = "_name";

    /// <summary>The languages the program carries inside itself.</summary>
    static readonly string[] BuiltinCodes = { "ru", "en" };

    public static string Language { get; private set; } = "ru";

    static Dictionary<string, string> _current = new();

    /// <summary>Built-ins plus whatever usable files the folder holds; rebuilt on load.</summary>
    static string[] _available = BuiltinCodes;
    static readonly Dictionary<string, string> _names = new();

    /// <summary>
    /// Set by the application on startup. The library has no config file of its own, and
    /// two programs share these strings, so neither may assume the other's folder.
    /// </summary>
    public static string Directory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaseLight", "lang");

    public static void Configure(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory)) Directory = directory;
    }

    public static string[] Available => _available;

    public static string DisplayName(string code) =>
        _names.TryGetValue(code, out var name) ? name : BuiltinName(code);

    static string BuiltinName(string code) => code switch
    {
        "ru" => "Русский",
        "en" => "English",
        _ => code
    };

    public static void Load(string language)
    {
        WriteDefaults();
        Scan();

        Language = Array.IndexOf(_available, language) >= 0 ? language : "ru";

        // английский подложкой: строка, пропущенная в переводе, показывается
        // по-английски, а не ключом
        var strings = English();
        if (Array.IndexOf(BuiltinCodes, Language) >= 0) Overlay(strings, Builtin(Language));
        Overlay(strings, ReadLocale(Language));

        _current = strings;
    }

    static void Overlay(Dictionary<string, string> onto, Dictionary<string, string>? from)
    {
        if (from == null) return;
        foreach (var kv in from) onto[kv.Key] = kv.Value;
    }

    /// <summary>Missing keys fall back to the key itself, so nothing ever renders blank.</summary>
    public static string T(string key) => _current.TryGetValue(key, out var v) ? v : key;

    /// <summary>
    /// A translated pair written inline, for one-off runtime text: log lines, device
    /// statuses, the words inside a statistics line.
    ///
    /// These are not worth dictionary keys. A key pays off when a string is reused or
    /// handed to a translator; a hundred and some log messages that each appear once would
    /// only turn into a hundred names nobody ever looks up, and the English would sit far
    /// away from the code that emits it.
    ///
    /// Anything other than Russian gets the English half: a language added as a file has
    /// no translation for these, so they follow the same English fallback as the keys.
    /// </summary>
    public static string P(string ru, string en) => Language == "ru" ? ru : en;

    /// <summary>
    /// Builds the language list out of the folder: the file name is the code, "_name" is
    /// what the list shows. Everything that reads as a translation is offered, even a
    /// half-finished one - the keys it lacks come from English.
    /// </summary>
    static void Scan()
    {
        _names.Clear();
        foreach (var code in BuiltinCodes) _names[code] = BuiltinName(code);

        var extra = new List<string>();
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(path);
                if (code.Length == 0) continue;

                var loaded = ReadLocale(code);
                if (loaded == null) continue;

                if (Array.IndexOf(BuiltinCodes, code) < 0) extra.Add(code);

                if (loaded.TryGetValue(NameKey, out var name) && name.Trim().Length > 0) _names[code] = name.Trim();
                else if (!_names.ContainsKey(code)) _names[code] = code;
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", P("не удалось прочитать папку переводов: ",
                                  "could not read the translation folder: ") + ex.Message);
        }

        extra.Sort(StringComparer.OrdinalIgnoreCase);
        _available = BuiltinCodes.Concat(extra).ToArray();
    }

    /// <summary>
    /// How many known keys a file must carry to be taken for a translation. Verifying the
    /// whole set would reject a partial translation that works perfectly well, while this
    /// much keeps an unrelated JSON file, an exported config for one, out of the list.
    /// </summary>
    const int MinKnownKeys = 8;

    /// <summary>Reads one file of the folder; null if it is missing or is not a translation.</summary>
    static Dictionary<string, string>? ReadLocale(string code)
    {
        string path = Path.Combine(Directory, code + ".json");
        if (!File.Exists(path)) return null;

        Dictionary<string, string>? loaded;
        try
        {
            // нестроковое значение бросает исключение здесь - это и есть проверка формата
            loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", P("не удалось прочитать перевод ",
                                  "could not read translation ") + Path.GetFileName(path) + ": " + ex.Message);
            return null;
        }

        if (loaded == null || !IsLocale(loaded))
        {
            ProbeLog.Log("lang", P("не похоже на перевод, файл пропущен: ",
                                  "not a translation, file skipped: ") + Path.GetFileName(path));
            return null;
        }

        return loaded;
    }

    static bool IsLocale(Dictionary<string, string> loaded)
    {
        var known = English();
        int hits = 0;
        foreach (var key in loaded.Keys)
            if (known.ContainsKey(key) && ++hits >= MinKnownKeys) return true;

        return false;
    }

    static void WriteDefaults()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // without the relaxed encoder every Cyrillic character lands as a numeric
            // escape, which makes a file meant for hand editing unreadable
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            foreach (var code in BuiltinCodes)
            {
                string path = Path.Combine(Directory, code + ".json");
                if (File.Exists(path) && CurrentVersionOf(path) == Version) continue;

                File.WriteAllText(path, JsonSerializer.Serialize(Builtin(code), opts));
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", P("не удалось записать переводы: ",
                                  "could not write the translations: ") + ex.Message);
        }
    }

    static string CurrentVersionOf(string path)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return d != null && d.TryGetValue(VersionKey, out var v) ? v : "";
        }
        catch
        {
            return "";
        }
    }

    static Dictionary<string, string> Builtin(string code)
    {
        var d = code == "en" ? English() : Russian();
        d[NameKey] = BuiltinName(code);
        d[VersionKey] = Version;
        return d;
    }

    static Dictionary<string, string> Russian() => new()
    {
        ["app.title"] = "CaseLight — подсветка корпуса",

        ["nav.canvas"] = "Отображать холст",

        ["bar.start"] = "Старт",
        ["bar.stop"] = "Стоп",
        ["bar.fit"] = "Центрировать холст",
        ["bar.screen"] = "Отображать данные с экрана",
        ["bar.screen.note"] = "Требует запущенной раскраски.",
        ["bar.apply"] = "Применить",
        ["bar.cancel"] = "Отмена",
        ["bar.dirty"] = "Есть несохранённые изменения",

        ["tab.main"] = "Основное",
        ["tab.devices"] = "Устройства",
        ["tab.capture"] = "Захват",
        ["tab.crop"] = "Кадрирование",
        ["tab.brightness"] = "Яркость",
        ["tab.color"] = "Цвета",
        ["tab.test"] = "Тест размещения",
        ["tab.power"] = "Питание",
        ["tab.about"] = "О программе",

        ["main.window"] = "Окно",
        ["main.tray"] = "Сворачивать в трей",
        ["main.tray.note"] = "Крестик прячет окно, программа продолжает работать. Выход - через меню значка в трее.",
        ["main.startmin"] = "Запускать свёрнутым",
        ["main.startup"] = "Запуск",
        ["main.autostart"] = "Запускать вместе с Windows",
        ["main.autostart.note"] = "Подсветкой управляет OpenRGB, поэтому автозапуск CaseLight имеет смысл только вместе с автозапуском OpenRGB.",
        ["main.autopaint"] = "Сразу начинать раскраску",
        ["main.server"] = "Сервер OpenRGB",
        ["main.serverstart"] = "Запускать OpenRGB, если он не запущен",
        ["main.serverstart.note"] = "Сервер запускается с ключами --server --startminimized: первый открывает порт 6742, второй убирает окно.",
        ["main.admin"] = "Запускать от администратора с запросом",
        ["main.admin.note"] = "Права администратора нужны OpenRGB для доступа к шине SMBus, через которую управляется оперативная память. При каждом запуске сервера будет запрос UAC.",
        ["main.task"] = "Запускать от администратора автоматически",
        ["main.task.note"] = "Создаёт задание в планировщике Windows для автоматического запуска OpenRGB от администратора, с однократным подтверждением.",
        ["main.path"] = "Путь к OpenRGB.exe",
        ["main.path.note"] = "Заполняется найденным файлом. Пустое поле - искать заново при каждом запуске: сначала в Program Files и %LocalAppData%\\Programs, затем в записях об удалении в реестре.",
        ["main.find"] = "Найти",
        ["main.launch"] = "Запустить сейчас",
        ["main.reconnect"] = "Переподключиться",
        ["main.settings"] = "Настройки",
        ["main.settings.note"] = "Один файл со всем: раскладка, размеры монитора, цвета, захват, питание.",
        ["main.export"] = "Экспорт…",
        ["main.import"] = "Импорт…",
        ["main.reset"] = "По умолчанию",
        ["main.reset.note"] = "Возвращает настройки к стандартным значениям. Раскладка фигур, размер монитора, выбранный экран, язык и положение окна не затрагиваются; автозапуск программы и задание планировщика выключаются. Как и любая другая правка, применяется сразу, а на диск попадает по кнопке «Применить».",
        ["main.reset.confirm"] = "Вернуть настройки к стандартным значениям? Раскладка, выбранный экран и язык не изменятся.",
        ["main.exporttitle"] = "Экспорт настроек CaseLight",
        ["main.importtitle"] = "Импорт настроек CaseLight",
        ["main.filter"] = "Настройки CaseLight (*.json)|*.json",
        ["main.logs"] = "Журнал",
        ["main.logs.note"] = "Рядом с журналом лежат файл настроек config.json и папка lang с переводами. Ссылка открывает эту папку.",
        ["main.log"] = "Вести журнал",
        ["main.language"] = "Язык",
        ["main.language.note"] = "Переводы лежат в JSON-файлах в папке lang рядом с настройками. Добавленный туда файл появляется в списке при следующем запуске, непереведённые строки берутся из английского. Как сделать перевод, описано в README.",

        ["devices.fixtures"] = "Фигуры",
        ["devices.fixtures.note"] = "Одна фигура на каждое светящееся устройство. Параметры выбранной фигуры открываются панелью поверх холста.",
        ["devices.add"] = "Добавить",
        ["devices.copy"] = "Копия",
        ["devices.remove"] = "Удалить",
        ["devices.showdisabled"] = "Отображать отключённые",
        ["devices.showdisabled.note"] = "Фигуры, снятые с раскраски, перестают рисоваться на холсте. Выбранная фигура рисуется в любом случае.",

        ["capture.source"] = "Источник кадров",
        ["capture.fromrimlight"] = "Получать от Rimlight",
        ["capture.auto"] = "Свой захват: автоматически",
        ["capture.dda"] = "Свой захват: только DDA",
        ["capture.wgc"] = "Свой захват: только WGC",
        ["capture.gdi"] = "Свой захват: только GDI",
        ["capture.method"] = "Метод",
        ["capture.method.note"] = "От Rimlight кадры приходят через разделяемую память, свой захват при этом не работает. «Автоматически» держит DDA и WGC вместе и переходит на GDI, когда те перестают выдавать кадры.",
        ["capture.screen"] = "Экран",
        ["capture.screen.note"] = "Монитор для захвата. При режиме получения от Rimlight выбор определяется им.",
        ["capture.fps"] = "Максимум кадров в секунду",
        ["capture.fps.free"] = "без ограничения",
        ["capture.fps.note"] = "Ограничивает частоту, с которой кадры сводятся к цветам зон, и вместе с ней нагрузку на видеокарту. Без ограничения кадры идут с той частотой, с какой приходят. Ограничение оплачивается задержкой: кадр, пришедший раньше срока, отбрасывается, и цвет ждёт следующего. Медленным устройствам задаётся свой делитель в параметрах фигуры.",
        ["capture.radius"] = "Область выборки",
        ["capture.radius.note"] = "Размер участка экрана, усредняемого для одного диода. При малом значении цвет меняется от любого движения в кадре, при большом усредняется до однородного оттенка.",
        ["capture.stats"] = "Статистика",
        ["capture.rect"] = "Прямоугольник монитора: {0} × {1} мм",

        ["stats.source"] = "Источник",
        ["stats.state"] = "Состояние",
        ["stats.frames"] = "Кадры",
        ["stats.rate"] = "Частота",
        ["stats.latency"] = "Задержка",
        ["stats.leds"] = "Диодов",

        ["crop.head"] = "Фильм шире экрана идёт с чёрными полосами сверху и снизу, и диоды, читающие эти места, остаются тёмными. Программа находит границы картинки и переносит выборку внутрь неё.",
        ["crop.enable"] = "Адаптивное кадрирование",
        ["crop.enable.note"] = "Пока полосы есть, выборка идёт изнутри картинки; когда пропадают — возвращается к краям экрана.",
        ["crop.vertical"] = "Полосы сверху и снизу",
        ["crop.vertical.note"] = "Фильм 2.35:1 или 21:9 на экране 16:9.",
        ["crop.horizontal"] = "Полосы слева и справа",
        ["crop.horizontal.note"] = "Видео 4:3 на широком экране и вертикальные ролики.",
        ["crop.stretch"] = "Растягивать картинку на всю раскладку",
        ["crop.stretch.note"] = "Картинка раскладывается по всем фигурам, тёмных мест в корпусе не остаётся. Если выключить, выборка только сходит с полос, оставаясь напротив своих мест на экране.",
        ["crop.min"] = "Минимальная полоса, % стороны",
        ["crop.min.note"] = "Полосы тоньше этой доли принимаются за тёмный край кадра.",
        ["crop.max"] = "Максимальная полоса, % стороны",
        ["crop.max.note"] = "Ограничивает глубину переноса, чтобы тёмная сцена не срезала картинку. У фильма 2.39:1 на экране 16:9 полоса — около 17% высоты.",
        ["crop.level"] = "Порог черноты",
        ["crop.level.note"] = "Значение канала, ниже которого пиксель считается чёрным. При нуле мешает шум сжатия, при высоком за полосу принимается тёмный край картинки.",
        ["crop.overlook"] = "Пропускать помехи, % стороны",
        ["crop.overlook.note"] = "Запас на субтитры и панель плеера, которые рисуются поверх полосы. Больше — держится увереннее, слишком много — захватывает край картинки.",
        ["crop.hold"] = "Задержка подтверждения",
        ["crop.hold.note"] = "Новые границы применяются, только если продержались столько времени. Больше — устойчивее к тёмным сценам, меньше — быстрее отклик.",
        ["crop.inset"] = "Запас от полосы, %",
        ["crop.inset.note"] = "Насколько глубже в картинку уводится найденная граница. Поиск идёт по уменьшенному кадру, где один пиксель отвечает за десятки, и первая строка, прочитанная как картинка, наполовину состоит из полосы — выборка с неё занижает яркость всей стороны.",
        ["crop.status.off"] = "Поиск полос выключен.",
        ["crop.status.none"] = "Полосы не найдены.",
        ["crop.status.v"] = "Найдено: сверху и снизу {0:0.0}%.",
        ["crop.status.h"] = "Найдено: слева и справа {0:0.0}%.",
        ["crop.status.both"] = "Найдено: сверху и снизу {0:0.0}%, слева и справа {1:0.0}%.",

        ["power.off"] = "Гасить подсветку",
        ["power.off.exit"] = "при выходе из программы",
        ["power.off.display"] = "когда экран выключен",
        ["power.off.lock"] = "при блокировке сессии",
        ["power.off.sleep"] = "при уходе в сон",
        ["power.wake"] = "После пробуждения",
        ["power.wake.nothing"] = "Ничего не делать",
        ["power.wake.restart"] = "Перезапустить OpenRGB",
        ["power.wake.what"] = "Что делать",
        ["power.wake.note"] = "Во сне контроллеры переподключаются к USB, а работавший сервер продолжает запись в прежние дескрипторы и возвращает признак успеха: подсветка при этом остаётся в состоянии, установленном при подаче питания. Перезапуск возвращает управление.",
        ["power.restartnow"] = "Перезапустить OpenRGB сейчас",
        ["power.delay"] = "Пауза после пробуждения",
        ["power.delay.note"] = "Сколько не трогать подсветку после выхода из сна. При перезапуске пауза откладывает сам перезапуск: контроллеры в это время переподключаются к USB, а поиск устройств по неустоявшейся шине даёт неполный список. В режиме «Ничего не делать» пауза откладывает первую запись, потому что сервер продолжает работать с прежними дескрипторами.",

        ["about.text"] = "Подсветка внутри корпуса воспроизводит изображение с экрана. Каждое светящееся устройство описывается там, где оно физически стоит, и получает цвет с ближайшего к нему участка экрана.",
        ["about.text2"] = "Управление идёт через OpenRGB: устройства на ARGB-контроллере и оперативная память на шине SMBus. Сервер запускается программой, если не запущен, и перезапускается после выхода из сна.",
        ["about.text3"] = "Кадры берутся собственным захватом или принимаются от Rimlight, подсветки монитора. Rimlight не обязателен.",
        ["about.updates"] = "Проверять обновления при запуске",
        ["update.available"] = "Вышла версия {0}.",
        ["update.open"] = "Открыть страницу релиза",
        ["update.hide"] = "Скрыть до следующего запуска",
        ["about.repo"] = "Репозиторий:",
        ["about.rimlight"] = "Rimlight, подсветка монитора:",

        ["tray.show"] = "Показать",
        ["tray.exit"] = "Выход",

        ["fixture.close"] = "Закрыть параметры",
        ["fixture.head"] = "Параметры фигуры",
        ["fixture.name"] = "Название",
        ["fixture.enabled"] = "Участвует в раскраске",
        ["fixture.every"] = "Обновлять раз в N кадров",
        ["fixture.every.note"] = "1 — каждый кадр. Оперативной памяти требуется больше: она на шине SMBus, запись туда медленная и на полной частоте задерживает остальные устройства. Обычно достаточно 10–15.",
        ["fixture.locate"] = "Найти в корпусе",
        ["fixture.binding"] = "Привязка к железу",
        ["fixture.device"] = "Контроллер",
        ["fixture.zone"] = "Зона (разъём)",
        ["fixture.first"] = "Первый диод зоны",
        ["fixture.count"] = "Сколько диодов",
        ["fixture.count.note"] = "Если на одном разъёме несколько устройств, их разводят по разным фигурам, поделив диапазон диодов.",
        ["fixture.place"] = "Место в корпусе, мм",
        ["fixture.x"] = "Центр по горизонтали",
        ["fixture.y"] = "Центр по вертикали",
        ["fixture.length"] = "Длина",
        ["fixture.length.strip.note"] = "Поперёк полосы размер задаёт область выборки.",
        ["fixture.length.ring.note"] = "Кольцо видно с торца, поперёк размер задаёт область выборки.",
        ["fixture.width"] = "Ширина",
        ["fixture.height"] = "Высота",
        ["fixture.rotation"] = "Поворот, градусов",
        ["fixture.arrangement"] = "Как идут диоды",
        ["fixture.arr.strip"] = "Полоса — у ленты есть два конца",
        ["fixture.arr.closed"] = "Замкнутое — кольцо или рамка",
        ["fixture.arr.point"] = "Точка — всё светится в одном месте",
        ["fixture.shape"] = "Форма",
        ["fixture.round"] = "Контур круглый",
        ["fixture.aspect"] = "Пропорции рамки",
        ["fixture.aspect.note"] = "Физические пропорции устройства, если оно не квадратное: у рамки тройного вентилятора это примерно втрое выше.",
        ["fixture.reverse"] = "Обход в другую сторону",
        ["fixture.edgeon"] = "Обращена ребром к наблюдателю",
        ["fixture.edgeon.note"] = "Кольцо сводится к вертикальной линии, ширина фигуры на цвет тогда не влияет.",
        ["fixture.anchor"] = "Начальный диод",
        ["fixture.anchor.note"] = "У замкнутого контура нет собственного первого диода, его назначают. Для вентилятора, обращённого ребром, это диод, расположенный внизу. Кнопка «показать» зажигает только его.",
        ["fixture.show"] = "показать",
        ["fixture.missing"] = "Контроллер «{0}» не виден: он отключён в OpenRGB либо сервер ещё не запущен. Фигура при этом не участвует в раскраске.",
        ["fixture.taller"] = "выше ×{0}",
        ["fixture.wider"] = "шире ×{0}",
        ["fixture.square"] = "квадрат",

        ["color.head"] = "Коррекция",
        ["color.brightness"] = "Максимальная яркость",
        ["color.brightness.note"] = "Верхний предел яркости, одинаковый для всех кадров. Уменьшают, когда подсветка слепит в тёмной комнате: тусклые кадры теряют яркость наравне с яркими.",
        ["color.saturation"] = "Насыщенность",
        ["color.gamma"] = "Гамма",
        ["color.temperature"] = "Температура",
        ["color.minluma"] = "Порог темноты",
        ["color.minluma.note"] = "Ниже этой яркости диод гаснет полностью. Нужен, когда на чёрном экране есть мелкие светлые детали: они поднимают среднюю яркость участка, и подсветка остаётся тускло гореть. При включённой минимальной подсветке погашенные диоды светятся ею.",
        ["color.backlight"] = "Минимальная подсветка",
        ["color.backlight.note"] = "Уровень, ниже которого подсветка не гаснет: на чёрном кадре корпус светится ровно и тускло вместо темноты. Диод, у которого хоть один канал ярче этого уровня, остаётся как есть, поэтому тёмные цветные сцены не выцветают. Разница между чёрным и почти чёрным пропадает. Значение — в единицах вывода, из 255.",
        ["color.shadow"] = "Обесцвечивать тёмное",
        ["color.shadow.note"] = "Чем темнее участок, тем сильнее его цвет сводится к серому. Исключает случаи, когда слишком тёмный объект светит другим цветом: на малой яркости каналы диода светят неодинаково, и разница между ними читается как оттенок. Ноль отключает.",
        ["color.gains"] = "Баланс по каналам",
        ["color.gains.note"] = "Диоды разных устройств передают цвет по-разному. Здесь задаётся общая поправка.",
        ["color.red"] = "Красный",
        ["color.green"] = "Зелёный",
        ["color.blue"] = "Синий",
        ["color.smoothing"] = "Плавность",
        ["color.smoothing.note"] = "Больше значение — быстрее переход. Привычнее выглядит быстрое нарастание и плавный спад.",
        ["color.rise"] = "Разгорается",
        ["color.fall"] = "Гаснет",

        ["test.note"] = "Вместо кадра экрана используется одно пятно, которое перемещается мышью по холсту. Вне пятна цвет чёрный, внутри — выбранный, проходящий через те же настройки. Так проверяется, что загорается именно то устройство, около которого стоит пятно.",
        ["test.stop"] = "Завершить тест",
        ["test.start"] = "Запустить тест",
        ["test.circle"] = "Круг",
        ["test.square"] = "Квадрат",
        ["test.shape"] = "Форма пятна",
        ["test.size"] = "Размер пятна",
        ["test.colour"] = "Выбрать цвет…",

        ["unit.mm"] = " мм",
        ["unit.s"] = " с",

        ["off"] = "выключено",
    };

    static Dictionary<string, string> English() => new()
    {
        ["app.title"] = "CaseLight — case lighting",

        ["nav.canvas"] = "Show canvas",

        ["bar.start"] = "Start",
        ["bar.stop"] = "Stop",
        ["bar.fit"] = "Fit canvas",
        ["bar.screen"] = "Show screen contents",
        ["bar.screen.note"] = "Requires the painting to be running.",
        ["bar.apply"] = "Apply",
        ["bar.cancel"] = "Cancel",
        ["bar.dirty"] = "There are unsaved changes",

        ["tab.main"] = "General",
        ["tab.devices"] = "Devices",
        ["tab.capture"] = "Capture",
        ["tab.crop"] = "Cropping",
        ["tab.brightness"] = "Brightness",
        ["tab.color"] = "Colour",
        ["tab.test"] = "Placement test",
        ["tab.power"] = "Power",
        ["tab.about"] = "About",

        ["main.window"] = "Window",
        ["main.tray"] = "Minimise to tray",
        ["main.tray.note"] = "The close button hides the window and the program keeps running. Exit through the tray icon menu.",
        ["main.startmin"] = "Start minimised",
        ["main.startup"] = "Startup",
        ["main.autostart"] = "Start with Windows",
        ["main.autostart.note"] = "The lighting is driven by OpenRGB, so starting CaseLight automatically only makes sense together with starting OpenRGB.",
        ["main.autopaint"] = "Start painting at once",
        ["main.server"] = "OpenRGB server",
        ["main.serverstart"] = "Start OpenRGB if it is not running",
        ["main.serverstart.note"] = "The server is started with --server --startminimized: the first opens port 6742, the second hides the window.",
        ["main.admin"] = "Run as administrator, with a prompt",
        ["main.admin.note"] = "OpenRGB needs administrator rights for the SMBus, which is how memory modules are driven. Every start of the server raises a UAC prompt.",
        ["main.task"] = "Run as administrator automatically",
        ["main.task.note"] = "Creates a Windows Task Scheduler entry that starts OpenRGB as administrator, confirmed once.",
        ["main.path"] = "Path to OpenRGB.exe",
        ["main.path.note"] = "Filled in with the file that was found. An empty field means searching again on every start: Program Files and %LocalAppData%\\Programs first, then the uninstall entries in the registry.",
        ["main.find"] = "Find",
        ["main.launch"] = "Start now",
        ["main.reconnect"] = "Reconnect",
        ["main.settings"] = "Settings",
        ["main.settings.note"] = "One file with everything: the layout, the monitor size, colour, capture, power.",
        ["main.export"] = "Export…",
        ["main.import"] = "Import…",
        ["main.reset"] = "Defaults",
        ["main.reset.note"] = "Puts the settings back to their standard values. The fixture layout, the monitor size, the chosen screen, the language and the window position are left alone; the autostart entry and the scheduler task are switched off. Like any other edit it applies at once and reaches the disk on Apply.",
        ["main.reset.confirm"] = "Put the settings back to their standard values? The layout, the chosen screen and the language are not affected.",
        ["main.exporttitle"] = "Export CaseLight settings",
        ["main.importtitle"] = "Import CaseLight settings",
        ["main.filter"] = "CaseLight settings (*.json)|*.json",
        ["main.logs"] = "Log",
        ["main.logs.note"] = "The log sits next to config.json and the lang folder with the translations. The link opens that folder.",
        ["main.log"] = "Write log",
        ["main.language"] = "Language",
        ["main.language.note"] = "Translations are JSON files in the lang folder next to the settings. A file added there appears in the list on the next start, with untranslated lines taken from English. The README describes how to make one.",

        ["devices.fixtures"] = "Fixtures",
        ["devices.fixtures.note"] = "One fixture for each lighting device. The parameters of the selected fixture open as a panel over the canvas.",
        ["devices.add"] = "Add",
        ["devices.copy"] = "Duplicate",
        ["devices.remove"] = "Remove",
        ["devices.showdisabled"] = "Show disabled",
        ["devices.showdisabled.note"] = "Fixtures taken out of the painting stop being drawn on the canvas. The selected one is drawn in any case.",

        ["capture.source"] = "Frame source",
        ["capture.fromrimlight"] = "From Rimlight",
        ["capture.auto"] = "Own capture: automatic",
        ["capture.dda"] = "Own capture: DDA only",
        ["capture.wgc"] = "Own capture: WGC only",
        ["capture.gdi"] = "Own capture: GDI only",
        ["capture.method"] = "Method",
        ["capture.method.note"] = "Frames from Rimlight arrive through shared memory, and own capture is not used then. «Automatic» keeps DDA and WGC together and falls back to GDI when they stop delivering frames.",
        ["capture.screen"] = "Screen",
        ["capture.screen.note"] = "The monitor to capture. In the mode that takes frames from Rimlight, it decides the choice.",
        ["capture.fps"] = "Maximum frames per second",
        ["capture.fps.free"] = "no limit",
        ["capture.fps.note"] = "Limits how often frames are reduced to zone colours, and with that the load on the graphics card. With no limit the frames go through as fast as they arrive. The limit is paid for in latency: a frame that comes in early is dropped and the colour waits for the next one. Slow devices get a divider of their own in the fixture parameters.",
        ["capture.radius"] = "Sampling area",
        ["capture.radius.note"] = "The size of the screen patch averaged for one LED. A small value makes the colour follow any movement in the frame, a large one averages it into an even tint.",
        ["capture.stats"] = "Statistics",
        ["capture.rect"] = "Monitor rectangle: {0} × {1} mm",

        ["stats.source"] = "Source",
        ["stats.state"] = "State",
        ["stats.frames"] = "Frames",
        ["stats.rate"] = "Rate",
        ["stats.latency"] = "Latency",
        ["stats.leds"] = "LEDs",

        ["crop.head"] = "Material wider than the screen comes with black bars above and below it, and the LEDs that read those places stay dark. The program finds the edges of the picture and moves the sampling inside them.",
        ["crop.enable"] = "Adaptive cropping",
        ["crop.enable.note"] = "While bars are present the sampling reads from inside the picture; when the bars go, it returns to the edges of the screen.",
        ["crop.vertical"] = "Bars above and below",
        ["crop.vertical.note"] = "2.35:1 or 21:9 material on a 16:9 screen.",
        ["crop.horizontal"] = "Bars left and right",
        ["crop.horizontal.note"] = "4:3 material on a wide screen, and vertical video.",
        ["crop.stretch"] = "Spread the picture over the whole layout",
        ["crop.stretch.note"] = "The picture is laid out across every fixture, leaving no dark places in the case. With this off the sampling only steps clear of the bars, staying opposite its own places on the screen.",
        ["crop.min"] = "Smallest bar, % of the side",
        ["crop.min.note"] = "Bars thinner than this are taken for a dark edge of the frame.",
        ["crop.max"] = "Largest bar, % of the side",
        ["crop.max.note"] = "Caps how far the sampling may move in, so a dark scene cannot cut the picture off. A 2.39:1 film on a 16:9 screen puts about 17% of the height in each bar.",
        ["crop.level"] = "Black threshold",
        ["crop.level.note"] = "The channel value below which a pixel counts as black. At zero the speckle of compression gets in the way; set too high, a dark edge of the picture is taken for bar.",
        ["crop.overlook"] = "Step over interference, % of the side",
        ["crop.overlook.note"] = "Margin for subtitles and player controls, which are drawn over the bar. Higher holds more reliably, too high takes in the edge of the picture.",
        ["crop.hold"] = "Confirmation delay",
        ["crop.hold.note"] = "New edges are acted on only once they have held for this long. Higher stands up to dark scenes better, lower responds sooner.",
        ["crop.inset"] = "Margin past the bar, %",
        ["crop.inset.note"] = "How much further into the picture the detected edge is moved. The scan runs on the reduced frame, where one pixel stands for dozens, so the first row read as picture is half bar - sampling from it drags the whole side down.",
        ["crop.status.off"] = "Bar detection off.",
        ["crop.status.none"] = "No bars found.",
        ["crop.status.v"] = "Found: {0:0.0}% top and bottom.",
        ["crop.status.h"] = "Found: {0:0.0}% left and right.",
        ["crop.status.both"] = "Found: {0:0.0}% top and bottom, {1:0.0}% left and right.",

        ["power.off"] = "Turn the lighting off",
        ["power.off.exit"] = "when the program exits",
        ["power.off.display"] = "when the display is off",
        ["power.off.lock"] = "when the session is locked",
        ["power.off.sleep"] = "when the machine goes to sleep",
        ["power.wake"] = "After waking",
        ["power.wake.nothing"] = "Do nothing",
        ["power.wake.restart"] = "Restart OpenRGB",
        ["power.wake.what"] = "What to do",
        ["power.wake.note"] = "During sleep the controllers reconnect over USB while the running server keeps writing to the old handles and reports success: the lighting stays as the power-on state left it. A restart gives control back.",
        ["power.restartnow"] = "Restart OpenRGB now",
        ["power.delay"] = "Pause after waking",
        ["power.delay.note"] = "How long the lighting is left alone after waking. With a restart the pause delays the restart itself: the controllers are reconnecting over USB at that moment, and a scan of an unsettled bus returns an incomplete list. In «do nothing» mode the pause delays the first write, because the server carries on with its old handles.",

        ["about.text"] = "Lighting inside the case reproduces what is on the screen. Every lighting device is described where it physically stands and takes its colour from the nearest part of the screen.",
        ["about.text2"] = "Everything is driven through OpenRGB: devices on the ARGB controller and memory modules on the SMBus. The server is started by the program if it is not running, and restarted after waking.",
        ["about.text3"] = "Frames come from own capture or are received from Rimlight, the monitor bias lighting. Rimlight is not required.",
        ["about.updates"] = "Check for updates at startup",
        ["update.available"] = "Version {0} has been released.",
        ["update.open"] = "Open the release page",
        ["update.hide"] = "Hide until the next start",
        ["about.repo"] = "Repository:",
        ["about.rimlight"] = "Rimlight, monitor bias lighting:",

        ["tray.show"] = "Show",
        ["tray.exit"] = "Exit",

        ["fixture.close"] = "Close parameters",
        ["fixture.head"] = "Fixture parameters",
        ["fixture.name"] = "Name",
        ["fixture.enabled"] = "Included in the painting",
        ["fixture.every"] = "Update once every N frames",
        ["fixture.every.note"] = "1 means every frame. Memory modules need more: they sit on the SMBus, writing there is slow and at full rate it delays the other devices. 10 to 15 is usually enough.",
        ["fixture.locate"] = "Find in the case",
        ["fixture.binding"] = "Hardware binding",
        ["fixture.device"] = "Controller",
        ["fixture.zone"] = "Zone (header)",
        ["fixture.first"] = "First LED of the zone",
        ["fixture.count"] = "How many LEDs",
        ["fixture.count.note"] = "If one header carries several devices, they are split into separate fixtures by dividing the range of LEDs.",
        ["fixture.place"] = "Place in the case, mm",
        ["fixture.x"] = "Centre, horizontal",
        ["fixture.y"] = "Centre, vertical",
        ["fixture.length"] = "Length",
        ["fixture.length.strip.note"] = "Across the strip the size is set by the sampling area.",
        ["fixture.length.ring.note"] = "The ring is seen edge-on; across it the size is set by the sampling area.",
        ["fixture.width"] = "Width",
        ["fixture.height"] = "Height",
        ["fixture.rotation"] = "Rotation, degrees",
        ["fixture.arrangement"] = "How the LEDs run",
        ["fixture.arr.strip"] = "Strip — a run with two ends",
        ["fixture.arr.closed"] = "Closed — a ring or a frame",
        ["fixture.arr.point"] = "Point — everything lights in one place",
        ["fixture.shape"] = "Shape",
        ["fixture.round"] = "Round contour",
        ["fixture.aspect"] = "Frame proportions",
        ["fixture.aspect.note"] = "The physical proportions of the device when it is not square: the frame of a triple fan is about three times as tall.",
        ["fixture.reverse"] = "Run the other way round",
        ["fixture.edgeon"] = "Edge-on to the viewer",
        ["fixture.edgeon.note"] = "The ring collapses to a vertical line, and the width of the fixture then has no effect on colour.",
        ["fixture.anchor"] = "First LED",
        ["fixture.anchor.note"] = "A closed contour has no first LED of its own, so one is assigned. For a fan seen edge-on it is the LED at the bottom. The «show» button lights that one alone.",
        ["fixture.show"] = "show",
        ["fixture.missing"] = "Controller «{0}» is not visible: it is switched off in OpenRGB or the server is not running yet. The fixture takes no part in the painting.",
        ["fixture.taller"] = "taller ×{0}",
        ["fixture.wider"] = "wider ×{0}",
        ["fixture.square"] = "square",

        ["color.head"] = "Correction",
        ["color.brightness"] = "Maximum brightness",
        ["color.brightness.note"] = "Upper limit on brightness, the same for every frame. Lowered when the light is too much for a dark room: dim frames lose as much as bright ones.",
        ["color.saturation"] = "Saturation",
        ["color.gamma"] = "Gamma",
        ["color.temperature"] = "Colour temperature",
        ["color.minluma"] = "Darkness threshold",
        ["color.minluma.note"] = "Below this brightness the LED goes out completely. Needed when a black screen carries small bright details: they raise the average brightness of the patch and leave the lighting dimly lit. With a minimum backlight set, the LEDs it puts out glow at that level.",
        ["color.backlight"] = "Minimum backlight",
        ["color.backlight.note"] = "The level the lighting never drops below: on a black frame the case glows evenly and dimly instead of going dark. An LED with any channel brighter than this level is left alone, so dark coloured scenes do not wash out. The difference between black and nearly black is lost. The value is in output units, out of 255.",
        ["color.shadow"] = "Shadow desaturation",
        ["color.shadow.note"] = "The darker the patch, the more its colour is pulled towards grey. This rules out the case where a very dark object glows in another colour: at low brightness the channels of an LED are not equally bright, and the difference between them reads as a tint. Zero switches it off.",
        ["color.gains"] = "Per-channel balance",
        ["color.gains.note"] = "LEDs of different devices render colour differently. This is the common correction for that.",
        ["color.red"] = "Red",
        ["color.green"] = "Green",
        ["color.blue"] = "Blue",
        ["color.smoothing"] = "Smoothing",
        ["color.smoothing.note"] = "A larger value means a faster transition. A quick rise with a gentle fall looks the most natural.",
        ["color.rise"] = "Rise",
        ["color.fall"] = "Fall",

        ["test.note"] = "A single patch is used instead of the screen frame, moved around the canvas with the mouse. Outside the patch the colour is black, inside it is the chosen one, put through the same settings. This is how it is checked that the device lighting up is the one the patch stands next to.",
        ["test.stop"] = "Finish the test",
        ["test.start"] = "Run the test",
        ["test.circle"] = "Circle",
        ["test.square"] = "Square",
        ["test.shape"] = "Patch shape",
        ["test.size"] = "Patch size",
        ["test.colour"] = "Choose colour…",

        ["unit.mm"] = " mm",
        ["unit.s"] = " s",

        ["off"] = "off",
    };
}
