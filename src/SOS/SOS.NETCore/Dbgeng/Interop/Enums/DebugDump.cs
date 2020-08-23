// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace SOS.Dbgeng.Interop
{
    public enum DEBUG_DUMP : uint
    {
        SMALL = 1024,
        DEFAULT = 1025,
        FULL = 1026,
        IMAGE_FILE = 1027,
        TRACE_LOG = 1028,
        WINDOWS_CD = 1029,
#pragma warning disable CA1069 // Enums values should not be duplicated
        KERNEL_DUMP = 1025,
        KERNEL_SMALL_DUMP = 1024,
        KERNEL_FULL_DUMP = 1026
#pragma warning restore CA1069 // Enums values should not be duplicated
    }
}