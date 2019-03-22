using System;
using System.Reflection;

namespace Microsoft.Diagnostic.SnapshotAnalysis.Abstractions
{
    public interface ISnapshotAnalyzer
    {
        void AddCommandsFromAttributedTypes(Assembly commandAssembly);
        void AddCommand(string commandName, Action<AnalyzeContext, string> commandMethod);
        void AddService<T>(T service);
        T GetService<T>();
    }
}