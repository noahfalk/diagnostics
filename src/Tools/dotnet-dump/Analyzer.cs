using Microsoft.Diagnostic.SnapshotAnalysis;
using Microsoft.Diagnostic.SnapshotAnalysis.Abstractions;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostic.Tools.Dump
{
    public class Analyzer
    {
        public async Task<int> Analyze(FileInfo dump_path, string[] command, string pluginPath)
        {
            using (var snapshotAnalyzer = new SnapshotAnalyzer())
            {
                try
                {
                    snapshotAnalyzer.AddCommandsFromAttributedTypes(typeof(Analyzer).Assembly);
                    snapshotAnalyzer.LoadPluginAssembly(pluginPath);

                    snapshotAnalyzer.ConsoleProvider.Out.WriteLine($"Loading core dump: {dump_path} ...");
                    snapshotAnalyzer.LoadDump(dump_path);

                    snapshotAnalyzer.ConsoleProvider.Out.WriteLine("Ready to process analysis commands. Type 'help' to list available commands or 'help [command]' to get detailed help on a command.");
                    snapshotAnalyzer.ConsoleProvider.Out.WriteLine("Type 'quit' or 'exit' to exit the session.");

                    AnalyzeContext analyzeContext = snapshotAnalyzer.GetService<AnalyzeContext>();

                    // Automatically enable symbol server support on Linux and MacOS
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        await snapshotAnalyzer.RunCommand("SetSymbolServer -ms");
                    }

                    // Run the commands from the dotnet-dump command line
                    if (command != null)
                    {
                        foreach (string cmd in command)
                        {
                            await snapshotAnalyzer.RunCommand(cmd);
                        }
                    }

                    // Start interactive command line processing
                    await snapshotAnalyzer.RunRepl();
                }
                catch (Exception ex) when
                    (ex is ClrDiagnosticsException ||
                     ex is FileNotFoundException ||
                     ex is DirectoryNotFoundException ||
                     ex is UnauthorizedAccessException ||
                     ex is PlatformNotSupportedException ||
                     ex is InvalidDataException ||
                     ex is InvalidOperationException ||
                     ex is NotSupportedException)
                {
                    snapshotAnalyzer.ConsoleProvider.Error.WriteLine($"{ex.Message}");
                    return 1;
                }
            }

            return 0;
        }
    }
}
