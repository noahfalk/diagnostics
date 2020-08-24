using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SOS
{
    static class DumpAsync
    {
        public static void Run(IConsoleService console, ClrRuntime runtime)
        {
            DumpAsyncOptions options = new DumpAsyncOptions();

            // Display a message if the heap isn't verified.
            if (!runtime.Heap.CanWalkHeap)
            {
                DisplayInvalidStructuresMessage(console);
                return;
            }

            // Find the state machine types
            ClrModule module = runtime.Modules.Where(m => m.Name.EndsWith("System.Private.CoreLib.dll")).FirstOrDefault();
            ClrType asyncStateMachineType = module.GetTypeByName("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox");
            ClrType finalizableAsyncStateMachineType = module.GetTypeByName("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+DebugFinalizableAsyncStateMachineBox");
            ClrType taskType = module.EnumerateTypes().Where(t => t.Name == "System.Threading.Tasks.Task" && t.BaseType.Name == "System.Object").FirstOrDefault();

            // Walk each heap object looking for async state machine objects.  As we're targeting .NET Core 2.1+, all such objects
            // will be Task or Task-derived types.
            Dictionary<ulong, AsyncRecord> asyncRecords = new Dictionary<ulong, AsyncRecord>();
            foreach (var obj in runtime.Heap.EnumerateObjects())
            {
                if (!IsDerivedFrom(obj.Type, taskType))
                {
                    continue;
                }
                AsyncRecord ar = new AsyncRecord();
                ar.Address = obj.Address;
                ar.MT = obj.Type.MethodTable;
                ar.Size = obj.Type.BaseSize;
                ar.StateMachineAddr = obj.Address;
                ar.StateMachineMT = obj.Type.MethodTable;
                ar.IsValueType = false;
                ar.IsTopLevel = true;
                ar.IsStateMachine = false;
                ar.TaskStateFlags = 0;
                ar.StateValue = 0;
                ar.FilteredByOptions = false;
                //ar.FilteredByOptions = // we process all objects to support forming proper chains, but then only display ones that match the user's request
                //    (mt == NULL || mt == itr->GetMT()) && // Match only MTs the user requested.
                //    (type == NULL || _wcsstr(itr->GetTypeName(), type) != NULL) && // Match only type name substrings the user requested.
                //    (addr == NULL || addr == itr->GetAddress()); // Match only the object at the specified address.
                ar.Continuations = new List<ulong>();

                ar.TaskStateFlags = obj.GetField<int>("m_stateFlags");

                // Get the async state machine object's StateMachine field.
                ClrInstanceField stateMachineField = obj.Type.GetFieldByName("StateMachine");
                if(stateMachineField != null)
                {
                    ar.IsStateMachine = true;
                    ar.IsValueType = stateMachineField.IsValueClass;

                    // Get the address and method table of the state machine.  While it'll generally be a struct, it is valid for it to be a
                    // class (the C# compiler generates a class in debug builds to better support Edit-And-Continue), so we accommodate both.
                    int stateFieldOffset = -1;
                    if (ar.IsValueType)
                    {
                        ar.StateMachineAddr = ar.Address + (uint)stateMachineField.Offset;
                        ar.StateMachineMT = stateMachineField.Type.MethodTable;
                        stateFieldOffset = stateMachineField.Type.GetFieldByName("<>1__state").Offset;
                    }
                    else
                    {
                        ar.StateMachineAddr = runtime.DataTarget.DataReader.ReadPointerUnsafe(ar.Address + (uint)stateMachineField.Offset);
                        if(ar.StateMachineAddr != 0)
                        {
                            ClrType stateMachineType = runtime.Heap.GetObjectType(ar.StateMachineAddr);
                            ar.StateMachineMT = stateMachineType.MethodTable; // update from Canon to actual type
                            stateFieldOffset = stateMachineType.GetFieldByName("<>__state").Offset;
                        }
                    }

                    if (stateFieldOffset >= 0 && (ar.IsValueType || stateFieldOffset != 0))
                    {
                        ar.StateValue = (int)runtime.DataTarget.DataReader.ReadDwordUnsafe(ar.StateMachineAddr + (uint)stateFieldOffset);
                    }
                }

                // If we only want to include incomplete async objects, skip this one if it's completed.
                if (!options.IncludeCompleted && ar.IsComplete)
                {
                    continue;
                }

                // If the user has asked to include "async stacks" information, resolve any continuation
                // that might be registered with it.  This could be a single continuation, or it could
                // be a list of continuations in the case of the same task being awaited multiple times.
                ulong nextAddr;
                if (options.IncludeStacks && TryGetContinuation(runtime, obj.Address, obj.Type, out nextAddr))
                {
                    if (obj.Type.Name.StartsWith("System.Collections.Generic.List"))
                    {
                        // The continuation is a List<object>.  Iterate through its internal object[]
                        // looking for non-null objects, and adding each one as a continuation.
                        ClrInstanceField itemsField = obj.Type.GetFieldByName("_items");
                        if(itemsField != null)
                        {

                            ulong listItemsPtr = runtime.DataTarget.DataReader.ReadPointerUnsafe(obj.Address + (uint)itemsField.Offset);
                            if(listItemsPtr != 0)
                            {
                                ClrObject objData = runtime.Heap.GetObject(listItemsPtr);
                                ClrType objDataType = runtime.Heap.GetObjectType(listItemsPtr);
                                if(objData.IsArray && objData.Type.ComponentType.Name == "System.Object")
                                {
                                    for (int i = 0; i < objData.Length; i++)
                                    {
                                        ulong elementPtr = runtime.DataTarget.DataReader.ReadPointerUnsafe(
                                            objData.Address + (uint)(objData.Type.BaseSize + i*objData.Type.ElementSize));
                                        if (elementPtr != 0)
                                        {
                                            ResolveContinuation(runtime, ref elementPtr);
                                            ar.Continuations.Add(elementPtr);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        ar.Continuations.Add(obj.Address);
                    }
                }

                // We've gathered all of the needed information for this heap object.  Add it to our list of async records.
                asyncRecords.Add(ar.Address, ar);
            }
        }

        static bool TryGetContinuation(ClrRuntime runtime, ulong addr, ClrType taskType, out ulong contAddr)
        {
            // Get the continuation field from the task.
            ClrInstanceField continuationField = taskType.GetFieldByName("m_continuationObject");
            if (continuationField != null)
            {
                ulong contObjPtr = runtime.DataTarget.DataReader.ReadPointerUnsafe(addr + (uint)continuationField.Offset);
                if(contObjPtr != 0)
                {
                    contAddr = contObjPtr;
                    ResolveContinuation(runtime, ref contAddr);
                    return true;
                }
            }
            contAddr = 0;
            return false;
        }

        static void ResolveContinuation(ClrRuntime runtime, ref ulong contAddr)
        {
            // Ideally this continuation is itself an async method box.
            ClrType contType = runtime.Heap.GetObjectType(contAddr);
            ClrInstanceField field = contType.GetFieldByName("StateMachine");
            if(field == null)
            { 
                // It was something else.

                // If it's a standard task continuation, get its task field.
                ClrInstanceField taskField = contType.GetFieldByName("m_task");
                if(taskField != null)
                {
                    contAddr = runtime.DataTarget.DataReader.ReadPointerUnsafe(contAddr + (uint)taskField.Offset);
                    if(contAddr != 0)
                    {
                        contType = runtime.Heap.GetObjectType(contAddr);
                    }
                }
                else
                {
                    // If it's storing an action wrapper, try to follow to that action's target.
                    ClrInstanceField actionField = contType.GetFieldByName("m_action");
                    if(actionField != null)
                    {
                        contAddr = runtime.DataTarget.DataReader.ReadPointerUnsafe(contAddr + (uint)actionField.Offset);
                        if (contAddr != 0)
                        {
                            contType = runtime.Heap.GetObjectType(contAddr);
                        }
                    }

                    // If we now have an Action, try to follow through to the delegate's target.
                    ClrInstanceField targetField = contType.GetFieldByName("_target");
                    if(targetField != null)
                    {
                        contAddr = runtime.DataTarget.DataReader.ReadPointerUnsafe(contAddr + (uint)actionField.Offset);
                        if (contAddr != 0)
                        {
                            contType = runtime.Heap.GetObjectType(contAddr);
                            ClrInstanceField continuationField = null;
                            if (contType.Name.StartsWith("System.Runtime.CompilerServices.AsyncMethodBuilderCore+ContinuationWrapper") &&
                                ((continuationField = contType.GetFieldByName("_continuation")) != null))
                            {
                                contAddr = runtime.DataTarget.DataReader.ReadPointerUnsafe(contAddr + (uint)actionField.Offset);
                                if (contAddr != 0)
                                {
                                    contType = runtime.Heap.GetObjectType(contAddr);
                                    ClrInstanceField continuationTargetField = contType.GetFieldByName("_target");
                                    if (continuationTargetField != null)
                                    {
                                        contAddr = runtime.DataTarget.DataReader.ReadPointerUnsafe(contAddr + (uint)actionField.Offset);
                                        if (contAddr != 0)
                                        {
                                            contType = runtime.Heap.GetObjectType(contAddr);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        static bool IsDerivedFrom(ClrType derived, ClrType baseType)
        {
            while(derived != null)
            {
                if (derived == baseType)
                {
                    return true;
                }
                derived = derived.BaseType;
            }
            return false;
        }

        static void DisplayInvalidStructuresMessage(IConsoleService console)
        {
            console.Write("The garbage collector data structures are not in a valid state for traversal.\n");
            console.Write("It is either in the \"plan phase,\" where objects are being moved around, or\n");
            console.Write("we are at the initialization or shutdown of the gc heap. Commands related to \n");
            console.Write("displaying, finding or traversing objects as well as gc heap segments may not \n");
            console.Write("work properly. !dumpheap and !verifyheap may incorrectly complain of heap \n");
            console.Write("consistency errors.\n");
        }
    }

    class DumpAsyncOptions
    {
        public ulong Addr { get; set; }
        public ulong Mt { get; set; }
        public string Type { get; set; }
        public bool IncludeAllTasks { get; set; }
        public bool IncludeCompleted { get; set; }
        public bool Fields { get; set; }
        public bool IncludeStacks { get; set; }
        public bool IncludeRoots { get; set; }
        public bool Dml { get; set; }
    }

    class AsyncRecord
    {
        public ulong Address { get; set; }
        public ulong MT { get; set; }
        public int Size { get; set; }
        public ulong StateMachineAddr { get; set; }
        public ulong StateMachineMT { get; set; }
        public bool FilteredByOptions { get; set; }
        public bool IsStateMachine { get; set; }
        public bool IsValueType { get; set; }
        public bool IsTopLevel { get; set; }
        public int TaskStateFlags { get; set; }
        public int StateValue { get; set; }
        public List<ulong> Continuations { get; set; }

        const int TASK_STATE_COMPLETED_MASK = 0x1600000;

        public bool IsComplete => (TaskStateFlags & TASK_STATE_COMPLETED_MASK) != 0;
    }
}
