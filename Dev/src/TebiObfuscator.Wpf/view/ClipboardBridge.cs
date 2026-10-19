using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Contenu du presse-papiers utile à l'outil : le texte brut, et sa version
/// mise en forme quand l'application d'origine en a déposé une.
/// </summary>
/// <param name="Text">Texte brut</param>
/// <param name="Html">Fragment HTML mis en forme, s'il y en a un</param>
internal sealed record ClipboardContent(string Text, string? Html);

/// <summary>
/// Trace d'un dépôt dans le presse-papiers, pour constater plus tard qu'il y
/// est toujours.
/// </summary>
/// <param name="Sequence">Numéro de version du presse-papiers juste après le dépôt</param>
/// <param name="Text">Texte déposé</param>
internal sealed record ClipboardStamp(uint Sequence, string Text);

/// <summary>
/// Accès au presse-papiers, fiabilisé et vérifié.
///
/// L'accès passe directement par les fonctions du système, et non par celles
/// de WPF. Ces dernières déposent un contenu en deux temps — une promesse de
/// données, puis les données elles-mêmes — et notifient donc deux fois de suite
/// les applications qui surveillent le presse-papiers : historique de Windows,
/// PowerToys, bureau à distance. WPF documente lui-même que ce double
/// changement peut amener ces applications à corrompre le contenu. Ici, le
/// texte est déposé en une seule opération, déjà rendu ; il subsiste tel quel
/// après la fermeture de l'application.
///
/// Surtout, chaque dépôt est relu : aucun compte rendu n'annonce que le
/// presse-papiers contient le texte sans l'avoir constaté.
///
/// Deux autres défauts du presse-papiers Windows sont traités. Il est partagé,
/// et une autre application peut le tenir ouvert au moment précis où l'on veut
/// y lire ou y écrire ; l'appel échoue alors, et une nouvelle tentative
/// quelques millisecondes plus tard réussit. Et une réponse copiée depuis une
/// interface de conversation y est déposée deux fois, en texte brut et en HTML
/// mis en forme : n'y remettre que le texte brut ferait perdre titres, listes,
/// gras et blocs de code au moment de coller dans un traitement de texte ou une
/// messagerie. La version HTML est donc restituée elle aussi.
/// </summary>
internal static class ClipboardBridge
{
    /// <summary>Format du texte Unicode.</summary>
    private const uint UnicodeTextFormat = 13;

    /// <summary>Nombre de tentatives avant d'abandonner.</summary>
    private const int Attempts = 10;

    private const string StartFragmentMarker = "<!--StartFragment-->";
    private const string EndFragmentMarker = "<!--EndFragment-->";

    /// <summary>Délai entre deux tentatives.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>Format HTML, dont le numéro est attribué par le système au premier usage.</summary>
    private static readonly Lazy<uint> HtmlFormat = new(() => RegisterClipboardFormat("HTML Format"));

    /// <summary>
    /// Lit le contenu du presse-papiers.
    /// </summary>
    /// <returns>Le contenu, ou <c>null</c> si le presse-papiers ne contient pas de texte</returns>
    /// <exception cref="InvalidOperationException">Si le presse-papiers reste inaccessible</exception>
    public static ClipboardContent? Read()
    {
        return WithRetry(() =>
        {
            using ClipboardSession session = ClipboardSession.Open(0);
            string? text = session.Text();
            if (text is null)
            {
                return null;
            }

            byte[]? html = session.ReadBytes(HtmlFormat.Value);
            return new ClipboardContent(text, html is null ? null : ExtractHtml(html));
        });
    }

