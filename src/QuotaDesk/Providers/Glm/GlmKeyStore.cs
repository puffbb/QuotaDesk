using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QuotaDesk.Providers.Glm;

/// <summary>密钥读取结果状态：区分"未配置"与"有文件但无法解密/读取"，二者给用户的指引不同。</summary>
public enum GlmKeyStatus
{
    /// <summary>未配置（没有密钥文件）。</summary>
    NotConfigured,
    /// <summary>读取并解密成功。</summary>
    Ok,
    /// <summary>文件存在但无法解密或读取（如来自其他 Windows 用户、已损坏、临时 IO 错误）。原文件保留。</summary>
    Unreadable,
}

/// <summary>TryLoad 的结果。</summary>
public sealed record GlmKeyRead(string? Key, GlmKeyStatus Status);

/// <summary>
/// 智谱 API Key 的本机存储：用户范围 DPAPI 加密后存 &lt;设置目录&gt;\glm-key.bin。
/// 明文 key 只在内存中短暂存在，不进设置 JSON、日志、命令行。
/// 解密失败时保留原文件（可能是其他用户或环境问题，静默删除会让用户误以为密钥丢失），
/// 由用户在设置中主动重设覆盖或删除。
/// </summary>
public static class GlmKeyStore
{
    /// <summary>缓存隔离用的账号标识：key 的 SHA-256 前 16 个十六进制字符（不可反推 key）。</summary>
    public static string AccountKeyOf(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()));
        return Convert.ToHexString(hash)[..16];
    }

    public static string FilePath(string settingsDir) => Path.Combine(settingsDir, "glm-key.bin");

    public static void Save(string settingsDir, string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("key 为空", nameof(key));
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(trimmed), null,
            DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(settingsDir);
        File.WriteAllBytes(FilePath(settingsDir), protectedBytes);
    }

    /// <summary>读取 key 并区分未配置/无法解密读取；不删除任何文件。</summary>
    public static GlmKeyRead TryLoad(string settingsDir)
    {
        var path = FilePath(settingsDir);
        if (!File.Exists(path)) return new GlmKeyRead(null, GlmKeyStatus.NotConfigured);
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null,
                DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(bytes).Trim();
            return key.Length > 0
                ? new GlmKeyRead(key, GlmKeyStatus.Ok)
                : new GlmKeyRead(null, GlmKeyStatus.Unreadable); // 空内容同样视为无法使用，但文件保留
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new GlmKeyRead(null, GlmKeyStatus.Unreadable);
        }
    }

    /// <summary>兼容读取：成功返回 key，其余返回 null。不删除原文件。</summary>
    public static string? Load(string settingsDir)
    {
        var read = TryLoad(settingsDir);
        return read.Status == GlmKeyStatus.Ok ? read.Key : null;
    }

    public static void Delete(string settingsDir) => TryDelete(settingsDir);

    private static void TryDelete(string settingsDir)
    {
        try { File.Delete(FilePath(settingsDir)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
