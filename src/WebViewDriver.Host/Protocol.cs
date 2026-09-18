using System.Text.Json.Serialization;

namespace WebViewDriver.Host;

internal static class WireProtocol
{
    /// <summary>Private wire protocol version; client and host fail fast on mismatch.</summary>
    public const int Version = 1;

    public const int MaxRequestBytes = 4 * 1024 * 1024; // scripts include ~14 KB Selenium atoms plus user payloads
    public const int DefaultEvalTimeoutMs = 60_000;
}

internal sealed class EvalRequest
{
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("script")] public string? Script { get; set; }
    [JsonPropertyName("timeoutMs")] public int? TimeoutMs { get; set; }
}

internal sealed class EvalResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("error")] public WireError? Error { get; set; }
}

internal sealed class WireError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

internal static class WireErrorCodes
{
    public const string UnknownTarget = "unknown target";
    public const string EvalFailed = "eval failed";
    public const string EvalTimeout = "eval timeout";
    public const string BadRequest = "bad request";
    public const string Unsupported = "unsupported";
}

internal sealed class ScreenshotRequest
{
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("rect")] public RectDto? Rect { get; set; }
}

internal sealed class RectDto
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
}

internal sealed class ScreenshotResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("dataBase64")] public string? DataBase64 { get; set; }
    [JsonPropertyName("error")] public WireError? Error { get; set; }
}

internal sealed class StatusResponse
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("hostVersion")] public string HostVersion { get; set; } = "";
    [JsonPropertyName("targets")] public List<TargetStatus> Targets { get; set; } = new();
}

internal sealed class TargetStatus
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("screenshots")] public bool Screenshots { get; set; }

    /// <summary>App-declared readiness (FR9); null = the app never declared readiness.</summary>
    [JsonPropertyName("ready")] public bool? Ready { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EvalRequest))]
[JsonSerializable(typeof(EvalResponse))]
[JsonSerializable(typeof(ScreenshotRequest))]
[JsonSerializable(typeof(ScreenshotResponse))]
[JsonSerializable(typeof(StatusResponse))]
internal sealed partial class WireJsonContext : JsonSerializerContext
{
}
