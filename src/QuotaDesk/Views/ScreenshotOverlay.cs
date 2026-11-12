using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace QuotaDesk.Views;

/// <summary>捕获屏幕快照后显示遮罩；单击自动识别窗口/原生控件，拖动自由框选。</summary>
public sealed class ScreenshotOverlay : WinForms.Form
{
    private readonly Rectangle _desktop;
    private readonly Bitmap _screen;
    private readonly List<Target> _targets;
    private readonly Action<Image> _writeClipboard;
    private Rectangle _selected;
    private Point _start;
    private bool _pressed;
    private bool _dragged;
    private bool _probing;
    private DateTime _lastProbe = DateTime.MinValue;
    private IntPtr _automationWindow;
    private Rectangle _automationRect;

    public ScreenshotOverlay() : this(WinForms.Clipboard.SetImage) { }

    internal ScreenshotOverlay(Action<Image> writeClipboard)
    {
        _writeClipboard = writeClipboard;
        _desktop = WinForms.SystemInformation.VirtualScreen;
        if (_desktop.Width <= 0 || _desktop.Height <= 0) throw new InvalidOperationException("屏幕尺寸不可用。");
        _screen = new Bitmap(_desktop.Width, _desktop.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        CaptureDesktop(_screen, _desktop);
        _targets = FindTargets();
        AutoScaleMode = WinForms.AutoScaleMode.None;
        FormBorderStyle = WinForms.FormBorderStyle.None;
        Text = "QuotaDesk 截图";
        ShowInTaskbar = false;
        StartPosition = WinForms.FormStartPosition.Manual;
        Bounds = _desktop;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = WinForms.Cursors.Cross;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 使用物理像素重新定位，避免负坐标或高 DPI 下被自动缩放。
        SetWindowPos(Handle, new IntPtr(-1), _desktop.X, _desktop.Y, _desktop.Width, _desktop.Height, 0x0010);
        var pos = WinForms.Cursor.Position;
        UpdateAutoSelection(pos);
        Invalidate();
        Activate();
    }

    protected override void OnPaint(WinForms.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.DrawImageUnscaled(_screen, 0, 0);
        using var shade = new SolidBrush(Color.FromArgb(96, 0, 0, 0));
        e.Graphics.FillRectangle(shade, ClientRectangle);
        if (_selected.Width > 0 && _selected.Height > 0)
        {
            var local = ToLocal(_selected);
            e.Graphics.DrawImage(_screen, local, local, GraphicsUnit.Pixel);
            using var border = new Pen(Color.FromArgb(255, 92, 104, 255), 2);
            e.Graphics.DrawRectangle(border, local.X, local.Y, Math.Max(0, local.Width - 1), Math.Max(0, local.Height - 1));
        }
        const string hint = "单击选择窗口或控件 · 拖动框选 · Esc 取消";
        using var bg = new SolidBrush(Color.FromArgb(205, 20, 24, 35));
        using var brush = new SolidBrush(Color.White);
        using var font = new Font("Microsoft YaHei UI", 10);
        var size = e.Graphics.MeasureString(hint, font);
        e.Graphics.FillRectangle(bg, 12, 12, size.Width + 18, size.Height + 10);
        e.Graphics.DrawString(hint, font, brush, 21, 17);
    }

    protected override void OnMouseDown(WinForms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == WinForms.MouseButtons.Right) { Close(); return; }
        if (e.Button != WinForms.MouseButtons.Left) return;
        _pressed = true;
        _dragged = false;
        _start = Global(e.Location);
        Capture = true;
    }

    protected override void OnMouseMove(WinForms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var current = Global(e.Location);
        if (_pressed)
        {
            if (!_dragged && (Math.Abs(current.X - _start.X) >= 4 || Math.Abs(current.Y - _start.Y) >= 4)) _dragged = true;
            if (_dragged) _selected = Rectangle.Intersect(_desktop, Rectangle.FromLTRB(Math.Min(_start.X, current.X), Math.Min(_start.Y, current.Y), Math.Max(_start.X, current.X), Math.Max(_start.Y, current.Y)));
        }
        else UpdateAutoSelection(current);
        Invalidate();
    }

    protected override void OnMouseUp(WinForms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != WinForms.MouseButtons.Left || !_pressed) return;
        _pressed = false;
        Capture = false;
        if (!_dragged) UpdateAutoSelection(Global(e.Location));
        var area = Rectangle.Intersect(_desktop, _selected);
        if (area.Width < 1 || area.Height < 1) return;
        try
        {
            using var result = _screen.Clone(ToLocal(area), _screen.PixelFormat);
            _writeClipboard(result);
            Close();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            WinForms.MessageBox.Show("无法写入剪贴板，请关闭占用剪贴板的程序后重试。", "QuotaDesk 截图", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
        }
    }

    protected override void OnKeyDown(WinForms.KeyEventArgs e)
    {
        if (e.KeyCode == WinForms.Keys.Escape) { e.Handled = true; Close(); return; }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _screen.Dispose();
        base.Dispose(disposing);
    }

