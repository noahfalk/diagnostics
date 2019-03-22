using Microsoft.Diagnostic.Repl;
using Microsoft.Diagnostic.SnapshotAnalysis.Abstractions;
using Microsoft.Diagnostics.Runtime;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostic.SnapshotAnalysis
{
    public class SnapshotAnalyzer : IDisposable, ISnapshotAnalyzer
    {
        readonly CommandProcessor _commandProcessor;
        DataTarget _target;
        AnalyzeContext _analyzeContext;

        public SnapshotAnalyzer()
        {
            _commandProcessor = new CommandProcessor();
            ConsoleProvider = new ConsoleProvider();
            AddService(ConsoleProvider);
        }

        public ConsoleProvider ConsoleProvider { get; private set; }

        public void LoadPluginAssembly(string pluginAssemblyPath)
        {
            Assembly plugin = Assembly.LoadFrom(pluginAssemblyPath);
            Type entrypointType = plugin.GetExportedTypes().Where(t => t.Name == "SnapshotAnalyzerPlugin").FirstOrDefault();
            MethodInfo entrypoint = entrypointType.GetMethod("Init", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(ISnapshotAnalyzer) }, null);
            if (entrypoint != null)
            {
                entrypoint.Invoke(null, new object[] { this });
            }
        }
            

        public void AddService<T>(T service)
        {
            _commandProcessor.AddService(service);
        }

        public T GetService<T>()
        {
            return _commandProcessor.GetService<T>();
        }

        public void AddCommandsFromAttributedTypes(Assembly commandAssembly)
        {
            _commandProcessor.AddCommandsFromAttributedTypes(commandAssembly);
        }

        public void AddCommand(string name, Action<AnalyzeContext, string> commandMethod)
        {
            _commandProcessor.AddCommand(new Command(name,
                "",
                null,
                new Argument()
                {
                    Name = "arguments",
                    ArgumentType = typeof(string[]),
                    Arity = new ArgumentArity(0, int.MaxValue)
                },
                true,
                new SimpleAnalyzeCommandHandler(this, commandMethod)));
        }

        class SimpleAnalyzeCommandHandler : ICommandHandler
        {
            SnapshotAnalyzer _owner;
            Action<AnalyzeContext, string> _commandMethod;
            public SimpleAnalyzeCommandHandler(SnapshotAnalyzer owner, Action<AnalyzeContext, string> commandMethod)
            {
                _owner = owner;
                _commandMethod = commandMethod;
            }
            public Task<int> InvokeAsync(InvocationContext context)
            {
                string[] args = (string[])context.ParseResult.CommandResult.GetValueOrDefault();
                string argString = null;
                if (args.Length > 0)
                {
                    argString = string.Join(" ", args);
                }
                _commandMethod(_owner._analyzeContext, argString);
                return Task.FromResult(0);
            }
        }

        public void LoadDump(FileInfo dump_path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _target = DataTarget.LoadCoreDump(dump_path.FullName);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _target = DataTarget.LoadCrashDump(dump_path.FullName, CrashDumpReader.ClrMD);
            }
            else
            {
                throw new PlatformNotSupportedException($"Unsupported operating system: {RuntimeInformation.OSDescription}");
            }

            // Create common analyze context for commands
            _analyzeContext = new AnalyzeContext(_target)
            {
                CurrentThreadId = unchecked((int)_target.DataReader.EnumerateAllThreads().FirstOrDefault())
            };
            _commandProcessor.AddService(_analyzeContext);
        }

        public async Task RunCommand(string command)
        {
            await _commandProcessor.Parse(command, ConsoleProvider);
        }

        public async Task RunRepl()
        {
            await ConsoleProvider.Start(async (string commandLine, CancellationToken cancellation) => {
                _analyzeContext.CancellationToken = cancellation;
                await _commandProcessor.Parse(commandLine, ConsoleProvider);
            });
        }

        public void Dispose()
        {
            if(_target != null)
            {
                _target.Dispose();
            }
        }
    }
}
