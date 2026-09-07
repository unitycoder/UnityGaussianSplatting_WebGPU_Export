// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace GaussianSplatting.Editor.Utils
{
    public static class PLYFileReader
    {
        public static void ReadFileHeader(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs)
        {
            vertexCount = 0;
            vertexStride = 0;
            attrs = new List<(string, ElementType)>();
            if (!File.Exists(filePath))
                return;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, fs);
        }

        static void ReadHeaderImpl(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs, FileStream fs)
        {
            // read header
            vertexCount = 0;
            vertexStride = 0;
            attrs = new List<(string, ElementType)>();
            const int kMaxHeaderLines = 9000;
            bool got_binary_le = false;
            for (int lineIdx = 0; lineIdx < kMaxHeaderLines; ++lineIdx)
            {
                var line = ReadLine(fs);
                if (line == "end_header" || line.Length == 0)
                    break;
                var tokens = line.Split(' ');
                if (tokens.Length == 3 && tokens[0] == "format" && tokens[1] == "binary_little_endian" && tokens[2] == "1.0")
                    got_binary_le = true;
                if (tokens.Length == 3 && tokens[0] == "element" && tokens[1] == "vertex")
                    vertexCount = int.Parse(tokens[2]);
                if (tokens.Length == 3 && tokens[0] == "property")
                {
                    ElementType type = tokens[1] switch
                    {
                        "float" => ElementType.Float,
                        "double" => ElementType.Double,
                        "uchar" => ElementType.UChar,
                        _ => ElementType.None
                    };
                    vertexStride += TypeToSize(type);
                    attrs.Add((tokens[2], type));
                }
            }

            if (!got_binary_le)
            {
                throw new IOException($"PLY {filePath} not supported: needs to be binary, little endian PLY format");
            }
        }

        public static void ReadFile(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs, out NativeArray<byte> vertices)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, fs);

            long dataBytes = (long)vertexCount * vertexStride;
            if (dataBytes > int.MaxValue)
                throw new IOException($"PLY {filePath} vertex payload is too large for raw byte buffer ({dataBytes} bytes). Use streaming conversion path.");

            vertices = new NativeArray<byte>((int)dataBytes, Allocator.Persistent);
            var readBytes = fs.Read(vertices);
            if (readBytes != vertices.Length)
                throw new IOException($"PLY {filePath} read error, expected {vertices.Length} data bytes got {readBytes}");
        }

        public static unsafe NativeArray<InputSplatData> ReadFileAsSplats(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, fs);

            string[] splatAttributes =
            {
                "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2",
                "f_rest_0", "f_rest_1", "f_rest_2", "f_rest_3", "f_rest_4", "f_rest_5", "f_rest_6", "f_rest_7", "f_rest_8", "f_rest_9",
                "f_rest_10", "f_rest_11", "f_rest_12", "f_rest_13", "f_rest_14", "f_rest_15", "f_rest_16", "f_rest_17", "f_rest_18", "f_rest_19",
                "f_rest_20", "f_rest_21", "f_rest_22", "f_rest_23", "f_rest_24", "f_rest_25", "f_rest_26", "f_rest_27", "f_rest_28", "f_rest_29",
                "f_rest_30", "f_rest_31", "f_rest_32", "f_rest_33", "f_rest_34", "f_rest_35", "f_rest_36", "f_rest_37", "f_rest_38", "f_rest_39",
                "f_rest_40", "f_rest_41", "f_rest_42", "f_rest_43", "f_rest_44", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"
            };

            int[] fileAttrOffsets = new int[attrs.Count];
            int runningOffset = 0;
            for (int i = 0; i < attrs.Count; ++i)
            {
                fileAttrOffsets[i] = runningOffset;
                runningOffset += TypeToSize(attrs[i].Item2);
            }

            int[] srcOffsets = new int[splatAttributes.Length];
            for (int i = 0; i < splatAttributes.Length; ++i)
            {
                int attrIndex = attrs.IndexOf((splatAttributes[i], ElementType.Float));
                srcOffsets[i] = attrIndex >= 0 ? fileAttrOffsets[attrIndex] : -1;
            }

            int dstStride = UnsafeUtility.SizeOf<InputSplatData>();
            var splats = new NativeArray<InputSplatData>(vertexCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            byte* dstBase = (byte*)splats.GetUnsafePtr();

            const int kChunkVertices = 8192;
            int chunkBytes = vertexStride * kChunkVertices;
            byte[] chunk = new byte[chunkBytes];

            int written = 0;
            while (written < vertexCount)
            {
                int vertsThisChunk = Math.Min(kChunkVertices, vertexCount - written);
                int bytesToRead = vertsThisChunk * vertexStride;
                ReadExact(fs, chunk, bytesToRead);

                fixed (byte* chunkPtr = chunk)
                {
                    for (int vi = 0; vi < vertsThisChunk; ++vi)
                    {
                        byte* srcVertex = chunkPtr + vi * vertexStride;
                        byte* dstVertex = dstBase + (written + vi) * dstStride;
                        for (int attr = 0; attr < srcOffsets.Length; ++attr)
                        {
                            int srcOffset = srcOffsets[attr];
                            if (srcOffset >= 0)
                                *(int*)(dstVertex + attr * 4) = *(int*)(srcVertex + srcOffset);
                        }
                    }
                }

                written += vertsThisChunk;
            }

            return splats;
        }

        static void ReadExact(FileStream fs, byte[] buffer, int byteCount)
        {
            int offset = 0;
            while (offset < byteCount)
            {
                int read = fs.Read(buffer, offset, byteCount - offset);
                if (read <= 0)
                    throw new IOException($"PLY read error, expected {byteCount} bytes, got {offset}");
                offset += read;
            }
        }

        public enum ElementType
        {
            None,
            Float,
            Double,
            UChar
        }

        public static int TypeToSize(ElementType t)
        {
            return t switch
            {
                ElementType.None => 0,
                ElementType.Float => 4,
                ElementType.Double => 8,
                ElementType.UChar => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
            };
        }

        static string ReadLine(FileStream fs)
        {
            var byteBuffer = new List<byte>();
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1 || b == '\n')
                    break;
                byteBuffer.Add((byte)b);
            }
            // if line had CRLF line endings, remove the CR part
            if (byteBuffer.Count > 0 && byteBuffer.Last() == '\r')
                byteBuffer.RemoveAt(byteBuffer.Count-1);
            return Encoding.UTF8.GetString(byteBuffer.ToArray());
        }
    }
}
