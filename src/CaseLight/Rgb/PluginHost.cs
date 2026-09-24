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
/// Finds, loads and runs the plugins that bring devices OpenRGB does not drive.
///
/// A plugin is a folder under <see cref="Root"/> with its DLLs. Nothing in it is loaded
/// until the plugin is switched on: loading a DLL runs its code, and a folder that happens
/// to be there is not a reason to run anything. A plugin once loaded stays loaded until the
/// program exits - switching it off disposes it and drops its devices, but .NET does not
/// unload an assembly that is still referenced from anywhere, and a half-unloaded one is
/// worse than a loaded one doing nothing.
/// </summary>
public sealed class PluginHost : IDisposable
{
    /// <summary>The plugins folder, next to the program.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>One folder of the plugins folder, and what became of it.</summary>
    public sealed class Entry
    {
        public string Id { get; init; } = "";
        public string Folder { get; init; } = "";

        /// <summary>The plugins the folder holds, once it has been loaded.</summary>
        public List<ILightPlugin> Plugins { get; } = new();

        /// <summary>Why it did not load or start; empty if it did.</summary>
        public string Error { get; set; } = "";

        public bool Running => Plugins.Count > 0;

        internal List<Type>? Types;
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
    /// Looks through the plugins folder again. Plugins already running stay as they are;
    /// a folder that has gone is stopped.
    /// </summary>
    public void Scan()
    {
        string[] folders;
        try { folders = Directory.Exists(Root) ? Directory.GetDirectories(Root) : Array.Empty<string>(); }
        catch { folders = Array.Empty<string>(); }

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
            ILightPlugin? plugin = null;
            try
            {
                plugin = (ILightPlugin)Activator.CreateInstance(type)!;
                if (plugin.ApiVersion != PluginApi.Version)
                {
                    e.Error = string.Format(Loc.P("плагин собран под версию контракта {0}, программе нужна {1}",
                                                  "the plugin is built for contract version {0}, the program needs {1}"),
                                            plugin.ApiVersion, PluginApi.Version);
                    plugin.Dispose();
                    continue;
                }

                plugin.DevicesChanged += OnDevicesChanged;
                plugin.Start();
                e.Plugins.Add(plugin);
                ProbeLog.Log(Loc.P("плагины", "plugins"), Loc.P("запущен: ", "started: ") + plugin.Name);
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
                ProbeLog.Log(Loc.P("плагины", "plugins"), e.Id + ": " + ex);
                if (plugin != null) Dispose(plugin);
            }
        }
    }

    void StopLocked(Entry e)
    {
        foreach (var plugin in e.Plugins) Dispose(plugin);
        e.Plugins.Clear();
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
                                               && typeof(ILightPlugin).IsAssignableFrom(t)
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
