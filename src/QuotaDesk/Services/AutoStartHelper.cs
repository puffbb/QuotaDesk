using Microsoft.Win32;

namespace QuotaDesk.Services;

/// <summary>
/// 开机自启动（可选项，默认关闭）：写当前用户的注册表 Run 项，仅指向当前 exe 路径。
/// 不做任何未经用户同意的开启。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuotaDesk";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) throw new InvalidOperationException("无法打开注册表 Run 项");
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定可执行文件路径");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
