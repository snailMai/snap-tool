using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnapTool;

internal sealed class YoudaoCredentials(string appId, string secret)
{
    internal string AppId { get; } = appId;
    internal string Secret { get; } = secret;
    internal bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(Secret);
}

internal sealed class TranslationSettings(string? filePath = null)
{
    private readonly string path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnapTool", "translation.json");
    private sealed record Stored(string AppId, string ProtectedSecret);

    internal YoudaoCredentials Load()
    {
        if (!File.Exists(path)) return new("", "");
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new JsonException();
            byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedSecret), null, DataProtectionScope.CurrentUser);
            try { return new(stored.AppId, Encoding.UTF8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException or ArgumentException)
        { throw new InvalidOperationException("无法读取翻译配置，请在翻译设置中重新填写并保存。配置仅适用于保存它的 Windows 用户。"); }
    }

    internal void Save(YoudaoCredentials credentials)
    {
        if (!credentials.IsConfigured) throw new ArgumentException("请填写有道应用 ID 和应用密钥。");
        byte[] bytes = Encoding.UTF8.GetBytes(credentials.Secret);
        string protectedSecret;
        try { protectedSecret = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(credentials.AppId, protectedSecret)));
            File.Move(temp, fullPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
