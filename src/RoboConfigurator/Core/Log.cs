using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RoboConfigurator.Core
{
    public enum LogLevel { Title, Info, Ok, Warn, Error }

    public sealed class LogEntry
    {
        public LogEntry(LogLevel level, string text) { Time = DateTime.Now; Level = level; Text = text; }
        public DateTime Time { get; }
        public LogLevel Level { get; }
        public string Text { get; }

        public override string ToString()
        {
            switch (Level)
            {
                case LogLevel.Title: return $"{Time:HH:mm:ss} === {Text} ===";
                case LogLevel.Ok:    return $"{Time:HH:mm:ss}   [OK] {Text}";
                case LogLevel.Warn:  return $"{Time:HH:mm:ss}   [!] {Text}";
                case LogLevel.Error: return $"{Time:HH:mm:ss}   [{Lang.T("ERROR", "ERREUR")}] {Text}";
                default:             return $"{Time:HH:mm:ss}   {Text}";
            }
        }
    }

    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _file;

        public static event Action<LogEntry> Written;

        public static void Open(string path)
        {
            _file = path;
            Write(LogLevel.Title, Lang.T("Started on ", "Lancement du ") + Lang.Date(DateTime.Now) + $" {DateTime.Now:HH:mm:ss}");
        }

        /// <summary>Arrête l'écriture dans journal.txt (avant la suppression du dossier) ; l'interface reste alimentée.</summary>
        public static void Close()
        {
            lock (Sync) _file = null;
        }

        public static string FilePath { get { lock (Sync) return _file; } }

        public static void Title(string text) => Write(LogLevel.Title, text);
        public static void Info(string text) => Write(LogLevel.Info, text);
        public static void Ok(string text) => Write(LogLevel.Ok, text);
        public static void Warn(string text) => Write(LogLevel.Warn, text);
        public static void Error(string text) => Write(LogLevel.Error, text);

        public static void Error(string context, Exception ex)
        {
            Error($"{context}{Lang.T(": ", " : ")}{ex.GetBaseException().Message}");
            for (var e = ex; e != null; e = e.InnerException)
            {
                string win32 = e is System.ComponentModel.Win32Exception w ? $", Win32 {w.NativeErrorCode}" : "";
                Info($"    {e.GetType().FullName} (HRESULT 0x{e.HResult:X8}{win32}) : {e.Message}");
            }
            foreach (string frame in (ex.StackTrace ?? "").Split('\n').Take(4))
                if (frame.Trim().Length > 0) Info("      " + frame.Trim());
        }

        // Clé de produit Windows / Office (5 groupes de 5) : seuls les 5 derniers caractères restent dans le journal
        private static readonly Regex ProductKey = new Regex(@"\b[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}\b", RegexOptions.IgnoreCase);

        private static string MaskKeys(string text) =>
            ProductKey.Replace(text ?? "", m => "XXXXX-XXXXX-XXXXX-XXXXX-" + m.Value.Substring(24));

        private static void Write(LogLevel level, string text)
        {
            var entry = new LogEntry(level, MaskKeys(text));
            lock (Sync)
            {
                if (_file != null)
                {
                    try { File.AppendAllText(_file, entry + Environment.NewLine, new UTF8Encoding(true)); }
                    catch (IOException) { /* le journal ne doit jamais bloquer la configuration */ }
                }
            }
            Written?.Invoke(entry);
        }
    }
}
