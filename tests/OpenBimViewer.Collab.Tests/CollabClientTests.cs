using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Xunit;

namespace OpenBimViewer.Collab.Tests
{
    /// <summary>The managed client and the native Core together, against a scripted server.</summary>
    public sealed class CollabClientTests : IDisposable
    {
        private const string AuthReply =
            "{\"accessToken\":\"a1\",\"refreshToken\":\"r1\",\"user\":{\"id\":\"u1\",\"email\":\"ann@example.com\",\"name\":\"Ann\"}}";

        private const string IssueJson =
            "{\"id\":\"i1\",\"guid\":\"6f1c1d3e-2b8f-4c47-9a40-1d0f7a2b9e10\",\"index\":3,\"title\":\"Cracked beam\",\"topicStatus\":\"Open\"," +
            "\"modifiedDate\":\"2026-10-01T00:00:00.000Z\"," +
            "\"viewpoints\":[{\"guid\":\"vp1\",\"snapshot\":\"s1.png\",\"camera\":{\"location\":{\"x\":1,\"y\":2,\"z\":3},\"direction\":{\"x\":0,\"y\":1,\"z\":0},\"up\":{\"x\":0,\"y\":0,\"z\":1},\"fieldOfView\":50}}," +
            "{\"guid\":\"vp2\",\"snapshot\":\"s2.png\"}]," +
            "\"comments\":[{\"author\":\"bob@example.com\",\"comment\":\"Bigger now\",\"viewpointGuid\":\"vp2\"}]}";

        private readonly string _cache = Path.Combine(Path.GetTempPath(), "obv-tests-" + Guid.NewGuid().ToString("N"));
        private readonly PumpedThread _thread = new PumpedThread();
        private readonly FakeServer _server = new FakeServer();
        private string? _savedSession;
        private CollabClient? _client;

        private CollabClient Client => _client ??= new CollabClient(_thread, new CollabClientOptions
        {
            CacheDirectory = _cache,
            SessionJson = _savedSession,
            SaveSession = json => _savedSession = json,
            HttpHandler = _server,
        });

