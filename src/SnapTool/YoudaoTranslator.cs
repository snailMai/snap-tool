using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTool;

// API v3: https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html
internal sealed class YoudaoTranslator : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    internal YoudaoTranslator(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        timeout = requestTimeout ?? TimeSpan.FromSeconds(20);
    }

    internal static string SignatureInput(string text)
    {
        var characters = text.EnumerateRunes().Select(r => r.ToString()).ToArray();
        return characters.Length <= 20 ? text : string.Concat(characters.Take(10))
            + characters.Length.ToString(CultureInfo.InvariantCulture) + string.Concat(characters.TakeLast(10));
    }

    internal static string Sign(string text, YoudaoCredentials credentials, string salt, string timestamp) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.AppId + SignatureInput(text)
            + salt + timestamp + credentials.Secret))).ToLowerInvariant();

    internal async Task<string> Translate(string text, string target, YoudaoCredentials credentials, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("原文为空，请先框选文字或填写原文。");
        if (text.EnumerateRunes().Count() > 5000) throw new InvalidOperationException("原文超过有道单次 5000 字符限制，请缩小选区或分段翻译。");
        if (target is not ("zh-CHS" or "en")) throw new InvalidOperationException("请选择简体中文或英文作为目标语言。");
        if (!credentials.IsConfigured) throw new InvalidOperationException("尚未配置有道。请在托盘右键“配置…”的“翻译设置”页填写应用 ID 和应用密钥后重试。");
        string salt = Guid.NewGuid().ToString("N");
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openapi.youdao.com/api")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = text, ["from"] = "auto", ["to"] = target, ["appKey"] = credentials.AppId,
                ["salt"] = salt, ["curtime"] = timestamp, ["signType"] = "v3", ["strict"] = "true",
                ["sign"] = Sign(text, credentials, salt, timestamp)
            })
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("有道请求频率受限，请稍后手动重试。");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("翻译服务暂时不可用，请检查服务配置后重试。");
            string json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return ParseResponse(json);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("翻译请求超时，原文已保留。请稍后手动重试。"); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("无法连接有道，请检查网络后重试。"); }
    }

    internal static string ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errorCode", out var code)
                || code.ValueKind != JsonValueKind.String) throw new JsonException();
            if (code.GetString() != "0") throw new InvalidOperationException(code.GetString() switch
            {
                "108" or "202" => "有道应用 ID 或密钥无效，请检查翻译设置。",
                "110" => "该应用尚未绑定文本翻译服务，请在有道控制台开通并绑定。",
                "401" => "有道账户余额不足，请检查账户余额。",
                "411" or "412" => "有道请求频率受限，请稍后手动重试。",
                "103" => "原文超过有道长度限制，请分段翻译。",
                "102" => "有道不支持本次识别出的语言，请检查原文。",
                "206" => "请求时间戳无效，请检查电脑时间。",
                _ => "有道未能完成翻译，请检查服务状态后重试。"
            });
            if (!root.TryGetProperty("translation", out var lines) || lines.ValueKind != JsonValueKind.Array) throw new JsonException();
            var output = new List<string>();
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.String) throw new JsonException();
                output.Add(line.GetString()!);
            }
            string result = string.Join(Environment.NewLine, output);
            if (string.IsNullOrWhiteSpace(result)) throw new JsonException();
            return result;
        }
        catch (JsonException) { throw new InvalidOperationException("有道返回了无效或空的翻译结果，原文已保留。"); }
    }

    public void Dispose() => client.Dispose();
}
