using System;
using System.Linq;
using System.Threading;
using OpenRGB.NET;

namespace RgbProbe;

/// <summary>
/// Talks to the OpenRGB SDK server to find out - and then to pin down - what is physically
/// wired to this machine.
///
/// The controller cannot tell how many LEDs are soldered onto a strip plugged into an
/// addressable header; that number is set from outside and defaults to whatever OpenRGB
/// guessed. So the useful part of this tool is not the listing, it is the modes that light
/// the strip up in a countable pattern until the real length is known.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        if (mode is "help" or "-h" or "--help") { Usage(); return 0; }
        if (mode == "api") { DumpApi(); return 0; }

        OpenRgbClient client;
        try
        {
            client = new OpenRgbClient(name: "RgbProbe");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Не удалось подключиться к OpenRGB на 127.0.0.1:6742.");
            Console.WriteLine("  " + ex.Message);
            Console.WriteLine();
            Console.WriteLine("OpenRGB должен быть запущен ОТ АДМИНИСТРАТОРА и с ключом --server.");
            return 1;
        }

        using (client)
        {
            switch (mode)
            {
                case "list": return List(client);
                case "resize": return Resize(client, args);
                case "mark": return Mark(client, args);
                case "walk": return Walk(client, args);
                case "fill": return Fill(client, args);
                case "zones": return Zones(client, args);
                case "off": return Off(client, args);
                default: Usage(); return 2;
            }
        }
    }

    static void Usage()
    {
        Console.WriteLine("Режимы:");
        Console.WriteLine("  list                          что видит сервер");
        Console.WriteLine("  resize <устр> <зона> <n>      задать длину зоны (для адресуемых хедеров)");
        Console.WriteLine("  mark   <устр> <зона>          маркеры: каждый 10-й красный, каждый 5-й зелёный");
        Console.WriteLine("  walk   <устр> <зона> [мс]     бегущий огонёк по зоне");
        Console.WriteLine("  fill   <устр> <r> <g> <b>     залить всё устройство одним цветом");
        Console.WriteLine("  off    [устр]                 погасить (без номера - все)");
        Console.WriteLine();
        Console.WriteLine("Номера устройств и зон - из вывода list.");
    }

    // ---- чтение ----------------------------------------------------------

    static int List(OpenRgbClient client)
    {
        var devices = client.GetAllControllerData();
        Console.WriteLine($"Контроллеров: {devices.Length}");
        Console.WriteLine();

        int grandTotal = 0;

        for (int i = 0; i < devices.Length; i++)
        {
            var d = devices[i];
            string name = string.IsNullOrWhiteSpace(d.Name) ? "<без имени>" : d.Name;

            Console.WriteLine($"=== [{i}] {name}");
            Console.WriteLine($"    тип:          {d.Type}");
            if (!string.IsNullOrWhiteSpace(d.Description)) Console.WriteLine($"    описание:     {d.Description}");
            if (!string.IsNullOrWhiteSpace(d.Version)) Console.WriteLine($"    версия:       {d.Version}");
            if (!string.IsNullOrWhiteSpace(d.Location)) Console.WriteLine($"    расположение: {d.Location}");
            Console.WriteLine($"    диодов всего: {d.Leds.Length}");
            grandTotal += d.Leds.Length;

            Console.WriteLine($"    зоны ({d.Zones.Length}):");
            for (int z = 0; z < d.Zones.Length; z++)
            {
                var zone = d.Zones[z];
                string resizable = zone.LedsMin == zone.LedsMax ? "" : "  <- длину задаём мы";
                Console.WriteLine($"      [{z}] {zone.Name,-24} {zone.Type,-8} диодов={zone.LedCount,4}" +
                                  $"  предел={zone.LedsMin}..{zone.LedsMax}{resizable}");
            }

            int direct = FindDirect(d);
            string activeName = d.Modes.Length > 0 ? d.Modes[d.ActiveModeIndex].Name : "?";
            Console.WriteLine($"    режим Direct: {(direct >= 0 ? "есть, индекс " + direct : "НЕТ")}" +
                              $"   (активный сейчас: {d.ActiveModeIndex} - {activeName})");
            Console.WriteLine();
        }

        Console.WriteLine($"Итого управляемых диодов: {grandTotal}");
        return 0;
    }

    /// <summary>Per-LED colour is the only mode worth anything here; the rest is decoration.</summary>
    static int FindDirect(Device d)
    {
        for (int i = 0; i < d.Modes.Length; i++)
            if (d.Modes[i].Flags.HasFlag(ModeFlags.HasPerLedColor))
                return i;
        return -1;
    }

    // ---- запись ----------------------------------------------------------

    static bool Resolve(OpenRgbClient client, string[] args, int argIndex,
                        out Device[] devices, out int dev)
    {
        devices = client.GetAllControllerData();
        dev = -1;

        if (args.Length <= argIndex || !int.TryParse(args[argIndex], out dev) ||
            dev < 0 || dev >= devices.Length)
        {
            Console.WriteLine($"Нужен номер устройства 0..{devices.Length - 1} (см. list).");
            return false;
        }
        return true;
    }

    static bool ResolveZone(Device d, string[] args, int argIndex, out int zone)
    {
        zone = -1;
        if (args.Length <= argIndex || !int.TryParse(args[argIndex], out zone) ||
            zone < 0 || zone >= d.Zones.Length)
        {
            Console.WriteLine($"Нужен номер зоны 0..{d.Zones.Length - 1} (см. list).");
            return false;
        }
        return true;
    }

    /// <summary>Switches to per-LED control, without which our colours are simply ignored.</summary>
    static void GoDirect(OpenRgbClient client, Device[] devices, int dev)
    {
        client.SetCustomMode(dev);
        Console.WriteLine($"[{dev}] {devices[dev].Name}: переведено в по-диодное управление");
    }

    static int Resize(OpenRgbClient client, string[] args)
    {
        if (!Resolve(client, args, 1, out var devices, out int dev)) return 2;
        if (!ResolveZone(devices[dev], args, 2, out int zone)) return 2;

        if (args.Length < 4 || !int.TryParse(args[3], out int size))
        {
            Console.WriteLine("Использование: resize <устр> <зона> <n>");
            return 2;
        }

        var z = devices[dev].Zones[zone];
        if (size < z.LedsMin || size > z.LedsMax)
        {
            Console.WriteLine($"Длина вне предела: {z.LedsMin}..{z.LedsMax}");
            return 2;
        }

        client.ResizeZone(dev, zone, size);
        Console.WriteLine($"[{dev}] зона [{zone}] {z.Name}: длина {z.LedCount} -> {size}");

        // the layout changed underneath us, so read it back rather than trusting our copy
        var after = client.GetControllerData(dev);
        Console.WriteLine($"    теперь у устройства диодов: {after.Leds.Length}");
        return 0;
    }

    static int ZoneCount(Device d, int zone) => (int)d.Zones[zone].LedCount;

    /// <summary>
    /// Lights a countable pattern: every tenth LED red, every fifth green, the rest dim
    /// blue. Counting ten reds beats counting sixty individual LEDs by eye, and the dim
    /// background still shows where the strip physically ends.
    /// </summary>
    static int Mark(OpenRgbClient client, string[] args)
    {
        if (!Resolve(client, args, 1, out var devices, out int dev)) return 2;
        if (!ResolveZone(devices[dev], args, 2, out int zone)) return 2;

        GoDirect(client, devices, dev);

        int count = ZoneCount(devices[dev], zone);
        var colors = new Color[count];

        for (int i = 0; i < count; i++)
        {
            colors[i] = i % 10 == 0 ? new Color(255, 0, 0)
                      : i % 5 == 0 ? new Color(0, 255, 0)
                      : new Color(0, 0, 40);
        }

        client.UpdateZoneLeds(dev, zone, colors);

        Console.WriteLine($"Зона [{zone}] {devices[dev].Zones[zone].Name}, задано {count} диодов.");
        Console.WriteLine("Красные стоят на 0, 10, 20, ... - сколько их видно, столько десятков в ленте.");
        Console.WriteLine("Зелёные - середины десятков (5, 15, 25...), остальные тускло-синие.");
        return 0;
    }

    static int Walk(OpenRgbClient client, string[] args)
    {
        if (!Resolve(client, args, 1, out var devices, out int dev)) return 2;
        if (!ResolveZone(devices[dev], args, 2, out int zone)) return 2;

        int dwell = args.Length > 3 && int.TryParse(args[3], out int ms) ? ms : 150;

        GoDirect(client, devices, dev);

        int count = ZoneCount(devices[dev], zone);
        var colors = new Color[count];

        Console.WriteLine($"Бегу по зоне [{zone}] {devices[dev].Zones[zone].Name}, {count} диодов, по {dwell} мс.");

        for (int i = 0; i < count; i++)
        {
            Array.Fill(colors, new Color(0, 0, 0));
            colors[i] = new Color(255, 255, 255);
            client.UpdateZoneLeds(dev, zone, colors);
            Console.Write($"\r  диод {i + 1}/{count}   ");
            Thread.Sleep(dwell);
        }

        Console.WriteLine();
        Console.WriteLine("Где огонёк остановился визуально - там и конец физической ленты.");
        return 0;
    }

    static int Fill(OpenRgbClient client, string[] args)
    {
        if (!Resolve(client, args, 1, out var devices, out int dev)) return 2;

        byte r = 0, g = 0, b = 0;
        if (args.Length >= 5)
        {
            byte.TryParse(args[2], out r);
            byte.TryParse(args[3], out g);
            byte.TryParse(args[4], out b);
        }

        GoDirect(client, devices, dev);

        var colors = Enumerable.Repeat(new Color(r, g, b), devices[dev].Leds.Length).ToArray();
        client.UpdateLeds(dev, colors);

        Console.WriteLine($"[{dev}] {devices[dev].Name}: {colors.Length} диодов -> ({r},{g},{b})");
        return 0;
    }

    /// <summary>
    /// Paints every zone of a device a different colour at once, so one glance at the case
    /// tells which header carries what. Beats filling them one at a time and trying to
    /// remember what changed.
    /// </summary>
    static int Zones(OpenRgbClient client, string[] args)
    {
        if (!Resolve(client, args, 1, out var devices, out int dev)) return 2;

        GoDirect(client, devices, dev);

        var palette = new[]
        {
            (new Color(255, 255, 255), "белый"),
            (new Color(255, 0, 0), "красный"),
            (new Color(0, 255, 0), "зелёный"),
            (new Color(0, 80, 255), "синий"),
            (new Color(255, 0, 255), "розовый"),
            (new Color(255, 160, 0), "оранжевый")
        };

        var zones = devices[dev].Zones;
        for (int z = 0; z < zones.Length; z++)
        {
            int count = (int)zones[z].LedCount;
            if (count == 0) { Console.WriteLine($"  [{z}] {zones[z].Name}: пустая, пропущена"); continue; }

            var (colour, name) = palette[z % palette.Length];
            client.UpdateZoneLeds(dev, z, Enumerable.Repeat(colour, count).ToArray());
            Console.WriteLine($"  [{z}] {zones[z].Name,-24} {count,4} диодов -> {name}");
        }

        return 0;
    }

    static int Off(OpenRgbClient client, string[] args)
    {
        var devices = client.GetAllControllerData();
        int wantedDev = args.Length > 1 && int.TryParse(args[1], out int w) ? w : -1;

        for (int i = 0; i < devices.Length; i++)
        {
            if (wantedDev >= 0 && wantedDev != i) continue;
            if (devices[i].Leds.Length == 0) continue;

            client.SetCustomMode(i);
            client.UpdateLeds(i, Enumerable.Repeat(new Color(0, 0, 0), devices[i].Leds.Length).ToArray());
            Console.WriteLine($"[{i}] {devices[i].Name}: погашено ({devices[i].Leds.Length} диодов)");
        }
        return 0;
    }

    static void DumpApi()
    {
        foreach (var t in new[] { typeof(OpenRgbClient), typeof(Device), typeof(Zone), typeof(Mode), typeof(Led) })
        {
            Console.WriteLine($"=== {t.FullName} ===");
            foreach (var m in t.GetMembers().Where(m => m.MemberType is System.Reflection.MemberTypes.Property
                                                                    or System.Reflection.MemberTypes.Method)
                                            .OrderBy(m => m.MemberType).ThenBy(m => m.Name))
                Console.WriteLine($"  {m.MemberType,-8} {m}");
            Console.WriteLine();
        }
    }
}
