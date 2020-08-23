// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace SOS.Dbgeng.Interop
{
    public enum DEBUG_EINDEX : uint
    {
        NAME = 0,
#pragma warning disable CA1069 // Enums values should not be duplicated
        FROM_START = 0,
#pragma warning restore CA1069 // Enums values should not be duplicated
        FROM_END = 1,
        FROM_CURRENT = 2
    }
}