using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SnapTool;

namespace SnapTool.Tests;

internal static class TranslationWindowChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        Assert(condition(), message);
    }
    private static async Task Activate(Window window)
    {
        if (!window.IsVisible) window.Show();
        bool activated = window.Activate();
        await Wait(() => window.IsActive, $"{window.GetType().Name} could not activate (request={activated}, visible={window.IsVisible}).");
    }
    private static Button Find(Window window, string label) =>
        RefreshChecks.Descendants<Button>(window).Single(b => Equals(b.Content, label));

    internal static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var fixture = new Window { Title = "Snap Tool activation fixture", Width = 360, Height = 180, ShowInTaskbar = false };
        try
        {
            OcrChecks.Pump(async () =>
            {
                fixture.Show();
                var normal = new TranslationWindow("synthetic source", (_, _, _) => Task.FromResult("synthetic result"));
                bool normalClosed = false; normal.Closed += (_, _) => normalClosed = true;
                try
                {
                    await Activate(normal);
                    normal.SourceBox.Focus(); normal.ResultBox.Focus(); normal.TargetBox.Focus();
                    normal.TargetBox.IsDropDownOpen = true; await Task.Delay(50);
                    Assert(!normalClosed && normal.IsVisible, "Internal controls/dropdown closed translation.");
                    normal.TargetBox.IsDropDownOpen = false;
                    await Activate(fixture); await Wait(() => normalClosed, "External activation did not close translation.");
                    Console.WriteLine("PASS translation internal focus/dropdown retained; external window activation closed.");
                }
                finally { if (!normalClosed) normal.Close(); }

                var late = new TaskCompletionSource<string>(); CancellationToken requestToken = default; bool pendingClosed = false;
                var pending = new TranslationWindow("synthetic pending", (_, _, token) => { requestToken = token; return late.Task; });
                pending.Closed += (_, _) => pendingClosed = true;
                try
                {
                    await Activate(pending); Assert(requestToken.CanBeCanceled, "Translation request was not started.");
                    await Activate(fixture); await Wait(() => pendingClosed, "Pending translation did not close.");
                    Assert(requestToken.IsCancellationRequested, "Request was not canceled on close.");
                    late.SetResult("late result"); await Task.Delay(50);
                    Assert(pending.ResultBox.Text == "", "Late translation updated a closed window.");
                    Console.WriteLine("PASS auto close canceled pending translation and ignored late completion.");
                }
                finally { late.TrySetResult("cleanup"); if (!pendingClosed) pending.Close(); }

                int copyAttempts = 0; bool copyingClosed = false;
                var copying = new TranslationWindow("synthetic copy", (_, _, _) => Task.FromResult("result"),
                    (text, token) => ImageOutput.CopyTextWithRetry(text, token, _ => { copyAttempts++; throw new ExternalException("synthetic busy clipboard"); }));
                copying.Closed += (_, _) => copyingClosed = true;
                try
                {
                    await Activate(copying); Find(copying, "复制原文").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(copyAttempts == 1, "Copy retry was not started.");
                    await Activate(fixture); await Wait(() => copyingClosed, "Copying translation did not close.");
                    await Task.Delay(150); Assert(copyAttempts == 1, "Clipboard retry continued after auto close.");
                    Console.WriteLine("PASS auto close canceled clipboard retry; real clipboard untouched.");
                }
                finally { if (!copyingClosed) copying.Close(); }
            });
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.Shutdown();
        }
    }
}
