using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiReap.Api.Serialization;

// Every timestamp the system stores is UTC (DateTime.UtcNow), but SQL Server datetime2 carries no
// kind, so EF materializes it as DateTimeKind.Unspecified and System.Text.Json writes it without an
// offset ("2026-09-25T13:13:01.11"). Browsers read such a value as local time, shifting it by the
// viewer's UTC offset. This writes every DateTime as an explicit UTC instant ("...Z") - the same
// instant, never converted to a server-side time zone. Applies to DateTime? as well.
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(), // an explicit offset was supplied
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc), // stored as UTC
            _ => value
        };
        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
