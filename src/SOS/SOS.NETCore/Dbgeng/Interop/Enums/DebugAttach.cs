// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace SOS.Dbgeng.Interop
{
    [Flags]
    public enum DEBUG_ATTACH : uint
    {
        KERNEL_CONNECTION = 0,
        LOCAL_KERNEL = 1,
        EXDI_DRIVER = 2,

#pragma warning disable CA1069 // Enums values should not be duplicated
        DEFAULT = 0,
        NONINVASIVE = 1,
        EXISTING = 2,
#pragma warning restore CA1069 // Enums values should not be duplicated
        NONINVASIVE_NO_SUSPEND = 4,
        INVASIVE_NO_INITIAL_BREAK = 8,
        INVASIVE_RESUME_PROCESS = 0x10,
        NONINVASIVE_ALLOW_PARTIAL = 0x20
    }
}