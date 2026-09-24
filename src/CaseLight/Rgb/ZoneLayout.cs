using System;
using System.Windows;

namespace CaseLight.Rgb;

/// <summary>
/// Where the LEDs of a zone physically are, as the device describes it: one rectangle per
/// LED, in whatever units the device uses. Only the proportions matter, a fixture fits
/// them into its own rectangle.
/// </summary>
public static class ZoneLayout
{
    /// <summary>Marks an empty cell of an OpenRGB matrix map.</summary>
    const uint NoLed = 0xFFFFFFFF;

    /// <summary>
    /// Turns an OpenRGB matrix map into one rectangle per LED of the zone, a cell being one
    /// unit square.
    ///
    /// An LED can take several cells: keyboards map the space bar or a tall Enter to all the
    /// cells the key covers, and the LED then gets the rectangle around them. Null if the map
    /// is empty or leaves any LED of the zone without a cell - a layout with holes would put
    /// the missing LEDs in the corner.
    /// </summary>
    public static Rect[]? FromMatrix(int height, int width, uint[] map, int ledCount)
    {
        if (height <= 0 || width <= 0 || ledCount <= 0 || map.Length < height * width) return null;

        var cells = new Rect[ledCount];
        var seen = new bool[ledCount];

        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
        {
            uint led = map[row * width + col];
            if (led == NoLed || led >= ledCount) continue;

            var cell = new Rect(col, row, 1, 1);
            cells[led] = seen[led] ? Rect.Union(cells[led], cell) : cell;
            seen[led] = true;
        }

        return Array.TrueForAll(seen, s => s) ? cells : null;
    }
}
