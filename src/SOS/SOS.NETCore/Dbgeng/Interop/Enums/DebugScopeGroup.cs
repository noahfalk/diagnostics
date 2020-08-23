﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace SOS.Dbgeng.Interop
{
    [Flags]
    public enum DEBUG_SCOPE_GROUP : uint
    {
        ARGUMENTS = 1,
        LOCALS = 2,
        ALL = 3
    }
}