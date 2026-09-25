using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CaseLight.Plugins;

namespace CaseLight.CapsLock;

/// <summary>
/// Lights one LED while Caps Lock is on.
///
/// The LED is chosen by its number rather than by the key: a plugin device reports where
/// its LEDs are, not which key each one sits under, and a table of keys would have to be
/// kept for every keyboard.
/// </summary>
public sealed class CapsLockEffect : ILightEffect
{
    /// <summary>How long a change in the section lights the LED, so that it can be found.</summary>
    const int PreviewMs = 2000;

    int _led;
    LightColor _colour;
    bool _configured;
    long _previewUntil;

    static string T(string ru, string en) => PluginApi.Language == "ru" ? ru : en;

    public int ApiVersion => PluginApi.Version;
    public string Name => "Caps Lock";
    public string Description => T("Индикатор Caps Lock на выбранном диоде.", "A Caps Lock indicator on the LED chosen.");
    public string Icon => "\uE765";

    // поверх эффектов на всю клавиатуру
    public int Order => 100;

    public IReadOnlyList<EffectSetting> Settings =>
    [
        new("led", SettingKind.Led, T("Номер диода", "LED number"))
        {
            Default = "0",
            Help = T("Диоды клавиатуры идут рядами сверху, в ряду слева направо. При правке номера диод загорается на 2 с, так его проще найти.",
                     "The LEDs of a keyboard go by rows from the top, left to right within a row. Editing the number lights the LED for 2 s, which makes it easier to find.")
        },
        new("colour", SettingKind.Color, T("Цвет", "Colour")) { Default = "#FFFFFF" },
    ];

    public void Start() { }

    public void Configure(EffectValues values)
    {
        _led = values.Int("led");
        _colour = values.Color("colour");

        if (_configured) _previewUntil = Environment.TickCount64 + PreviewMs;
        _configured = true;
    }

    public bool Paint(EffectCanvas canvas)
    {
        bool on = (GetKeyState(VkCapital) & 1) != 0 || Environment.TickCount64 < _previewUntil;
        if (!on || _led >= canvas.LedCount) return false;

        canvas.Set(_led, _colour);
        return true;
    }

    public void Dispose() { }

    const int VkCapital = 0x14;

    [DllImport("user32.dll")]
    static extern short GetKeyState(int virtualKey);
}
