using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Web.Endpoints;

namespace LocalAudiobookStudio.Web.Jobs;

internal static class JobWireJson
{
    internal static readonly JsonSerializerOptions RequestOptions = Create();
    internal static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter<JobKind>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<JobState>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<JobRequestedState>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
    }
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(ApiRequest.JsonOptions);
        Configure(options);
        return options;
    }
}
