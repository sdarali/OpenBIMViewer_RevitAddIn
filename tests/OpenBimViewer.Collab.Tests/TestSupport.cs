using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenBimViewer.Collab.Tests
{
    /// <summary>A logic thread the test drives: posted work runs only while <see cref="Run{T}"/> pumps.</summary>
    internal sealed class PumpedThread : ILogicThread
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();

        public void Post(Action action) => _queue.Add(action);

        public IDisposable Delay(TimeSpan delay, Action action)
        {
            var timer = new Timer(_ => Post(action), null, delay, Timeout.InfiniteTimeSpan);
            return timer;
        }

        /// <summary>Start <paramref name="start"/> on this thread and pump until its task finishes.</summary>
        public T Run<T>(Func<Task<T>> start)
        {
            var task = start();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The operation didn't finish.");
                if (_queue.TryTake(out var next, 50)) next();
            }
            return task.GetAwaiter().GetResult();
        }

        public void Run(Func<Task> start) => Run(async () =>
        {
            await start();
            return true;
        });
    }

    internal sealed class Recorded
    {
        public string Method = "";
        public string Url = "";
        public string Body = "";
        public string Authorization = "";
        public string ContentType = "";
    }

    /// <summary>A scripted server: each request is recorded and answered by <see cref="Respond"/>.</summary>
    internal sealed class FakeServer : HttpMessageHandler
    {
        public readonly List<Recorded> Requests = new List<Recorded>();
        public Func<Recorded, HttpResponseMessage> Respond = _ => Json(404, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = new Recorded
            {
                Method = request.Method.Method,
                Url = request.RequestUri!.ToString(),
                Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(),
                Authorization = request.Headers.Authorization?.ToString() ?? "",
                ContentType = request.Content?.Headers.ContentType?.MediaType ?? "",
            };
            lock (Requests) Requests.Add(r);
            return Respond(r);
        }

        public static HttpResponseMessage Json(int status, string body) =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Bytes(byte[] body) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        public static HttpResponseMessage Unreachable() => throw new HttpRequestException("No route to host");
    }
}
