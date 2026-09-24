/// Promise wrapper over the host's Begin*/web-message protocol.
///
/// Every host object method that touches the filesystem, the network or the database is a
/// `Begin<Name>(requestId, ...args)` that returns the moment it has queued the work; the
/// answer arrives later as a `hostCallResult` web message. This file puts the two halves
/// back together so callers can write `await bcodeHost.call('BeginReadFile', path)` and
/// get the same shape they had when these were ordinary blocking host methods.
///
/// Why the protocol changed at all: WebView2 does not run host object calls on a thread of
/// its own, as this codebase used to assume in several comments. The bridge is a COM object
/// created on the WinForms STA thread, so calls are marshalled back onto the UI thread.
/// Anything slow inside one froze the window, and the four methods that blocked on a task
/// with GetAwaiter().GetResult() deadlocked the process outright — the window would close
/// and BcodeViewer.exe would stay running. See Host/AsyncHostCall.cs.
///
/// Calls that answer purely out of host memory (GetSnippets, GetTheme, GetEditorConfig,
/// GetWorkspaceRoot, the Notify* events, the SQL runner's own start/poll pair) are left as
/// direct host object calls: they were never the problem and routing them through here
/// would only add a message hop.
(function () {
  const pending = new Map();
  let sequence = 0;

  window.chrome.webview.addEventListener('message', (event) => {
    const message = event.data;
    if (!message) return;

    // A piece of a still-running answer (the chat panel typing itself out). Deliberately
    // does NOT settle the promise: the final result carries the complete text, so a caller
    // that ignores chunks — or one whose onChunk throws — still gets the whole answer.
    if (message.type === 'hostCallChunk') {
      const streaming = pending.get(message.id);
      if (streaming && streaming.onChunk) {
        try { streaming.onChunk(message.text); }
        catch { /* a rendering fault must not derail the call itself */ }
      }
      return;
    }

    if (message.type !== 'hostCallResult') return;

    const entry = pending.get(message.id);
    if (!entry) return; // already settled, or belongs to a previous page load
    pending.delete(message.id);

    if (message.ok) entry.resolve(message.result);
    else entry.reject(new Error(message.error || 'host call failed'));
  });

  window.bcodeHost = {
    /// Calls a Begin* host method and resolves with its result string. Rejects with
    /// Error('cancelled') when a newer request of the same kind superseded this one —
    /// callers that expect that (ghost text) should treat it as "no answer", not a fault.
    call(method, ...args) {
      return this.callStreaming(method, null, ...args);
    },

    /// Same call, plus a callback fed each fragment of the answer as it arrives (host methods
    /// that stream — BeginAskAI today). The promise still resolves with the COMPLETE text, so
    /// onChunk is for rendering only; a caller never has to stitch the pieces together itself.
    callStreaming(method, onChunk, ...args) {
      const id = 'r' + (++sequence);
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject, onChunk });
        try {
          // The returned promise is just the IPC acknowledgement — the real answer comes
          // back on the message channel, so it is deliberately not awaited here. It is
          // still caught: an unhandled rejection from it would surface as a page error
          // with no connection to the call that caused it.
          const ack = window.chrome.webview.hostObjects.host[method](id, ...args);
          if (ack && typeof ack.catch === 'function') {
            ack.catch((e) => {
              if (!pending.has(id)) return; // the real answer beat the failure report
              pending.delete(id);
              reject(e);
            });
          }
        } catch (e) {
          pending.delete(id);
          reject(e);
        }
      });
    },

    /// The raw bridge, for the in-memory calls listed above.
    get raw() {
      return window.chrome.webview.hostObjects.host;
    },
  };
})();
