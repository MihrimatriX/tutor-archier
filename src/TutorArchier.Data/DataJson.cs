using System.Text.Json;
using System.Text.Json.Serialization;

namespace TutorArchier.Data;

internal static class DataJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
