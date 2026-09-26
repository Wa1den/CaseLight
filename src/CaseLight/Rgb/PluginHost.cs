using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using CaseLight.Core.Capture;
using CaseLight.Core.Text;
using CaseLight.Plugins;

namespace CaseLight.Rgb;

/// <summary>
/// Finds, loads and runs the plugins that bring devices OpenRGB does not drive, and the
/// effects that draw over devices (<see cref="ILightEffect"/>).
///
/// A plugin is a folder with its DLLs under one of <see cref="Roots"/>. Nothing in it is
/// loaded until the plugin is switched on: loading a DLL runs its code, and a folder that
/// happens to be there is not a reason to run anything. A plugin once loaded stays loaded
/// until the program exits - switching it off disposes it and drops its devices, but .NET
/// does not unload an assembly that is still referenced from anywhere, and a half-unloaded
/// one is worse than a loaded one doing nothing.
/// </summary>
public sealed class PluginHost : IDisposable
{
    /// <summary>
    /// The plugins folder next to the settings. It is always writable and survives replacing
    /// the program, which is why it comes first.
    /// </summary>
    public static string UserRoot => Path.Combine(CaseLight.Model.Scene.Folder, "plugins");

    /// <summary>The plugins folder next to the program, for a plugin shipped along with it.</summary>
    public static string AppRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>
    /// Where plugins are looked for, in order. A folder name found in both is taken from the
    /// first: the copy next to the settings is the one the user put there.
    /// </summary>
    public static string[] Roots => [UserRoot, AppRoot];

    /// <summary>One folder of the plugins folder, and what became of it.</summary>
    public sealed class Entry
    {
        public string Id { get; init; } = "";
        public string Folder { get; init; } = "";

        /// <summary>The plugins the folder holds, once it has been loaded.</summary>
        public List<ILightPlugin> Plugins { get; } = new();

        /// <summary>The effects the folder holds, once it has been loaded.</summary>
        public List<ILightEffect> Effects { get; } = new();

        /// <summary>Why it did not load or start; empty if it did.</summary>
        public string Error { get; set; } = "";

        public bool Running => Plugins.Count > 0 || Effects.Count > 0;

        /// <summary>The name of the first plugin or effect started from the folder; null until then.</summary>
        public string? Name => Plugins.Count > 0 ? Plugins[0].Name : Effects.Count > 0 ? Effects[0].Name : null;

        /// <summary>Its description, as <see cref="Name"/>.</summary>
        public string? Description => Plugins.Count > 0 ? Plugins[0].Description : Effects.Count > 0 ? Effects[0].Description : null;

        internal List<Type>? Types;

