using Microsoft.Diagnostics.Tools.RuntimeClient;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.DTracer
{
    public class ProcessMonitor
    {
        Task _monitor;
        CancellationTokenSource _cts = new CancellationTokenSource();
        List<int> _processIds = new List<int>();

        public void Start()
        {
            _monitor = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(100);
                    List<int> currentProcIds = new List<int>(EventPipeClient.ListAvailablePorts());
                    foreach(int id in currentProcIds)
                    {
                        if(!_processIds.Contains(id))
                        {
                            _processIds.Add(id);
                            this.ProcessStarted?.Invoke(this, id);
                        }
                    }
                    foreach(int id in _processIds.ToArray())
                    {
                        if(!currentProcIds.Contains(id))
                        {
                            _processIds.Remove(id);
                        }
                    }
                }
            }, _cts.Token);
        }

        public void Stop()
        {
            _cts.Cancel();
            _monitor.Wait();
        }

        public event EventHandler<int> ProcessStarted;
    }
}
