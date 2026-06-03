using System.Text;
using eft_dma_radar.Silk.DMA;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Shared reader for Unity-flavoured <c>std::string</c> values (libc++-style
    /// with short-string optimization). The discriminator byte at <c>+0x1F</c>
    /// selects between the in-place SSO buffer (small strings, â‰¤30 chars) and
    /// a heap-allocated long string (pointer at <c>+0x00</c>, length at <c>+0x10</c>).
    /// <para>
    /// Used in multiple places â€” originally for TagManager layer names, now
    /// also for per-actor <c>GameObject.name</c> values (Marcel's
    /// <c>NpShapeâ†’NativeColliderâ†’NativeGameObject</c> chain). Extracted to its
    /// own type so the layout knowledge has one home and isn't duplicated.
    /// </para>
    /// </summary>
    internal static class UnityStdString
    {
        // Maximum reasonable length â€” guards against garbage-pointer reads.
        // Unity GameObject names are typically &lt; 64 chars in practice; we
        // allow 256 to handle long generated names ("MeshCollider (instance) (...)")
        // without truncation.
        private const int MaxLength = 256;

        /// <summary>
        /// Reads the string stored at <paramref name="slotAddr"/>. Returns the
        /// decoded ASCII string on success, or null on any read failure /
        /// implausible state. Empty strings come back as <c>""</c>.
        /// </summary>
        /// <param name="slotAddr">Address of the <c>std::string</c> object's
        /// first byte (the start of its SSO buffer / data pointer overlap).</param>
        public static string? TryRead(ulong slotAddr)
        {
            if (!Memory.TryReadValue<byte>(slotAddr + PhysXOffsets.StdString_SsoFlag, out var flag, false))
                return null;

            int length;
            ulong dataAddr;

            if (flag >= 0x40)
            {
                // LONG MODE â€” data pointer at +0x00, length at +0x10.
                if (!Memory.TryReadValue<ulong>(slotAddr + PhysXOffsets.StdString_Length, out var lenRaw, false)
                    || lenRaw > (ulong)MaxLength)
                    return null;
                length = (int)lenRaw;
                if (length == 0) return string.Empty;
                if (!Memory.TryReadPtr(slotAddr + PhysXOffsets.StdString_DataOrSsoBuf, out var dataPtr, false)
                    || !dataPtr.IsValidVirtualAddress())
                    return null;
                dataAddr = dataPtr;
            }
            else
            {
                // SSO MODE â€” length = 31 - flag, data is in-place from +0x00.
                length = 31 - flag;
                if (length < 0 || length > 31) return null;
                if (length == 0) return string.Empty;
                dataAddr = slotAddr + PhysXOffsets.StdString_DataOrSsoBuf;
            }

            byte[]? raw;
            try { raw = Memory.ReadArray<byte>(dataAddr, length, false); }
            catch { return null; }
            if (raw is null) return null;
            return DecodeAscii(raw, length);
        }

        /// <summary>
        /// Decodes a byte slice as ASCII, stopping at the first non-printable byte.
        /// Unity object names and layer names are pure ASCII; rejecting anything
        /// else helps detect "we read garbage" without raising.
        /// </summary>
        private static string DecodeAscii(byte[] bytes, int length)
        {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                byte b = bytes[i];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) return string.Empty; // unprintable â‡’ wrong memory
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
