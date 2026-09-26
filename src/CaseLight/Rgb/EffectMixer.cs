using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CaseLight.Core.Capture;
using CaseLight.Core.Text;
using CaseLight.Model;
using CaseLight.Plugins;

namespace CaseLight.Rgb;

/// <summary>
/// Lays the effects of plugins (<see cref="ILightEffect"/>) over the frames of plugin
/// devices.
///
/// Every frame for a plugin device passes through here. A device with no effect on it gets
/// the frame at once, as before. A device with effects keeps the frame as the picture
/// under them, and a thread of its own composes and sends it about 60 times a second: a
/// still screen produces no frames, and a spectrum or a fading level has to move all the
/// same. A device no fixture drives has black under its effects and is let go to its own
/// effect whenever none of them draws.
///
/// Only plugin devices are covered. A device of OpenRGB shares one socket with the paint
/// loop and has slow buses behind it, and 60 extra writes a second there would hold up the
/// rest.
/// </summary>
public sealed class EffectMixer : IDisposable
{
    const int PeriodMs = 16;
    const int IdleMs = 250;

    /// <summary>One device, and what is known about what it shows.</summary>
    sealed class Slot(ILightDevice device)
    {
        public readonly ILightDevice Device = device;
        public int LedCount;
        public IReadOnlyList<LedRect>? Layout;
        public int[][] Rows = [];

        /// <summary>The last frame of the painting; null while the device is released.</summary>
        public byte[]? Picture;

        /// <summary>The last frame sent, to skip sending the same one again.</summary>
        public byte[] Sent = [];

        /// <summary>Whether the device holds a frame of ours rather than its own effect.</summary>
        public bool Taken;

        public readonly List<Bound> Effects = new();
        public bool HadEffects;
        public bool Failed;
    }

    /// <summary>One running effect, and the settings last handed to it.</summary>
    sealed class Bound(string key, ILightEffect effect)
    {
        public readonly string Key = key;
        public readonly ILightEffect Effect = effect;
        public Dictionary<string, string>? Values;
        public bool Configured;
        public string Device = "";
        public int Order;
        public bool Failed;
        public EffectTarget Target;
        public HashSet<string> Fixtures = new();
    }

    /// <summary>
    /// The LEDs of one fixture in the frame of the paint loop, for effects on fixtures.
    /// </summary>
    /// <param name="Start">First LED of the fixture in the frame.</param>
    /// <param name="Areas">The area each LED reads the screen from, in millimetres on the plan.</param>
    public sealed record FixtureRun(string FixtureId, int Start, LedRect[] Areas);

    /// <summary>
    /// Ids of the fixtures some effect draws on. The paint loop goes on painting a still
    /// screen about 60 times a second while any of its fixtures is here.
    /// </summary>
    public IReadOnlySet<string> FixtureTargets => _fixtureTargets;
    volatile HashSet<string> _fixtureTargets = new();

    readonly PluginHost _plugins;
    readonly Func<Scene> _scene;
    readonly Func<bool> _live;

    readonly object _gate = new();
    readonly Dictionary<ILightDevice, Slot> _slots = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ILightEffect, Bound> _bound = new(ReferenceEqualityComparer.Instance);
    readonly AutoResetEvent _wake = new(false);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Thread _thread;
    volatile bool _stop;
    volatile bool _devicesStale = true;

    ILightDevice[] _devices = [];

    /// <param name="scene">The live settings, read on every frame.</param>
    /// <param name="live">Whether the painting runs; effects draw only then.</param>
    public EffectMixer(PluginHost plugins, Func<Scene> scene, Func<bool> live)
    {
        _plugins = plugins;
        _scene = scene;
        _live = live;

        plugins.Changed += () => { _devicesStale = true; _wake.Set(); };

        _thread = new Thread(Loop) { IsBackground = true, Name = "caselight-effects" };
        _thread.Start();
    }

    /// <summary>
    /// The layout and rows of a device as effects see them, for the section of an effect
    /// to offer the same rows the effect will draw on.
    /// </summary>
    public static (IReadOnlyList<LedRect>? Layout, int[][] Rows, int LedCount) Geometry(ILightDevice device)
    {
        int count = 0;
        IReadOnlyList<LedRect>? layout = null;

        try
        {
            var zones = device.Zones;
            count = zones.Sum(z => z.LedCount);

            // Раскладка есть только у одной зоны: единиц у зон нет общих, и склеивать их не на чем.
            if (zones.Count == 1 && zones[0].Layout is { } l && l.Count == zones[0].LedCount) layout = l;
        }
        catch { /* плагин сломан, устройство без диодов */ }

        return (layout, LedGrid.Rows(layout, count), count);
    }

