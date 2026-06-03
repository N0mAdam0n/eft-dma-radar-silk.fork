// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc
{
    public static class SizeChecker<T>
    {
        public static readonly int Size = GetSize();

        private static int GetSize()
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                throw new NotSupportedException(typeof(T).ToString());
            return Unsafe.SizeOf<T>();
        }
    }
}
