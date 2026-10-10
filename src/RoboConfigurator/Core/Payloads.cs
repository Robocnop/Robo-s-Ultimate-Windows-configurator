using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;

namespace RoboConfigurator.Core
{
    public sealed class Payload
    {
        public Payload(string file, string url) { File = file; Url = url; }
        public string File { get; }
        public string Url { get; }
    }

    /// <summary>
    /// Fichiers nécessaires aux étapes. Ils sont pris dans l'exe s'ils y ont été intégrés au moment de la
    /// compilation (dossier payloads\), sinon téléchargés depuis le Nextcloud.
    /// </summary>
    public static class Payloads
    {
        public static readonly Payload Ninite    = new Payload("Ninite.exe",      "https://nextcloud.robocnop.freeddns.org:4444/s/Cg9d7m58fWtJY8n/download");
        public static readonly Payload CrapFixer = new Payload("CrapFixer.zip",   "https://nextcloud.robocnop.freeddns.org:4444/s/Czayj7x7AmdAsJj/download");
        public static readonly Payload Office    = new Payload("OfficeSetup.exe", "https://nextcloud.robocnop.freeddns.org:4444/s/AabmSfHFBTR6e6J/download");
        public static readonly Payload Mas       = new Payload("MAS_AIO.cmd",     "https://nextcloud.robocnop.freeddns.org:4444/s/mZQFfJ5YzBX5JeR/download");

        /// <summary>Fichier à préparer avant de lancer l'étape, ou null.</summary>
        public static Payload ForStep(string step)
        {
            switch (step)
            {
                case StepNames.Ninite: return Ninite;
                case StepNames.CrapFixer: return CrapFixer;
                case StepNames.Office: return Office;
                default: return null;
            }
        }

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RoboConfigurator/2.0");
            return client;
        }

        /// <summary>Renvoie le chemin du fichier dans <paramref name="dir"/>, en l'extrayant ou le téléchargeant si besoin.</summary>
        /// <param name="progress">Pourcentage téléchargé, ou -1 si la taille est inconnue.</param>
        public static string Ensure(Payload payload, string dir, Action<int> progress = null)
        {
            string path = Path.Combine(dir, payload.File);
            if (System.IO.File.Exists(path) && new FileInfo(path).Length > 0)
            {
                Log.Ok(Lang.T($"{payload.File} already present", $"{payload.File} déjà présent"));
                return path;
            }

            using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload." + payload.File))
            {
                if (embedded != null)
                {
                    using (var file = System.IO.File.Create(path)) embedded.CopyTo(file);
                    Log.Ok(Lang.T($"{payload.File} extracted from the app", $"{payload.File} extrait de l'application"));
                    return path;
                }
            }

            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Log.Info(Lang.T($"Downloading {payload.File} (attempt {attempt}/3)...", $"Téléchargement de {payload.File} (essai {attempt}/3)..."));
                    Download(payload.Url, path + ".part", progress);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    System.IO.File.Move(path + ".part", path);
                    Log.Ok($"{payload.File} ({new FileInfo(path).Length / 1048576.0:0.0} {Lang.T("MB", "Mo")})");
                    return path;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Warn(Lang.T("Failed: ", "Échec : ") + ex.GetBaseException().Message);
                    Thread.Sleep(5000);
                }
            }
            throw new Exception(Lang.T($"Could not download {payload.File}: {last?.GetBaseException().Message}",
                                       $"Impossible de télécharger {payload.File} : {last?.GetBaseException().Message}"));
        }

        private static void Download(string url, string path, Action<int> progress)
        {
            using (var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var output = System.IO.File.Create(path))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int lastPercent = -2, read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        done += read;
                        int percent = total > 0 ? (int)(done * 100 / total.Value) : -1;
                        if (percent != lastPercent) { lastPercent = percent; progress?.Invoke(percent); }
                    }
                }
            }
        }
    }
}
