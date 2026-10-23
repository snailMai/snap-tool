using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SnapTool;

namespace SnapTool.Tests;

internal static class ClipboardTranslationChecks
{
    private static void Assert(bool condition) { if (!condition) throw new Exception("Clipboard translation assertion failed."); }
    internal static void Run(Action<string, Action> test)
    {
        SelectionCopyChecks.Run(test);
        test("clipboard translation preserves copied multiline text and Unicode limit without OCR", () => OcrChecks.Pump(async () =>
        {
            string input = " Hello world\r\nSecond line. "; string? shown = null; int reports = 0, calls = 0;
            var flow = new ClipboardTranslation(() => { calls++; return input; }, text => shown = text, _ => reports++);
            await flow.Run(default); Assert(shown == input && calls == 1 && reports == 0);
            input = string.Concat(Enumerable.Repeat("😀", 5000)); shown = null;
            await flow.Run(default); Assert(shown == input && reports == 0);
            input += "a"; shown = null;
            await flow.Run(default); Assert(shown == null && reports == 1);
        }));
        test("empty non-text and over-limit clipboard content never opens translation", () => OcrChecks.Pump(async () =>
        {
            foreach (string input in new[] { "", " \r\n\t", new string('a', 5001) })
            {
                int shown = 0, reports = 0;
                var flow = new ClipboardTranslation(() => input, _ => shown++, message => { Assert(message.Length > 0); reports++; });
                await flow.Run(default); Assert(shown == 0 && reports == 1);
            }
        }));
        test("busy clipboard has bounded retries and can be triggered again after failure", () => OcrChecks.Pump(async () =>
        {
            int reads = 0, shown = 0, reports = 0; bool busy = true;
            var flow = new ClipboardTranslation(() => { reads++; if (busy) throw new ExternalException("synthetic clipboard busy"); return "text"; },
                _ => shown++, _ => reports++);
            await flow.Run(default); Assert(reads == 4 && shown == 0 && reports == 1);
            busy = false; await flow.Run(default); Assert(reads == 5 && shown == 1 && reports == 1);
            reads = 0; shown = 0;
            var recovered = new ClipboardTranslation(() => { if (++reads < 3) throw new ExternalException("synthetic busy"); return "text"; },
                _ => shown++, _ => throw new Exception("Unexpected report"));
            await recovered.Run(default); Assert(reads == 3 && shown == 1);
        }));
        test("repeated clipboard trigger during retry reads and opens only once", () => OcrChecks.Pump(async () =>
        {
            int reads = 0, shown = 0;
            var flow = new ClipboardTranslation(() => { if (++reads == 1) throw new ExternalException("synthetic busy"); return "text"; },
                _ => shown++, _ => throw new Exception("Unexpected report"));
            var first = flow.Run(default); await flow.Run(default); await first;
            Assert(reads == 2 && shown == 1);
        }));
        test("closing application during clipboard retry suppresses late reads and output", () => OcrChecks.Pump(async () =>
        {
            using var lifetime = new CancellationTokenSource(); int reads = 0, shown = 0, reports = 0;
            var flow = new ClipboardTranslation(() => { reads++; throw new ExternalException("synthetic busy"); }, _ => shown++, _ => reports++);
            var operation = flow.Run(lifetime.Token); lifetime.Cancel(); await operation;
            await flow.Run(lifetime.Token); Assert(reads == 1 && shown == 0 && reports == 0);
        }));
    }
}
