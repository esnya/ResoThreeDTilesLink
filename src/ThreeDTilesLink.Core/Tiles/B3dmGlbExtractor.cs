using System.Buffers.Binary;
using ThreeDTilesLink.Core.Contracts;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class B3dmGlbExtractor : IB3dmGlbExtractor
    {
        private static ReadOnlySpan<byte> B3dmMagic => "b3dm"u8;
        private static ReadOnlySpan<byte> GlbMagic => "glTF"u8;

        public byte[] ExtractGlb(byte[] b3dmBytes)
        {
            ArgumentNullException.ThrowIfNull(b3dmBytes);

            ReadOnlySpan<byte> span = b3dmBytes;
            if (span.Length < 28)
            {
                throw new InvalidOperationException("b3dm payload is too short.");
            }

            if (!span[..4].SequenceEqual(B3dmMagic))
            {
                throw new InvalidOperationException("b3dm payload is missing the b3dm header.");
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
            if (version != 1)
            {
                throw new InvalidOperationException($"Unsupported b3dm version: {version}");
            }

            uint byteLength = BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]);
            if (byteLength > span.Length || byteLength < 28)
            {
                throw new InvalidOperationException("b3dm payload reported an invalid byte length.");
            }

            uint featureTableJsonByteLength = BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]);
            uint featureTableBinaryByteLength = BinaryPrimitives.ReadUInt32LittleEndian(span[16..20]);
            uint batchTableJsonByteLength = BinaryPrimitives.ReadUInt32LittleEndian(span[20..24]);
            uint batchTableBinaryByteLength = BinaryPrimitives.ReadUInt32LittleEndian(span[24..28]);

            ulong glbOffset = 28UL +
                featureTableJsonByteLength +
                featureTableBinaryByteLength +
                batchTableJsonByteLength +
                batchTableBinaryByteLength;
            if (glbOffset >= byteLength)
            {
                throw new InvalidOperationException("b3dm payload does not contain an embedded glTF asset.");
            }

            int glbStart = checked((int)glbOffset);
            int glbLength = checked((int)byteLength - glbStart);
            ReadOnlySpan<byte> glbSpan = span.Slice(glbStart, glbLength);
            if (glbSpan.Length < 4 || !glbSpan[..4].SequenceEqual(GlbMagic))
            {
                throw new InvalidOperationException("b3dm payload does not contain a valid embedded glTF asset.");
            }

            return glbSpan.ToArray();
        }
    }
}
