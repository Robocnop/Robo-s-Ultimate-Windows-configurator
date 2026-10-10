using System;
using System.Globalization;
using System.Windows.Markup;

namespace RoboConfigurator.Core
{
    /// <summary>Langue de l'application : anglais par défaut, français si Windows est en français.</summary>
    public static class Lang
    {
        public static bool French { get; private set; } =
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";

        /// <summary>Force la langue (option -Lang en|fr). Renvoie false si le code n'est pas reconnu.</summary>
        public static bool Set(string code)
        {
            switch ((code ?? "").Trim().ToLowerInvariant())
            {
                case "en": French = false; return true;
                case "fr": French = true; return true;
                default: return false;
            }
        }

        /// <summary>Texte dans la langue de l'application.</summary>
        public static string T(string en, string fr) => French ? fr : en;

        /// <summary>Date courte : 31/12/2026 en français, 2026-12-31 en anglais.</summary>
        public static string Date(DateTime d) =>
            d.ToString(French ? "dd/MM/yyyy" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

namespace RoboConfigurator
{
    /// <summary>Texte traduit dans le XAML : <c>Text="{local:T 'Steps', 'Étapes'}"</c>.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class T : MarkupExtension
    {
        public T(string en, string fr) { En = en; Fr = fr; }

        [ConstructorArgument("en")] public string En { get; set; }
        [ConstructorArgument("fr")] public string Fr { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) => Core.Lang.T(En, Fr);
    }
}