    /// <summary>
    /// A frame of the painting for a plugin device. Called from the paint loop; does not
    /// wait for the device or for the effects.
    /// </summary>
    public void Write(DeviceInfo info, byte[] rgb)
    {
        var device = info.Plugin!;

        lock (_gate)
        {
            var slot = SlotFor(device);
            slot.Picture = rgb;

            if (slot.Effects.Count > 0)
            {
                _wake.Set();
                return;
            }

            Send(slot, rgb, skipSame: false);
        }
    }

    /// <summary>
    /// The painting lets go of a device. Its effects, if it has any and the painting still
    /// runs, go on over black; otherwise the device goes back to its own effect.
    /// </summary>
    public void Release(DeviceInfo info)
    {
        var device = info.Plugin!;

        lock (_gate)
        {
            var slot = SlotFor(device);
            slot.Picture = null;

            if (slot.Effects.Count > 0 && _live())
            {
                _wake.Set();
                return;
            }

            Let(slot, force: true);
        }
    }

    void Loop()
    {
        using var timer = new PrecisionTimer();

        while (!_stop)
        {
            bool busy;
            try { busy = Tick(); }
            catch (Exception ex)
            {
                ProbeLog.Log(Loc.P("эффекты", "effects"), ex.ToString());
                busy = false;
            }

            int wait = busy ? PeriodMs : IdleMs;
            if (timer.Handle is { } tick && timer.Arm(wait)) WaitHandle.WaitAny([tick, _wake]);
            else _wake.WaitOne(wait);
        }
    }

    /// <returns>Whether any effect has a device, so the next frame is due soon.</returns>
    bool Tick()
    {
        bool live = _live();
        var effects = _plugins.Effects();
        var settings = _scene().Effects;

        if (_devicesStale)
        {
            _devicesStale = false;
            _devices = _plugins.Devices().Select(d => d.Device).ToArray();
        }

        lock (_gate)
        {
            BindEffects(effects, settings);
            ForgetGoneDevices();

            foreach (var slot in _slots.Values) slot.Effects.Clear();

            foreach (var bound in _bound.Values)
            {
                if (bound.Device == "") continue;

                var device = _devices.FirstOrDefault(d => SafeName(d) == bound.Device);
                if (device != null) SlotFor(device).Effects.Add(bound);
            }

            bool any = false;
            double seconds = _clock.Elapsed.TotalSeconds;

            foreach (var slot in _slots.Values)
            {
                if (slot.Effects.Count == 0)
                {
                    // Эффекты с устройства ушли: вернуть картинку или отпустить его.
                    if (slot.HadEffects)
                    {
                        slot.HadEffects = false;
                        if (slot.Picture != null && live) Send(slot, slot.Picture, skipSame: false);
                        else Let(slot, force: false);
                    }
                    continue;
                }

                slot.HadEffects = true;
                any = true;

                if (!live)
                {
                    Let(slot, force: false);
                    continue;
                }

                Compose(slot, seconds);
            }

            return any && live;
        }
    }

    /// <summary>Picks up effects that started or stopped, and hands changed settings over.</summary>
    void BindEffects((string Key, ILightEffect Effect)[] effects, Dictionary<string, Dictionary<string, string>> settings)
    {
        foreach (var gone in _bound.Keys.Where(e => !effects.Any(x => ReferenceEquals(x.Effect, e))).ToArray())
            _bound.Remove(gone);

        foreach (var (key, effect) in effects)
        {
            if (!_bound.TryGetValue(effect, out var bound))
            {
                bound = new Bound(key, effect);
                try { bound.Order = effect.Order; } catch { /* порядок по умолчанию */ }
                try { bound.Target = effect.Target; } catch { /* устройство, как у всех прежних */ }
                _bound[effect] = bound;
            }

            settings.TryGetValue(key, out var values);
            if (bound.Configured && ReferenceEquals(bound.Values, values)) continue;

            bound.Values = values;
            bound.Configured = true;
            bound.Failed = false;
            var parsed = new EffectValues(values ?? new Dictionary<string, string>(), []);
            bound.Device = bound.Target == EffectTarget.Device ? parsed.Device : "";
            bound.Fixtures = bound.Target == EffectTarget.Fixtures ? new HashSet<string>(parsed.Fixtures) : new();

            try { effect.Configure(new EffectValues(values ?? new Dictionary<string, string>(), effect.Settings)); }
            catch (Exception ex) { Fail(bound, ex); }
        }

        _fixtureTargets = _bound.Values.Where(b => b.Target == EffectTarget.Fixtures && !b.Failed)
                                       .SelectMany(b => b.Fixtures).ToHashSet();
    }

