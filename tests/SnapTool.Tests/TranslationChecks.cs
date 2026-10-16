using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;

namespace SnapTool.Tests;

internal static class TranslationChecks
{
    private static readonly YoudaoCredentials Fake = new("app-id", "secret");
    private static HttpResponseMessage Response(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }

    internal static void Run(Action<string, Action> test)
    {
        void Check(string name, Func<Task> action) => test(name, () => OcrChecks.Pump(action));
        test("Youdao v3 fixed SHA256 vector", () => Assert(YoudaoTranslator.Sign("hello", Fake, "0123", "456789") ==
            "b3ae8f08adee5614a1d8f685378a5f03d7d51e0d8c8de102a97547e16953a898"));
        test("Youdao truncates long text and counts Unicode scalars", () =>
        {
            Assert(YoudaoTranslator.SignatureInput(new string('a', 20)) == new string('a', 20));
            Assert(YoudaoTranslator.SignatureInput(new string('a', 21)) == new string('a', 10) + "21" + new string('a', 10));
            string emoji = string.Concat(Enumerable.Repeat("😀", 21));
            Assert(YoudaoTranslator.SignatureInput(emoji) == string.Concat(Enumerable.Repeat("😀", 10)) + "21" + string.Concat(Enumerable.Repeat("😀", 10)));
        });
        Check("Youdao POST encodes source and signs without sending secret", async () =>
        {
            string original = "Hello & world+测试\r\n2=3?"; int count = 0;
            using var translator = new YoudaoTranslator(new Handler(async (request, _) =>
            {
                count++; Assert(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://openapi.youdao.com/api");
                Assert(request.Content!.Headers.ContentType!.MediaType == "application/x-www-form-urlencoded");
                string form = await request.Content.ReadAsStringAsync();
                var fields = form.Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
                Assert(fields["q"] == original && fields["from"] == "auto" && fields["to"] == "zh-CHS" && fields["strict"] == "true");
                Assert(fields["signType"] == "v3" && fields["appKey"] == Fake.AppId && fields.Count == 9);
                Assert(fields["sign"] == YoudaoTranslator.Sign(original, Fake, fields["salt"], fields["curtime"]));
                Assert(!fields.Values.Contains(Fake.Secret));
                return Response("""{"errorCode":"0","translation":["你好","第二行"]}""");
            }));
            Assert(await translator.Translate(original, "zh-CHS", Fake, default) == "你好" + Environment.NewLine + "第二行"); Assert(count == 1);
        });
        Check("Youdao validation blocks requests before network", async () =>
        {
            int calls = 0; using var client = new YoudaoTranslator(new Handler((_, _) => { calls++; throw new Exception("Unexpected request"); }));
            await Reject(() => client.Translate("", "en", Fake, default), "为空");
            await Reject(() => client.Translate(new string('x', 5001), "en", Fake, default), "5000");
            await Reject(() => client.Translate("text", "xx", Fake, default), "目标语言");
            await Reject(() => client.Translate("text", "en", new("", ""), default), "尚未配置");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(() => client.Translate("text", "en", Fake, cancelled.Token)); Assert(calls == 0);
        });
        Check("Youdao accepts 5000 characters and fresh salts", async () =>
        {
            var salts = new List<string>();
            using var client = new YoudaoTranslator(new Handler(async (request, _) =>
            { salts.Add((await request.Content!.ReadAsStringAsync()).Split('&').Single(x => x.StartsWith("salt="))); return Response("""{"errorCode":"0","translation":["ok"]}"""); }));
            await client.Translate(new string('x', 5000), "en", Fake, default); await client.Translate("text", "en", Fake, default);
            Assert(salts.Count == 2 && salts[0] != salts[1]);
        });
        foreach (var (code, message) in new[] { ("202", "密钥"), ("401", "余额"), ("110", "绑定"), ("411", "频率"), ("103", "长度"), ("206", "时间") })
            test("Youdao maps error " + code, () =>
            {
                try { YoudaoTranslator.ParseResponse("{\"errorCode\":\"" + code + "\",\"query\":\"private-text\"}"); throw new Exception("Expected failure"); }
                catch (InvalidOperationException ex) { Assert(ex.Message.Contains(message) && !ex.Message.Contains("private-text")); }
            });
        test("Youdao invalid responses never expose body", () =>
        {
            foreach (string body in new[] { "private-text", "[]", "{}", """{"errorCode":0}""", """{"errorCode":"private-text"}""",
                """{"errorCode":"0","translation":[]}""", """{"errorCode":"0","translation":[null]}""", """{"errorCode":"0","translation":[" "]}""" })
            {
                try { YoudaoTranslator.ParseResponse(body); throw new Exception("Expected failure"); }
                catch (InvalidOperationException ex) { Assert(!ex.Message.Contains("private-text")); }
            }
        });
        Check("Youdao HTTP failure has no automatic retry", async () =>
        {
            int count = 0; using var client = new YoudaoTranslator(new Handler((_, _) => { count++; return Task.FromResult(Response("private-text", HttpStatusCode.TooManyRequests)); }));
            await Reject(() => client.Translate("text", "en", Fake, default), "频率"); Assert(count == 1);
        });
        Check("Youdao network errors are sanitized", async () =>
        {
            using var client = new YoudaoTranslator(new Handler((_, _) => throw new HttpRequestException("private-text")));
            await Reject(() => client.Translate("text", "en", Fake, default), "网络");
        });
        Check("Youdao timeout preserves actionable error", async () =>
        {
            using var client = new YoudaoTranslator(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(""); }), TimeSpan.FromMilliseconds(20));
            await Reject(() => client.Translate("text", "en", Fake, default), "超时");
        });
        Check("Youdao user cancellation stays cancellation", async () =>
        {
            using var cancel = new CancellationTokenSource();
            using var client = new YoudaoTranslator(new Handler(async (_, token) => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return Response(""); }));
            await Cancelled(() => client.Translate("text", "en", Fake, cancel.Token));
        });
        Check("translation session ignores duplicates and late results", async () =>
        {
            int calls = 0; var firstResult = new TaskCompletionSource<string>();
            var session = new TranslationSession((_, _, _) => ++calls == 1 ? firstResult.Task : Task.FromResult("new result"));
            var pending = session.Run("old", "en"); await session.Run("duplicate", "en"); Assert(calls == 1);
            session.Cancel(); Assert(!session.Busy); await session.Run("new", "en");
            firstResult.SetResult("old result"); await pending; Assert(session.Result == "new result" && calls == 2);
            session.Invalidate(); Assert(session.Result == ""); session.Close(); await session.Run("closed", "en"); Assert(calls == 2);
        });
        Check("translation close suppresses pending completion", async () =>
        {
            var completion = new TaskCompletionSource<string>();
            var session = new TranslationSession((_, _, _) => completion.Task); var task = session.Run("text", "en");
            session.Close(); completion.SetResult("late"); await task; Assert(session.Result == "");
        });
        Check("translation failure permits retry", async () =>
        {
            int calls = 0; var session = new TranslationSession((_, _, _) => ++calls == 1
                ? Task.FromException<string>(new InvalidOperationException("余额不足")) : Task.FromResult("ok"));
            await session.Run("text", "en"); Assert(session.Status.Contains("余额") && !session.Busy);
            await session.Run("text", "en"); Assert(session.Result == "ok");
        });
        Check("capture translation opens original without writing clipboard", async () =>
        {
            var image = OcrChecks.Sample(); var snapshot = new DesktopSnapshot(new(0, 0, 900, 220), [new(0, 0, 900, 220)], image);
            int copied = 0, closed = 0; string result = "";
            var session = new CaptureSession(snapshot, () => closed++, (_, _) => { }, (_, _) => Task.FromResult("Hello"),
                (_, _) => { copied++; return Task.CompletedTask; }, text => result = text);
            session.Selection.Begin(new(0, 0), 0); session.Selection.End(new(200, 100)); await session.RecognizeText(true);
            Assert(result == "Hello" && closed == 1 && copied == 0);
        });
        Check("capture cancellation does not open translation", async () =>
        {
            var image = OcrChecks.Sample(); var snapshot = new DesktopSnapshot(new(0, 0, 900, 220), [new(0, 0, 900, 220)], image);
            int opened = 0; var pending = new TaskCompletionSource<string>();
            var session = new CaptureSession(snapshot, () => { }, (_, _) => { }, (_, _) => pending.Task, showTranslation: _ => opened++);
            session.Selection.Begin(new(0, 0), 0); session.Selection.End(new(200, 100)); var task = session.RecognizeText(true);
            session.Close(); pending.SetResult("late"); await task; Assert(opened == 0);
        });
        test("translation settings DPAPI roundtrip and no plaintext secret", () =>
        {
            string directory = Path.Combine(Program.FindRoot(), ".tmp", "translation-checks", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json"); var store = new TranslationSettings(path);
            try
            {
                Assert(!store.Load().IsConfigured); store.Save(new("synthetic-id", "synthetic-secret-value"));
                Assert(!File.ReadAllText(path).Contains("synthetic-secret-value"));
                var loaded = store.Load(); Assert(loaded.AppId == "synthetic-id" && loaded.Secret == "synthetic-secret-value");
                store.Save(new("new-id", "new-secret")); Assert(store.Load().AppId == "new-id");
                File.WriteAllText(path, "invalid");
                try { store.Load(); throw new Exception("Expected failure"); }
                catch (InvalidOperationException ex) { Assert(ex.Message.Contains("重新填写")); }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        Check("translation window edits and target changes invalidate result", async () =>
        {
            int calls = 0; string target = "";
            var window = new TranslationWindow("Hello", (_, language, _) => { calls++; target = language; return Task.FromResult("你好"); });
            try
            {
                window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield();
                Assert(window.ResultBox.Text == "你好" && calls == 1 && target == "zh-CHS");
                window.SourceBox.Text = "new text"; Assert(window.ResultBox.Text == "");
                window.TargetBox.SelectedIndex = 1; window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield();
                Assert(target == "en" && calls == 2);
            }
            finally { window.Close(); }
        });
    }

    internal static void RenderPreview()
    {
        string dir = Path.Combine(Program.FindRoot(), ".tmp", "translation-preview"); Directory.CreateDirectory(dir);
        var settings = new SettingsWindow(ShortcutProfile.Default, _ => { }, () => new("", ""), _ => { });
        ((DockPanel)settings.Content).Children.OfType<TabControl>().Single().SelectedIndex = 1;
        var translation = new TranslationWindow("Please save your changes before closing the window.\n\nError code: 404", (_, _, _) => Task.FromResult("关闭窗口前，请保存更改。\n\n错误代码：404"));
        try
        {
            translation.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (var (window, name, width, height) in new[] { ((Window)settings, "settings", 690, 680), ((Window)translation, "translation", 900, 570) })
            {
                var content = (FrameworkElement)window.Content;
                if (window == settings) { content.Width = width - 48; content.Height = height - 48; }
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                image.Render(background); image.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(dir, name + ".png")); encoder.Save(file);
            }
        }
        finally { settings.Close(); translation.Close(); }
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Translation assertion failed"); }
    private static async Task Reject(Func<Task> action, string message)
    { try { await action(); } catch (InvalidOperationException ex) { Assert(ex.Message.Contains(message)); return; } throw new Exception("Expected failure"); }
    private static async Task Cancelled(Func<Task> action)
    { try { await action(); } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation"); }
}
