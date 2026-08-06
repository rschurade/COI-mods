using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Mods;
using Mafi.Localization;

namespace ShippingPP;

/// <summary>
/// The mod's translation layer. The game's own localization only covers strings shipped in its
/// <c>Translations/*.json</c> files, and it refuses to load any translation data after the first
/// string has been requested — so mod strings can never end up in there. Instead every
/// user-facing string of this mod is funnelled through <see cref="Text"/> with a stable id, and
/// the translation is looked up in the mod's OWN <c>Translations/&lt;lang&gt;.json</c> (the same
/// file format the game uses, parsed with the game's parser) for whatever language the player
/// has selected. Without a matching file the English source text is used.
///
/// Ids are also recorded here, so the full string list can be written back out as a template for
/// translators — see <see cref="TryExportTemplate"/>.
/// </summary>
internal static class ModTranslations
{
    /// <summary>Sub-directory of the mod folder holding the translation files.</summary>
    public const string DIR = "Translations";

    /// <summary>Presence of a file with this name in <see cref="DIR"/> makes the mod write out
    /// the English template on start (see <see cref="TryExportTemplate"/>).</summary>
    private const string EXPORT_FLAG = "EXPORT_TEMPLATE";

    private const string TEMPLATE_FILE = "en.json";

    private readonly struct Entry
    {
        public readonly string Id;
        public readonly string EnUs;

        public Entry(string id, string enUs)
        {
            Id = id;
            EnUs = enUs;
        }
    }

    private static string s_modRootDir;
    private static Dict<string, string> s_translations = new Dict<string, string>();
    private static readonly Lyst<Entry> s_entries = new Lyst<Entry>();
    private static readonly Dict<string, string> s_registeredIds = new Dict<string, string>();

    /// <summary>Name of the translation file the current <see cref="s_translations"/> was built
    /// from — the marker that the loaded data still matches the selected language.</summary>
    private static string s_loadedFileName;

    /// <summary>
    /// Points the translation lookup at the mod's own folder. Called from the mod's constructor,
    /// before anything builds a string (proto names are created during proto registration, UI
    /// strings when a window first opens).
    /// </summary>
    public static void Initialize(ModManifest manifest)
    {
        s_modRootDir = manifest?.RootDirectoryPath;
        if (string.IsNullOrEmpty(s_modRootDir))
        {
            Log.Warning("Shipping++: mod root directory unknown; translations not loaded.");
        }
        s_loadedFileName = null;
        load();
    }

    /// <summary>
    /// Reads the translation file of the player's current language. Re-run whenever the selected
    /// language no longer matches the loaded data, so strings built before the game applied its
    /// language setting are not stuck with the English fallback.
    /// </summary>
    private static void load()
    {
        try
        {
            s_translations = new Dict<string, string>();
            string fileName = LocalizationManager.CurrentLangInfo.FileName;
            s_loadedFileName = fileName;
            if (string.IsNullOrEmpty(s_modRootDir) || string.IsNullOrEmpty(fileName))
            {
                // No mod folder, or no language applied yet — English source text is used.
                return;
            }
            string path = Path.Combine(Path.Combine(s_modRootDir, DIR), fileName);
            if (!File.Exists(path))
            {
                Log.Info($"Shipping++: no translation file '{fileName}', using English.");
                return;
            }
            if (!LocalizationUtils.TryParseJsonFileData(File.ReadAllText(path),
                    out Dict<string, LocalizationManager.LocData> data, out string error))
            {
                // Partial data is still usable — the game treats its own files the same way.
                Log.Warning($"Shipping++: issues in translation file '{fileName}': {error}");
            }
            if (data != null)
            {
                foreach (System.Collections.Generic.KeyValuePair<string,
                    LocalizationManager.LocData> pair in data)
                {
                    if (pair.Value.TranslatedStrings.IsNotEmpty)
                    {
                        s_translations[pair.Key] = pair.Value.TranslatedStrings.First;
                    }
                }
            }
            Log.Info($"Shipping++: loaded {s_translations.Count} translated strings "
                + $"from '{fileName}'.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to load translations: {ex}");
        }
    }

    /// <summary>
    /// The translated text for the given id, or the English source text when the current
    /// language has no translation for it. Every call registers the id for
    /// <see cref="TryExportTemplate"/>.
    /// </summary>
    public static string Text(string id, string enUs)
    {
        if (s_loadedFileName != LocalizationManager.CurrentLangInfo.FileName)
        {
            load();
        }
        if (!s_registeredIds.TryGetValue(id, out string registered))
        {
            s_registeredIds[id] = enUs;
            s_entries.Add(new Entry(id, enUs));
        }
        else if (registered != enUs)
        {
            // Two different strings under one id: both would show the same translation. (The
            // same string asked for repeatedly is fine — a pattern shared by several protos.)
            Log.Warning($"Shipping++: translation id '{id}' used for two different strings.");
        }
        return s_translations.TryGetValue(id, out string translated)
            && !string.IsNullOrEmpty(translated) ? translated : enUs;
    }

    /// <summary>The translated text for the given id, ready to be shown in the UI.</summary>
    public static LocStrFormatted Str(string id, string enUs)
    {
        return Text(id, enUs).AsLoc();
    }

    /// <summary>
    /// Fills the arguments into a (translated) pattern. Falls back to the unformatted pattern if
    /// a translation dropped or mangled a placeholder, which would otherwise throw.
    /// </summary>
    public static LocStrFormatted Fmt(string pattern, params object[] args)
    {
        try
        {
            return string.Format(pattern, args).AsLoc();
        }
        catch (FormatException)
        {
            Log.Warning($"Shipping++: bad placeholders in translated string '{pattern}'.");
            return pattern.AsLoc();
        }
    }

    /// <summary>
    /// Writes every string of this mod to <c>Translations/en.json</c> as the starting point for
    /// a translation — but only when a file named <c>EXPORT_TEMPLATE</c> sits in that folder, so
    /// a normal game never writes into the mod directory. That flag file is consumed by the
    /// export. The result uses the game's own translation format: copy it to
    /// <c>&lt;language&gt;.json</c> (the file names the game uses in its own Translations
    /// folder) and translate the second entry of each pair.
    /// </summary>
    public static void TryExportTemplate()
    {
        try
        {
            if (string.IsNullOrEmpty(s_modRootDir))
            {
                return;
            }
            string dir = Path.Combine(s_modRootDir, DIR);
            string flag = Path.Combine(dir, EXPORT_FLAG);
            if (!File.Exists(flag))
            {
                return;
            }
            // One export per flag: the file is not left behind to be picked up by a release
            // build, and a later game start won't overwrite an edited en.json.
            File.Delete(flag);
            // Proto strings are registered by now; the UI catalog is only built when a window
            // first opens, so its type initializer is run explicitly.
            RuntimeHelpers.RunClassConstructor(typeof(Txt).TypeHandle);

            var sb = new StringBuilder(4096);
            sb.AppendLine("[");
            for (int i = 0; i < s_entries.Count; i++)
            {
                Entry entry = s_entries[i];
                sb.Append("\t[\"").Append(escape(entry.Id)).Append("\", \"")
                    .Append(escape(entry.EnUs)).Append(i == s_entries.Count - 1 ? "\"]" : "\"],")
                    .AppendLine();
            }
            sb.AppendLine("]");
            string path = Path.Combine(dir, TEMPLATE_FILE);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Log.Info($"Shipping++: exported {s_entries.Count} strings to '{path}'.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to export the translation template: {ex}");
        }
    }

    private static string escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
