using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snapture.Core.Ipc;

/// <summary>
/// Resilient localhost TCP server exposing the recording control surface to
/// external clients — primarily a future Elgato Stream Deck plugin. Protocol is
/// newline-delimited JSON (NDJSON): clients send one command object per line and
/// receive response/event objects per line.
///
/// Resilience characteristics:
///  - Binds to 127.0.0.1 only (never exposed off-box).
///  - The accept loop auto-restarts with backoff if the listener faults.
///  - Each client runs in isolation; one client's error never affects others.
///  - Events are broadcast best-effort; a dead client is dropped, not awaited.
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly int _port;
    private readonly IControlCommandHandler _handler;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public ControlServer(int port, IControlCommandHandler handler)
    {
        _port = port;
        _handler = handler;
    }

    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    /// <summary>Optional sink for diagnostics (connection/errors). Never throws.</summary>
    public Action<string>? Log { get; set; }

    public void Start()
    {
        if (IsRunning)
            return;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoff = TimeSpan.FromMilliseconds(500);
        while (!token.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, _port);
                listener.Start();
                Log?.Invoke($"ControlServer listening on 127.0.0.1:{_port}");
                backoff = TimeSpan.FromMilliseconds(500); // reset after a clean start

                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    _ = HandleClientAsync(client, token); // fire and forget, isolated
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"ControlServer listener fault: {ex.Message}");
                // Back off then re-create the listener (e.g. transient bind issue).
                try { await Task.Delay(backoff, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 10_000));
            }
            finally
            {
                try { listener?.Stop(); } catch { }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        var id = Guid.NewGuid();
        var conn = new ClientConnection(client);
        _clients[id] = conn;
        Log?.Invoke($"Client {id} connected ({_clients.Count} total)");

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            // Greet the new client with the current state.
            await SendCurrentStateGreetingAsync(conn, linked.Token).ConfigureAwait(false);

            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var command = ControlCommand.Parse(line);
                ControlResponse response;
                if (command is null)
                {
                    response = ControlResponse.Failure(null, "Malformed command (expected JSON).");
                }
                else
                {
                    try
                    {
                        response = await _handler.HandleAsync(command, linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        response = ControlResponse.Failure(command.Id, ex.Message);
                    }
                }

                await conn.SendAsync(Serialize(response), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"Client {id} error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            conn.Dispose();
            Log?.Invoke($"Client {id} disconnected ({_clients.Count} total)");
        }
    }

    private async Task SendCurrentStateGreetingAsync(ClientConnection conn, CancellationToken token)
    {
        try
        {
            var greet = await _handler.HandleAsync(
                new ControlCommand { Command = "getState" }, token).ConfigureAwait(false);
            var evt = new ControlEvent { Event = "hello", State = greet.State };
            await conn.SendAsync(Serialize(evt), token).ConfigureAwait(false);
        }
        catch { /* greeting is best-effort */ }
    }

    /// <summary>Push an event to every connected client (best-effort).</summary>
    public void Broadcast(ControlEvent evt)
    {
        if (_clients.IsEmpty)
            return;
        var payload = Serialize(evt);
        foreach (var conn in _clients.Values)
            _ = conn.SendAsync(payload, CancellationToken.None);
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o, JsonOptions);

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var conn in _clients.Values)
            conn.Dispose();
        _clients.Clear();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
        }
        _cts?.Dispose();
    }

    /// <summary>Wraps a client socket with a write lock so frames don't interleave.</summary>
    private sealed class ClientConnection(TcpClient client) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly TcpClient _client = client;

        public async Task SendAsync(string json, CancellationToken token)
        {
            if (!_client.Connected)
                return;
            await _writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
                await _client.GetStream().WriteAsync(bytes, token).ConfigureAwait(false);
            }
            catch { /* client gone; reaped by read loop */ }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            try { _client.Close(); } catch { }
            _writeLock.Dispose();
        }
    }
}
