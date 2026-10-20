using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Revit
{
    /// <summary>
    /// The only safe way to touch the Revit API from the web UI.
    /// Callers enqueue work and await a Task; Revit runs the work on its main thread
    /// inside a valid API context when it next becomes idle.
    /// </summary>
    public class RevitDispatcher : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();
        private ExternalEvent _event;

        /// <summary>Must be called from a valid Revit API context (e.g. OnStartup).</summary>
        public void Initialize() => _event = ExternalEvent.Create(this);

        public Task<T> InvokeAsync<T>(Func<UIApplication, T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(app =>
            {
                try { tcs.SetResult(work(app)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            ExternalEventRequest request = _event.Raise();
            if (request == ExternalEventRequest.Denied || request == ExternalEventRequest.TimedOut)
                Log.Write("ExternalEvent.Raise returned " + request + " (work stays queued until Revit is idle).");
            return tcs.Task;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out Action<UIApplication> work))
                work(app);
        }

        public string GetName() => "BIM Command Center Dispatcher";
    }
}
