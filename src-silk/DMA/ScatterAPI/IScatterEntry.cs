// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using VmmSharpEx.Scatter;

namespace eft_dma_radar.Silk.DMA.ScatterAPI
{
    public interface IScatterEntry : IDisposable
    {
        ulong Address { get; }
        int CB { get; }
        bool IsFailed { get; set; }
        void ReadResult(VmmScatter scatter);
    }
}
