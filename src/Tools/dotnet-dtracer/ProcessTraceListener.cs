using Microsoft.Diagnostics.Tools.RuntimeClient;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.DTracer
{
    public class ProcessTraceListener
    {
        Task _task;
        int _pid;
        TraceCorrelator _correlator;

        public ProcessTraceListener(int pid, TraceCorrelator correlator)
        {
            _pid = pid;
            _correlator = correlator;
        }

        public void Start()
        {
            _task = Task.Run(() =>
            {
                SessionConfigurationV2 config = new SessionConfigurationV2(10, EventPipeSerializationFormat.NetTrace, false, GetProviders());
                using (Stream s = EventPipeClient.CollectTracing2(_pid, config, out ulong sessionId))
                using (EventPipeEventSource source = new EventPipeEventSource(s))
                {
                    source.Dynamic.All += _correlator.AddEvent;
                    try
                    {
                        source.Process();
                    }
                    catch(Exception)
                    {
                        // Sadly we get an untyped exception object when the stream abruptly disconnects
                        // We should fix this in TraceEvent
                    }
                }
            });
        }

        private Provider[] GetProviders()
        {
            return new Provider[]
                {
                    new Provider("System.Threading.Tasks.TplEventSource",
                        keywords: 0x80,
                        eventLevel: EventLevel.LogAlways),
                    new Provider(
                        name: "Microsoft-Diagnostics-DiagnosticSource",
                        keywords: 0x3,
                        eventLevel: EventLevel.Verbose,
                        filterData: "FilterAndPayloadSpecs=\"" +
                        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.BeginRequest@Activity1Start:-" +
                            "httpContext.Request.Method" +
                            ";httpContext.Request.Host" +
                            ";httpContext.Request.Path" +
                            ";ActivityStartTime=*Activity.StartTimeUtc.Ticks" +
                            //";httpContext.Request.QueryString" +
                            ";ActivityParentId=*Activity.ParentId" +
                            ";ActivityId=*Activity.Id" +
                        "\n" +
                        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.EndRequest@Activity1Stop:-" +
                            "httpContext.Response.StatusCode" +
                            ";ActivityId=*Activity.Id" +
                            ";ActivityDuration=*Activity.Duration.Ticks" +
                            //";ActivityTags=*Activity.Tags.*Enumerate" +
                        "\n" +
                        "HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut@Event:-" +
                        "\n" +
                        "HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut.Start@Activity2Start:-" +
                            "ActivityParentId=*Activity.ParentId" +
                            ";ActivityId=*Activity.Id" +
                            ";ActivityStartTime=*Activity.StartTimeUtc.Ticks" +
                        "\n" +
                        "HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut.Stop@Activity2Stop:-" +
                            "ActivityId=*Activity.Id" +
                            ";ActivityDuration=*Activity.Duration.Ticks" +
                        "\n" +
                        "\""
                        )
                };
        }
    }
}
