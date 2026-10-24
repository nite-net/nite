using System.Buffers.Binary;
using System.Text;

namespace Nite.Esbuild;

/// <summary>
/// Encoder/decoder for esbuild's stdio service protocol. This mirrors <c>encodePacket</c> and
/// <c>decodePacket</c> in esbuild's JS host: every packet is a little-endian uint32 length, a uint32
/// id (<c>id &lt;&lt; 1 | isResponse</c>) and one tagged value.
/// </summary>
/// <remarks>
/// Values map to .NET as: null, bool, int (uint32 on the wire), string, byte[],
/// <see cref="List{T}"/> of object and <see cref="Dictionary{TKey,TValue}"/> of string to object.
/// </remarks>
internal static class EsbuildPacket
{
    private const byte TagNull = 0, TagBool = 1, TagInt = 2, TagString = 3, TagBytes = 4, TagArray = 5, TagObject = 6;

    public static byte[] Encode(uint id, bool isRequest, object? value)
    {
        var buffer = new MemoryStream();

        WriteUInt32(buffer, 0); // length placeholder
        WriteUInt32(buffer, (id << 1) | (isRequest ? 0u : 1u));
        Visit(value);

        var bytes = buffer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 4));
        return bytes;

        void Visit(object? v)
        {
            switch (v)
            {
                case null:
                    buffer.WriteByte(TagNull);
                    break;
                case bool b:
                    buffer.WriteByte(TagBool);
                    buffer.WriteByte(b ? (byte)1 : (byte)0);
                    break;
                case int i:
                    buffer.WriteByte(TagInt);
                    WriteUInt32(buffer, unchecked((uint)i));
                    break;
                case uint u:
                    buffer.WriteByte(TagInt);
                    WriteUInt32(buffer, u);
                    break;
                case string s:
                    buffer.WriteByte(TagString);
                    WriteBytes(buffer, Encoding.UTF8.GetBytes(s));
                    break;
                case byte[] data:
                    buffer.WriteByte(TagBytes);
                    WriteBytes(buffer, data);
                    break;
                case IDictionary<string, object?> map:
                    buffer.WriteByte(TagObject);
                    WriteUInt32(buffer, (uint)map.Count);
                    foreach (var (key, item) in map)
                    {
                        WriteBytes(buffer, Encoding.UTF8.GetBytes(key));
                        Visit(item);
                    }
                    break;
                case System.Collections.IEnumerable list:
                    var items = list.Cast<object?>().ToList();
                    buffer.WriteByte(TagArray);
                    WriteUInt32(buffer, (uint)items.Count);
                    foreach (var item in items)
                    {
                        Visit(item);
                    }
                    break;
                default:
                    throw new ArgumentException($"Cannot encode a value of type {v.GetType()} for esbuild.");
            }
        }
    }

    /// <summary>Decodes a packet body (everything after the length prefix).</summary>
    public static (uint Id, bool IsRequest, object? Value) Decode(ReadOnlySpan<byte> packet)
    {
        var offset = 0;
        var rawId = ReadUInt32(packet, ref offset);
        var value = Visit(packet, ref offset);
        if (offset != packet.Length)
        {
            throw new InvalidDataException("Invalid esbuild packet: trailing bytes.");
        }
        return (rawId >> 1, (rawId & 1) == 0, value);

        static object? Visit(ReadOnlySpan<byte> span, ref int offset)
        {
            var tag = span[offset++];
            switch (tag)
            {
                case TagNull:
                    return null;
                case TagBool:
                    return span[offset++] != 0;
                case TagInt:
                    return unchecked((int)ReadUInt32(span, ref offset));
                case TagString:
                    return Encoding.UTF8.GetString(ReadBytes(span, ref offset));
                case TagBytes:
                    return ReadBytes(span, ref offset).ToArray();
                case TagArray:
                {
                    var count = ReadUInt32(span, ref offset);
                    var list = new List<object?>((int)count);
                    for (var i = 0; i < count; i++)
                    {
                        list.Add(Visit(span, ref offset));
                    }
                    return list;
                }
                case TagObject:
                {
                    var count = ReadUInt32(span, ref offset);
                    var map = new Dictionary<string, object?>((int)count);
                    for (var i = 0; i < count; i++)
                    {
                        var key = Encoding.UTF8.GetString(ReadBytes(span, ref offset));
                        map[key] = Visit(span, ref offset);
                    }
                    return map;
                }
                default:
                    throw new InvalidDataException($"Invalid esbuild packet: unknown tag {tag}.");
            }
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteBytes(Stream stream, byte[] data)
    {
        WriteUInt32(stream, (uint)data.Length);
        stream.Write(data);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, ref int offset)
    {
        if (offset + 4 > span.Length)
        {
            throw new InvalidDataException("Invalid esbuild packet: truncated.");
        }
        var value = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
        offset += 4;
        return value;
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> span, ref int offset)
    {
        var length = (int)ReadUInt32(span, ref offset);
        if (offset + length > span.Length)
        {
            throw new InvalidDataException("Invalid esbuild packet: truncated.");
        }
        var slice = span.Slice(offset, length);
        offset += length;
        return slice;
    }
}
