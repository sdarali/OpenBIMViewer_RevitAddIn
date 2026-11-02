using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using OpenBimViewer.Collab.Native;

namespace OpenBimViewer.Collab
{
    public sealed class CollabClientOptions
    {
        /// <summary>Folder for the Core's offline cache and outbox.</summary>
        public string CacheDirectory { get; set; } = "";
        /// <summary>Sent as X-Client.</summary>
        public string ClientName { get; set; } = "open-bim-revit";
        /// <summary>What <see cref="SaveSession"/> last stored, or null.</summary>
        public string? SessionJson { get; set; }
        /// <summary>Persist the session (it holds the refresh token, so store it encrypted).</summary>
        public Action<string>? SaveSession { get; set; }
        /// <summary>Level 0 verbose â€¦ 3 error.</summary>
        public Action<int, string>? Log { get; set; }
        public CertificateTrust CertificateTrust { get; set; } = new CertificateTrust();
        /// <summary>Folder holding IfcCollabCore.dll; defaults to this assembly's folder.</summary>
        public string? NativeDirectory { get; set; }
        /// <summary>For tests: the HTTP stack to use instead of the default one.</summary>
        public HttpMessageHandler? HttpHandler { get; set; }
    }

    /// <summary>
    /// The IFC Collab Core's Client (login, token refresh, projects, issues, comments, views, snapshot
    /// replacement, offline queue) behind a Task API. The Core does the business rules; this class
    /// gives it HTTP, timers and session storage.
    ///
    /// Not thread-safe: create it, call it and dispose it on the logic thread. Results come back
    /// there too.
    /// </summary>
    public sealed class CollabClient : IDisposable
    {
        private sealed class RequestState
        {
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public bool CancelledByCore;
        }

        private sealed class CallResult
        {
            public Dictionary<string, object?> Json = new Dictionary<string, object?>();
            public byte[] Data = Array.Empty<byte>();
        }

        // The native side keeps these function pointers, so they must never be collected.
        private static readonly NativeMethods.SendFn SendCallback = OnSend;
        private static readonly NativeMethods.IdFn CancelCallback = OnCancel;
        private static readonly NativeMethods.PostDelayedFn PostDelayedCallback = OnPostDelayed;
        private static readonly NativeMethods.IdFn CancelTimerCallback = OnCancelTimer;
        private static readonly NativeMethods.TextFn SaveSessionCallback = OnSaveSession;
        private static readonly NativeMethods.LogFn LogCallback = OnLog;
        private static readonly NativeMethods.ResultFn ResultCallback = OnResult;
        private static readonly NativeMethods.OnlineFn OnlineCallback = OnOnline;

        private readonly ILogicThread _thread;
        private readonly CollabClientOptions _options;
        private readonly HttpClient _http;
        private readonly Dictionary<ulong, RequestState> _requests = new Dictionary<ulong, RequestState>();
        private readonly Dictionary<ulong, IDisposable> _timers = new Dictionary<ulong, IDisposable>();
        private readonly Dictionary<ulong, TaskCompletionSource<CallResult>> _calls = new Dictionary<ulong, TaskCompletionSource<CallResult>>();
        private GCHandle _self;
        private IntPtr _bridge;
        private ulong _nextCallId = 1;
        private Vocabulary? _vocabulary;

        public CollabClient(ILogicThread thread, CollabClientOptions options)
        {
            _thread = thread ?? throw new ArgumentNullException(nameof(thread));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            NativeMethods.EnsureLoaded(options.NativeDirectory);

            var handler = options.HttpHandler;
            if (handler == null)
            {
                handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = options.CertificateTrust.Validate,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                };
            }
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            _self = GCHandle.Alloc(this);
            var host = new NativeMethods.Host
            {
                Context = GCHandle.ToIntPtr(_self),
                Send = Marshal.GetFunctionPointerForDelegate(SendCallback),
                Cancel = Marshal.GetFunctionPointerForDelegate(CancelCallback),
                PostDelayed = Marshal.GetFunctionPointerForDelegate(PostDelayedCallback),
                CancelTimer = Marshal.GetFunctionPointerForDelegate(CancelTimerCallback),
                SaveSession = Marshal.GetFunctionPointerForDelegate(SaveSessionCallback),
                Log = Marshal.GetFunctionPointerForDelegate(LogCallback),
                Result = Marshal.GetFunctionPointerForDelegate(ResultCallback),
                OnlineChanged = Marshal.GetFunctionPointerForDelegate(OnlineCallback),
            };
            var config = Json.Write(new Dictionary<string, object?> { ["clientName"] = options.ClientName });
            Directory.CreateDirectory(options.CacheDirectory);
            _bridge = NativeMethods.Create(ref host, NativeMethods.Utf8(config),
                options.SessionJson == null ? null : NativeMethods.Utf8(options.SessionJson), NativeMethods.Utf8(options.CacheDirectory));
            if (_bridge == IntPtr.Zero)
            {
                _self.Free();
                throw new InvalidOperationException("IfcCollabCore.dll refused to start.");
            }
        }

