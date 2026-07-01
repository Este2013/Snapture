using System.Text.Json;

namespace Snapture.Core.Ipc;

/// <summary>
/// A command received from a control client (e.g. the Stream Deck plugin). The
/// wire format is one JSON object per line:
/// <code>{"id":"42","command":"start","args":{"mode":"display"}}</code>
/// </summary>
public sealed class ControlCommand
{
    public required string Command { get; init; }
    public string? Id { get; init; }
    public JsonElement Args { get; init; }

    /// <summary>Read a string field from <c>args</c>, or null if absent.</summary>
    public string? GetString(string name) =>
        Args.ValueKind == JsonValueKind.Object
        && Args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Read a bool field from <c>args</c> (accepts JSON true or the string "true").</summary>
    public bool GetBool(string name)
    {
        if (Args.ValueKind != JsonValueKind.Object || !Args.TryGetProperty(name, out var v))
            return false;
        return v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    public static ControlCommand? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmd) || cmd.ValueKind != JsonValueKind.String)
                return null;

            JsonElement args = default;
            if (root.TryGetProperty("args", out var a))
                args = a.Clone(); // clone so it survives JsonDocument disposal

            string? id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            return new ControlCommand { Command = cmd.GetString()!, Id = id, Args = args };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A reply to a single command. Serialized as
/// <c>{"type":"response","id":"42","ok":true,"state":"recording"}</c>.
/// </summary>
public sealed class ControlResponse
{
    public string Type => "response";
    public string? Id { get; init; }
    public bool Ok { get; init; }
    public string? State { get; init; }
    public string? Error { get; init; }
    public object? Data { get; init; }

    public static ControlResponse Success(string? id, string? state = null, object? data = null)
        => new() { Id = id, Ok = true, State = state, Data = data };

    public static ControlResponse Failure(string? id, string error, string? state = null)
        => new() { Id = id, Ok = false, Error = error, State = state };
}

/// <summary>
/// An unsolicited event pushed to all connected clients, e.g.
/// <c>{"type":"event","event":"stateChanged","state":"recording"}</c>.
/// </summary>
public sealed class ControlEvent
{
    public string Type => "event";
    public required string Event { get; init; }
    public string? State { get; init; }
    public object? Data { get; init; }
}

/// <summary>
/// Implemented by the host app to fulfill commands. The handler is responsible
/// for marshaling to the UI thread where needed (e.g. showing the overlay).
/// </summary>
public interface IControlCommandHandler
{
    Task<ControlResponse> HandleAsync(ControlCommand command, CancellationToken cancellationToken);
}