        public void Dispose()
        {
            _client?.Dispose();
            try
            {
                Directory.Delete(_cache, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private void SignIn()
        {
            _server.Respond = r => r.Url.EndsWith("/api/auth/login") ? FakeServer.Json(200, AuthReply) : FakeServer.Json(404, "{}");
            _thread.Run(() => Client.LoginAsync("srv.example.com", "ann@example.com", "secret"));
        }

        [Fact]
        public void LoginStoresTheSessionButNotThePassword()
        {
            SignIn();

            var state = Client.State;
            Assert.True(state.LoggedIn);
            Assert.Equal("https://srv.example.com", state.ServerUrl);
            Assert.Equal("ann@example.com", state.User.Email);
            Assert.Equal(new[] { "https://srv.example.com" }, state.RecentServers);
            Assert.Contains("\"refreshToken\":\"r1\"", _savedSession);
            Assert.DoesNotContain("secret", _savedSession);
            Assert.Contains("\"password\":\"secret\"", _server.Requests[0].Body);
        }

        [Fact]
        public void FailedLoginReportsTheServersMessage()
        {
            _server.Respond = _ => FakeServer.Json(401, "{\"error\":\"Invalid email or password\"}");
            var error = Assert.Throws<CollabException>(() => _thread.Run(() => Client.LoginAsync("https://srv", "a@b.c", "x")));
            Assert.Equal("Invalid email or password", error.Message);
            Assert.False(Client.State.LoggedIn);
        }

        [Fact]
        public void ASavedSessionResumesInANewClient()
        {
            SignIn();
            _client!.Dispose();
            _client = null;

            _server.Respond = r => r.Url.EndsWith("/api/auth/refresh") ? FakeServer.Json(200, AuthReply) : FakeServer.Json(404, "{}");
            Assert.True(Client.State.HasSavedSession);
            _thread.Run(() => Client.ResumeAsync());
            Assert.True(Client.State.LoggedIn);
        }

        [Fact]
        public void IssuesComeBackWithSnapshotUrlsAndCameras()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(200, "[" + IssueJson + "]");

            var issues = _thread.Run(() => Client.GetIssuesAsync("p1"));

            var issue = Assert.Single(issues);
            Assert.Equal("Cracked beam", issue.Title);
            Assert.Equal(3, issue.Index);
            Assert.Equal("vp1", issue.SnapshotViewpointGuid);
            Assert.Equal("https://srv.example.com/api/snapshots/s1.png", issue.SnapshotUrl);
            Assert.NotNull(issue.Camera);
            Assert.Equal(50, issue.Camera!.FieldOfView);
            Assert.Equal(2, issue.Camera.Location.Y);
            var comment = Assert.Single(issue.Comments);
            Assert.Equal("vp2", comment.ViewpointGuid);
            Assert.Equal("https://srv.example.com/api/snapshots/s2.png", comment.SnapshotUrl);
            Assert.Null(comment.Camera);
            Assert.Equal("Bearer a1", _server.Requests.Last().Authorization);
        }

        [Fact]
        public void AnExpiredTokenIsRefreshedAndTheRequestReplayed()
        {
            SignIn();
            var calls = 0;
            _server.Respond = r =>
            {
                if (r.Url.EndsWith("/api/auth/refresh"))
                    return FakeServer.Json(200, AuthReply.Replace("\"a1\"", "\"a2\""));
                return calls++ == 0 ? FakeServer.Json(401, "{}") : FakeServer.Json(200, "[{\"id\":\"p1\",\"name\":\"Tower\"}]");
            };

            var projects = _thread.Run(() => Client.GetProjectsAsync());

            Assert.Equal("Tower", Assert.Single(projects).Name);
            Assert.Equal("Bearer a2", _server.Requests.Last().Authorization);
        }

        [Fact]
        public void SavedViewsCarryTheirCamera()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(200,
                "[{\"id\":\"v1\",\"name\":\"Lobby\",\"snapshot\":\"v.png\",\"isStarting\":true," +
                "\"camera\":{\"location\":{\"x\":5,\"y\":6,\"z\":7},\"direction\":{\"x\":1,\"y\":0,\"z\":0},\"up\":{\"x\":0,\"y\":0,\"z\":1},\"fieldOfView\":60}}]");

            var view = Assert.Single(_thread.Run(() => Client.GetViewsAsync("p1")));

            Assert.Equal("Lobby", view.Name);
            Assert.True(view.IsStarting);
            Assert.Equal(7, view.Camera!.Location.Z);
            Assert.Equal("https://srv.example.com/api/snapshots/v.png", view.SnapshotUrl);
        }

        [Fact]
        public void CreatingAnIssueSendsTheViewpointSnapshotAndSelection()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(201, IssueJson);
            var issue = new NewIssue
            {
                Title = "From Revit",
                Priority = "High",
                Components = { "2O2Fr$t4X7Zf8NOew3FLOH" },
                Viewpoint = new ViewpointCapture
                {
                    Camera = new Camera { Location = new Vec3(1, 2, 3), Direction = new Vec3(0, 1, 0) },
                    Png = new byte[] { 1, 2, 3 },
                    Selection = { "2O2Fr$t4X7Zf8NOew3FLOH" },
                },
            };

            var created = _thread.Run(() => Client.CreateIssueAsync("p1", issue));

            Assert.Equal("i1", created.Id);
            var sent = Json.ParseObject(_server.Requests.Last().Body);
            Assert.Equal("From Revit", sent.Str("title"));
            Assert.Equal("High", sent.Str("priority"));
            var vp = sent.Obj("viewpoint")!;
            Assert.Equal("data:image/png;base64,AQID", vp.Str("snapshot"));
            Assert.Equal("perspective", vp.Obj("camera")!.Str("type"));
            Assert.Equal("2O2Fr$t4X7Zf8NOew3FLOH", ((System.Collections.Generic.Dictionary<string, object?>)vp.Arr("selection")[0]!).Str("ifcGuid"));
            Assert.Equal("application/json", _server.Requests.Last().ContentType);
        }

