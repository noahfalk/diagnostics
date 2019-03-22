using System;

// this namespace has all the extensibility APIs you would need in the plugin
// to interoperate with the SnapshotAnalyzer
using Microsoft.Diagnostic.SnapshotAnalysis.Abstractions;


namespace Microsoft.Diagnostic.SnapshotAnalysis.ExamplePlugin
{
    // To have a valid plugin you need to implement a type with exactly this name
    // and a public static Init() method with the signature shown.
    public static class SnapshotAnalyzerPlugin
    {
        public static void Init(ISnapshotAnalyzer snapshotAnalyzer)
        {
            // Do whatever you want to enumerate the commands you want to register...
            // Commands registered this way won't have any help, description or parsing support
            // but it gets a simple job done
            snapshotAnalyzer.AddCommand("PrintVersion", PrintVersion);

            // On the other hand calling something like this could scan the assembly
            // to register commands with full help, description, and parsing support
            // but you would have to define the new types.
            // snapshotAnalyzer.AddCommandsFromAttributedTypes(this.GetType().Assembly);

            // Other interfaces for command registration should also be possible, we'd 
            // just have to agree on it.
        }


        private static void PrintVersion(AnalyzeContext analyzeContext, string args)
        {
            Console.WriteLine(analyzeContext.Runtime.ClrInfo.Version);
            if(!string.IsNullOrEmpty(args))
            {
                Console.WriteLine("For demo purposes the args were: " + args);
            }
        }
    }
}
