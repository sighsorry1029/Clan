using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace Clan;

internal static class ClanLocalization
{
    private const string WireStatusPrefix = "@ClanL10n1:";
    private const int MaximumWireStatusLength = ClanDataRules.MaxStatusLength;
    private const int MaximumWireArgumentCount = 8;
    private const int MaximumWireArgumentUtf8Bytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static bool _malformedWireStatusLogged;

    internal static event Action? LanguageChanged
    {
        add => ClanLocalizationManager.LanguageChanged += value;
        remove => ClanLocalizationManager.LanguageChanged -= value;
    }

    internal static void Initialize(BaseUnityPlugin plugin, Harmony harmony)
    {
        ClanLocalizationManager.Initialize(plugin, harmony);
    }

    internal static void Dispose()
    {
        ClanLocalizationManager.Dispose();
    }

    internal static string Text(string key)
    {
        return ClanLocalizationManager.GetText(key);
    }

    internal static string Format(string key, params object[] arguments)
    {
        string template = Text(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException exception)
        {
            ClanPlugin.ClanLogger.LogWarning($"Invalid format string for Clan localization key '{key}': {exception.Message}");
            return template;
        }
    }

    /// <summary>
    /// Encodes a locale-neutral status key and bounded UTF-8 arguments for transport.
    /// Format: @ClanL10n1:&lt;ascii-key&gt;|&lt;base64-utf8-argument&gt;...
    /// </summary>
    internal static string EncodeStatus(string key, params object[] arguments)
    {
        string normalizedKey = (key ?? string.Empty).Trim().TrimStart('$');
        if (normalizedKey.Length == 0)
        {
            return string.Empty;
        }

        if (normalizedKey.Length > 96 || !IsSafeWireKey(normalizedKey))
        {
            ClanPlugin.ClanLogger.LogWarning($"Refused to encode invalid Clan status localization key '{normalizedKey}'.");
            return normalizedKey;
        }

        object[] safeArguments = arguments ?? Array.Empty<object>();
        int argumentCount = Math.Min(safeArguments.Length, MaximumWireArgumentCount);
        StringBuilder encoded = new(WireStatusPrefix.Length + normalizedKey.Length + argumentCount * 24);
        encoded.Append(WireStatusPrefix).Append(normalizedKey);

        for (int index = 0; index < argumentCount; ++index)
        {
            string value = Convert.ToString(safeArguments[index], CultureInfo.InvariantCulture) ?? string.Empty;
            string boundedValue = LimitUtf8(value, MaximumWireArgumentUtf8Bytes);
            encoded.Append('|').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(boundedValue)));
        }

        if (encoded.Length <= MaximumWireStatusLength)
        {
            return encoded.ToString();
        }

        ClanPlugin.ClanLogger.LogWarning($"Clan status localization payload for '{normalizedKey}' exceeded its wire limit.");
        return WireStatusPrefix + normalizedKey;
    }

    internal static string ResolveStatus(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(WireStatusPrefix, StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        try
        {
            if (value.Length > MaximumWireStatusLength)
            {
                throw new FormatException("status payload exceeds its wire limit");
            }

            string payload = value.Substring(WireStatusPrefix.Length);
            string[] fields = payload.Split('|');
            if (fields.Length == 0 || fields.Length > MaximumWireArgumentCount + 1 ||
                fields[0].Length == 0 || fields[0].Length > 96 || !IsSafeWireKey(fields[0]))
            {
                throw new FormatException("status payload contains an invalid localization key or argument count");
            }

            if (fields.Length == 1)
            {
                return Text(fields[0]);
            }

            List<object> arguments = new(fields.Length - 1);
            for (int index = 1; index < fields.Length; ++index)
            {
                byte[] bytes = Convert.FromBase64String(fields[index]);
                if (bytes.Length > MaximumWireArgumentUtf8Bytes)
                {
                    throw new FormatException("status argument exceeds its wire limit");
                }

                arguments.Add(StrictUtf8.GetString(bytes));
            }

            return Format(fields[0], arguments.ToArray());
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            if (!_malformedWireStatusLogged)
            {
                _malformedWireStatusLogged = true;
                ClanPlugin.ClanLogger.LogWarning($"Ignored a malformed localized Clan status payload: {exception.Message}");
            }

            return Text("clan_status_unavailable");
        }
    }

    internal static string Role(ClanRole role)
    {
        return role switch
        {
            ClanRole.Leader => Text("clan_role_leader"),
            ClanRole.Officer => Text("clan_role_officer"),
            ClanRole.Member => Text("clan_role_member"),
            ClanRole.Guest => Text("clan_role_guest"),
            _ => role.ToString()
        };
    }

    private static bool IsSafeWireKey(string key)
    {
        foreach (char character in key)
        {
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character != '_' && character != '-' && character != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string LimitUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        StringBuilder bounded = new(value.Length);
        int byteCount = 0;
        for (int index = 0; index < value.Length; ++index)
        {
            int characterLength = char.IsHighSurrogate(value[index]) && index + 1 < value.Length &&
                                  char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;
            string character = value.Substring(index, characterLength);
            int characterBytes = Encoding.UTF8.GetByteCount(character);
            if (byteCount + characterBytes > maximumBytes)
            {
                break;
            }

            bounded.Append(character);
            byteCount += characterBytes;
            index += characterLength - 1;
        }

        return bounded.ToString();
    }
}