        [Fact]
        public void UpdatingAnIssueSendsOnlyTheChangedFields()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(200, IssueJson.Replace("\"Open\"", "\"Closed\""));

            var current = Issue.From(Json.ParseObject(IssueJson));
            var changes = IssueChanges.Diff(current, "Cracked beam", "", "", "Closed", "", "bob@example.com");
            var updated = _thread.Run(() => Client.UpdateIssueAsync("p1", "i1", changes));

            Assert.Equal("Closed", updated.TopicStatus);
            var request = _server.Requests.Last();
            Assert.Equal("PATCH", request.Method);
            Assert.EndsWith("/api/projects/p1/issues/i1", request.Url);
            var sent = Json.ParseObject(request.Body);
            Assert.Equal(new[] { "assignedTo", "topicStatus" }, sent.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void CommentsCanAttachAView()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(201, "{}");

            _thread.Run(() => Client.AddCommentAsync("p1", "i1", "See here", new ViewpointCapture { Png = new byte[] { 9 } }));

            var sent = Json.ParseObject(_server.Requests.Last().Body);
            Assert.Equal("See here", sent.Str("comment"));
            Assert.Equal("data:image/png;base64,CQ==", sent.Obj("viewpoint")!.Str("snapshot"));
            Assert.EndsWith("/issues/i1/comments", _server.Requests.Last().Url);
        }

        [Fact]
        public void ReplacingASnapshotPutsTheImageAndReturnsItsNewUrl()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(200, "{\"snapshot\":\"new.png\"}");

            var url = _thread.Run(() => Client.ReplaceSnapshotAsync("p1", "i1", "vp2", new byte[] { 1, 2, 3 }));

            Assert.Equal("https://srv.example.com/api/snapshots/new.png", url);
            var request = _server.Requests.Last();
            Assert.Equal("PUT", request.Method);
            Assert.EndsWith("/issues/i1/viewpoints/vp2/snapshot", request.Url);
            Assert.Equal("data:image/png;base64,AQID", Json.ParseObject(request.Body).Str("snapshot"));
        }

        [Fact]
        public void DownloadsReturnTheBytesUnchanged()
        {
            SignIn();
            var png = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
            _server.Respond = _ => FakeServer.Bytes(png);

            var bytes = _thread.Run(() => Client.DownloadAsync("https://srv.example.com/api/snapshots/s1.png"));

            Assert.Equal(png, bytes);
        }

        [Fact]
        public void OfflineEditsAreQueuedAndSynced()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(200, "[" + IssueJson + "]");
            _thread.Run(() => Client.GetIssuesAsync("p1"));

            var online = true;
            Client.OnlineChanged += value => online = value;
            _server.Respond = _ => FakeServer.Unreachable();
            var updated = _thread.Run(() => Client.UpdateIssueAsync("p1", "i1", new IssueChanges { TopicStatus = "Closed" }));

            Assert.Equal("Closed", updated.TopicStatus);
            Assert.False(Client.State.Online);
            Assert.NotNull(Client.LastTransportError);
            var pending = Assert.Single(Client.PendingChanges("p1"));
            Assert.Equal("update", pending.Action);
            _thread.Run(async () => await System.Threading.Tasks.Task.Delay(50)); // let OnlineChanged arrive
            Assert.False(online);

            _server.Respond = r => r.Url.EndsWith("/sync")
                ? FakeServer.Json(200, "{\"sessionId\":\"s\",\"results\":[{\"opId\":\"" + pending.OpId + "\",\"status\":\"applied\"}]}")
                : FakeServer.Json(200, "[]");
            var result = _thread.Run(() => Client.SyncAsync("p1"));