        /// <summary>
        /// Version of the plugin, empty if it cannot be told.
        ///
        /// Shown because two copies of a plugin can be about at once, one next to the settings
        /// and one next to the program, and which of them runs is not otherwise visible: a
        /// newer build copied to the wrong place looked exactly like a fix that did not work.
        /// Taken from the loaded assembly once the plugin runs, and until then from the file
        /// properties of its DLL, which does not run any of its code.
        /// </summary>
        public string Version
        {
            get
            {
                try
                {
                    var assembly = Plugins.FirstOrDefault()?.GetType().Assembly
                                   ?? Effects.FirstOrDefault()?.GetType().Assembly;
                    string? v = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                ?? assembly?.GetName().Version?.ToString(3);

                    if (v == null)
                    {
                        var dll = Directory.GetFiles(Folder, "*.dll")
                            .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f), FolderContext.Contract, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => Path.GetFileName(f).Contains(Id, StringComparison.OrdinalIgnoreCase))
                            .FirstOrDefault();
                        if (dll != null) v = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion;
                    }

                    // сборка дописывает к версии хэш коммита после «+»
                    return v?.Split('+')[0] ?? "";
                }
                catch { return ""; }
            }
        }
    }

    readonly object _gate = new();
    readonly List<Entry> _entries = new();

    /// <summary>Raised from any thread when a running plugin gains or loses a device, or a plugin starts or stops.</summary>
    public event Action? Changed;

    /// <summary>The folders found at the last <see cref="Scan"/>.</summary>
    public IReadOnlyList<Entry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>
    /// Looks through the plugins folders again. Plugins already running stay as they are;
    /// a folder that has gone is stopped.
    /// </summary>
    public void Scan()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
        {
            string[] found;
            try { found = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
            catch { found = Array.Empty<string>(); }

            foreach (var folder in found) byName.TryAdd(Path.GetFileName(folder), folder);
        }

        var folders = byName.Values.ToArray();

        lock (_gate)
        {
            foreach (var gone in _entries.Where(e => !folders.Contains(e.Folder, StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                StopLocked(gone);
                _entries.Remove(gone);
            }

            foreach (var folder in folders)
            {
                if (_entries.Any(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase))) continue;
                _entries.Add(new Entry { Id = Path.GetFileName(folder), Folder = folder });
            }

            _entries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Starts the plugins named and stops the rest.</summary>
    public void Apply(IReadOnlyCollection<string> enabled)
    {
        bool changed = false;

        lock (_gate)
        {
            foreach (var e in _entries)
            {
                bool want = enabled.Contains(e.Id, StringComparer.OrdinalIgnoreCase);
                if (want == e.Running) continue;

                if (want) StartLocked(e);
                else StopLocked(e);
                changed = true;
            }
        }

        if (changed) Changed?.Invoke();
    }

    void StartLocked(Entry e)
    {
        e.Error = "";

        try { e.Types ??= FindTypes(e.Folder); }
        catch (Exception ex)
        {
            e.Error = ex.Message;
            ProbeLog.Log(Loc.P("плагины", "plugins"), e.Id + ": " + ex);
            return;
        }

        if (e.Types.Count == 0)
        {
            e.Error = Loc.P("в папке нет плагина для CaseLight", "the folder holds no CaseLight plugin");
            return;
        }

        foreach (var type in e.Types)
        {
            object? created = null;
            try
            {
                created = Activator.CreateInstance(type)!;

                int version = created switch
                {
                    ILightPlugin p => p.ApiVersion,
                    ILightEffect f => f.ApiVersion,
                    _ => PluginApi.Version
                };

                if (version != PluginApi.Version)
                {
                    e.Error = string.Format(Loc.P("плагин собран под версию контракта {0}, программе нужна {1}",
                                                  "the plugin is built for contract version {0}, the program needs {1}"),
                                            version, PluginApi.Version);
                    (created as IDisposable)?.Dispose();
                    continue;
                }

                if (created is ILightPlugin plugin)
                {
                    plugin.DevicesChanged += OnDevicesChanged;
                    plugin.Start();
                    e.Plugins.Add(plugin);
                    ProbeLog.Log(Loc.P("плагины", "plugins"), Loc.P("запущен: ", "started: ") + plugin.Name);
                }
                else if (created is ILightEffect effect)
                {
                    effect.Start();
                    e.Effects.Add(effect);
                    ProbeLog.Log(Loc.P("плагины", "plugins"), Loc.P("запущен эффект: ", "effect started: ") + effect.Name);
                }
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
                ProbeLog.Log(Loc.P("плагины", "plugins"), e.Id + ": " + ex);
                if (created is ILightPlugin plugin) Dispose(plugin);
                else if (created is ILightEffect effect) Dispose(effect);
            }
        }
    }

    void StopLocked(Entry e)
    {
        foreach (var plugin in e.Plugins) Dispose(plugin);
        e.Plugins.Clear();

        foreach (var effect in e.Effects) Dispose(effect);
        e.Effects.Clear();
    }

    static void Dispose(ILightEffect effect)
    {
        try { effect.Dispose(); }
        catch (Exception ex) { ProbeLog.Log(Loc.P("плагины", "plugins"), effect.GetType().Name + ": " + ex.Message); }
    }

    void Dispose(ILightPlugin plugin)
    {
        plugin.DevicesChanged -= OnDevicesChanged;

        // своё устройство плагин отпускает сам, но заводской эффект вернётся и у того,
        // что забудет это сделать
        try { foreach (var d in plugin.Devices) d.Release(); } catch { /* плагин уже сломан */ }
        try { plugin.Dispose(); }
        catch (Exception ex) { ProbeLog.Log(Loc.P("плагины", "plugins"), plugin.GetType().Name + ": " + ex.Message); }
    }

    void OnDevicesChanged(object? sender, EventArgs e) => Changed?.Invoke();

    /// <summary>
    /// Every running effect, with the key its settings are kept under: the full name of its
    /// class, which stays the same whatever the language of its name or the folder it lies in.
    /// </summary>
    public (string Key, ILightEffect Effect)[] Effects()
    {
        lock (_gate)
            return _entries.SelectMany(e => e.Effects.Select(f => (f.GetType().FullName ?? f.Name, f))).ToArray();
    }

    /// <summary>
    /// What an effect of the folder lacks to have anything to draw on: the device plugin it
    /// needs is off or missing. Empty when nothing is missing or the folder holds no effect.
    /// </summary>
    public string Missing(Entry entry)
    {
        lock (_gate)
        {
            var running = _entries.SelectMany(e => e.Plugins).Select(p => SafeName(p)).ToArray();

            foreach (var effect in entry.Effects)
            {
                // эффекту на фигурах плагин устройств не нужен: фигуры бывают на любом устройстве
                try { if (effect.Target == EffectTarget.Fixtures) continue; }
                catch { /* сломанный эффект считается эффектом на устройстве */ }

                IReadOnlyList<string> wanted;
                try { wanted = effect.Requires; }
                catch { wanted = []; }

                if (wanted.Count == 0)
                {
                    if (running.Length == 0)
                        return Loc.P("нужен включённый плагин устройств: эффект рисует только на их устройствах",
                                     "a device plugin has to be on: the effect draws only on their devices");
                    continue;
                }

                if (!wanted.Any(w => running.Contains(w, StringComparer.OrdinalIgnoreCase)))
                    return string.Format(Loc.P("нужен включённый плагин: {0}", "a plugin has to be on: {0}"),
                                         string.Join(Loc.P(" или ", " or "), wanted));
            }
        }

        return "";
    }

    static string SafeName(ILightPlugin plugin)
    {
        try { return plugin.Name; }
        catch { return ""; }
    }

    /// <summary>Every device of every running plugin, with the plugin it came from.</summary>
    public (ILightPlugin Plugin, ILightDevice Device)[] Devices()
    {
        var list = new List<(ILightPlugin, ILightDevice)>();

        lock (_gate)
        {
            foreach (var e in _entries)
            foreach (var plugin in e.Plugins)
            {
                try { list.AddRange(plugin.Devices.Select(d => (plugin, d))); }
                catch (Exception ex) { e.Error = ex.Message; }
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Loads the DLLs of a folder and returns the plugin classes in them.
    ///
    /// Each folder gets a load context of its own, so two plugins can carry different
    /// versions of the same library. The contract assembly is the exception: it has to be
    /// the program's own copy, or the plugin's classes implement an interface the program
    /// does not know.
    /// </summary>
    static List<Type> FindTypes(string folder)
    {
        var context = new FolderContext(folder);
        var types = new List<Type>();

        foreach (var dll in Directory.GetFiles(folder, "*.dll"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(dll), FolderContext.Contract, StringComparison.OrdinalIgnoreCase)) continue;

            Assembly assembly;
            try { assembly = context.LoadFromAssemblyPath(dll); }
            catch (BadImageFormatException) { continue; }   // не сборка .NET, а родная библиотека

            Type[] exported;
            try { exported = assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { exported = ex.Types.Where(t => t != null).ToArray()!; }

            types.AddRange(exported.Where(t => t is { IsClass: true, IsAbstract: false }
                                               && (typeof(ILightPlugin).IsAssignableFrom(t) || typeof(ILightEffect).IsAssignableFrom(t))
                                               && t.GetConstructor(Type.EmptyTypes) != null));
        }

        return types;
    }

    sealed class FolderContext(string folder) : AssemblyLoadContext(Path.GetFileName(folder))
    {
        public static readonly string Contract = typeof(ILightPlugin).Assembly.GetName().Name!;

        protected override Assembly? Load(AssemblyName name)
        {
            // null отдаёт поиск основному контексту: там контракт и сама платформа
            if (name.Name == Contract) return null;

            string path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string path = Path.Combine(folder, unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName : unmanagedDllName + ".dll");
            return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    /// <summary>Stops every plugin; their devices go back to their own effects.</summary>
    public void Dispose()
    {
        lock (_gate)
            foreach (var e in _entries) StopLocked(e);
    }
}
