using System.Collections.Concurrent;
using System.Text.Json;

namespace BcodeViewer.App.Host;

/// <summary>
/// Runs a host-object method OFF the UI thread and pushes its result to the page as a web
/// message, instead of computing it inline while the page waits on the call.
///
/// This exists because of a measured fact the rest of this folder used to have backwards.
/// WebView2 does NOT dispatch host object calls onto a background thread of its own: the
/// bridge is a COM object created on the WinForms STA thread, so every IDispatch call is
/// marshalled back to that apartment and runs on the UI thread, with
/// WindowsFormsSynchronizationContext installed. Measured directly — a host method called
/// from page script reports the same managed thread id as the UI thread. Two consequences,
/// both of which were being hit here:
///
///   * Any real work inside a host method freezes the window for its whole duration. That
///     covers every File.* call against a UNC share and every SQL round trip — the source
///     of the editor "feeling laggy" while typing and saving.
///   * <c>task.GetAwaiter().GetResult()</c> inside a host method deadlocks the process
///     outright. The awaited continuation is posted back to the UI thread's
///     synchronization context, which is the very thread blocked waiting for it, and
///     nothing ever releases it. The window then never closes and BcodeViewer.exe never
///     exits — which is what left the single-instance mutex held, the WebView2 profile
///     folder behind in %TEMP%, and the next build failing with MSB3021.
///
/// So anything that does I/O goes through here: <c>Begin*</c> hands back at once having
/// only queued the work, the work itself runs on the thread pool, and the answer arrives
/// at the page as a <c>hostCallResult</c> message (see Web/hostcall.js, which wraps the
/// pair back up into a plain awaitable call).
/// </summary>
public sealed class AsyncHostCall
{
    /// <summary>Posts one JSON message to the page. Supplied by MainForm, which owns the
    /// marshalling onto the UI thread that CoreWebView2 requires — with BeginInvoke, never
    /// Invoke, since blocking a worker on the UI thread here would reintroduce in miniature
    /// exactly the stall this class exists to remove.</summary>
    private readonly Action<string> _postJson;

    /// <summary>In-flight work that a newer request of the same kind supersedes, keyed by
    /// group name. Only ghost text uses this today: Monaco asks again on every content
    /// change, and without it a fast typist has one live HTTP call per character, each
    /// billed and each arriving too late to be useful.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _groups = new();

    public AsyncHostCall(Action<string> postJson) => _postJson = postJson;

    /// <summary>
    /// Queues <paramref name="work"/> and returns immediately. Every outcome — success,
    /// cancellation, or an exception of any kind — comes back to the page as one message
    /// for <paramref name="requestId"/>, so a caller's promise can never be left hanging.
    /// </summary>
    /// <param name="cancelGroup">When non-null, cancels whatever previous call is still
    /// running under the same name before starting this one.</param>
    public void Begin(string requestId, string? cancelGroup, Func<CancellationToken, Task<string>> work)
    {
        var cts = new CancellationTokenSource();
        if (cancelGroup is not null)
        {
            // Cancel only — disposing the previous source here would race its own still-
            // running request, whose token registration then throws ObjectDisposedException
            // instead of cancelling cleanly. Each call disposes its OWN source below, once
            // its work has actually finished unwinding.
            if (_groups.TryGetValue(cancelGroup, out var previous))
            {
                try { previous.Cancel(); }
                catch (ObjectDisposedException) { /* it finished first — nothing to cancel */ }
            }
            _groups[cancelGroup] = cts;
        }

        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                // ConfigureAwait(false) all the way down from here: nothing in this
                // continuation chain wants the UI thread, and capturing a context that a
                // caller might later block on is the whole bug this class replaces.
                var result = await work(cts.Token).ConfigureAwait(false);
                message = Envelope(requestId, ok: true, result, error: null);
            }
            catch (OperationCanceledException)
            {
                // The normal outcome for a superseded ghost-text request, not a fault. The
                // page distinguishes it so it can stay silent rather than show an error.
                message = Envelope(requestId, ok: false, result: null, error: "cancelled");
            }
            catch (Exception ex)
            {
                message = Envelope(requestId, ok: false, result: null, error: ex.Message);
            }
            finally
            {
                if (cancelGroup is not null)
                    _groups.TryRemove(new KeyValuePair<string, CancellationTokenSource>(cancelGroup, cts));
                cts.Dispose();
            }

            // Outside the try: a failure to post is a dead WebView2 (window already closing),
            // where there is no page left to tell about it either way.
            try { _postJson(message); }
            catch { /* page gone */ }
        });
    }

    /// <summary>
    /// Same contract as <see cref="Begin(string, string?, Func{CancellationToken, Task{string}})"/>,
    /// with a second channel: whatever the work hands to its <c>emit</c> callback goes to
    /// the page immediately as a <c>hostCallChunk</c> message for the same request id.
    ///
    /// The final <c>hostCallResult</c> still carries the WHOLE answer, so a caller that
    /// ignores the chunks behaves exactly as it did before streaming existed, and a dropped
    /// chunk cannot turn into a truncated reply.
    /// </summary>
    public void Begin(string requestId, string? cancelGroup, Func<CancellationToken, Action<string>, Task<string>> work)
    {
        Begin(requestId, cancelGroup, token => work(token, chunk =>
        {
            if (string.IsNullOrEmpty(chunk)) return;
            try { _postJson(Chunk(requestId, chunk)); }
            catch { /* page gone — the final envelope will fail the same way, harmlessly */ }
        }));
    }

    /// <summary>Overload for work that is synchronous but still must not run on the UI
    /// thread — every File.* call against a UNC share qualifies.</summary>
    public void Begin(string requestId, Func<string> work) =>
        Begin(requestId, null, _ => Task.FromResult(work()));

    /// <summary>Fire-and-forget: the page isn't waiting for an answer, it just must not be
    /// made to wait for the side effect either (launching Explorer, for instance).</summary>
    public void Post(Action work) =>
        _ = Task.Run(() =>
        {
            try { work(); }
            catch { /* best effort — there is no caller left to report to */ }
        });

    /// <summary>One incremental piece of a still-running answer. Deliberately a different
    /// message type from the result envelope: the page must not settle a promise on it.</summary>
    private static string Chunk(string id, string text) =>
        JsonSerializer.Serialize(new { type = "hostCallChunk", id, text });

    private static string Envelope(string id, bool ok, string? result, string? error) =>
        JsonSerializer.Serialize(new
        {
            type = "hostCallResult",
            id,
            ok,
            result,
            error,
        });
}
