﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Diagnostics.Monitoring.RestServer
{
    /// <summary>
    /// Periodically gets metrics from the app, and persists these to a metrics store.
    /// </summary>
    public sealed class MetricsService : BackgroundService
    {
        private readonly DiagnosticsEventPipeProcessor _pipeProcessor;
        private readonly IDiagnosticServices _services;
        private readonly MetricsStoreService _store;

        public MetricsService(IDiagnosticServices services,
            IOptions<PrometheusConfiguration> metricsConfiguration,
            MetricsStoreService metricsStore)
        {
            _store = metricsStore;

            _pipeProcessor = new DiagnosticsEventPipeProcessor(PipeMode.Metrics, metricLoggers: new[] { new MetricsLogger(_store.MetricsStore) },
                metricIntervalSeconds: metricsConfiguration.Value.UpdateIntervalSeconds);
            _services = services;
        }
        
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return Task.Run( async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        //TODO In multi-process scenarios, how do we decide which process to choose?
                        //One possibility is to enable metrics after a request to begin polling for metrics
                        IProcessInfo pi = await _services.GetProcessAsync(filter: null, stoppingToken);
                        await _pipeProcessor.Process(pi.Client, pi.ProcessId, Timeout.InfiniteTimeSpan, stoppingToken);
                    }
                    catch(Exception e) when (!(e is OperationCanceledException))
                    {
                        //Most likely we failed to resolve the pid. Attempt to do this again.
                        await Task.Delay(5000);
                    }
                }
            }, stoppingToken);
        }

        public override async void Dispose()
        {
            base.Dispose();
            await _pipeProcessor.DisposeAsync();
        }
    }
}
