using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RoboConfigurator.Core
{
    /// <summary>Droits et suppression du dossier de travail (C:\RoboConfigurator).</summary>
    public static class WorkFolder
    {
        /// <summary>
        /// Un dossier créé à la racine de C:\ hérite du droit « Modification » des utilisateurs authentifiés :
        /// n'importe quel compte pourrait lire la configuration ou remplacer un installateur lancé ensuite en administrateur.
        /// Seuls les administrateurs et SYSTEM gardent l'accès, sans héritage.
        /// </summary>
        /// <returns>Ligne pour le journal.</returns>
        public static string Secure(string dir)
        {
            try
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, security);
                return Lang.T($"Permissions of {dir}: administrators and SYSTEM only", $"Droits de {dir} : administrateurs et SYSTEM uniquement");
            }
            catch (Exception ex)
            {
                return Lang.T($"Could not restrict the permissions of {dir}: {ex.Message}", $"Impossible de restreindre les droits de {dir} : {ex.Message}");
            }
        }

        public static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        /// <summary>
        /// Supprime le dossier et son contenu. Une jonction ou un lien symbolique est supprimé lui-même, jamais sa cible.
        /// Ce qui est verrouillé (exe lancé depuis ce dossier...) part au redémarrage.
        /// </summary>
        /// <returns>Nombre de fichiers qui seront supprimés au redémarrage.</returns>
        public static int Delete(string root)
        {
            root = Path.GetFullPath(root).TrimEnd('\\');
            if (IsLink(root))
                throw new IOException(Lang.T($"{root} is a link: it is not deleted", $"{root} est un lien : il n'est pas supprimé"));
            int pending = 0;
            DeleteTree(root, root, ref pending);
            return pending;
        }

        private static void DeleteTree(string root, string dir, ref int pending)
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                EnsureInside(root, file);
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                catch (Exception) { SystemInfo.DeleteAtReboot(file); pending++; }
            }
            foreach (string sub in Directory.GetDirectories(dir))
            {
                EnsureInside(root, sub);
                // Jonction ou lien : on retire le lien, sans parcourir sa cible
                if (IsLink(sub)) { RemoveDirectory(sub); continue; }
                DeleteTree(root, sub, ref pending);
            }
            RemoveDirectory(dir);
        }

        private static void RemoveDirectory(string dir)
        {
            try
            {
                if (!IsLink(dir)) new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
                Directory.Delete(dir, false);
            }
            catch (Exception) { SystemInfo.DeleteAtReboot(dir); }
        }

        private static void EnsureInside(string root, string path)
        {
            if (!Path.GetFullPath(path).StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException(Lang.T($"{path} is outside {root}: clean-up stopped", $"{path} est hors de {root} : nettoyage arrêté"));
        }
    }
}
