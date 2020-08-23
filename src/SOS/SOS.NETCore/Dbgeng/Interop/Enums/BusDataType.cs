﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace SOS.Dbgeng.Interop
{
    public enum BUS_DATA_TYPE
    {
        ConfigurationSpaceUndefined = -1,
        Cmos,
        EisaConfiguration,
        Pos,
        CbusConfiguration,
        PCIConfiguration,
        VMEConfiguration,
        NuBusConfiguration,
        PCMCIAConfiguration,
        MPIConfiguration,
        MPSAConfiguration,
        PNPISAConfiguration,
        SgiInternalConfiguration,
        MaximumBusDataType
    }
}