            Assert.Equal(1, result.Applied);
            Assert.Equal(0, result.Remaining);
            Assert.Empty(Client.PendingChanges("p1"));
            Assert.True(Client.State.Online);
        }

        [Fact]
        public void LogoutForgetsTheRefreshToken()
        {
            SignIn();
            _server.Respond = _ => FakeServer.Json(204, "");
            Client.Logout();
            Assert.False(Client.State.LoggedIn);
            Assert.Contains("\"refreshToken\":\"\"", _savedSession);
        }

        [Fact]
        public void VocabularyListsTheBcfEnumerations()
        {
            var vocabulary = Client.Vocabulary;
            Assert.Contains("Open", vocabulary.TopicStatuses);
            Assert.Contains("Closed", vocabulary.TopicStatuses);
            Assert.Contains("High", vocabulary.Priorities);
            Assert.StartsWith("#", vocabulary.StatusColors["Open"]);
        }

        [Fact]
        public void DisposingFailsWhatIsStillWaiting()
        {
            SignIn();
            _server.Respond = _ =>
            {
                System.Threading.Thread.Sleep(200);
                return FakeServer.Json(200, "[]");
            };
            var pending = Client.GetProjectsAsync();
            _client!.Dispose();
            _client = null;
            var error = Assert.Throws<AggregateException>(() => pending.Wait(5000));
            Assert.IsType<CollabException>(error.InnerException);
        }
    }

    public sealed class HelperTests
    {
        [Fact]
        public void JsonRoundTrips()
        {
            const string text = "{\"a\":[1,2.5,-3e2,true,false,null],\"s\":\"q\\\"\\\\\\n\\u00e9\\ud83d\\ude00\",\"o\":{}}";
            var parsed = Json.ParseObject(text);
            Assert.Equal(-300.0, (double)((System.Collections.Generic.List<object?>)parsed["a"]!)[2]!);
            Assert.Equal("q\"\\\né\U0001F600", parsed.Str("s"));
            Assert.Equal(parsed.Str("s"), Json.ParseObject(Json.Write(parsed)).Str("s"));
            Assert.Throws<FormatException>(() => Json.Parse("{\"a\":}"));
            Assert.Throws<FormatException>(() => Json.Parse("[1] 2"));
        }

        [Theory]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
        [InlineData("3c09de4c-61a1-4a87-a41d-1a6f5b4f1e01")]
        [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
        public void IfcGuidMatchesTheBase64Definition(string guid)
        {
            // The definition: the 128-bit number in IFC's base-64 alphabet, 22 digits, most significant first.
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
            var n = System.Numerics.BigInteger.Parse("0" + guid.Replace("-", ""), System.Globalization.NumberStyles.HexNumber);
            var expected = new char[22];
            for (var i = 21; i >= 0; i--)
            {
                expected[i] = alphabet[(int)(n % 64)];
                n /= 64;
            }

            var encoded = IfcGuid.Encode(Guid.Parse(guid));

            Assert.Equal(new string(expected), encoded);
            Assert.True(IfcGuid.IsValid(encoded));
        }

        [Fact]
        public void DpapiRoundTripsForThisUserOnly()
        {
            var secret = System.Text.Encoding.UTF8.GetBytes("refresh-token");
            var entropy = new byte[] { 1, 2, 3 };
            var sealedBytes = Dpapi.Protect(secret, entropy);
            Assert.NotEqual(secret, sealedBytes);
            Assert.Equal(secret, Dpapi.Unprotect(sealedBytes, entropy));
            Assert.ThrowsAny<Exception>(() => Dpapi.Unprotect(sealedBytes, new byte[] { 9 }));
        }

        [Fact]
        public void PinnedCertificatesAreKeyedByHostAndPort()
        {
            var trust = new CertificateTrust();
            Assert.False(trust.HasPinsFor("srv:443"));
            trust.Trust("srv:443", "ab".PadRight(64, 'c'));
            Assert.True(trust.HasPinsFor("srv:443"));
            Assert.Equal("", CertificateTrust.Normalize("not a fingerprint"));
            Assert.Equal(95, CertificateTrust.Normalize(new string('a', 64)).Length);
        }
    }
}
