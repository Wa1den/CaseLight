using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CaseLight.Core.Capture;

namespace CaseLight.View;

/// <summary>
/// The calibration test colour over the whole screen, kept just under the settings window.
///
/// The whole screen, where Rimlight fills only the edges: the fixtures stand anywhere
/// around the case, and the eye compares each of them with the patch of screen nearest to
/// it. The window never takes activation, so clicking it does not lift it over the
/// settings, and it is placed right behind them in the z-order. A topmost window would
/// cover the sliders on a single monitor while the colour is up.
/// </summary>
public sealed class PatchWindow : Window
{
    readonly Window _owner;
    readonly MonitorInfo _monitor;
    IntPtr _hwnd;

    public PatchWindow(Window owner, MonitorInfo monitor, Color color)
    {
        _owner = owner;
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Title = "CaseLight";
        SetColor(color);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

            HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
            PlaceBelowOwner();
        };

        // показ без активации всё равно кладёт окно наверх, поэтому место под окном
        // настроек занимается ещё раз, уже после него
        Loaded += (_, _) => PlaceBelowOwner();

        _owner.Activated += OnOwnerActivated;
        Closed += (_, _) => _owner.Activated -= OnOwnerActivated;
    }

    public Color Color { get; private set; }

    public void SetColor(Color color)
    {
        Color = color;
        Background = new SolidColorBrush(color);
    }

    void OnOwnerActivated(object? sender, EventArgs e) => PlaceBelowOwner();

    /// <summary>
    /// Covers the screen in physical pixels and goes directly behind the settings window.
    /// Pixels rather than WPF units: the screen may be scaled differently from the one the
    /// window was created on.
    /// </summary>
    public void PlaceBelowOwner()
    {
        if (_hwnd == IntPtr.Zero) return;

        var owner = new WindowInteropHelper(_owner).Handle;
        SetWindowPos(_hwnd, owner == IntPtr.Zero ? HWND_TOP : owner,
            _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, SWP_NOACTIVATE);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // щелчок по заливке не активирует её и не поднимает над окном настроек
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    const int GWL_EXSTYLE = -20;
    const long WS_EX_NOACTIVATE = 0x08000000;
    const long WS_EX_TOOLWINDOW = 0x00000080;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;
    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
