using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.UI;

namespace LampProbe;

/// <summary>
/// Finds out what Windows itself can do with the lighting in this machine.
///
/// Once ASUS' own service is gone the motherboard controller shows up to Windows as a
/// standard HID LampArray, which has a public API - no reverse engineering, no SMBus
/// driver. This tool answers the two questions that decide the whole approach: which
/// devices appear, and can we actually drive individual lamps on them.
/// </summary>
static class Program
{
    static async Task<int> Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        var arrays = await FindAsync();
        if (arrays.Length == 0)
        {
            Console.WriteLine("Устройств LampArray не найдено.");
            Console.WriteLine("Либо Windows их не подхватила, либо контроллер держит чужая служба.");
            return 1;
        }

        switch (mode)
        {
            case "api": DumpApi(); return 0;
            case "list": Describe(arrays); return 0;
            case "fill": return Fill(arrays, args);
            case "off": return Fill(arrays, new[] { "off", "0", "0", "0" });
            case "walk": return await WalkAsync(arrays, args);
            default:
                Console.WriteLine("Режимы: list | fill <r> <g> <b> | off | walk [мс на диод]");
                return 2;
        }
    }

    static async Task<LampArray[]> FindAsync()
    {
        string selector = LampArray.GetDeviceSelector();
        var found = await DeviceInformation.FindAllAsync(selector);

        Console.WriteLine($"Селектор нашёл устройств: {found.Count}");

        var list = new System.Collections.Generic.List<LampArray>();
        foreach (var di in found)
        {
            LampArray? la = null;
            try { la = await LampArray.FromIdAsync(di.Id); }
            catch (Exception ex) { Console.WriteLine($"  {di.Name}: не открылось - {ex.Message}"); }

            if (la == null)
            {
                Console.WriteLine($"  {di.Name}: FromIdAsync вернул null (устройство занято?)");
                continue;
            }

            Console.WriteLine($"  + {di.Name}");
            list.Add(la);
        }

        Console.WriteLine();
        return list.ToArray();
    }

    /// <summary>Prints the real API surface, so the probe stops guessing at member names.</summary>
    static void DumpApi()
    {
        foreach (var t in new[] { typeof(LampArray), typeof(LampInfo) })
        {
            Console.WriteLine($"=== {t.FullName} ===");
            foreach (var m in t.GetMembers().OrderBy(m => m.MemberType).ThenBy(m => m.Name))
                Console.WriteLine($"  {m.MemberType,-8} {m}");
            Console.WriteLine();
        }
    }

    static void Describe(LampArray[] arrays)
    {
        for (int i = 0; i < arrays.Length; i++)
        {
            var a = arrays[i];
            Console.WriteLine($"=== [{i}] {a.LampArrayKind} ===");
            Console.WriteLine($"  ламп:            {a.LampCount}");
            Console.WriteLine($"  подключено:      {a.IsConnected}");
            Console.WriteLine($"  управление наше: {a.IsEnabled}");
            Console.WriteLine($"  VID:PID:         {a.HardwareVendorId:X4}:{a.HardwareProductId:X4}  версия {a.HardwareVersion}");
            Console.WriteLine($"  габарит, м:      {a.BoundingBox.X:F3} x {a.BoundingBox.Y:F3} x {a.BoundingBox.Z:F3}");
            Console.WriteLine($"  яркость:         {a.BrightnessLevel:F2}");
            Console.WriteLine($"  DeviceId:        {a.DeviceId}");
            Console.WriteLine($"  мин. интервал:   {a.MinUpdateInterval.TotalMilliseconds:F1} мс " +
                              $"(потолок {(a.MinUpdateInterval.TotalMilliseconds > 0 ? 1000.0 / a.MinUpdateInterval.TotalMilliseconds : 0):F0} к/с)");

            // Positions are what make positional mapping possible at all: if the vendor
            // filled them in, every lamp already knows where it physically sits.
            int show = Math.Min(a.LampCount, 24);
            for (int l = 0; l < show; l++)
            {
                var info = a.GetLampInfo(l);
                Console.WriteLine($"    лампа {l,3}: позиция ({info.Position.X:F3}, {info.Position.Y:F3}, {info.Position.Z:F3}) м" +
                                  $"  уровни R/G/B={info.RedLevelCount}/{info.GreenLevelCount}/{info.BlueLevelCount}" +
                                  $"  фикс.цвет={(info.FixedColor?.ToString() ?? "нет")}" +
                                  $"  задержка={info.UpdateLatency.TotalMilliseconds:F1} мс" +
                                  $"  назначение={info.Purposes}");
            }
            if (a.LampCount > show) Console.WriteLine($"    ... ещё {a.LampCount - show}");
            Console.WriteLine();
        }
    }

    static int Fill(LampArray[] arrays, string[] args)
    {
        byte r = 0, g = 0, b = 0;
        if (args.Length >= 4)
        {
            byte.TryParse(args[1], out r);
            byte.TryParse(args[2], out g);
            byte.TryParse(args[3], out b);
        }

        var colour = Color.FromArgb(255, r, g, b);
        foreach (var a in arrays)
        {
            a.SetColor(colour);
            Console.WriteLine($"[{a.LampArrayKind}] {a.LampCount} ламп -> ({r},{g},{b}), управление наше: {a.IsEnabled}");
        }

        Console.WriteLine("Отправлено. Если ничего не изменилось - контроль у другого приложения.");
        return 0;
    }

    static async Task<int> WalkAsync(LampArray[] arrays, string[] args)
    {
        int dwell = args.Length > 1 && int.TryParse(args[1], out int ms) ? ms : 400;

        var white = Color.FromArgb(255, 255, 255, 255);
        var black = Color.FromArgb(255, 0, 0, 0);

        foreach (var a in arrays)
        {
            Console.WriteLine($"=== [{a.LampArrayKind}], {a.LampCount} ламп, по {dwell} мс ===");
            a.SetColor(black);

            for (int l = 0; l < a.LampCount; l++)
            {
                var info = a.GetLampInfo(l);
                Console.WriteLine($"  лампа {l,3} — ({info.Position.X:F3}, {info.Position.Y:F3}, {info.Position.Z:F3}) м");

                a.SetColorsForIndices(new[] { white }, new[] { l });
                await Task.Delay(dwell);
                a.SetColorsForIndices(new[] { black }, new[] { l });
            }

            a.SetColor(black);
        }

        Console.WriteLine("Пробежка закончена.");
        return 0;
    }
}
