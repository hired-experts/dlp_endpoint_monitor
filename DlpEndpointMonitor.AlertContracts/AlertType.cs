using System.Text.Json.Serialization;

namespace DlpEndpointMonitor.AlertContracts;

[JsonConverter(typeof(JsonStringEnumConverter<AlertType>))]
public enum AlertType
{
    [JsonStringEnumMemberName("toast")]      Toast,
    [JsonStringEnumMemberName("fullScreen")] FullScreen
}
