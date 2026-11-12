using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using QuotaDesk.Views;
using MessageBox = System.Windows.MessageBox;

namespace QuotaDesk.Services;

/// <summary>独立消息窗口持有全局热键，不依赖悬浮窗是否显示。</summary>
public sealed class ScreenshotHotkey : IDisposable
{
    private readonly HwndSource _source;
    private int _id;
    private int _modifiers;
    private int _key;
    private bool _capturing;

    public ScreenshotHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("QuotaDesk.ScreenshotHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public static bool IsValid(int modifiers, int key) => modifiers is > 0 and <= 7 && (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7B);

    public static string Format(int modifiers, int key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        parts.Add(key is >= 0x70 and <= 0x7B ? $"F{key - 0x6F}" : ((char)key).ToString());
        return string.Join(" + ", parts);
    }

    public bool Configure(bool enabled, int modifiers, int key, out string? error)
    {
        error = null;
        if (!enabled)
        {
            if (_id != 0) UnregisterHotKey(_source.Handle, _id);
            _id = 0;
            return true;
        }
        if (!IsValid(modifiers, key)) { error = "请使用 Ctrl、Alt 或 Shift 加字母、数字或 F1–F12。"; return false; }
        if (_id != 0 && _modifiers == modifiers && _key == key) return true;
        var next = _id == 1 ? 2 : 1;
        if (!RegisterHotKey(_source.Handle, next, (uint)modifiers | 0x4000, (uint)key))
        {
            error = "快捷键注册失败，可能已被其他程序占用。请换一个组合。";
            return false;
        }
        if (_id != 0) UnregisterHotKey(_source.Handle, _id);
        _id = next;
        _modifiers = modifiers;
        _key = key;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == _id && !_capturing)
        {
            handled = true;
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox { Name: "ScreenshotShortcutBox" }) return IntPtr.Zero;
            _capturing = true;
            try { using var screen = new ScreenshotOverlay(); screen.ShowDialog(); }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show("无法开始截图，请检查当前桌面或远程连接。", "QuotaDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _capturing = false; }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_id != 0) UnregisterHotKey(_source.Handle, _id);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _id = 0;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