        public CertificateTrust CertificateTrust => _options.CertificateTrust;

        /// <summary>Why the last request didn't reach the server (DNS, TLS, refused...), or null.</summary>
        public string? LastTransportError { get; private set; }

        /// <summary>The server became reachable (true) or unreachable (false).</summary>
        public event Action<bool>? OnlineChanged;

        public ClientState State => ClientState.From(Query("state"));

        public Vocabulary Vocabulary => _vocabulary ??= Vocabulary.From(Query("vocabulary"));

        // ---------------------------------------------------------------- session

        public Task LoginAsync(string server, string email, string password) =>
            CallAsync("login", new Dictionary<string, object?> { ["server"] = server, ["email"] = email, ["password"] = password });

        /// <summary>Sign in again with the saved refresh token.</summary>
        public Task ResumeAsync() => CallAsync("resume");

        public void Logout() => Query("logout");

        public void ForgetServer(string server) => Query("forgetServer", new Dictionary<string, object?> { ["server"] = server });

        /// <summary>The Core's form of a server address ("https://" added, trailing "/api" dropped).</summary>
        public string NormalizeServer(string server) =>
            Query("normalizeServer", new Dictionary<string, object?> { ["server"] = server }).Str("url");

        public Task CheckConnectivityAsync() => CallAsync("checkConnectivity");

        // ---------------------------------------------------------------- data

        public async Task<IReadOnlyList<Project>> GetProjectsAsync() =>
            Items(await CallAsync("projects")).Select(Project.From).ToList();

        public async Task<IReadOnlyList<SavedView>> GetViewsAsync(string projectId) =>
            Items(await CallAsync("views", ForProject(projectId))).Select(SavedView.From).ToList();

        public async Task<IReadOnlyList<Issue>> GetIssuesAsync(string projectId) =>
            Items(await CallAsync("issues", ForProject(projectId))).Select(Issue.From).ToList();

        public async Task<Issue> CreateIssueAsync(string projectId, NewIssue issue)
        {
            var args = ForProject(projectId);
            args["title"] = issue.Title;
            args["description"] = issue.Description;
            args["topicType"] = issue.TopicType;
            args["priority"] = issue.Priority;
            args["components"] = issue.Components.ToList();
            if (issue.Viewpoint != null) args["viewpoint"] = issue.Viewpoint.ToJson();
            return Issue.From((await CallAsync("createIssue", args)).Json.Obj("issue") ?? new Dictionary<string, object?>());
        }

        public async Task<Issue> UpdateIssueAsync(string projectId, string issueId, IssueChanges changes)
        {
            var args = ForProject(projectId);
            args["issueId"] = issueId;
            changes.AddTo(args);
            return Issue.From((await CallAsync("updateIssue", args)).Json.Obj("issue") ?? new Dictionary<string, object?>());
        }

        public Task AddCommentAsync(string projectId, string issueId, string text, ViewpointCapture? viewpoint)
        {
            var args = ForProject(projectId);
            args["issueId"] = issueId;
            args["text"] = text;
            if (viewpoint != null) args["viewpoint"] = viewpoint.ToJson();
            return CallAsync("addComment", args);
        }

        /// <summary>Replace the image of an issue's or a comment's viewpoint. Returns the new image URL.</summary>
        public async Task<string> ReplaceSnapshotAsync(string projectId, string issueId, string viewpointGuid, byte[] png)
        {
            var args = ForProject(projectId);
            args["issueId"] = issueId;
            args["viewpointGuid"] = viewpointGuid;
            args["png"] = Convert.ToBase64String(png);
            return (await CallAsync("updateSnapshot", args)).Json.Str("snapshotUrl");
        }

        /// <summary>GET an image (snapshots are served without auth).</summary>
        public async Task<byte[]> DownloadAsync(string url) =>
            (await CallAsync("download", new Dictionary<string, object?> { ["url"] = url })).Data;

        /// <summary>Send the changes made offline in this project.</summary>
        public async Task<SyncResult> SyncAsync(string projectId)
        {
            var r = (await CallAsync("sync", ForProject(projectId))).Json;
            return new SyncResult
            {
                Applied = (int)r.Num("applied"),
                Rejected = (int)r.Num("rejected"),
                Gone = (int)r.Num("gone"),
                Remaining = (int)r.Num("remaining"),
            };
        }

