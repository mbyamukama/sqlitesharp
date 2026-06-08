using System.Buffers.Binary;

namespace SQLite.Engine.BTree;

/// <summary>
/// Varint codec and cell payload parsing for SQLite B-tree pages.
/// A varint is a variable-length integer (1–9 bytes, big-endian, MSB-first).
/// </summary>
public static class Cell
{
    /// <summary>
    /// Read a varint from the given span. Returns the number of bytes consumed (1–9).
    /// </summary>
    public static int ReadVarint(ReadOnlySpan<byte> buf, out long value)
    {
        // Fast path for single-byte varint (common case)
        if ((buf[0] & 0x80) == 0)
        {
            value = buf[0];
            return 1;
        }

        value = 0;
        for (int i = 0; i < 8; i++)
        {
            value = (value << 7) | (long)(buf[i] & 0x7F);
            if ((buf[i] & 0x80) == 0)
            {
                return i + 1;
            }
        }

        // 9th byte uses all 8 bits
        value = (value << 8) | buf[8];
        return 9;
    }

    /// <summary>
    /// Compute the number of bytes of payload stored locally on a leaf page
    /// (not on overflow pages). This implements the formula from the file format spec.
    /// </summary>
    public static int LocalPayloadSize(int payloadSize, int usableSize, bool isIndex)
    {
        int maxLocal, minLocal;

        if (isIndex)
        {
            // Index B-tree leaf: maxLocal = ((usableSize-12)*64/255)-23
            maxLocal = ((usableSize - 12) * 64 / 255) - 23;
            minLocal = ((usableSize - 12) * 32 / 255) - 23;
        }
        else
        {
            // Table B-tree leaf: maxLocal = usableSize - 35
            maxLocal = usableSize - 35;
            minLocal = ((usableSize - 12) * 32 / 255) - 23;
        }

        if (payloadSize <= maxLocal)
        {
            return payloadSize;
        }

        // Spill to overflow
        int local = minLocal + ((payloadSize - minLocal) % (usableSize - 4));
        if (local > maxLocal)
        {
            local = minLocal;
        }
        return local;
    }

    /// <summary>
    /// Parse a record header to extract serial types for each column.
    /// Returns the number of bytes consumed from the record body start.
    /// </summary>
    public static int ParseRecordHeader(ReadOnlySpan<byte> payload, out int[] serialTypes)
    {
        int offset = 0;
        int headerBytesConsumed = ReadVarint(payload[offset..], out long headerSize);
        offset = headerBytesConsumed;

        var types = new List<int>();
        while (offset < (int)headerSize)
        {
            offset += ReadVarint(payload[offset..], out long serialType);
            types.Add((int)serialType);
        }

        serialTypes = types.ToArray();
        return (int)headerSize;
    }

    /// <summary>
    /// Get the size in bytes of a value given its serial type code.
    /// </summary>
    public static int SerialTypeSize(int serialType)
    {
        return serialType switch
        {
            0 => 0,   // NULL
            1 => 1,   // 8-bit int
            2 => 2,   // 16-bit int
            3 => 3,   // 24-bit int
            4 => 4,   // 32-bit int
            5 => 6,   // 48-bit int
            6 => 8,   // 64-bit int
            7 => 8,   // IEEE 754 double
            8 => 0,   // integer 0 (schema format >= 4)
            9 => 0,   // integer 1 (schema format >= 4)
            10 or 11 => 0, // reserved
            _ => serialType >= 12
                ? (serialType % 2 == 0 ? (serialType - 12) / 2 : (serialType - 13) / 2)
                : 0,
        };
    }

