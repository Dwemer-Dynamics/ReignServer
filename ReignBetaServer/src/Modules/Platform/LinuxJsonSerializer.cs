#if REIGN_LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// Preserve the legacy dictionary/object-array contract while hosting the server on .NET.
namespace System.Web.Script.Serialization
{
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = 2097152;
        public int RecursionLimit { get; set; } = 100;

        private JsonSerializerOptions Options => new JsonSerializerOptions
        {
            MaxDepth = RecursionLimit,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new ObjectValues() }
        };

        public string Serialize(object value)
        {
            string json = JsonSerializer.Serialize(value, Options);
            if (json.Length > MaxJsonLength) throw new InvalidOperationException("JSON exceeds the configured length limit.");
            return json;
        }

        public T Deserialize<T>(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (json.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds the configured length limit.");
            return JsonSerializer.Deserialize<T>(json, Options);
        }

        public object DeserializeObject(string json) => Deserialize<object>(json);

        private sealed class ObjectValues : JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                    return ReadValue(document.RootElement);
            }

            private static object ReadValue(JsonElement value)
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.Object:
                        var dictionary = new Dictionary<string, object>();
                        foreach (var property in value.EnumerateObject()) dictionary[property.Name] = ReadValue(property.Value);
                        return dictionary;
                    case JsonValueKind.Array: return value.EnumerateArray().Select(ReadValue).ToArray();
                    case JsonValueKind.String: return value.GetString();
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.Number:
                        if (value.TryGetInt32(out int integer)) return integer;
                        if (value.TryGetInt64(out long wide)) return wide;
                        if (value.TryGetDecimal(out decimal number)) return number;
                        return value.GetDouble();
                    default: return null;
                }
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                if (value.GetType() == typeof(object)) { writer.WriteStartObject(); writer.WriteEndObject(); }
                else JsonSerializer.Serialize(writer, value, value.GetType(), options);
            }
        }
    }
}
#endif