        public IReadOnlyList<PendingChange> PendingChanges(string projectId) =>
            Query("pending", ForProject(projectId)).Arr("items").OfType<Dictionary<string, object?>>().Select(o => new PendingChange
            {
                OpId = o.Str("opId"),
                Entity = o.Str("entity"),
                Action = o.Str("action"),
                Label = o.Str("label"),
                Error = o.Str("error"),
            }).ToList();

        public void Dispose()
        {
            if (_bridge == IntPtr.Zero) return;
            var bridge = _bridge;
            // Destroying the Core cancels what it has in flight through our cancel callback,
            // which needs the bridge, so clear it only afterwards.
            NativeMethods.Destroy(bridge);
            _bridge = IntPtr.Zero;
            foreach (var request in _requests.Values) request.Cancel.Cancel();
            _requests.Clear();
            foreach (var timer in _timers.Values) timer.Dispose();
            _timers.Clear();
            var calls = _calls.Values.ToList();
            _calls.Clear();
            foreach (var call in calls) call.TrySetException(new CollabException("Signed out."));
            _self.Free();
            _http.Dispose();
        }

        // ---------------------------------------------------------------- plumbing

        private static Dictionary<string, object?> ForProject(string projectId) => new Dictionary<string, object?> { ["projectId"] = projectId };

        private static IEnumerable<Dictionary<string, object?>> Items(CallResult r) => r.Json.Arr("items").OfType<Dictionary<string, object?>>();

        private Task<CallResult> CallAsync(string op, Dictionary<string, object?>? args = null)
        {
            if (_bridge == IntPtr.Zero) throw new ObjectDisposedException(nameof(CollabClient));
            var id = _nextCallId++;
            var tcs = new TaskCompletionSource<CallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = tcs;
            NativeMethods.Call(_bridge, id, NativeMethods.Utf8(op), NativeMethods.Utf8(Json.Write(args ?? new Dictionary<string, object?>())));
            return tcs.Task;
        }

        private Dictionary<string, object?> Query(string op, Dictionary<string, object?>? args = null)
        {
            if (_bridge == IntPtr.Zero) throw new ObjectDisposedException(nameof(CollabClient));
            var result = NativeMethods.Query(_bridge, NativeMethods.Utf8(op), NativeMethods.Utf8(Json.Write(args ?? new Dictionary<string, object?>())));
            return Json.ParseObject(NativeMethods.FromUtf8(result));
        }

        private void Log(int level, string message)
        {
            try
            {
                _options.Log?.Invoke(level, message);
            }
            catch
            {
                // Logging must never take the add-in down.
            }
        }