    /// <summary>
    /// Read a value from the record body given its serial type.
    /// Returns the value as an object (null, long, double, string, or byte[]).
    /// </summary>
    public static object? ReadValue(ReadOnlySpan<byte> data, int serialType)
    {
        switch (serialType)
        {
            case 0: return null;
            case 1: return (long)(sbyte)data[0];
            case 2: return (long)BinaryPrimitives.ReadInt16BigEndian(data);
            case 3:
                int i24 = (data[0] << 16) | (data[1] << 8) | data[2];
                // Sign-extend from 24 bits
                if ((i24 & 0x800000) != 0) i24 |= unchecked((int)0xFF000000);
                return (long)i24;
            case 4: return (long)BinaryPrimitives.ReadInt32BigEndian(data);
            case 5:
                long i48 = ((long)data[0] << 40) | ((long)data[1] << 32) |
                           ((long)data[2] << 24) | ((long)data[3] << 16) |
                           ((long)data[4] << 8) | data[5];
                // Sign-extend from 48 bits
                if ((i48 & 0x800000000000L) != 0) i48 |= unchecked((long)0xFFFF000000000000L);
                return i48;
            case 6: return BinaryPrimitives.ReadInt64BigEndian(data);
            case 7:
                long bits = BinaryPrimitives.ReadInt64BigEndian(data);
                return BitConverter.Int64BitsToDouble(bits);
            case 8: return 0L;
            case 9: return 1L;
            default:
                if (serialType >= 12 && serialType % 2 == 0)
                {
                    // BLOB
                    int len = (serialType - 12) / 2;
                    return data[..len].ToArray();
                }
                else if (serialType >= 13 && serialType % 2 == 1)
                {
                    // TEXT (UTF-8)
                    int len = (serialType - 13) / 2;
                    return System.Text.Encoding.UTF8.GetString(data[..len]);
                }
                return null;
        }
    }

    // ─── Write methods (Phase 4) ────────────────────────────────────────────

    /// <summary>
    /// Write a varint to the buffer. Returns number of bytes written (1–9).
    /// </summary>
    public static int WriteVarint(Span<byte> buf, long value)
    {
        ulong v = (ulong)value;

        if (v <= 0x7F)
        {
            buf[0] = (byte)v;
            return 1;
        }

        // Count how many bytes we need
        int len;
        if (v <= 0x3FFF) len = 2;
        else if (v <= 0x1FFFFF) len = 3;
        else if (v <= 0x0FFFFFFF) len = 4;
        else if (v <= 0x07FFFFFFFF) len = 5;
        else if (v <= 0x03FFFFFFFFFF) len = 6;
        else if (v <= 0x01FFFFFFFFFFFF) len = 7;
        else if (v <= 0x00FFFFFFFFFFFFFF) len = 8;
        else len = 9;

        if (len == 9)
        {
            // 9th byte uses all 8 bits
            buf[8] = (byte)(v & 0xFF);
            v >>= 8;
            for (int i = 7; i >= 0; i--)
            {
                buf[i] = (byte)((v & 0x7F) | 0x80);
                v >>= 7;
            }
            return 9;
        }

        for (int i = len - 1; i > 0; i--)
        {
            buf[i] = (byte)((v & 0x7F) | 0x80);
            v >>= 7;
        }
        buf[0] = (byte)((v & 0x7F) | 0x80);
        buf[len - 1] &= 0x7F; // Clear high bit on last byte
        return len;
    }

    /// <summary>
    /// Compute the serial type for a value.
    /// </summary>
    public static int GetSerialType(object? value)
    {
        return value switch
        {
            null => 0,
            long l => GetIntSerialType(l),
            int i => GetIntSerialType(i),
            double => 7,
            string s => 13 + s.Length * 2, // odd >= 13 means text
            byte[] b => 12 + b.Length * 2,  // even >= 12 means blob
            _ => 0,
        };
    }

    private static int GetIntSerialType(long v)
    {
        if (v == 0) return 8;
        if (v == 1) return 9;
        ulong u = v < 0 ? (ulong)~v : (ulong)v;
        if (u <= 0x7F) return 1;
        if (u <= 0x7FFF) return 2;
        if (u <= 0x7FFFFF) return 3;
        if (u <= 0x7FFFFFFF) return 4;
        if (u <= 0x7FFFFFFFFFFF) return 5;
        return 6;
    }