    private Point Global(Point p) => new(p.X + _desktop.X, p.Y + _desktop.Y);
    private Rectangle ToLocal(Rectangle r) => new(r.X - _desktop.X, r.Y - _desktop.Y, r.Width, r.Height);

    private static void CaptureDesktop(Bitmap destination, Rectangle desktop)
    {
        using var graphics = Graphics.FromImage(destination);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var targetDc = graphics.GetHdc();
            try
            {
                // SRCCOPY | CAPTUREBLT：尽可能包含分层窗口，如透明悬浮窗。
                if (!BitBlt(targetDc, 0, 0, desktop.Width, desktop.Height, screenDc, desktop.X, desktop.Y, 0x40CC0020))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { graphics.ReleaseHdc(targetDc); }
        }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }
    }
    private void UpdateAutoSelection(Point p)
    {
        foreach (var target in _targets)
        {
            if (!target.Bounds.Contains(p)) continue;
            if (_automationWindow == target.Handle && _automationRect.Contains(p))
            {
                _selected = _automationRect;
                return;
            }
            foreach (var child in target.Children.OrderBy(r => (long)r.Width * r.Height))
                if (child.Contains(p)) { _selected = child; QueueAutomation(target, p); return; }
            _selected = target.Bounds;
            QueueAutomation(target, p);
            return;
        }
        var screen = WinForms.Screen.FromPoint(p).Bounds;
        _selected = Rectangle.Intersect(screen, _desktop);
    }

    private void QueueAutomation(Target target, Point point)
    {
        if (_probing || (DateTime.UtcNow - _lastProbe).TotalMilliseconds < 250) return;
        _probing = true;
        _lastProbe = DateTime.UtcNow;
        _ = Task.Run(() => FindAutomationBounds(target, point)).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _probing = false;
                    if (IsDisposed || _pressed || task.Status != TaskStatus.RanToCompletion || task.Result is not { } rect) return;
                    var current = WinForms.Cursor.Position;
                    if (!rect.Contains(current) || _targets.FirstOrDefault(t => t.Bounds.Contains(current))?.Handle != target.Handle) return;
                    _automationWindow = target.Handle;
                    _automationRect = rect;
                    _selected = rect;
                    Invalidate();
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    // UI Automation 只在后台查找，不读取控件文字；部分应用未提供可访问的子控件则回退窗口框。
    private static Rectangle? FindAutomationBounds(Target target, Point point)
    {
        try
        {
            var element = System.Windows.Automation.AutomationElement.FromHandle(target.Handle);
            if (element is null) return null;
            var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
            Rectangle? best = null;
            for (var depth = 0; depth < 10; depth++)
            {
                var child = walker.GetFirstChild(element);
                System.Windows.Automation.AutomationElement? next = null;
                Rectangle nextRect = Rectangle.Empty;
                for (var count = 0; child is not null && count < 120; count++)
                {
                    var bounds = child.Current.BoundingRectangle;
                    if (!bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
                        && bounds.Width >= 16 && bounds.Height >= 16 && bounds.Width <= int.MaxValue && bounds.Height <= int.MaxValue)
                    {
                        var rect = Rectangle.Intersect(target.Bounds, Rectangle.FromLTRB((int)bounds.Left, (int)bounds.Top, (int)bounds.Right, (int)bounds.Bottom));
                        if (rect.Width >= 16 && rect.Height >= 16 && rect.Contains(point)
                            && (next is null || (long)rect.Width * rect.Height < (long)nextRect.Width * nextRect.Height))
                        { next = child; nextRect = rect; }
                    }
                    child = walker.GetNextSibling(child);
                }
                if (next is null) break;
                best = nextRect;
                element = next;
            }
            return best;
        }
        catch (Exception ex) when (ex is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException) { return null; }
    }

    private List<Target> FindTargets()
    {
        var result = new List<Target>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd) || !ReadRect(hwnd, out var rect)) return true;
            var name = new System.Text.StringBuilder(128);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW") return true;
            rect = Rectangle.Intersect(rect, _desktop);
            if (rect.Width < 10 || rect.Height < 10) return true;
            var children = new List<Rectangle>();
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (IsWindowVisible(child) && ReadRect(child, out var r))
                {
                    r = Rectangle.Intersect(r, rect);
                    if (r.Width >= 16 && r.Height >= 16) children.Add(r);
                }
                return true;
            }, IntPtr.Zero);
            result.Add(new Target(hwnd, rect, children));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool ReadRect(IntPtr hwnd, out Rectangle result)
    {
        result = Rectangle.Empty;
        if (!GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return false;
        result = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return true;
    }

    private static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, 14, out var value, sizeof(int)) == 0 && value != 0;
    private sealed record Target(IntPtr Handle, Rectangle Bounds, List<Rectangle> Children);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sourceX, int sourceY, uint operation);
}