        private async void Send(ulong id, Dictionary<string, object?> meta, byte[] body)
        {
            var state = new RequestState();
            _requests[id] = state;
            var response = new Dictionary<string, object?>();
            byte[]? responseBody = null;
            var timeout = meta.Num("timeout");
            if (timeout > 0) state.Cancel.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                using (var request = new HttpRequestMessage(new HttpMethod(meta.Str("method")), meta.Str("url")))
                {
                    string? contentType = null;
                    foreach (var header in meta.Arr("headers").OfType<List<object?>>())
                    {
                        var name = header.Count > 0 ? header[0] as string ?? "" : "";
                        var value = header.Count > 1 ? header[1] as string ?? "" : "";
                        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
                        else request.Headers.TryAddWithoutValidation(name, value);
                    }
                    if (body.Length > 0)
                    {
                        request.Content = new ByteArrayContent(body);
                        if (contentType != null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                    }

                    using (var reply = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, state.Cancel.Token).ConfigureAwait(false))
                    {
                        var bytes = await reply.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        var file = meta.Str("file");
                        if (file.Length > 0) File.WriteAllBytes(file, bytes);
                        else responseBody = bytes;

                        var headers = new List<object?>();
                        foreach (var h in reply.Headers.Concat(reply.Content.Headers))
                        {
                            headers.Add(new List<object?> { h.Key, string.Join(", ", h.Value) });
                        }
                        response["connected"] = true;
                        response["status"] = (int)reply.StatusCode;
                        response["headers"] = headers;
                        LastTransportError = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                response["cancelled"] = state.CancelledByCore;
                response["timedOut"] = !state.CancelledByCore;
                if (!state.CancelledByCore) LastTransportError = "The server took too long to respond.";
            }
            catch (Exception ex)
            {
                LastTransportError = DescribeTransportError(ex);
                Log(2, $"{meta.Str("method")} {meta.Str("url")}: {LastTransportError}");
            }

            _thread.Post(() =>
            {
                _requests.Remove(id);
                state.Cancel.Dispose();
                if (_bridge == IntPtr.Zero) return;
                var length = responseBody?.Length ?? 0;
                NativeMethods.CompleteRequest(_bridge, id, NativeMethods.Utf8(Json.Write(response)), responseBody, (UIntPtr)length);
            });
        }

        private string DescribeTransportError(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is AuthenticationException || (e is WebException w && w.Status == WebExceptionStatus.TrustFailure))
                {
                    var rejected = CertificateTrust.LastRejected;
                    return rejected == null
                        ? "The server's HTTPS certificate isn't trusted by Windows."
                        : "The server's HTTPS certificate isn't trusted by Windows: " + string.Join(" ", rejected.Problems);
                }
                if (e is WebException web)
                {
                    switch (web.Status)
                    {
                        case WebExceptionStatus.NameResolutionFailure: return "Can't find the server. Check the address and your network or VPN.";
                        case WebExceptionStatus.ConnectFailure: return "Can't connect to the server. Check the address and port, and that the server is running.";
                        case WebExceptionStatus.SecureChannelFailure: return "Couldn't set up a secure (TLS) connection with the server.";
                    }
                }
                if (e is System.Net.Sockets.SocketException socket)
                {
                    return socket.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound
                        ? "Can't find the server. Check the address and your network or VPN."
                        : "Can't connect to the server. Check the address and port, and that the server is running.";
                }
            }
            var innermost = error;
            while (innermost.InnerException != null) innermost = innermost.InnerException;
            return innermost.Message;
        }

        // ---------------------------------------------------------------- native callbacks
        // These run inside bridge calls, on the logic thread. Nothing may throw back into C++.

        private static CollabClient? From(IntPtr context)
        {
            try
            {
                return GCHandle.FromIntPtr(context).Target as CollabClient;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static void OnSend(IntPtr context, ulong requestId, IntPtr requestJson, IntPtr body, UIntPtr bodySize)
        {
            var self = From(context);
            if (self == null) return;
            try
            {
                self.Send(requestId, Json.ParseObject(NativeMethods.FromUtf8(requestJson)), NativeMethods.Copy(body, bodySize));
            }
            catch (Exception ex)
            {
                self.Log(3, "send: " + ex);
            }
        }

        private static void OnCancel(IntPtr context, ulong requestId)
        {
            var self = From(context);
            if (self == null || !self._requests.TryGetValue(requestId, out var state)) return;
            state.CancelledByCore = true;
            state.Cancel.Cancel();
        }

        private static void OnPostDelayed(IntPtr context, ulong timerId, double delaySeconds)
        {
            var self = From(context);
            if (self == null) return;
            try
            {
                self._timers[timerId] = self._thread.Delay(TimeSpan.FromSeconds(Math.Max(0, delaySeconds)), () =>
                {
                    self._timers.Remove(timerId);
                    if (self._bridge != IntPtr.Zero) NativeMethods.FireTimer(self._bridge, timerId);
                });
            }
            catch (Exception ex)
            {
                self.Log(3, "timer: " + ex);
            }
        }

        private static void OnCancelTimer(IntPtr context, ulong timerId)
        {
            var self = From(context);
            if (self == null || !self._timers.TryGetValue(timerId, out var timer)) return;
            self._timers.Remove(timerId);
            timer.Dispose();
        }

        private static void OnSaveSession(IntPtr context, IntPtr json)
        {
            var self = From(context);
            if (self == null) return;
            try
            {
                self._options.SaveSession?.Invoke(NativeMethods.FromUtf8(json));
            }
            catch (Exception ex)
            {
                self.Log(3, "Could not save the session: " + ex.Message);
            }
        }

        private static void OnLog(IntPtr context, int level, IntPtr message) => From(context)?.Log(level, NativeMethods.FromUtf8(message));

        private static void OnResult(IntPtr context, ulong callId, IntPtr resultJson, IntPtr data, UIntPtr dataSize)
        {
            var self = From(context);
            if (self == null || !self._calls.TryGetValue(callId, out var tcs)) return;
            self._calls.Remove(callId);
            try
            {
                var result = new CallResult { Json = Json.ParseObject(NativeMethods.FromUtf8(resultJson)), Data = NativeMethods.Copy(data, dataSize) };
                if (result.Json.Bool("ok")) tcs.TrySetResult(result);
                else tcs.TrySetException(new CollabException(result.Json.Str("error") is var e && e.Length > 0 ? e : "The operation failed."));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        private static void OnOnline(IntPtr context, int online)
        {
            var self = From(context);
            if (self == null) return;
            // Raised later, not from inside the bridge call, so handlers may call back in.
            self._thread.Post(() => self.OnlineChanged?.Invoke(online != 0));
        }
    }
}
