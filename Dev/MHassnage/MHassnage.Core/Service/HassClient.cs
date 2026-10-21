using MHassnage.Core.Model.OwnException;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MHassnage.Core.Service;

/// <summary>
/// Client WebSocket minimal de Home Assistant (/api/websocket), authentifié par jeton longue durée.
///
/// Échange séquentiel : une commande, puis lecture jusqu'à la réponse de même id
/// (pas d'abonnement, donc pas de messages concurrents). Le jeton n'est jamais journalisé.
/// </summary>
public sealed class HassClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    private HassClient() => _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

    /// <summary>Version de Hass annoncée à la connexion.</summary>
    public string HaVersion { get; private set; } = "";

    /// <exception cref="HassAuthException">Jeton refusé.</exception>
    public static async Task<HassClient> ConnectAsync(string url, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new HassAuthException("Jeton absent.");

        var client = new HassClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await client._socket.ConnectAsync(WebSocketUri(url), timeout.Token);

            var required = await client.ReceiveAsync(timeout.Token); // auth_required
            client.HaVersion = required["ha_version"]?.GetValue<string>() ?? "";

            await client.SendAsync(new JsonObject { ["type"] = "auth", ["access_token"] = token }, timeout.Token);
            var answer = await client.ReceiveAsync(timeout.Token);

            switch (answer["type"]?.GetValue<string>())
            {
                case "auth_ok":
                    return client;

                case "auth_invalid":
                    throw new HassAuthException(answer["message"]?.GetValue<string>() ?? "Jeton refusé.");
                default:
                    throw new InvalidOperationException($"Réponse d'authentification inattendue : {answer["type"]}");
            }
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Envoie une commande et renvoie son champ « result ».</summary>
    public async Task<JsonNode?> CommandAsync(string type, JsonObject? payload = null, CancellationToken ct = default)
    {
        int id = ++_nextId;
        var message = payload?.DeepClone().AsObject() ?? [];
        message["id"] = id;
        message["type"] = type;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        await SendAsync(message, timeout.Token);
        while (true)
        {
            var answer = await ReceiveAsync(timeout.Token);
            if (answer["id"]?.GetValue<int>() != id) continue;

            if (answer["success"]?.GetValue<bool>() == true) return answer["result"];

            var error = answer["error"];
            throw new HassCommandException(type,
                error?["code"]?.GetValue<string>() ?? "inconnu",
                error?["message"]?.GetValue<string>() ?? "");
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    /// <summary>Lit un message complet (les réponses comme get_states font plusieurs Mo).</summary>
    private async Task<JsonNode> ReceiveAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(chunk, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Connexion fermée par Home Assistant.");
            await buffer.WriteAsync(chunk, 0, result.Count, ct);
        } while (!result.EndOfMessage);

        return JsonNode.Parse(buffer.ToArray())
               ?? throw new JsonException("Message vide reçu de Home Assistant.");
    }

    /// <summary>http(s)://hôte:port → ws(s)://hôte:port/api/websocket</summary>
    public static Uri WebSocketUri(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        var builder = new UriBuilder(url);
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        builder.Path = "/api/websocket";
        return builder.Uri;
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch (WebSocketException) { /* fermeture best effort */ }
        }
        _socket.Dispose();
    }
}
