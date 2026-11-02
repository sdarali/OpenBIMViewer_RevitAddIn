using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using OpenBimViewer.Collab;

namespace OpenBimViewer.Revit.Services
{
    /// <summary>
    /// Runs work in Revit's API context from the modeless panel. The panel can't call the Revit API
    /// from its own event handlers, so it queues a function here, raises an ExternalEvent, and
    /// Revit calls back when the API is available. The returned task completes on the UI thread.
    /// </summary>
    internal sealed class RevitTasks : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();
        private readonly ExternalEvent _event;

        /// <summary>Must be created in an API context (e.g. OnStartup).</summary>
        public RevitTasks() => _event = ExternalEvent.Create(this);

        public Task<T> Run<T>(Func<UIApplication, T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(app =>
            {
                try
                {
                    tcs.TrySetResult(work(app));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            // Pending is fine too: one Execute drains everything queued.
            var request = _event.Raise();
            if (request == ExternalEventRequest.Denied || request == ExternalEventRequest.TimedOut)
            {
                tcs.TrySetException(new InvalidOperationException("Revit is busy. Finish the current command and try again."));
            }
            return tcs.Task;
        }

        public Task Run(Action<UIApplication> work) => Run(app =>
        {
            work(app);
            return true;
        });

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var work)) work(app);
        }

        public string GetName() => "Open BIM Viewer";
    }

    /// <summary>The IFC Collab Core runs on Revit's UI thread, through its dispatcher.</summary>
    internal sealed class DispatcherThread : ILogicThread
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherThread(Dispatcher dispatcher) => _dispatcher = dispatcher;

        public void Post(Action action) => _dispatcher.BeginInvoke(action);

        public IDisposable Delay(TimeSpan delay, Action action)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = delay };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
            return new TimerHandle(timer);
        }

        private sealed class TimerHandle : IDisposable
        {
            private readonly DispatcherTimer _timer;
            public TimerHandle(DispatcherTimer timer) => _timer = timer;
            public void Dispose() => _timer.Stop();
        }
    }
}
