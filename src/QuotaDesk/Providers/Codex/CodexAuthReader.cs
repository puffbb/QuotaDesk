using System.IO;
using System.Text.Json;

namespace QuotaDesk.Providers.Codex;

public enum CodexAuthStatus
{
    Ok,
    FileMissing,
    Unreadable,
    MissingTokens,
    MissingAccessToken,
}

/// <summary>Codex OAuth 凭据读取结果。错误信息只含原因，绝不含令牌内容。</summary>
public sealed record CodexAuthResult
{
    public CodexAuthStatus Status { get; init; }
    public string? AccessToken { get; init; }
    /// <summary>可能为空：部分账号没有 account_id，此时请求头省略 ChatGPT-Account-Id。</summary>
    public string? AccountId { get; init; }

    public bool IsOk => Status == CodexAuthStatus.Ok;
}

/// <summary>
/// 只读解析 ~/.codex/auth.json（或 CODEX_HOME/auth.json）。不写回、不刷新登录。
/// 结构参考 Pulse CodexUsageService.swift 与测试夹具。
/// </summary>
public static class CodexAuthReader
{
    /// <summary>解析凭据文件路径：显式指定 &gt; CODEX_HOME &gt; 用户目录\.codex。</summary>
    public static string ResolveAuthPath(string? codexHome = null, string? userProfile = null)
    {
        var dir = codexHome
                  ?? Environment.GetEnvironmentVariable("CODEX_HOME")
                  ?? Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(dir, "auth.json");
    }

    public static CodexAuthResult Read(string path)
    {
        if (!File.Exists(path))
            return new CodexAuthResult { Status = CodexAuthStatus.FileMissing };

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return new CodexAuthResult { Status = CodexAuthStatus.Unreadable };
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return new CodexAuthResult { Status = CodexAuthStatus.MissingTokens };

            var accessToken = tokens.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString()
                : null;
            if (string.IsNullOrEmpty(accessToken))
                return new CodexAuthResult { Status = CodexAuthStatus.MissingAccessToken };

            var accountId = tokens.TryGetProperty("account_id", out var ai) && ai.ValueKind == JsonValueKind.String
                ? ai.GetString()
                : null;

            return new CodexAuthResult { Status = CodexAuthStatus.Ok, AccessToken = accessToken, AccountId = accountId };
        }
    }
}
