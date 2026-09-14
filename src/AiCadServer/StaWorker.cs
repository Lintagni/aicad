using System;
using System.Collections.Generic;
using System.Threading;

namespace AiCadServer
{
    /// <summary>
    /// A single STA thread that owns every COM call to AutoCAD.
    ///
    /// HttpListener hands requests to arbitrary thread-pool threads, which are
    /// MTA. Driving an STA COM server from those means every call is marshalled,
    /// and AutoCAD is unforgiving about it. Funnelling all COM through one STA
    /// thread keeps the apartment right and serialises access for free.
    /// </summary>
    public class StaWorker : IDisposable
    {
        private class WorkItem
        {
            public Func<object> Work;
            public object Result;
            public Exception Error;
            public ManualResetEvent Done;
        }

        private readonly Thread _thread;
        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
        private readonly object _gate = new object();
        private volatile bool _stopping;

        public StaWorker()
        {
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Name = "AiCad COM";
            _thread.Start();
        }

        private void Run()
        {
            while (true)
            {
                WorkItem item = null;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stopping) Monitor.Wait(_gate);
                    if (_stopping && _queue.Count == 0) return;
                    item = _queue.Dequeue();
                }

                try { item.Result = item.Work(); }
                catch (Exception ex) { item.Error = ex; }
                finally { item.Done.Set(); }
            }
        }

        /// <summary>Runs the work on the STA thread and waits for it.</summary>
        public object Invoke(Func<object> work)
        {
            if (_stopping) throw new ObjectDisposedException("StaWorker");

            WorkItem item = new WorkItem();
            item.Work = work;
            item.Done = new ManualResetEvent(false);

            lock (_gate)
            {
                _queue.Enqueue(item);
                Monitor.Pulse(_gate);
            }

            item.Done.WaitOne();
            item.Done.Close();
            if (item.Error != null) throw item.Error;
            return item.Result;
        }

        public T Invoke<T>(Func<T> work)
        {
            object result = Invoke(delegate { return (object)work(); });
            return result == null ? default(T) : (T)result;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stopping = true;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
