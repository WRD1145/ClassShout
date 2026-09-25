using System.Buffers;
using System.Text.Json;

namespace ClassShout.Core.Protocol;

/// <summary>
/// 控制消息的 JSON 编解码。
/// 采用「先序列化实体、再把属性搬进带 type 字段的对象」的做法，
/// 目的是不依赖 System.Text.Json 的多态特性，Android 上与 AOT 裁剪都更稳。
/// </summary>
public static class ShoutCodec
{
    private const string TypeProperty = "type";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>把控制消息序列化为一行 JSON。</summary>
    public static byte[] Encode(ShoutMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);

        var buffer = new ArrayBufferWriter<byte>(body.Length + 32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(TypeProperty, message.Type);
            using (var document = JsonDocument.Parse(body))
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals(TypeProperty))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>解析控制消息；无法识别时返回 <c>null</c>，由调用方决定是否记日志。</summary>
    public static ShoutMessage? Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(TypeProperty, out var typeProperty) ||
                typeProperty.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return typeProperty.GetString() switch
            {
                HelloMessage.TypeName => root.Deserialize<HelloMessage>(JsonOptions),
                TextShoutMessage.TypeName => root.Deserialize<TextShoutMessage>(JsonOptions),
                ImageStartMessage.TypeName => root.Deserialize<ImageStartMessage>(JsonOptions),
                ImageEndMessage.TypeName => root.Deserialize<ImageEndMessage>(JsonOptions),
                AudioStartMessage.TypeName => root.Deserialize<AudioStartMessage>(JsonOptions),
                AudioEndMessage.TypeName => root.Deserialize<AudioEndMessage>(JsonOptions),
                StopMessage.TypeName => root.Deserialize<StopMessage>(JsonOptions),
                AckMessage.TypeName => root.Deserialize<AckMessage>(JsonOptions),
                StatusMessage.TypeName => root.Deserialize<StatusMessage>(JsonOptions),
                ByeMessage.TypeName => root.Deserialize<ByeMessage>(JsonOptions),
                ErrorMessage.TypeName => root.Deserialize<ErrorMessage>(JsonOptions),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
