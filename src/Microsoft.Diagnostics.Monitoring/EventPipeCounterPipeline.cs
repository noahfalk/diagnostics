using Microsoft.Diagnostics.Monitoring.Contracts;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Monitoring
{
    public class EventPipeCounterPipelineSettings
    {
        public int ProcessId { get; set; }
        public EventPipeCounterGroup[] CounterGroups { get; set; }
        public TimeSpan RefreshInterval { get; set; }
        public IEventPipeCounterPipelineOutput Output { get; set; }
    }

    public class EventPipeCounterGroup
    {
        public string ProviderName { get; set; }
        public string[] CounterNames { get; set; }
    }

    public interface IEventPipeCounterPipelineOutput
    {
        void PipelineStarted();
        void CounterPayloadReceived(string providerName, ICounterPayload counter);
        void PipelineStopped();
    }

    public class EventPipeCounterPipeline : IPipeline
    {
        EventPipeCounterPipelineSettings _settings;

        CancellationTokenSource _stopCTS;
        CancellationTokenSource _abortCTS;
        Lazy<Task> _runPipeline;

        public EventPipeCounterPipeline(EventPipeCounterPipelineSettings settings)
        {
            if (settings.ProcessId <= 0)
            {
                throw new ArgumentException("ProcessId setting must be >= 0");
            }
            if (settings.CounterGroups.Length == 0)
            {
                throw new ArgumentException("CounterGroups must not be empty");
            }
            int intervalSecs = (int)settings.RefreshInterval.TotalSeconds;
            if (intervalSecs < 1)
            {
                throw new ArgumentException("RefreshInterval must be >= 1 second");
            }
            if (settings.Output == null)
            {
                throw new ArgumentNullException("settings.Output");
            }

            _settings = settings;
            _stopCTS = new CancellationTokenSource();
            _abortCTS = new CancellationTokenSource();
            _runPipeline = new Lazy<Task>(RunPipeline);
        }

        public Task RunAsync()
        {
            return _runPipeline.Value;
        }

        public Task StopAsync(CancellationToken token = default)
        {
            _stopCTS.Cancel();
            if (token != default)
            {
                token.Register(Abort);
            }
            return _runPipeline.Value;
        }

        public void Abort()
        {
            _abortCTS.Cancel();
        }

        private async Task RunPipeline()
        {
            EventPipeCounterGroup[] groups = _settings.CounterGroups;
            int intervalSecs = (int)_settings.RefreshInterval.TotalSeconds;
            EventPipeSession session = null;
            EventPipeEventSource source = null;
            try
            {
                _settings.Output.PipelineStarted();

                IEnumerable<EventPipeProvider> providers = groups.Select(g =>
                    new EventPipeProvider(g.ProviderName, EventLevel.LogAlways, 0, new Dictionary<string, string>()
                    {{ "EventCounterIntervalSec", intervalSecs.ToString()}} ));

                CounterFilter filter = new CounterFilter();
                foreach (EventPipeCounterGroup g in groups)
                {
                    if (g.CounterNames == null)
                    {
                        filter.AddFilter(g.ProviderName);
                    }
                    else
                    {
                        filter.AddFilter(g.ProviderName, g.CounterNames);
                    }
                }

                _abortCTS.Token.Register(() => 
                {
                    try
                    {
                        session?.Dispose();
                        source?.StopProcessing();
                    }
                    catch (Exception) { } // TODO: We need to do better at categorizing which exceptions
                                          // are anticipated and which are bugs
                });
                _stopCTS.Token.Register(() =>
                {
                    try
                    {
                        session?.Stop();
                    }
                    catch (EndOfStreamException ex)
                    {
                        // If the app we're monitoring exits abruptly, this may throw in which case we just swallow the exception and exit gracefully.
                        Debug.WriteLine($"[ERROR] {ex.ToString()}");
                    }
                    // We may time out if the process ended before we sent StopTracing command. We can just exit in that case.
                    catch (TimeoutException)
                    {
                    }
                    // On Unix platforms, we may actually get a PNSE since the pipe is gone with the process, and Runtime Client Library
                    // does not know how to distinguish a situation where there is no pipe to begin with, or where the process has exited
                    // before dotnet-counters and got rid of a pipe that once existed.
                    catch (PlatformNotSupportedException)
                    {
                    }
                });
                await Task.Run(() => {
                    DiagnosticsClient diagnosticsClient = new DiagnosticsClient(_settings.ProcessId);
                    session = diagnosticsClient.StartEventPipeSession(providers, false);
                    source = new EventPipeEventSource(session.EventStream);
                    source.Dynamic.All += e => DynamicAllMonitor(filter, intervalSecs, e);
                    source.Process();
                }, _abortCTS.Token);
            }
            catch (DiagnosticsClientException ex)
            {
                throw new PipelineException("Failed to start the counter session: " + ex.Message, ex);
            }
            catch (OperationCanceledException)
            {
                throw new PipelineAbortedException();
            }
            finally
            {
                session?.Dispose();
                source?.Dispose();
                _settings.Output.PipelineStopped();
            }
        }

        private void DynamicAllMonitor(CounterFilter filter, int intervalSecs, TraceEvent obj)
        {
            if (obj.EventName.Equals("EventCounters"))
            {
                IDictionary<string, object> payloadVal = (IDictionary<string, object>)(obj.PayloadValue(0));
                IDictionary<string, object> payloadFields = (IDictionary<string, object>)(payloadVal["Payload"]);

                // If it's not a counter we asked for, ignore it.
                if (!filter.Filter(obj.ProviderName, payloadFields["Name"].ToString())) return;

                ICounterPayload payload = payloadFields["CounterType"].Equals("Sum") ? 
                    (ICounterPayload)new IncrementingCounterPayload(payloadFields, intervalSecs) :
                    (ICounterPayload)new CounterPayload(payloadFields);
                _settings.Output.CounterPayloadReceived(obj.ProviderName, payload);
            }
        }
    }
}
