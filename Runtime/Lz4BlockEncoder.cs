using System;

namespace SensorFlex.Recorder
{
    // LZ4 raw block encoder (no frame header). Wire-compatible with COMPRESSION_LZ4_RAW
    // and Lz4BlockDecoder in the player package.
    internal static class Lz4BlockEncoder
    {
        const int HashLog  = 12;
        const int HashSize = 1 << HashLog;
        const int MaxDist  = 65535;
        const int MflLimit = 12;
        // LZ4 block format: the last 5 bytes must be literals. Matches that run into them
        // decode with lenient decoders but are rejected by the reference decoder (LZ4_decompress_safe).
        const int LastLiterals = 5;

        // Returns bytes written to dst, or -1 if dst too small.
        public static int Encode(byte[] src, int srcOff, int srcLen,
                                  byte[] dst, int dstOff, int dstCap)
        {
            if (srcLen < 1) return 0;
            var table = new int[HashSize];
            int sEnd   = srcOff + srcLen;
            int sLimit = sEnd - MflLimit;
            int dEnd   = dstOff + dstCap;
            int dPos   = dstOff;
            int anchor = srcOff;
            int sPos   = srcOff;

            if (srcLen >= 4)
            {
                table[Hash4(src, sPos)] = sPos - srcOff;
                sPos++;

                while (sPos < sLimit)
                {
                    int h        = Hash4(src, sPos);
                    int matchRel = table[h];
                    table[h]     = sPos - srcOff;

                    int matchPos = srcOff + matchRel;
                    int dist     = sPos - matchPos;

                    if (dist > 0 && dist <= MaxDist &&
                        src[matchPos]     == src[sPos]     &&
                        src[matchPos + 1] == src[sPos + 1] &&
                        src[matchPos + 2] == src[sPos + 2] &&
                        src[matchPos + 3] == src[sPos + 3])
                    {
                        int mLen = 4;
                        int sMax = Math.Min(sEnd - LastLiterals - sPos, MaxDist);
                        while (mLen < sMax && src[matchPos + mLen] == src[sPos + mLen]) mLen++;

                        int litLen = sPos - anchor;
                        int tLitN  = Math.Min(litLen, 15);
                        int tMchN  = Math.Min(mLen - 4, 15);
                        int need   = 1 + (litLen >= 15 ? (litLen - 15) / 255 + 2 : 0)
                                       + litLen + 2
                                       + (mLen - 4 >= 15 ? (mLen - 4 - 15) / 255 + 2 : 0);
                        if (dPos + need > dEnd) break;

                        dst[dPos++] = (byte)((tLitN << 4) | tMchN);
                        if (litLen >= 15)
                        {
                            int extra = litLen - 15;
                            while (extra >= 255) { dst[dPos++] = 255; extra -= 255; }
                            dst[dPos++] = (byte)extra;
                        }
                        Buffer.BlockCopy(src, anchor, dst, dPos, litLen);
                        dPos += litLen;
                        dst[dPos++] = (byte)(dist & 0xFF);
                        dst[dPos++] = (byte)(dist >> 8);
                        if (mLen - 4 >= 15)
                        {
                            int extra = mLen - 4 - 15;
                            while (extra >= 255) { dst[dPos++] = 255; extra -= 255; }
                            dst[dPos++] = (byte)extra;
                        }
                        sPos  += mLen;
                        anchor = sPos;
                        if (sPos < sLimit) table[Hash4(src, sPos)] = sPos - srcOff;
                        sPos++;
                        continue;
                    }
                    sPos++;
                }
            }

            int finalLen = sEnd - anchor;
            int tLit     = Math.Min(finalLen, 15);
            int oh       = 1 + (finalLen >= 15 ? (finalLen - 15) / 255 + 1 : 0);
            if (dPos + oh + finalLen > dEnd) return -1;
            dst[dPos++] = (byte)(tLit << 4);
            if (finalLen >= 15)
            {
                int extra = finalLen - 15;
                while (extra >= 255) { dst[dPos++] = 255; extra -= 255; }
                dst[dPos++] = (byte)extra;
            }
            Buffer.BlockCopy(src, anchor, dst, dPos, finalLen);
            dPos += finalLen;
            return dPos - dstOff;
        }

        static int Hash4(byte[] src, int pos)
        {
            uint v = (uint)src[pos] | ((uint)src[pos+1] << 8) |
                     ((uint)src[pos+2] << 16) | ((uint)src[pos+3] << 24);
            return (int)((v * 2654435761u) >> (32 - HashLog));
        }
    }
}
