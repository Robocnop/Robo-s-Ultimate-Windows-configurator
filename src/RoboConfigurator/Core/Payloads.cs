using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RoboConfigurator.Core
{
    public sealed class Payload
    {
        /// <param name="signer">Signataire Authenticode attendu (CN) pour un exe ; null = pas de contrôle de signature.</param>
        /// <param name="sha256">Empreinte attendue ; null = pas de contrôle (le fichier peut être mis à jour sur le serveur sans nouvel exe).</param>
        public Payload(string file, string url, string signer = null, string sha256 = null)
        {
            File = file; Url = url; Signer = signer; Sha256 = sha256;
        }
        public string File { get; }
        public string Url { get; }
        public string Signer { get; }
        public string Sha256 { get; }
    }

    /// <summary>Le fichier obtenu n'est pas celui attendu (page web d'erreur, signature invalide...).</summary>
    public sealed class InvalidPayloadException : Exception
    {
        public InvalidPayloadException(string message) : base(message) { }
    }

    /// <summary>Le serveur a répondu avec un code d'erreur HTTP.</summary>
    public sealed class DownloadHttpException : Exception
    {
        public DownloadHttpException(int status, string reason) : base($"HTTP {status} {reason}") { Status = status; }
        public int Status { get; }
    }

    /// <summary>
    /// Fichiers nécessaires aux étapes. Ils sont pris dans l'exe s'ils y ont été intégrés au moment de la
    /// compilation (dossier payloads\), sinon téléchargés depuis le Nextcloud (Ninite depuis ninite.com).
    /// Chaque fichier est vérifié avant usage : format, et signature numérique pour les exe.
    /// </summary>
    public static class Payloads
    {
        // Signataires relevés le 05/11/2026 sur les fichiers servis par ninite.com et par le Nextcloud
        public const string NiniteSigner = "Secure By Design Inc.";
        public const string MicrosoftSigner = "Microsoft Corporation";

        public static readonly Payload Ninite    = new Payload("Ninite.exe",      NiniteApps.Url(NiniteApps.Default), NiniteSigner);
        public static readonly Payload CrapFixer = new Payload("CrapFixer.zip",   "https://nextcloud.robocnop.be/s/Czayj7x7AmdAsJj/download");
        public static readonly Payload Office    = new Payload("OfficeSetup.exe", "https://nextcloud.robocnop.be/s/AabmSfHFBTR6e6J/download", MicrosoftSigner);
        public static readonly Payload Mas       = new Payload("MAS_AIO.cmd",     "https://nextcloud.robocnop.be/s/mZQFfJ5YzBX5JeR/download");

        public static Payload ForStep(string step, RunConfig config)
        {
            switch (step)
            {
                // Seule la sélection par défaut peut être intégrée à l'exe en Ninite.exe
                case StepNames.Ninite:
                    return NiniteApps.IsDefault(config.NiniteApps) ? Ninite
                         : new Payload(NiniteApps.FileName(config.NiniteApps), NiniteApps.Url(config.NiniteApps), NiniteSigner);
                case StepNames.CrapFixer: return CrapFixer;
                case StepNames.Office: return Office;
                default: return null;
            }
        }

        internal static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RoboConfigurator/" + Updates.Current);
            return client;
        }

        /// <summary>Attente avant le 2ᵉ puis le 3ᵉ essai.</summary>
        internal static int[] RetryDelaysSeconds = { 5, 15 };

        /// <param name="progress">Pourcentage téléchargé, ou -1 si la taille est inconnue.</param>
        public static string Ensure(Payload payload, string dir, Action<int> progress = null)
        {
            string path = Path.Combine(dir, payload.File);
            string part = path + ".part";
            if (System.IO.File.Exists(path))
            {
                try
                {
                    string check = Verify(payload, path);
                    Log.Ok(Lang.T($"{payload.File} already present ({check})", $"{payload.File} déjà présent ({check})"));
                    return path;
                }
                catch (InvalidPayloadException ex)
                {
                    Log.Warn(Lang.T($"{payload.File} is present but invalid ({ex.Message}): it is deleted and fetched again",
                                    $"{payload.File} est présent mais invalide ({ex.Message}) : il est supprimé puis récupéré à nouveau"));
                    System.IO.File.Delete(path);
                }
            }

            using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload." + payload.File))
            {
                if (embedded != null)
                {
                    // Extrait dans .part puis renommé : une extraction interrompue ne laisse pas de fichier tronqué
                    using (var file = System.IO.File.Create(part)) embedded.CopyTo(file);
                    string check = Verify(payload, part);
                    MoveInPlace(part, path);
                    Log.Ok(Lang.T($"{payload.File} extracted from the app ({check})", $"{payload.File} extrait de l'application ({check})"));
                    return path;
                }
            }

            Exception last = null;
            for (int attempt = 1; attempt <= RetryDelaysSeconds.Length + 1; attempt++)
            {
                try
                {
                    Log.Info(Lang.T($"Downloading {payload.File} from {Host(payload.Url)} (attempt {attempt}/3)...", $"Téléchargement de {payload.File} depuis {Host(payload.Url)} (essai {attempt}/3)..."));
                    Download(payload.Url, part, progress);
                    string check = Verify(payload, part);
                    MoveInPlace(part, path);
                    Log.Ok($"{payload.File} ({new FileInfo(path).Length / 1048576.0:0.0} {Lang.T("MB", "Mo")}, {check})");
                    return path;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Warn(Lang.T("Failed: ", "Échec : ") + Describe(ex));
                    if (Describe(ex) != ex.GetBaseException().Message)
                        Log.Info($"    {ex.GetBaseException().GetType().FullName} : {ex.GetBaseException().Message}");
                    try { System.IO.File.Delete(part); } catch (Exception) { }
                    if (attempt <= RetryDelaysSeconds.Length)
                    {
                        int delay = RetryDelaysSeconds[attempt - 1];
                        Log.Info(Lang.T($"New attempt in {delay} s", $"Nouvel essai dans {delay} s"));
                        Thread.Sleep(delay * 1000);
                    }
                }
            }
            throw new Exception(Lang.T($"Could not download {payload.File}: {Describe(last)}", $"Impossible de télécharger {payload.File} : {Describe(last)}"));
        }

        /// <summary>
        /// Mode simulation : vérifie le lien sans télécharger le fichier (seuls les en-têtes de la réponse sont lus).
        /// Lève une exception avec un message clair si le lien ne répond pas comme il faut.
        /// </summary>
        public static string CheckLink(Payload payload)
        {
            using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload." + payload.File))
                if (embedded != null) return Lang.T($"{payload.File}: embedded in the app, no download", $"{payload.File} : intégré à l'application, pas de téléchargement");
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                using (var response = Http.GetAsync(payload.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode) throw new DownloadHttpException((int)response.StatusCode, response.ReasonPhrase);
                    long? size = response.Content.Headers.ContentLength;
                    string type = response.Content.Headers.ContentType?.MediaType ?? "?";
                    // Un partage Nextcloud expiré répond parfois par une page web
                    if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidPayloadException(Lang.T("the link returns a web page instead of the file", "le lien renvoie une page web au lieu du fichier"));
                    return Lang.T($"Link OK for {payload.File}: {Host(payload.Url)}, HTTP {(int)response.StatusCode}, {(size.HasValue ? $"{size.Value / 1048576.0:0.0} MB" : "size unknown")}, {type}",
                                  $"Lien OK pour {payload.File} : {Host(payload.Url)}, HTTP {(int)response.StatusCode}, {(size.HasValue ? $"{size.Value / 1048576.0:0.0} Mo" : "taille inconnue")}, {type}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.T($"Link for {payload.File} ({Host(payload.Url)}): {Describe(ex)}", $"Lien de {payload.File} ({Host(payload.Url)}) : {Describe(ex)}"));
            }
        }

        private static void MoveInPlace(string part, string path)
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(part, path);
        }

        private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        private static void Download(string url, string path, Action<int> progress)
        {
            using (var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode) throw new DownloadHttpException((int)response.StatusCode, response.ReasonPhrase);
                long? total = response.Content.Headers.ContentLength;
                Log.Info(Lang.T($"HTTP {(int)response.StatusCode}, size announced: {(total.HasValue ? $"{total.Value:N0} bytes" : "unknown")}",
                                $"HTTP {(int)response.StatusCode}, taille annoncée : {(total.HasValue ? $"{total.Value:N0} octets" : "inconnue")}"));
                long done = 0;
                using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var output = System.IO.File.Create(path))
                {
                    var buffer = new byte[81920];
                    int lastPercent = -2, read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        done += read;
                        int percent = total > 0 ? (int)(done * 100 / total.Value) : -1;
                        if (percent != lastPercent) { lastPercent = percent; progress?.Invoke(percent); }
                    }
                }
                if (total.HasValue && done != total.Value)
                    throw new IOException(Lang.T($"download cut off: {done:N0} of {total.Value:N0} bytes received", $"téléchargement interrompu : {done:N0} octets reçus sur {total.Value:N0}"));
            }
        }

        /// <summary>Vérifie le format du fichier (et sa signature pour un exe) ; renvoie ce qui a été vérifié, pour le journal.</summary>
        internal static string Verify(Payload payload, string path)
        {
            long length = new FileInfo(path).Length;
            if (length == 0) throw new InvalidPayloadException(Lang.T("empty file", "fichier vide"));
            var head = new byte[Math.Min(4096, length)];
            using (var stream = System.IO.File.OpenRead(path)) stream.Read(head, 0, head.Length);

            string check;
            switch (Path.GetExtension(payload.File).ToLowerInvariant())
            {
                case ".exe":
                    if (head.Length < 2 || head[0] != 'M' || head[1] != 'Z') throw NotTheFile(head, Lang.T("not a Windows program", "ce n'est pas un programme Windows"));
                    if (payload.Signer == null) { check = Lang.T("format checked", "format vérifié"); break; }
                    string signer = Signature.VerifiedSigner(path);
                    if (!signer.Equals(payload.Signer, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidPayloadException(Lang.T($"signed by “{signer}” instead of “{payload.Signer}”", $"signé par « {signer} » au lieu de « {payload.Signer} »"));
                    check = Lang.T($"signed by {signer}", $"signé par {signer}");
                    break;
                case ".zip":
                    if (head.Length < 4 || head[0] != 'P' || head[1] != 'K' || head[2] != 3 || head[3] != 4) throw NotTheFile(head, Lang.T("not a zip archive", "ce n'est pas une archive zip"));
                    check = Lang.T("format checked", "format vérifié");
                    break;
                case ".cmd":
                    if (LooksLikeHtml(head) || head.Contains((byte)0)) throw NotTheFile(head, Lang.T("not a command script", "ce n'est pas un script de commandes"));
                    check = Lang.T("format checked", "format vérifié");
                    break;
                default:
                    check = Lang.T("not checked", "non vérifié");
                    break;
            }

            if (payload.Sha256 != null)
            {
                string actual;
                using (var sha = SHA256.Create())
                using (var stream = System.IO.File.OpenRead(path))
                    actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!actual.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidPayloadException(Lang.T($"SHA-256 {actual} instead of {payload.Sha256}", $"SHA-256 {actual} au lieu de {payload.Sha256}"));
                check += ", SHA-256 OK";
            }
            return check;
        }

        private static InvalidPayloadException NotTheFile(byte[] head, string reason) =>
            new InvalidPayloadException(LooksLikeHtml(head) ? Lang.T("a web page was received instead of the file (expired link or server error)",
                                                                     "une page web a été reçue à la place du fichier (lien expiré ou erreur du serveur)")
                                                            : reason);

        private static bool LooksLikeHtml(byte[] head)
        {
            int i = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
            while (i < head.Length && (head[i] == ' ' || head[i] == '\t' || head[i] == '\r' || head[i] == '\n')) i++;
            return i < head.Length && head[i] == '<';
        }

        /// <summary>Message clair pour un échec de téléchargement ; le détail technique reste dans le journal.</summary>
        internal static string Describe(Exception ex)
        {
            if (ex == null) return "";
            if (ex is InvalidPayloadException) return Lang.T("the file received is not valid: ", "le fichier reçu n'est pas valide : ") + ex.Message;
            if (ex is DownloadHttpException http)
            {
                if (http.Status == 404) return Lang.T("download link not found (HTTP 404): the file was moved or deleted on the server",
                                                      "lien de téléchargement introuvable (HTTP 404) : le fichier a été déplacé ou supprimé du serveur");
                if (http.Status == 401 || http.Status == 403) return Lang.T($"access refused by the server (HTTP {http.Status})", $"accès refusé par le serveur (HTTP {http.Status})");
                if (http.Status >= 500) return Lang.T($"server error (HTTP {http.Status}), try again later", $"erreur du serveur (HTTP {http.Status}), réessayer plus tard");
                return ex.Message;
            }
            if (ex is TaskCanceledException || ex is OperationCanceledException) return Lang.T("the server did not answer in time", "le serveur n'a pas répondu à temps");
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (!(e is WebException web)) continue;
                switch (web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                        return Lang.T("no Internet connection (the server name could not be resolved)", "pas de connexion Internet (le nom du serveur n'a pas pu être résolu)");
                    case WebExceptionStatus.ConnectFailure:
                        return Lang.T("could not reach the server (no Internet connection, or blocked by a firewall)", "serveur injoignable (pas de connexion Internet, ou bloqué par un pare-feu)");
                    case WebExceptionStatus.TrustFailure:
                    case WebExceptionStatus.SecureChannelFailure:
                        return Lang.T("secure connection failed (check the PC date and time)", "connexion sécurisée impossible (vérifier la date et l'heure du PC)");
                    case WebExceptionStatus.Timeout:
                        return Lang.T("the server did not answer in time", "le serveur n'a pas répondu à temps");
                }
            }
            return ex.GetBaseException().Message;
        }
    }
}