    /// <summary>
    /// Draws the effects on fixtures over the frame of the paint loop, in place. Called by
    /// the paint loop between the colour pass and the write to the devices.
    /// </summary>
    /// <param name="output">Three bytes per LED, in the order of the runs.</param>
    /// <returns>Whether any effect drew.</returns>
    public bool PaintFixtures(IReadOnlyList<FixtureRun> runs, byte[] output)
    {
        if (_fixtureTargets.Count == 0) return false;
        bool any = false;

        lock (_gate)
        {
            double seconds = _clock.Elapsed.TotalSeconds;

            foreach (var bound in _bound.Values.OrderBy(b => b.Order))
            {
                if (bound.Failed || bound.Target != EffectTarget.Fixtures || bound.Fixtures.Count == 0) continue;

                var chosen = runs.Where(r => bound.Fixtures.Contains(r.FixtureId)).ToArray();
                if (chosen.Length == 0) continue;

                int total = chosen.Sum(r => r.Areas.Length);
                var frame = new byte[total * 3];
                var layout = new LedRect[total];
                var parts = new int[chosen.Length][];

                int at = 0;
                for (int c = 0; c < chosen.Length; c++)
                {
                    var run = chosen[c];
                    int n = run.Areas.Length;

                    Buffer.BlockCopy(output, run.Start * 3, frame, at * 3, n * 3);
                    run.Areas.CopyTo(layout, at);
                    parts[c] = Enumerable.Range(at, n).ToArray();
                    at += n;
                }

                // рядов у фигур нет: их диоды разбросаны по плану, а не стоят сеткой
                var canvas = new EffectCanvas(frame, layout, [], hasPicture: true, seconds)
                {
                    Parts = parts
                };

                bool drew;
                try { drew = bound.Effect.Paint(canvas); }
                catch (Exception ex) { Fail(bound, ex); continue; }

                if (!drew) continue;
                any = true;

                at = 0;
                foreach (var run in chosen)
                {
                    Buffer.BlockCopy(frame, at * 3, output, run.Start * 3, run.Areas.Length * 3);
                    at += run.Areas.Length;
                }
            }
        }

        return any;
    }

    void ForgetGoneDevices()
    {
        foreach (var gone in _slots.Keys.Where(d => !_devices.Contains(d, ReferenceEqualityComparer.Instance)).ToArray())
        {
            var slot = _slots[gone];

            // устройство, которое ещё пишет раскраска, остаётся: список устройств мог отстать
            if (slot.Picture != null) continue;
            _slots.Remove(gone);
        }
    }

    void Compose(Slot slot, double seconds)
    {
        var picture = slot.Picture;
        bool hasPicture = picture != null && picture.Length == slot.LedCount * 3;

        var frame = new byte[slot.LedCount * 3];
        if (hasPicture) Buffer.BlockCopy(picture!, 0, frame, 0, frame.Length);

        var canvas = new EffectCanvas(frame, slot.Layout, slot.Rows, hasPicture, seconds);
        bool drew = false;

        foreach (var bound in slot.Effects.OrderBy(b => b.Order))
        {
            if (bound.Failed) continue;

            try { drew |= bound.Effect.Paint(canvas); }
            catch (Exception ex) { Fail(bound, ex); }
        }

        if (!drew && !hasPicture)
        {
            Let(slot, force: false);
            return;
        }

        Send(slot, frame, skipSame: true);
    }

    Slot SlotFor(ILightDevice device)
    {
        if (_slots.TryGetValue(device, out var slot)) return slot;

        slot = new Slot(device);
        (slot.Layout, slot.Rows, slot.LedCount) = Geometry(device);
        _slots[device] = slot;
        return slot;
    }

    /// <param name="skipSame">
    /// Skip a frame equal to the one sent last. Frames of effects are composed 60 times a
    /// second whether anything moved or not; frames of the painting go through as they come,
    /// as they did before effects.
    /// </param>
    void Send(Slot slot, byte[] rgb, bool skipSame)
    {
        if (skipSame && slot.Taken && rgb.AsSpan().SequenceEqual(slot.Sent)) return;

        try
        {
            slot.Device.Write(rgb);
            slot.Sent = rgb;
            slot.Taken = true;
            slot.Failed = false;
        }
        catch (Exception ex)
        {
            if (!slot.Failed) ProbeLog.Log(Loc.P("плагины", "plugins"), SafeName(slot.Device) + ": " + ex.Message);
            slot.Failed = true;
        }
    }

    /// <param name="force">Release even if nothing of ours is on the device, as the painting asks.</param>
    void Let(Slot slot, bool force)
    {
        if (!slot.Taken && !force) return;

        try { slot.Device.Release(); }
        catch (Exception ex) { ProbeLog.Log(Loc.P("плагины", "plugins"), SafeName(slot.Device) + ": " + ex.Message); }

        slot.Taken = false;
        slot.Sent = [];
    }

    static void Fail(Bound bound, Exception ex)
    {
        bound.Failed = true;
        ProbeLog.Log(Loc.P("эффекты", "effects"), bound.Key + ": " + ex);
    }

    static string SafeName(ILightDevice device)
    {
        try { return device.Name; }
        catch { return ""; }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(1000);

        lock (_gate)
            foreach (var slot in _slots.Values) Let(slot, force: false);

        _wake.Dispose();
    }
}
