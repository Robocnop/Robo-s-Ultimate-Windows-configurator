using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoboConfigurator.Core
{
    public sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string DownloadUrl { get; set; }
        public string PageUrl { get; set; }
    }

    public static class Updates
    {
        // N'inclut pas les pré-versions
        private const string LatestApi = "https://api.github.com/repos/Robocnop/Robo-s-Ultimate-Windows-configurator/releases/latest";

        [DataContract]
        private sealed class Release
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "html_url")] public string Page { get; set; }
            [DataMember(Name = "assets")] public Asset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class Asset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        }

        /// <summary>Sans le hash du commit : "2.6.0" ou "2.6.0-beta.1".</summary>
        public static string Current
        {
            get
            {
                var attribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                string version = attribute?.InformationalVersion ?? Assembly.GetExecutingAssembly().GetName().Version.ToString();
                return version.Split('+')[0];
            }
        }

        /// <summary>null si l'exe est à jour ou si GitHub est injoignable.</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                Release release;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                using (var response = await Payloads.Http.GetAsync(LatestApi, timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync())
                        release = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(stream);
                }

                string latest = (release.Tag ?? "").TrimStart('v', 'V');
                if (!IsNewer(latest, Current))
                {
                    Log.Info(Lang.T($"Update check: version {Current} is up to date", $"Mise à jour : la version {Current} est à jour"));
                    return null;
                }
                Log.Warn(Lang.T($"A newer version is available: {latest} (this exe: {Current})", $"Une version plus récente est disponible : {latest} (cet exe : {Current})"));
                var exe = (release.Assets ?? new Asset[0]).FirstOrDefault(a => string.Equals(a.Name, "RoboConfigurator.exe", StringComparison.OrdinalIgnoreCase));
                return new UpdateInfo { Version = latest, DownloadUrl = exe?.Url ?? release.Page, PageUrl = release.Page };
            }
            catch (Exception ex)
            {
                Log.Info(Lang.T("Update check impossible: ", "Vérification de mise à jour impossible : ") + ex.GetBaseException().Message);
                return null;
            }
        }

        /// <summary>Seules les pages et fichiers de github.com sont ouverts : Process.Start lancerait aussi un chemin local.</summary>
        public static bool IsTrustedUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

        /// <summary>À numéro égal, la version finale est plus récente que la pré-version.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            if (!TryParse(candidate, out var a, out bool aPre) || !TryParse(current, out var b, out bool bPre)) return false;
            int compare = a.CompareTo(b);
            return compare > 0 || (compare == 0 && bPre && !aPre);
        }

        private static bool TryParse(string value, out Version version, out bool prerelease)
        {
            string[] parts = (value ?? "").Split(new[] { '-' }, 2);
            prerelease = parts.Length > 1;
            return Version.TryParse(parts[0], out version);
        }
    }
}
