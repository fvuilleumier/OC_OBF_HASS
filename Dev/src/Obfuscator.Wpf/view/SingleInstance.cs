using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Instance unique de l'application, pour les lancements qui portent des
/// arguments : une autre application qui demande d'ouvrir un projet sur sa
/// publication ou son rapatriement retrouve la fenêtre déjà ouverte, au lieu
/// d'en ouvrir une seconde sur le même coffre.
///
/// La première instance tient un verrou nommé et écoute un canal nommé. Une
/// instance lancée avec des arguments, si le verrou est déjà tenu, transmet sa
/// demande par le canal et s'arrête. Un lancement sans argument garde le
/// comportement de toujours : il ouvre sa propre fenêtre.
///
/// Verrou et canal sont propres à l'utilisateur et à sa session : le nom porte
/// son identifiant de sécurité, et le canal n'accepte que des connexions du
/// même utilisateur. Une demande reçue n'est qu'un chemin et un geste, relus
/// et revérifiés comme des arguments de lancement.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>Taille maximale d'une demande : un chemin et un geste n'en demandent pas plus.</summary>
    private const int MaximumMessageBytes = 16 * 1024;

    /// <summary>Délai laissé à l'instance ouverte pour accepter une demande.</summary>
    private const int ConnectTimeoutMilliseconds = 3000;

    /// <summary>Autorise tout processus à prendre le premier plan, pour l'appel <c>AllowSetForegroundWindow</c>.</summary>
    private const int AnyProcess = -1;

    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;

    /// <summary>
    /// Prépare l'instance unique de l'utilisateur courant.
    /// </summary>
    public SingleInstance()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        _name = $"Obfuscator.Wpf.{identity.User?.Value ?? Environment.UserName}";
    }

    /// <summary>
    /// Tente de devenir l'instance principale, celle qui reçoit les demandes.
    /// </summary>
    /// <returns><c>true</c> si aucune autre instance ne tient le verrou</returns>
    public bool TryBecomePrimary()
    {
        // « Local\ » limite le verrou à la session ouverte : une autre session
        // du même poste a sa propre instance.
        Mutex mutex = new(initiallyOwned: true, $@"Local\{_name}", out bool created);
        if (!created)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    /// <summary>
    /// Transmet une demande à l'instance principale.
    /// </summary>
    /// <param name="request">Demande valide, chemin absolu</param>
    /// <returns><c>true</c> si l'instance principale l'a reçue</returns>
    public bool TrySend(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // L'instance qui reçoit la demande doit pouvoir passer au premier
            // plan : c'est celle-ci, que l'utilisateur vient de lancer, qui en
            // a le droit et le lui cède.
            AllowSetForegroundWindow(AnyProcess);

            using NamedPipeClientStream client = new(".", _name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeoutMilliseconds);
            client.Write(JsonSerializer.SerializeToUtf8Bytes(new LaunchMessage(request.ProjectPath, request.Gesture)));
            client.Flush();
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Écoute les demandes des instances lancées ensuite, jusqu'à la fermeture.
    /// </summary>
    /// <param name="received">Appelé, hors du fil de l'écran, pour chaque demande valide</param>
    public void Listen(Action<LaunchRequest> received)
    {
        ArgumentNullException.ThrowIfNull(received);
        CancellationToken token = _stop.Token;
        _ = Task.Run(() => ListenAsync(received, token), token);
    }

    /// <summary>
    /// Boucle d'écoute : une connexion à la fois, chacune pour une demande.
    /// </summary>
    /// <param name="received">Destinataire des demandes</param>
    /// <param name="token">Jeton d'arrêt</param>
    /// <returns>La tâche d'écoute</returns>
    private async Task ListenAsync(Action<LaunchRequest> received, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                NamedPipeServerStream server = new(
                    _name,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using ConfiguredAsyncDisposable disposal = server.ConfigureAwait(false);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                byte[] message = await ReadMessageAsync(server, token).ConfigureAwait(false);

                if (Decode(message) is LaunchRequest request)
                {
                    received(request);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Une demande illisible ou interrompue est ignorée : la
                // suivante aura son propre canal.
            }
        }
    }

    /// <summary>
    /// Lit une demande entière, dans la limite de sa taille maximale.
    /// </summary>
    /// <param name="stream">Canal connecté</param>
    /// <param name="token">Jeton d'arrêt</param>
    /// <returns>Les octets reçus</returns>
    /// <exception cref="IOException">Si la demande dépasse la taille permise</exception>
    private static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumMessageBytes)
            {
                throw new IOException("Demande trop longue.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Relit une demande reçue, comme des arguments de lancement : un chemin
    /// absolu et un geste connu, rien d'autre.
    /// </summary>
    /// <param name="message">Octets reçus</param>
    /// <returns>La demande, ou <c>null</c> si elle est invalide</returns>
    private static LaunchRequest? Decode(byte[] message)
    {
        LaunchMessage? decoded = JsonSerializer.Deserialize<LaunchMessage>(message);
        if (decoded is null || !Enum.IsDefined(decoded.Gesture))
        {
            return null;
        }

        if (decoded.ProjectPath is not null && !Path.IsPathFullyQualified(decoded.ProjectPath))
        {
            return null;
        }

        return new LaunchRequest(decoded.ProjectPath, decoded.Gesture, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();

        if (_mutex is not null)
        {
            // Le verrou est rendu par le fil qui l'a pris, celui de l'écran,
            // à la fermeture de l'application.
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Pris par un autre fil : sa fin le libérera.
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }

    /// <summary>Cède le droit de passer au premier plan.</summary>
    /// <param name="processId">Processus autorisé, ou -1 pour tous</param>
    /// <returns>Vrai si le droit a été cédé</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Demande telle qu'elle voyage d'une instance à l'autre.
    /// </summary>
    /// <param name="ProjectPath">Dossier du projet, absolu</param>
    /// <param name="Gesture">Geste demandé</param>
    private sealed record LaunchMessage(string? ProjectPath, LaunchGesture Gesture);
}