    /// <summary>
    /// Dépose un contenu dans le presse-papiers, puis le relit pour s'assurer
    /// qu'il y figure.
    /// </summary>
    /// <param name="content">Contenu à déposer</param>
    /// <param name="owner">Fenêtre propriétaire du dépôt</param>
    /// <returns>La trace du dépôt, pour le contrôler plus tard</returns>
    /// <exception cref="InvalidOperationException">Si le presse-papiers reste inaccessible, ou si le texte relu n'est pas celui déposé</exception>
    public static ClipboardStamp Write(ClipboardContent content, nint owner)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] text = Encoding.Unicode.GetBytes(content.Text + "\0");
        byte[]? html = content.Html is null ? null : Encoding.UTF8.GetBytes(WrapHtml(content.Html) + "\0");

        WithRetry(() =>
        {
            using ClipboardSession session = ClipboardSession.Open(owner);
            session.Empty();
            session.Put(UnicodeTextFormat, text);
            if (html is not null)
            {
                session.Put(HtmlFormat.Value, html);
            }
        });

        string? deposited = ReadText();
        if (!string.Equals(deposited, content.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Le texte n'a pas pu être déposé dans le presse-papiers : {DescribeOwner()} l'a remplacé aussitôt. Réessayez ; si cela se répète, fermez l'outil qui surveille le presse-papiers.");
        }

        return new ClipboardStamp(GetClipboardSequenceNumber(), content.Text);
    }

    /// <summary>
    /// Indique si une autre application a remplacé un dépôt depuis qu'il a été
    /// fait.
    /// </summary>
    /// <param name="stamp">Trace du dépôt</param>
    /// <returns>L'application qui l'a remplacé, ou <c>null</c> si le dépôt est intact ou si l'on ne peut conclure</returns>
    public static string? Intruder(ClipboardStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);

        if (GetClipboardSequenceNumber() == stamp.Sequence)
        {
            return null;
        }

        // Le numéro de version change aussi quand le système complète les
        // formats dérivés du texte : seul le texte fait foi.
        string? current;
        try
        {
            current = ReadText();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (string.Equals(current, stamp.Text, StringComparison.Ordinal) || OwnerProcessId() == (uint)Environment.ProcessId)
        {
            return null;
        }

        return DescribeOwner();
    }

    /// <summary>
    /// Lit le seul texte du presse-papiers.
    /// </summary>
    /// <returns>Le texte, ou <c>null</c> s'il n'y en a pas</returns>
    /// <exception cref="InvalidOperationException">Si le presse-papiers reste inaccessible</exception>
    private static string? ReadText()
    {
        return WithRetry(() =>
        {
            using ClipboardSession session = ClipboardSession.Open(0);
            return session.Text();
        });
    }

    /// <summary>
    /// Désigne l'application propriétaire du presse-papiers, pour l'écrire
    /// dans un message.
    /// </summary>
    /// <returns>Son nom entre guillemets, ou une désignation générique</returns>
    private static string DescribeOwner()
    {
        uint processId = OwnerProcessId();
        if (processId == 0)
        {
            return "une autre application";
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return $"« {process.ProcessName} »";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // L'application s'est terminée entre-temps, ou refuse d'être examinée.
            return "une autre application";
        }
    }

    /// <summary>
    /// Donne le processus propriétaire du presse-papiers.
    /// </summary>
    /// <returns>Son identifiant, ou 0 s'il est inconnu</returns>
    private static uint OwnerProcessId()
    {
        nint window = GetClipboardOwner();
        if (window == 0)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId;
    }

    /// <summary>
    /// Exécute une opération sur le presse-papiers en la retentant tant qu'une
    /// autre application le tient ouvert.
    /// </summary>
    /// <typeparam name="T">Type du résultat</typeparam>
    /// <param name="operation">Opération à exécuter</param>
    /// <returns>Le résultat de l'opération</returns>
    /// <exception cref="InvalidOperationException">Si toutes les tentatives échouent</exception>
    private static T WithRetry<T>(Func<T> operation)
    {
        ExternalException? last = null;

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return operation();
            }
            catch (ExternalException exception)
            {
                last = exception;
                if (attempt < Attempts)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        throw new InvalidOperationException(
            "Le presse-papiers est occupé par une autre application. Réessayez dans un instant.",
            last);
    }

    /// <summary>
    /// Exécute une opération sans résultat sur le presse-papiers, en la
    /// retentant tant qu'une autre application le tient ouvert.
    /// </summary>
    /// <param name="operation">Opération à exécuter</param>
    /// <exception cref="InvalidOperationException">Si toutes les tentatives échouent</exception>
    private static void WithRetry(Action operation)
    {
        _ = WithRetry<object?>(() =>
        {
            operation();
            return null;
        });
    }

    /// <summary>
    /// Extrait le document HTML d'un contenu au format du presse-papiers, dont
    /// l'en-tête indique les bornes en octets UTF-8.
    /// </summary>
    /// <param name="data">Contenu brut du format HTML</param>
    /// <returns>Le document HTML, ou <c>null</c> s'il est illisible</returns>
    private static string? ExtractHtml(byte[] data)
    {
        int length = Array.IndexOf(data, (byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        // L'en-tête est en ASCII : lu octet pour caractère, ses positions sont
        // directement des positions d'octets.
        string header = Encoding.Latin1.GetString(data, 0, length);
        int start = HeaderValue(header, "StartHTML:");
        int end = HeaderValue(header, "EndHTML:");

        if (start >= 0 && end > start && end <= length)
        {
            return Encoding.UTF8.GetString(data, start, end - start);
        }

        // Certains logiciels écrivent un en-tête incomplet : le document
        // commence alors au premier chevron.
        string whole = Encoding.UTF8.GetString(data, 0, length);
        int tag = whole.IndexOf('<', StringComparison.Ordinal);
        return tag >= 0 ? whole[tag..] : null;
    }

    /// <summary>
    /// Compose le format HTML du presse-papiers : un en-tête qui donne, en
    /// octets UTF-8, les bornes du document et du fragment copié.
    /// </summary>
    /// <param name="html">Document HTML</param>
    /// <returns>Le contenu prêt à être déposé</returns>
    private static string WrapHtml(string html)
    {
        if (!html.Contains(StartFragmentMarker, StringComparison.Ordinal))
        {
            html = $"<html><body>{StartFragmentMarker}{html}{EndFragmentMarker}</body></html>";
        }

        const string Template = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

        // Les nombres étant écrits sur dix chiffres, la longueur de l'en-tête
        // ne dépend pas de leur valeur : on la connaît avant de les calculer.
        int headerLength = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, Template, 0, 0, 0, 0));
        int fragmentStart = html.IndexOf(StartFragmentMarker, StringComparison.Ordinal) + StartFragmentMarker.Length;
        int fragmentEnd = html.IndexOf(EndFragmentMarker, StringComparison.Ordinal);
        if (fragmentEnd < fragmentStart)
        {
            fragmentEnd = html.Length;
        }

        int startHtml = headerLength;
        int endHtml = startHtml + Encoding.UTF8.GetByteCount(html);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(html.AsSpan(0, fragmentStart));
        int endFragment = startHtml + Encoding.UTF8.GetByteCount(html.AsSpan(0, fragmentEnd));

        return string.Format(CultureInfo.InvariantCulture, Template, startHtml, endHtml, startFragment, endFragment) + html;
    }

    /// <summary>
    /// Lit une valeur numérique de l'en-tête HTML du presse-papiers.
    /// </summary>
    /// <param name="header">En-tête, lu octet pour caractère</param>
    /// <param name="name">Nom de l'entrée, deux-points compris</param>
    /// <returns>La valeur, ou -1 si elle est absente</returns>
    private static int HeaderValue(string header, string name)
    {
        int position = header.IndexOf(name, StringComparison.Ordinal);
        if (position < 0)
        {
            return -1;
        }

        int start = position + name.Length;
        int end = start;
        while (end < header.Length && char.IsAsciiDigit(header[end]))
        {
            end++;
        }

        return int.TryParse(header.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : -1;
    }

    // ═══════════════════════════ Fonctions système ══════════════════════════

    /// <summary>Obtient le numéro d'un format nommé.</summary>
    /// <param name="name">Nom du format</param>
    /// <returns>Son numéro, ou 0 en cas d'échec</returns>
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RegisterClipboardFormat(string name);

    /// <summary>Donne le numéro de version du presse-papiers, qui change à chaque modification.</summary>
    /// <returns>Le numéro de version</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>Donne la fenêtre propriétaire du presse-papiers.</summary>
    /// <returns>La fenêtre, ou 0</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetClipboardOwner();

    /// <summary>Donne le processus d'une fenêtre.</summary>
    /// <param name="window">Fenêtre</param>
    /// <param name="processId">Identifiant du processus</param>
    /// <returns>L'identifiant du fil d'exécution de la fenêtre</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>
    /// Presse-papiers ouvert, refermé à la sortie du bloc qui l'utilise : le
    /// laisser ouvert le bloquerait pour toutes les autres applications. Une
    /// session refermée refuse tout accès, le presse-papiers pouvant alors
    /// appartenir à une autre application.
    /// </summary>
    private sealed class ClipboardSession : IDisposable
    {
        /// <summary>Allocation déplaçable, seule forme que le presse-papiers accepte.</summary>
        private const uint MoveableMemory = 0x0002;

        /// <summary>Vrai une fois le presse-papiers refermé.</summary>
        private bool _closed;

        private ClipboardSession()
        {
        }

        /// <summary>
        /// Ouvre le presse-papiers.
        /// </summary>
        /// <param name="owner">Fenêtre propriétaire d'un dépôt, ou 0 pour une lecture</param>
        /// <returns>La session ouverte</returns>
        /// <exception cref="Win32Exception">Si une autre application le tient ouvert</exception>
        public static ClipboardSession Open(nint owner)
        {
            if (!OpenClipboard(owner))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new ClipboardSession();
        }

        /// <summary>
        /// Vide le presse-papiers.
        /// </summary>
        /// <exception cref="Win32Exception">En cas d'échec</exception>
        public void Empty()
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        /// <summary>
        /// Lit le texte Unicode, que le système produit au besoin depuis un
        /// texte d'un autre encodage.
        /// </summary>
        /// <returns>Le texte, ou <c>null</c> s'il n'y en a pas</returns>
        public string? Text()
        {
            byte[]? bytes = ReadBytes(UnicodeTextFormat);
            if (bytes is null)
            {
                return null;
            }

            string text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
            int end = text.IndexOf('\0', StringComparison.Ordinal);
            return end < 0 ? text : text[..end];
        }

        /// <summary>
        /// Lit le contenu brut d'un format.
        /// </summary>
        /// <param name="format">Format demandé</param>
        /// <returns>Ses octets, ou <c>null</c> s'il est absent</returns>
        public byte[]? ReadBytes(uint format)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            nint memory = GetClipboardData(format);
            if (memory == 0)
            {
                return null;
            }

            nint pointer = GlobalLock(memory);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                byte[] bytes = new byte[checked((int)GlobalSize(memory))];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }
        }

        /// <summary>
        /// Dépose un format. Le bloc mémoire appartient ensuite au système, et
        /// survit donc à l'application.
        /// </summary>
        /// <param name="format">Format déposé</param>
        /// <param name="bytes">Contenu, terminateur compris</param>
        /// <exception cref="Win32Exception">En cas d'échec</exception>
        public void Put(uint format, byte[] bytes)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            nint memory = GlobalAlloc(MoveableMemory, (nuint)bytes.Length);
            if (memory == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            nint pointer = GlobalLock(memory);
            if (pointer == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                _ = GlobalFree(memory);
                throw new Win32Exception(error);
            }

            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            _ = GlobalUnlock(memory);

            if (SetClipboardData(format, memory) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                _ = GlobalFree(memory);
                throw new Win32Exception(error);
            }
        }

        /// <summary>
        /// Referme le presse-papiers.
        /// </summary>
        public void Dispose()
        {
            if (!_closed)
            {
                _closed = true;
                _ = CloseClipboard();
            }
        }

        /// <summary>Ouvre le presse-papiers pour la fenêtre indiquée.</summary>
        /// <param name="owner">Fenêtre, ou 0 pour une simple lecture</param>
        /// <returns><c>true</c> en cas de succès</returns>
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(nint owner);

        /// <summary>Referme le presse-papiers.</summary>
        /// <returns><c>true</c> en cas de succès</returns>
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        /// <summary>Vide le presse-papiers et en prend possession.</summary>
        /// <returns><c>true</c> en cas de succès</returns>
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        /// <summary>Lit un format du presse-papiers.</summary>
        /// <param name="format">Format demandé</param>
        /// <returns>Le bloc mémoire, ou 0 si le format est absent</returns>
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint GetClipboardData(uint format);

        /// <summary>Dépose un format dans le presse-papiers, qui prend possession du bloc mémoire.</summary>
        /// <param name="format">Format déposé</param>
        /// <param name="memory">Bloc mémoire</param>
        /// <returns>Le bloc mémoire, ou 0 en cas d'échec</returns>
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint SetClipboardData(uint format, nint memory);

        /// <summary>Alloue un bloc mémoire global.</summary>
        /// <param name="flags">Options d'allocation</param>
        /// <param name="bytes">Taille en octets</param>
        /// <returns>Le bloc, ou 0 en cas d'échec</returns>
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint GlobalAlloc(uint flags, nuint bytes);

        /// <summary>Libère un bloc mémoire global.</summary>
        /// <param name="memory">Bloc à libérer</param>
        /// <returns>0 en cas de succès</returns>
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint GlobalFree(nint memory);

        /// <summary>Verrouille un bloc mémoire global et en donne l'adresse.</summary>
        /// <param name="memory">Bloc à verrouiller</param>
        /// <returns>L'adresse, ou 0 en cas d'échec</returns>
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint GlobalLock(nint memory);

        /// <summary>Déverrouille un bloc mémoire global.</summary>
        /// <param name="memory">Bloc à déverrouiller</param>
        /// <returns><c>true</c> s'il reste verrouillé ailleurs</returns>
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(nint memory);

        /// <summary>Donne la taille d'un bloc mémoire global.</summary>
        /// <param name="memory">Bloc examiné</param>
        /// <returns>Sa taille en octets</returns>
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nuint GlobalSize(nint memory);
    }
}
