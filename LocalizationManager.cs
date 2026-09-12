// Adapted from AzumattDev/LocalizationManager 1.4.0.
// https://github.com/AzumattDev/LocalizationManager
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using YamlDotNet.Serialization;

namespace Clan;

/// <summary>
/// Small, defensive source integration of LocalizationManager's embedded-resource and
/// BepInEx-wide external override behavior. It intentionally remains internal so Clan
/// does not expose or require a second BepInEx plugin.
/// </summary>
internal static class ClanLocalizationManager
{
    private static readonly string[] SupportedExtensions = { ".yml", ".json" };
    private const long MaximumExternalTranslationBytes = 1024 * 1024;
    private const int MaximumTranslationValueLength = 4096;
    private static readonly object Sync = new();
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);
    private static BaseUnityPlugin? _plugin;
    private static ManualLogSource? _logger;
    private static Dictionary<string, string> _embeddedEnglish = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _currentTexts = new(StringComparer.Ordinal);
    private static string _currentLanguage = "English";
    private static bool _hasLoadedLanguage;
    private static bool _initialized;

    internal static event Action? LanguageChanged;

    internal static void Initialize(BaseUnityPlugin plugin, Harmony harmony)
    {
        if (_initialized)
        {
            return;
        }

        _plugin = plugin;
        _logger = ClanPlugin.ClanLogger;
        _embeddedEnglish = LoadEmbeddedTranslation("English")
            ?? throw new InvalidOperationException(
                $"{plugin.Info.Metadata.Name} has no embedded translations/English.yml or translations/English.json resource.");
        _currentTexts = new Dictionary<string, string>(_embeddedEnglish, StringComparer.Ordinal);
        _initialized = true;

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(Localization), nameof(Localization.SetupLanguage)),
            postfix: new HarmonyMethod(typeof(ClanLocalizationManager), nameof(AfterSetupLanguage)));
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(FejdStartup), "SetupGui"),
            postfix: new HarmonyMethod(typeof(ClanLocalizationManager), nameof(AfterSetupGui)));

        Localization? localization = Localization.instance;
        if (localization != null)
        {
            Reload(SafeSelectedLanguage(localization));
        }
    }

    internal static void Dispose()
    {
        lock (Sync)
        {
            _plugin = null;
            _logger = null;
            _embeddedEnglish = new Dictionary<string, string>(StringComparer.Ordinal);
            _currentTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            _currentLanguage = "English";
            _hasLoadedLanguage = false;
            LoggedWarnings.Clear();
            LanguageChanged = null;
            _initialized = false;
        }
    }

    internal static string GetText(string key)
    {
        string normalizedKey = NormalizeKey(key);
        lock (Sync)
        {
            if (_currentTexts.TryGetValue(normalizedKey, out string? localized))
            {
                return localized;
            }

            if (_embeddedEnglish.TryGetValue(normalizedKey, out string? english))
            {
                return english;
            }
        }

        WarnOnce($"missing:{normalizedKey}", $"Missing Clan localization key '{normalizedKey}'.");
        return normalizedKey;
    }

    private static void AfterSetupLanguage(string language)
    {
        Reload(language);
    }

    private static void AfterSetupGui()
    {
        Localization? localization = Localization.instance;
        if (localization != null && NeedsReload(SafeSelectedLanguage(localization)))
        {
            Reload(SafeSelectedLanguage(localization));
        }
    }

    private static bool NeedsReload(string language)
    {
        lock (Sync)
        {
            return !_hasLoadedLanguage ||
                   !_currentLanguage.Equals(language, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void Reload(string language)
    {
        if (!_initialized || _plugin == null)
        {
            return;
        }

        string selectedLanguage = string.IsNullOrWhiteSpace(language) ? "English" : language.Trim();
        Dictionary<string, List<string>> externalFiles = FindExternalTranslations();
        Dictionary<string, string> merged = new(_embeddedEnglish, StringComparer.Ordinal);

        MergeExternalTranslation(merged, externalFiles, "English");
        if (!selectedLanguage.Equals("English", StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, string>? embeddedTranslation = LoadEmbeddedTranslation(selectedLanguage);
            if (embeddedTranslation != null)
            {
                Merge(
                    merged,
                    ValidateTranslation($"embedded {selectedLanguage} translation", embeddedTranslation));
            }
            MergeExternalTranslation(merged, externalFiles, selectedLanguage);
        }

        lock (Sync)
        {
            _currentTexts = merged;
            _currentLanguage = selectedLanguage;
            _hasLoadedLanguage = true;
        }

        InvokeLanguageChanged();
    }

    private static Dictionary<string, List<string>> FindExternalTranslations()
    {
        BaseUnityPlugin? plugin = _plugin;
        if (plugin == null)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        string pluginName = plugin.Info.Metadata.Name;
        string? bepinExRoot = Directory.GetParent(Paths.PluginPath)?.FullName;
        if (string.IsNullOrWhiteSpace(bepinExRoot) || !Directory.Exists(bepinExRoot))
        {
            WarnOnce("missing-bepinex-root", "Could not locate the BepInEx root while searching for Clan translations.");
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in EnumerateFilesSafely(bepinExRoot!))
        {
            string extension = Path.GetExtension(file);
            if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(file);
            string prefix = pluginName + ".";
            if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || stem.Length == prefix.Length)
            {
                continue;
            }

            string language = stem.Substring(prefix.Length);
            if (!result.TryGetValue(language, out List<string>? paths))
            {
                paths = new List<string>();
                result[language] = paths;
            }

            paths.Add(file);
        }

        foreach (List<string> paths in result.Values)
        {
            paths.Sort(CompareExternalCandidates);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        Stack<string> pending = new();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WarnOnce(
                    $"scan-files:{directory}",
                    $"Could not scan '{directory}' for Clan translations: {exception.Message}");
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                yield return file;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WarnOnce(
                    $"scan-directories:{directory}",
                    $"Could not scan subdirectories of '{directory}' for Clan translations: {exception.Message}");
                continue;
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int index = directories.Length - 1; index >= 0; --index)
            {
                string child = directories[index];
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    WarnOnce(
                        $"scan-attributes:{child}",
                        $"Could not inspect '{child}' while searching for Clan translations: {exception.Message}");
                }
            }
        }
    }

    private static int CompareExternalCandidates(string left, string right)
    {
        int configPriority = IsUnderDirectory(left, Paths.ConfigPath).CompareTo(IsUnderDirectory(right, Paths.ConfigPath));
        if (configPriority != 0)
        {
            return -configPriority;
        }

        int extensionPriority = ExtensionPriority(left).CompareTo(ExtensionPriority(right));
        return extensionPriority != 0
            ? extensionPriority
            : StringComparer.OrdinalIgnoreCase.Compare(Path.GetFullPath(left), Path.GetFullPath(right));
    }

    private static int ExtensionPriority(string path)
    {
        return Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void MergeExternalTranslation(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, List<string>> externalFiles,
        string language)
    {
        if (!externalFiles.TryGetValue(language, out List<string>? candidates))
        {
            return;
        }

        for (int index = 0; index < candidates.Count; ++index)
        {
            string path = candidates[index];
            Dictionary<string, string>? translation = TryLoadExternalTranslation(path);
            if (translation == null)
            {
                continue;
            }

            Merge(target, translation);
            for (int duplicateIndex = index + 1; duplicateIndex < candidates.Count; ++duplicateIndex)
            {
                WarnOnce(
                    $"duplicate:{language}:{candidates[duplicateIndex]}",
                    $"Ignoring duplicate Clan {language} translation '{candidates[duplicateIndex]}'; using '{path}'.");
            }

            return;
        }
    }

    private static Dictionary<string, string>? TryLoadExternalTranslation(string path)
    {
        try
        {
            long fileLength = new FileInfo(path).Length;
            if (fileLength <= 0L || fileLength > MaximumExternalTranslationBytes)
            {
                throw new InvalidDataException(
                    $"translation size must be between 1 and {MaximumExternalTranslationBytes} bytes");
            }

            string data = File.ReadAllText(path, Encoding.UTF8);
            Dictionary<string, string>? translation = ParseTranslation(data);
            if (translation == null || translation.Count == 0)
            {
                throw new InvalidDataException("the translation file is empty");
            }

            translation = ValidateTranslation(path, translation);
            if (translation.Count == 0)
            {
                throw new InvalidDataException("the translation file has no valid Clan keys");
            }

            _logger?.LogInfo($"Loaded external Clan translation '{path}'.");
            return translation;
        }
        catch (Exception exception)
        {
            WarnOnce(
                $"external:{path}:{exception.GetType().FullName}:{exception.Message}",
                $"Could not load external Clan translation '{path}': {exception.Message}. Falling back to the next valid source.");
            return null;
        }
    }

    private static Dictionary<string, string> ValidateTranslation(
        string source,
        IReadOnlyDictionary<string, string> translation)
    {
        Dictionary<string, string> validated = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in translation)
        {
            if (!_embeddedEnglish.TryGetValue(pair.Key, out string? english))
            {
                WarnOnce(
                    $"unknown-key:{source}:{pair.Key}",
                    $"Ignored unknown Clan translation key '{pair.Key}' in '{source}'.");
                continue;
            }
            if (pair.Value.Length > MaximumTranslationValueLength)
            {
                WarnOnce(
                    $"long-value:{source}:{pair.Key}",
                    $"Ignored Clan translation key '{pair.Key}' in '{source}' because its value exceeds {MaximumTranslationValueLength} characters.");
                continue;
            }
            if (!HasCompatiblePlaceholders(english, pair.Value))
            {
                WarnOnce(
                    $"placeholders:{source}:{pair.Key}",
                    $"Ignored Clan translation key '{pair.Key}' in '{source}' because its format placeholders do not match English.");
                continue;
            }

            validated[pair.Key] = pair.Value;
        }

        return validated;
    }

    private static bool HasCompatiblePlaceholders(string english, string candidate)
    {
        return TryReadPlaceholderIndexes(english, out HashSet<int> englishIndexes) &&
               TryReadPlaceholderIndexes(candidate, out HashSet<int> candidateIndexes) &&
               englishIndexes.SetEquals(candidateIndexes);
    }

    private static bool TryReadPlaceholderIndexes(
        string template,
        out HashSet<int> indexes)
    {
        indexes = new HashSet<int>();
        int position = 0;
        while (position < template.Length)
        {
            char character = template[position];
            if (character == '{')
            {
                if (position + 1 < template.Length && template[position + 1] == '{')
                {
                    position += 2;
                    continue;
                }

                int closingBrace = template.IndexOf('}', position + 1);
                if (closingBrace < 0 ||
                    !int.TryParse(
                        template.Substring(position + 1, closingBrace - position - 1),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int argumentIndex))
                {
                    return false;
                }

                indexes.Add(argumentIndex);
                position = closingBrace + 1;
                continue;
            }

            if (character == '}')
            {
                if (position + 1 >= template.Length || template[position + 1] != '}')
                {
                    return false;
                }
                position += 2;
                continue;
            }

            ++position;
        }

        return true;
    }

    private static Dictionary<string, string>? LoadEmbeddedTranslation(string language)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string extension in SupportedExtensions)
        {
            string suffix = $"translations.{language}{extension}";
            string? resourceName = assembly.GetManifestResourceNames()
                .OrderBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                continue;
            }

            using StreamReader reader = new(stream, Encoding.UTF8, true);
            return ParseTranslation(reader.ReadToEnd());
        }

        return null;
    }

    private static Dictionary<string, string>? ParseTranslation(string data)
    {
        Dictionary<string, string>? parsed = new DeserializerBuilder()
            .IgnoreFields()
            .WithDuplicateKeyChecking()
            .Build()
            .Deserialize<Dictionary<string, string>>(data);
        if (parsed == null)
        {
            return null;
        }

        Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in parsed)
        {
            string key = NormalizeKey(pair.Key);
            if (!string.IsNullOrWhiteSpace(key) && pair.Value != null)
            {
                normalized[key] = pair.Value;
            }
        }

        return normalized;
    }

    private static void Merge(IDictionary<string, string> target, IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in source)
        {
            target[pair.Key] = pair.Value;
        }
    }

    private static string SafeSelectedLanguage(Localization localization)
    {
        try
        {
            return localization.GetSelectedLanguage();
        }
        catch (Exception exception)
        {
            WarnOnce("selected-language", $"Could not read the selected language: {exception.Message}. Using English.");
            return _currentLanguage;
        }
    }

    private static string NormalizeKey(string key)
    {
        return (key ?? string.Empty).Trim().TrimStart('$');
    }

    private static void InvokeLanguageChanged()
    {
        Delegate[] subscribers = LanguageChanged?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Delegate subscriber in subscribers)
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(
                    $"Clan language-change subscriber failed: {exception.GetBaseException().Message}");
            }
        }
    }

    private static void WarnOnce(string identity, string message)
    {
        lock (Sync)
        {
            if (!LoggedWarnings.Add(identity))
            {
                return;
            }
        }

        _logger?.LogWarning(message);
    }
}
