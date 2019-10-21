using System;

namespace Microsoft.Diagnostics.Tools.DTracer
{
    class Program
    {
        ProcessMonitor _monitor = new ProcessMonitor();
        TraceCorrelator _correlator = new TraceCorrelator();
        static void Main(string[] args)
        {
            Program p = new Program();
            p.Run();
        }

        void Run()
        {
            Console.WriteLine("Watching for processes...");
            _correlator.Start();
            _monitor.ProcessStarted += _monitor_ProcessStarted;
            _monitor.Start();
            Console.ReadLine();
        }

        private void _monitor_ProcessStarted(object sender, int id)
        {
            Console.WriteLine($"Process {id} started");
            ProcessTraceListener listener = new ProcessTraceListener(id, _correlator);
            listener.Start();
        }
    }
}
