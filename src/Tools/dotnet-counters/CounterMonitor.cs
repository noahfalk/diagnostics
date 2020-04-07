// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.NETCore.Client;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tools.Counters.Exporters;
using Microsoft.Diagnostics.Monitoring;
using Microsoft.Diagnostics.Monitoring.Contracts;

namespace Microsoft.Diagnostics.Tools.Counters
{
    public static class CounterMonitor
    {
        public static async Task<int> Monitor(CancellationToken ct, List<string> counter_list, IConsole console, int processId, int refreshInterval)
        {
            return await HandleExceptions(console, async () =>
            {
                EventPipeCounterPipelineSettings settings = BuildSettings(processId, counter_list, refreshInterval, console);
                settings.Output = new ConsoleWriter();
                await RunUILoop(settings, allowPause: true, ct);
            });
        }

        public static async Task<int> Collect(CancellationToken ct, List<string> counter_list, IConsole console, int processId, int refreshInterval, CountersExportFormat format, string output)
        {
            return await HandleExceptions(console, async () =>
            {
                EventPipeCounterPipelineSettings settings = BuildSettings(processId, counter_list, refreshInterval, console);

                if (output.Length == 0)
                {
                    throw new CommandLineError("Output cannot be an empty string");
                }

                string extension = null;
                Func<Stream, IEventPipeCounterPipelineOutput> exporterFactory = null;
                if (format == CountersExportFormat.csv)
                {
                    extension = ".csv";
                    exporterFactory = s => new CounterCsvStreamExporter(s);
                }
                else if (format == CountersExportFormat.json)
                {
                    // Try getting the process name.
                    string processName = "";
                    try
                    {
                        processName = Process.GetProcessById(processId).ProcessName;
                    }
                    catch (Exception) { }

                    extension = "*.json";
                    exporterFactory = s => new CounterJsonStreamExporter(s, processName);
                }
                else
                {
                    throw new CommandLineError($"The output format {format} is not a valid output format.");
                }

                string filePath = output.EndsWith(extension) ? output : output + extension;
                if (File.Exists(filePath))
                {
                    Console.WriteLine($"[Warning] {filePath} already exists. This file will be overwritten.");
                }
                using(Stream outputStream = File.Create(filePath))
                {
                    settings.Output = exporterFactory(outputStream);

                    Console.WriteLine("Starting a counter session. Press Q to quit.");
                    await RunUILoop(settings, allowPause: false, ct);
                    Console.WriteLine("File saved to " + filePath);
                }
            });
        }

        private static async Task<int> HandleExceptions(IConsole console, Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (PipelineAbortedException)
            {
                // This can happen when the runtime doesn't respond promptly to a request to stop sending events.
                // We don't treat it as failure worth notifying users about
            }
            catch (CommandLineError e)
            {
                console.Error.WriteLine(e.Message);
                return 0;
            }
            catch (PipelineException e)
            {
                console.Error.WriteLine(e.Message);
                return 0;
            }
            return 1;
        }

        private static EventPipeCounterPipelineSettings BuildSettings(int processId, List<string> counterList, int refreshInterval, IConsole console)
        {
            EventPipeCounterPipelineSettings settings = new EventPipeCounterPipelineSettings();
            if (processId == 0)
            {
                throw new CommandLineError("--process-id is required.");
            }
            settings.ProcessId = processId;
            settings.CounterGroups = BuildCounterGroups(counterList, console);
            settings.RefreshInterval = TimeSpan.FromSeconds(refreshInterval);
            return settings;
        }

        private static EventPipeCounterGroup[] BuildCounterGroups(List<string> counterList, IConsole console)
        {
            List<EventPipeCounterGroup> groups = new List<EventPipeCounterGroup>();
            if (counterList.Count == 0)
            {
                console.Out.WriteLine($"counter_list is unspecified. Monitoring all counters by default.");
                groups.Add(new EventPipeCounterGroup() { ProviderName = "System.Runtime" });
            }
            else
            {
                for (var i = 0; i < counterList.Count; i++)
                {
                    string counterSpecifier = counterList[i];
                    string[] tokens = counterSpecifier.Split('[');
                    string providerName = tokens[0];

                    if (tokens.Length == 1)
                    {
                        groups.Add(new EventPipeCounterGroup() { ProviderName = providerName });
                    }
                    else
                    {
                        string counterNames = tokens[1];
                        string[] enabledCounters = counterNames.Substring(0, counterNames.Length-1).Split(',');
                        groups.Add(new EventPipeCounterGroup() { ProviderName = providerName, CounterNames = enabledCounters });
                    }
                }
            }
            return groups.ToArray();
        }
        

        private static async Task RunUILoop(EventPipeCounterPipelineSettings settings, bool allowPause, CancellationToken ct)
        {
            EventPipeCounterPipeline pipeline = new EventPipeCounterPipeline(settings);
            while(true)
            {
                Task<ConsoleKey> keyTask = PollForNextKeypress(ct);
                List<Task> tasks = new List<Task>();
                tasks.Add(keyTask);
                if(pipeline != null)
                {
                    tasks.Add(pipeline.RunAsync());
                }
                Task completedTask = await Task.WhenAny(tasks);
                if(completedTask == keyTask)
                {
                    try
                    {
                        ConsoleKey key = await keyTask;
                        if (key == ConsoleKey.Q)
                        {
                            break;
                        }
                        else if(key == ConsoleKey.P && allowPause && pipeline != null)
                        {
                            await pipeline.StopAsync(TimeSpan.FromSeconds(1));
                            pipeline = null;
                        }
                        else if(key == ConsoleKey.R && pipeline == null)
                        {
                            pipeline = new EventPipeCounterPipeline(settings);
                        }
                    }
                    catch(TaskCanceledException)
                    {
                        break; // ctrl-c
                    }
                }
                else
                {
                    break; // pipeline stopped on its own which implies an error happened
                           // the exception will be thrown in pipeline.StopAsync below
                }
            }

            if (pipeline != null)
            {
                await pipeline.StopAsync(TimeSpan.FromSeconds(1));
            }
        }

        private static async Task<ConsoleKey> PollForNextKeypress(CancellationToken token)
        {
            while(!token.IsCancellationRequested)
            {
                await Task.Delay(250, token);
                if(Console.KeyAvailable)
                {
                    return Console.ReadKey(true).Key;
                }
            }
            throw new TaskCanceledException();
        }
    }

    class CommandLineError : Exception 
    {
        public CommandLineError(string message) : base(message) { }
    }
}
