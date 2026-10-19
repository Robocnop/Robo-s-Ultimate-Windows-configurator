using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RoboConfigurator.Core
{
    public sealed class NiniteApp
    {
        private readonly string[] _category;
        public NiniteApp(string slug, string name, string[] category) { Slug = slug; Name = name; _category = category; }
        /// <summary>Identifiant utilisé dans l'URL de ninite.com.</summary>
        public string Slug { get; }
        public string Name { get; }
        public string Icon => "pack://application:,,,/Assets/AppIcons/" + Slug.Replace('.', '_') + ".png";
        // Traduite à la lecture : le catalogue peut être créé avant la lecture de -Lang
        public string Category => Lang.T(_category[0], _category[1]);
    }

    // Identifiants et icônes relevés sur ninite.com le 19/10/2026
    public static class NiniteApps
    {
        public static readonly string[] Default = { "7zip", "chrome", "firefox", "foxit", "notepadplusplus" };

        private static readonly string[] Browsers = { "Web browsers", "Navigateurs" };
        private static readonly string[] Messaging = { "Messaging", "Messagerie" };
        private static readonly string[] Media = { "Media", "Multimédia" };
        private static readonly string[] Imaging = { "Imaging", "Images" };
        private static readonly string[] Documents = { "Documents", "Documents" };
        private static readonly string[] Security = { "Security", "Sécurité" };
        private static readonly string[] Storage = { "Online storage", "Stockage en ligne" };
        private static readonly string[] Utilities = { "Utilities", "Utilitaires" };
        private static readonly string[] Compression = { "Compression", "Compression" };
        private static readonly string[] Runtimes = { "Runtimes", "Composants d'exécution" };
        private static readonly string[] Developer = { "Developer tools", "Outils de développement" };
        private static readonly string[] Other = { "Other", "Autres" };

        public static readonly NiniteApp[] All =
        {
            new NiniteApp("chrome", "Chrome", Browsers),
            new NiniteApp("firefox", "Firefox", Browsers),
            new NiniteApp("brave", "Brave", Browsers),
            new NiniteApp("operaChromium", "Opera", Browsers),
            new NiniteApp("vivaldi", "Vivaldi", Browsers),

            new NiniteApp("zoom", "Zoom", Messaging),
            new NiniteApp("discord", "Discord", Messaging),
            new NiniteApp("teams", "Teams", Messaging),
            new NiniteApp("slack", "Slack", Messaging),
            new NiniteApp("telegram", "Telegram", Messaging),
            new NiniteApp("whatsapp", "WhatsApp", Messaging),
            new NiniteApp("thunderbird", "Thunderbird", Messaging),

            new NiniteApp("vlc", "VLC", Media),
            new NiniteApp("spotify", "Spotify", Media),
            new NiniteApp("audacity", "Audacity", Media),
            new NiniteApp("klitecodecs", "K-Lite Codecs", Media),
            new NiniteApp("handbrake", "HandBrake", Media),
            new NiniteApp("obsstudio", "OBS Studio", Media),
            new NiniteApp("itunes", "iTunes", Media),
            new NiniteApp("foobar", "foobar2000", Media),
            new NiniteApp("musicbee", "MusicBee", Media),

            new NiniteApp("paint.net", "Paint.NET", Imaging),
            new NiniteApp("gimp", "GIMP", Imaging),
            new NiniteApp("krita", "Krita", Imaging),
            new NiniteApp("inkscape", "Inkscape", Imaging),
            new NiniteApp("blender", "Blender", Imaging),
            new NiniteApp("irfanview", "IrfanView", Imaging),
            new NiniteApp("xnview", "XnView", Imaging),
            new NiniteApp("greenshot", "Greenshot", Imaging),
            new NiniteApp("sharex", "ShareX", Imaging),

            new NiniteApp("foxit", "Foxit Reader", Documents),
            new NiniteApp("sumatrapdf", "SumatraPDF", Documents),
            new NiniteApp("libreoffice", "LibreOffice", Documents),
            new NiniteApp("pdf24creator", "PDF24 Creator", Documents),
            new NiniteApp("cutepdf", "CutePDF", Documents),

            new NiniteApp("malwarebytes", "Malwarebytes", Security),
            new NiniteApp("keepass2", "KeePass 2", Security),
            new NiniteApp("bitwarden", "Bitwarden", Security),
            new NiniteApp("1password", "1Password", Security),

            new NiniteApp("onedrive", "OneDrive", Storage),
            new NiniteApp("googledrivefordesktop", "Google Drive", Storage),
            new NiniteApp("dropbox", "Dropbox", Storage),

            new NiniteApp("powertoys", "PowerToys", Utilities),
            new NiniteApp("everything", "Everything", Utilities),
            new NiniteApp("anydesk", "AnyDesk", Utilities),
            new NiniteApp("teamviewer15", "TeamViewer 15", Utilities),
            new NiniteApp("rustdesk", "RustDesk", Utilities),
            new NiniteApp("windowsapp", "Windows App", Utilities),
            new NiniteApp("tailscale", "Tailscale", Utilities),
            new NiniteApp("wireguard", "WireGuard", Utilities),
            new NiniteApp("teracopy", "TeraCopy", Utilities),
            new NiniteApp("revo", "Revo Uninstaller", Utilities),
            new NiniteApp("windirstat", "WinDirStat", Utilities),
            new NiniteApp("wiztree", "WizTree", Utilities),
            new NiniteApp("advancedipscanner", "Advanced IP Scanner", Utilities),
            new NiniteApp("openshell", "Open-Shell", Utilities),
            new NiniteApp("imgburn", "ImgBurn", Utilities),
            new NiniteApp("qbittorrent", "qBittorrent", Utilities),

            new NiniteApp("7zip", "7-Zip", Compression),
            new NiniteApp("peazip", "PeaZip", Compression),
            new NiniteApp("winrar", "WinRAR", Compression),

            new NiniteApp(".net4.8.1", ".NET 4.8.1", Runtimes),
            new NiniteApp(".netx8", ".NET Desktop Runtime x64 8", Runtimes),
            new NiniteApp(".netx9", ".NET Desktop Runtime x64 9", Runtimes),
            new NiniteApp(".netx10", ".NET Desktop Runtime x64 10", Runtimes),
            new NiniteApp("adoptjavax21", "Java (AdoptOpenJDK) x64 21", Runtimes),
            new NiniteApp("adoptjavax8", "Java (AdoptOpenJDK) x64 8", Runtimes),
            new NiniteApp("vcredistx15", "VC Redist x64 2015+", Runtimes),
            new NiniteApp("vcredist15", "VC Redist x86 2015+", Runtimes),

            new NiniteApp("notepadplusplus", "Notepad++", Developer),
            new NiniteApp("vscode", "Visual Studio Code", Developer),
            new NiniteApp("git", "Git", Developer),
            new NiniteApp("githubdesktop", "GitHub Desktop", Developer),
            new NiniteApp("pythonx3", "Python x64 3", Developer),
            new NiniteApp("node.js24", "Node.js 24", Developer),
            new NiniteApp("putty", "PuTTY", Developer),
            new NiniteApp("winscp", "WinSCP", Developer),
            new NiniteApp("filezilla", "FileZilla", Developer),
            new NiniteApp("winmerge", "WinMerge", Developer),

            new NiniteApp("steam", "Steam", Other),
            new NiniteApp("epic", "Epic Games Launcher", Other),
            new NiniteApp("googleearth", "Google Earth", Other),
            new NiniteApp("evernote", "Evernote", Other),
            new NiniteApp("nvda", "NVDA", Other),
        };

        public static NiniteApp Find(string slug) => All.FirstOrDefault(a => a.Slug.Equals((slug ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        public static string[] Normalize(IEnumerable<string> slugs)
        {
            var known = new HashSet<string>((slugs ?? new string[0]).Select(Find).Where(a => a != null).Select(a => a.Slug));
            return All.Where(a => known.Contains(a.Slug)).Select(a => a.Slug).ToArray();
        }

        public static bool IsDefault(IEnumerable<string> slugs) => Normalize(slugs).SequenceEqual(Normalize(Default));

        public static string Names(IEnumerable<string> slugs) => string.Join(", ", Normalize(slugs).Select(s => Find(s).Name));

        public static string Url(IEnumerable<string> slugs) => "https://ninite.com/" + string.Join("-", Normalize(slugs)) + "/ninite.exe";

        // Un nom par sélection : Payloads.Ensure réutilise tout fichier déjà téléchargé
        public static string FileName(IEnumerable<string> slugs)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("-", Normalize(slugs))));
                return "Ninite-" + BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant() + ".exe";
            }
        }
    }
}
