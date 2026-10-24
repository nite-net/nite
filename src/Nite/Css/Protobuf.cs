using System.Text;

namespace Nite.Css;

/// <summary>
/// The sliver of protocol buffers the embedded Sass protocol needs: varints, strings, booleans,
/// enums and nested messages. Field numbers come from Sass's embedded_sass.proto.
/// </summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _buffer = new();

    public ProtoWriter String(int field, string? value)
    {
        if (value is not null)
        {
            Bytes(field, Encoding.UTF8.GetBytes(value));
        }
        return this;
    }

    public ProtoWriter Bool(int field, bool value)
    {
        if (value)
        {
            Tag(field, 0);
            Varint(_buffer, 1);
        }
        return this;
    }

    public ProtoWriter Enum(int field, int value)
    {
        if (value != 0)
        {
            Tag(field, 0);
            Varint(_buffer, (ulong)value);
        }
        return this;
    }

    public ProtoWriter Message(int field, ProtoWriter message) => Bytes(field, message.ToArray());

    public byte[] ToArray() => _buffer.ToArray();

    private ProtoWriter Bytes(int field, byte[] bytes)
    {
        Tag(field, 2);
        Varint(_buffer, (ulong)bytes.Length);
        _buffer.Write(bytes);
        return this;
    }

    private void Tag(int field, int wireType) => Varint(_buffer, (ulong)((field << 3) | wireType));

    public static void Varint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}

/// <summary>A decoded message: each field number with its values (numbers or raw bytes).</summary>
internal sealed class ProtoMessage
{
    private readonly Dictionary<int, List<object>> _fields = [];

    public static ProtoMessage Parse(ReadOnlySpan<byte> data)
    {
        var message = new ProtoMessage();
        var offset = 0;
        while (offset < data.Length)
        {
            var tag = ReadVarint(data, ref offset);
            var field = (int)(tag >> 3);
            object value;
            switch ((int)(tag & 7))
            {
                case 0:
                    value = ReadVarint(data, ref offset);
                    break;
                case 1:
                    value = BitConverter.ToUInt64(data.Slice(offset, 8));
                    offset += 8;
                    break;
                case 2:
                    var length = (int)ReadVarint(data, ref offset);
                    value = data.Slice(offset, length).ToArray();
                    offset += length;
                    break;
                case 5:
                    value = (ulong)BitConverter.ToUInt32(data.Slice(offset, 4));
                    offset += 4;
                    break;
                default:
                    throw new FormatException($"Unsupported protobuf wire type in field {field}.");
            }
            if (!message._fields.TryGetValue(field, out var values))
            {
                message._fields[field] = values = [];
            }
            values.Add(value);
        }
        return message;
    }

    public bool Has(int field) => _fields.ContainsKey(field);

    public ProtoMessage? Message(int field) =>
        _fields.TryGetValue(field, out var values) && values[^1] is byte[] bytes ? Parse(bytes) : null;

    public string? String(int field) =>
        _fields.TryGetValue(field, out var values) && values[^1] is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    public IEnumerable<string> Strings(int field) =>
        _fields.TryGetValue(field, out var values) ? values.OfType<byte[]>().Select(b => Encoding.UTF8.GetString(b)) : [];

    public ulong Number(int field) =>
        _fields.TryGetValue(field, out var values) && values[^1] is ulong number ? number : 0;

    public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (offset >= data.Length)
            {
                throw new FormatException("Truncated varint.");
            }
            var b = data[offset++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
        }
    }
}
