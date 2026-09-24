using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CaseLight.Plugins;

namespace CaseLight.NuPhy;

/// <summary>
/// Keyboards by NuPhy on the NuPhy IO firmware, driven over their vendor HID interface with
/// the command NuPhyIO declares for this and does not use itself (LedSyncDownload, 0xDD).
///
/// Keyboards are looked for every few seconds rather than on device notifications: that
/// needs a window to receive them, and a plugin has none. A keyboard plugged in shows up
/// within that time; one unplugged is dropped the same way.
/// </summary>
public sealed class NuPhyPlugin : ILightPlugin
{
    const int ScanMs = 3000;

    readonly object _gate = new();
    readonly Dictionary<string, NuPhyKeyboard> _keyboards = new(StringComparer.OrdinalIgnoreCase);
    readonly ManualResetEvent _stop = new(false);
    Thread? _thread;

    public int ApiVersion => PluginApi.Version;
    public string Name => "NuPhy";
    public string Description => "Keyboards NuPhy Air75 HE: per-key colour through the vendor HID interface.";

    public IReadOnlyList<ILightDevice> Devices
    {
        get { lock (_gate) return _keyboards.Values.ToArray(); }
    }

    public event EventHandler? DevicesChanged;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "NuPhy scan" };
        _thread.Start();
    }

    void Loop()
    {
        do Scan();
        while (!_stop.WaitOne(ScanMs));
    }

    void Scan()
    {
        var present = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in Model.Known)
            foreach (var path in HidChannel.Find(model.PathMatch))
                present[path] = model;

        bool changed = false;
        var gone = new List<NuPhyKeyboard>();

        lock (_gate)
        {
            foreach (var path in _keyboards.Keys.Where(p => !present.ContainsKey(p)).ToArray())
            {
                gone.Add(_keyboards[path]);
                _keyboards.Remove(path);
                changed = true;
            }

            foreach (var (path, model) in present)
            {
                if (_keyboards.ContainsKey(path)) continue;
                _keyboards[path] = new NuPhyKeyboard(model, path);
                changed = true;
            }
        }

        foreach (var k in gone) k.Dispose();
        if (changed) DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(1000);

        NuPhyKeyboard[] all;
        lock (_gate)
        {
            all = _keyboards.Values.ToArray();
            _keyboards.Clear();
        }

        foreach (var k in all) k.Dispose();
        _stop.Dispose();
    }
}