    /// <summary>
    /// Write a value to a buffer given its serial type. Returns bytes written.
    /// </summary>
    public static int WriteValue(Span<byte> dest, object? value, int serialType)
    {
        switch (serialType)
        {
            case 0: return 0; // NULL
            case 1:
                dest[0] = (byte)(long)value!;
                return 1;
            case 2:
                BinaryPrimitives.WriteInt16BigEndian(dest, (short)(long)value!);
                return 2;
            case 3:
            {
                int v = (int)(long)value!;
                dest[0] = (byte)(v >> 16);
                dest[1] = (byte)(v >> 8);
                dest[2] = (byte)v;
                return 3;
            }
            case 4:
                BinaryPrimitives.WriteInt32BigEndian(dest, (int)(long)value!);
                return 4;
            case 5:
            {
                long v = (long)value!;
                dest[0] = (byte)(v >> 40);
                dest[1] = (byte)(v >> 32);
                dest[2] = (byte)(v >> 24);
                dest[3] = (byte)(v >> 16);
                dest[4] = (byte)(v >> 8);
                dest[5] = (byte)v;
                return 6;
            }
            case 6:
                BinaryPrimitives.WriteInt64BigEndian(dest, (long)value!);
                return 8;
            case 7:
                BinaryPrimitives.WriteInt64BigEndian(dest, BitConverter.DoubleToInt64Bits((double)value!));
                return 8;
            case 8: return 0; // integer 0
            case 9: return 0; // integer 1
            default:
                if (serialType >= 12 && serialType % 2 == 0)
                {
                    // BLOB
                    byte[] blob = (byte[])value!;
                    blob.AsSpan().CopyTo(dest);
                    return blob.Length;
                }
                else if (serialType >= 13 && serialType % 2 == 1)
                {
                    // TEXT
                    string text = (string)value!;
                    int len = System.Text.Encoding.UTF8.GetBytes(text, dest);
                    return len;
                }
                return 0;
        }
    }

    /// <summary>
    /// Build a complete SQLite record from an array of column values.
    /// Returns the record as a byte array (header + body).
    /// </summary>
    public static byte[] BuildRecord(object?[] values)
    {
        // Compute serial types and sizes
        int[] serialTypes = new int[values.Length];
        int headerSize = 0;
        int dataSize = 0;

        for (int i = 0; i < values.Length; i++)
        {
            serialTypes[i] = GetSerialType(values[i]);
            headerSize += VarintSize(serialTypes[i]);
            dataSize += SerialTypeSize(serialTypes[i]);
        }

        // Header size includes its own varint length
        int headerSizeVarintLen = VarintSize(headerSize + VarintSize(headerSize + 1));
        // Recalculate: the header-size field includes itself
        int totalHeaderSize = headerSizeVarintLen + headerSize;
        // Re-check if the size of the header-size varint changed
        if (VarintSize(totalHeaderSize) != headerSizeVarintLen)
        {
            headerSizeVarintLen = VarintSize(totalHeaderSize + 1);
            totalHeaderSize = headerSizeVarintLen + headerSize;
        }

        byte[] record = new byte[totalHeaderSize + dataSize];
        int pos = 0;

        // Write header-size varint
        pos += WriteVarint(record.AsSpan(pos), totalHeaderSize);

        // Write serial type varints
        for (int i = 0; i < values.Length; i++)
        {
            pos += WriteVarint(record.AsSpan(pos), serialTypes[i]);
        }

        // Write data values
        for (int i = 0; i < values.Length; i++)
        {
            pos += WriteValue(record.AsSpan(pos), values[i], serialTypes[i]);
        }

        return record;
    }

    /// <summary>
    /// Build a table B-tree leaf cell: [varint payload-size] [varint rowid] [payload]
    /// </summary>
    public static byte[] BuildTableLeafCell(long rowId, byte[] payload)
    {
        int payloadSizeLen = VarintSize(payload.Length);
        int rowidLen = VarintSize(rowId);
        byte[] cell = new byte[payloadSizeLen + rowidLen + payload.Length];
        int pos = 0;
        pos += WriteVarint(cell.AsSpan(pos), payload.Length);
        pos += WriteVarint(cell.AsSpan(pos), rowId);
        Buffer.BlockCopy(payload, 0, cell, pos, payload.Length);
        return cell;
    }

    /// <summary>
    /// Compute the number of bytes needed to encode a varint.
    /// </summary>
    public static int VarintSize(long value)
    {
        ulong v = (ulong)value;
        if (v <= 0x7F) return 1;
        if (v <= 0x3FFF) return 2;
        if (v <= 0x1FFFFF) return 3;
        if (v <= 0x0FFFFFFF) return 4;
        if (v <= 0x07FFFFFFFF) return 5;
        if (v <= 0x03FFFFFFFFFF) return 6;
        if (v <= 0x01FFFFFFFFFFFF) return 7;
        if (v <= 0x00FFFFFFFFFFFFFF) return 8;
        return 9;
    }
}
