using System.Collections.Generic;
using CaseLight.Plugins;

namespace CaseLight.NuPhy;

/// <summary>A keyboard the plugin knows: how to find it and where its LEDs are.</summary>
/// <param name="Name">The device name fixtures are bound by.</param>
/// <param name="ProductId">USB product id of the keyboard in normal mode (the updater has another).</param>
/// <param name="KeyLeds">LEDs under the keys; they come first in a frame.</param>
/// <param name="SideLeds">LEDs along the sides. They are part of a frame, but the firmware does not take them from it.</param>
/// <param name="Rows">Width of each key, in key units, row by row from the top, in the order of the LEDs.</param>
sealed record Model(string Name, int ProductId, int KeyLeds, int SideLeds, double[][] Rows)
{
    public const int VendorId = 0x19F5;

    /// <summary>
    /// Keyboards checked on the device. NuPhyIO lists more HE models on the same protocol,
    /// with their LED counts, but the order of the LEDs was only read off an Air75 HE.
    /// </summary>
    public static readonly Model[] Known =
    {
        // Порядок диодов по GetDefaultKeyMatrix (прошивка 1.10): по рядам слева направо,
        // 16, 15, 15, 14, 14 и 10 клавиш. Ширины по раскладке 75 %, ряд 16 единиц.
        new("NuPhy Air75 HE", 0x6120, 84, 12, new[]
        {
            Repeat(1, 16),                                                     // Esc F1..F12, правая верхняя, Ins, Del
            [.. Repeat(1, 13), 2, 1],                                          // ` 1..0 - =, Bksp, PgUp
            [1.5, .. Repeat(1, 12), 1.5, 1],                                   // Tab, Q..], \, PgDn
            [1.75, .. Repeat(1, 11), 2.25, 1],                                 // Caps, A..', Enter, Home
            [2.25, .. Repeat(1, 10), 1.75, 1, 1],                              // LShift, Z../, RShift, Up, End
            [1.25, 1.25, 1.25, 6.25, 1, 1, 1, 1, 1, 1]                         // LCtrl LWin LAlt Space RAlt Fn RCtrl, стрелки
        })
    };

    static double[] Repeat(double width, int count)
    {
        var row = new double[count];
        for (int i = 0; i < count; i++) row[i] = width;
        return row;
    }

    /// <summary>The key under each LED, in key units, rows one unit apart.</summary>
    public IReadOnlyList<LedRect> Layout()
    {
        var keys = new List<LedRect>(KeyLeds);
        for (int row = 0; row < Rows.Length; row++)
        {
            double x = 0;
            foreach (double width in Rows[row])
            {
                keys.Add(new LedRect(x, row, width, 1));
                x += width;
            }
        }

        return keys;
    }

    /// <summary>The part of an interface path that picks out the vendor interface of this model.</summary>
    public string PathMatch => $"vid_{VendorId:x4}&pid_{ProductId:x4}&mi_01";
}
