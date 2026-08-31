using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Splatform;
using UnityEngine;

namespace Clan;

public enum ClanRole
{
    Leader = 0,
    Officer = 1,
    Member = 2,
    Guest = 3
}

internal enum ClanRequestType
{
    RequestSnapshot = 0,
    CreateClan = 1,
    Invite = 2,
    Apply = 3,
    AcceptInvite = 4,
    DeclineInvite = 5,
    AcceptApplication = 6,
    RejectApplication = 7,
    KickPlayer = 8,
    TransferLeadership = 9,
    SetRole = 10,
    LeaveClan = 11,
    SendClanChat = 12,
    SendClanPing = 13,
    UpdatePosition = 14,
    CancelApplication = 15,
    UpdateClanProfile = 16,
    RequestDirectory = 17,
    RenameClan = 18,
    RequestHud = 19
}

internal enum ClanOperationResultCode
{
    None = 0,
    Success = 1,
    Unchanged = 2,
    InvalidName = 3,
    IdentityUnavailable = 4,
    Unauthorized = 5,
    ClanChanged = 6,
    NameTaken = 7,
    RateLimited = 8,
    Unavailable = 9,
    Failed = 10,
    SnapshotRateLimited = 11,
    IdentityRejected = 12
}

internal enum ClanResponseType
{
    Snapshot = 0,
    Chat = 1,
    MapPing = 2,
    PositionUpdate = 3,
    Directory = 4,
    DirectoryInvalidated = 5,
    Hud = 6
}

internal enum ClanDirectoryPlayerState
{
    None = 0,
    Clan = 1,
    Pending = 2,
    Invited = 3
}

internal enum ClanValidationField
{
    None = 0,
    ClanName = 1,
    ClanDescription = 2,
    ClanEmblemKey = 3
}

internal enum ClanValidationError
{
    Required = 0,
    TooLong = 1,
    RichText = 2,
    ControlCharacters = 3,
    InvalidUnicode = 4,
    InvalidCharacters = 5,
    TrailingSeparator = 6,
    MissingLetterOrNumber = 7,
    InvalidStartOrEnd = 8
}

internal static class ClanDataRules
{
    public const int MaxPlatformIdLength = 128;
    public const int MaxPlayerKeyLength = 160;
    public const int MaxPlayerNameLength = 64;
    public const int ClanIdLength = 32;
    public const int MaxClanNameLength = 40;
    public const int MaxClanDescriptionLength = 160;
    public const int MaxClanEmblemKeyLength = 32;
    public const int MaxChatMessageLength = 400;
    public const int MaxStatusLength = 1024;
    public const int MaxInviteIdLength = 64;
    public const int MaxClans = 1024;
    public const int MaxMembersPerClan = 512;
    public const int MaxApplicationsPerClan = 512;
    public const int MaxInvites = 4096;
    public const int MaxDirectoryPlayers = 1024;
    public const int MaxHudPlayers = 10;
    public const float MaxHudHealth = 1_000_000f;

    private static readonly object ValidationErrorDataKey = new();

    public static string RequireText(
        string? value,
        int maxLength,
        string fieldName,
        bool allowEmpty = true,
        bool allowLineBreaks = false,
        ClanValidationField validationField = ClanValidationField.None)
    {
        string text = (value ?? "").Trim();
        if (!allowEmpty && text.Length == 0)
        {
            throw ValidationFailure(
                validationField,
                ClanValidationError.Required,
                $"{fieldName} is required.");
        }
        if (text.Length > maxLength)
        {
            throw ValidationFailure(
                validationField,
                ClanValidationError.TooLong,
                $"{fieldName} exceeds {maxLength} characters.");
        }
        if (text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0)
        {
            throw ValidationFailure(
                validationField,
                ClanValidationError.RichText,
                $"{fieldName} cannot contain rich-text tags.");
        }

        foreach (char character in text)
        {
            if (char.IsControl(character) &&
                !(allowLineBreaks && (character == '\r' || character == '\n' || character == '\t')))
            {
                throw ValidationFailure(
                    validationField,
                    ClanValidationError.ControlCharacters,
                    $"{fieldName} contains unsupported control characters.");
            }
        }
        return text;
    }

    public static string RequireClanDescription(
        string? value,
        string fieldName = "clan description")
    {
        return RequireText(
            value,
            MaxClanDescriptionLength,
            fieldName,
            allowEmpty: true,
            allowLineBreaks: false,
            validationField: ClanValidationField.ClanDescription);
    }

    public static string ReadClanDescription(
        ZPackage package,
        string fieldName = "clan description")
    {
        return RequireClanDescription(package.ReadString(), fieldName);
    }

    public static void WriteClanDescription(
        ZPackage package,
        string? value,
        string fieldName = "clan description")
    {
        package.Write(RequireClanDescription(value, fieldName));
    }

    public static string RequireClanName(string? value, string fieldName = "clan name")
    {
        string normalized;
        try
        {
            normalized = (value ?? "").Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException exception)
        {
            throw ValidationFailure(
                ClanValidationField.ClanName,
                ClanValidationError.InvalidUnicode,
                $"{fieldName} contains invalid Unicode data.",
                exception);
        }

        StringBuilder canonical = new(normalized.Length);
        bool previousWasSpace = false;
        foreach (char character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator)
            {
                if (canonical.Length > 0 && !previousWasSpace)
                {
                    canonical.Append(' ');
                }
                previousWasSpace = true;
                continue;
            }

            canonical.Append(character);
            previousWasSpace = false;
        }

        string name = canonical.ToString().Trim(' ');
        if (name.Length == 0)
        {
            throw ValidationFailure(
                ClanValidationField.ClanName,
                ClanValidationError.Required,
                $"{fieldName} is required.");
        }

        int scalarCount = 0;
        bool hasLetterOrNumber = false;
        bool previousWasSeparator = true;
        bool canAcceptMark = false;
        for (int index = 0; index < name.Length;)
        {
            char first = name[index];
            int scalarLength = 1;
            if (char.IsHighSurrogate(first))
            {
                if (index + 1 >= name.Length || !char.IsLowSurrogate(name[index + 1]))
                {
                    throw ValidationFailure(
                        ClanValidationField.ClanName,
                        ClanValidationError.InvalidUnicode,
                        $"{fieldName} contains invalid Unicode data.");
                }
                scalarLength = 2;
            }
            else if (char.IsLowSurrogate(first))
            {
                throw ValidationFailure(
                    ClanValidationField.ClanName,
                    ClanValidationError.InvalidUnicode,
                    $"{fieldName} contains invalid Unicode data.");
            }

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(name, index);
            bool isLetter = category is UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter;
            bool isNumber = category is UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or
                UnicodeCategory.OtherNumber;
            bool isMark = category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark;
            bool isSeparator = scalarLength == 1 &&
                               (first == ' ' || first == '-' || first == '_' || first == '·');

            if (isLetter || isNumber)
            {
                hasLetterOrNumber = true;
                previousWasSeparator = false;
                canAcceptMark = true;
            }
            else if (isMark && canAcceptMark)
            {
                previousWasSeparator = false;
            }
            else if (isSeparator && !previousWasSeparator)
            {
                previousWasSeparator = true;
                canAcceptMark = false;
            }
            else
            {
                throw ValidationFailure(
                    ClanValidationField.ClanName,
                    ClanValidationError.InvalidCharacters,
                    $"{fieldName} can contain only letters, numbers, marks, spaces, '-', '_', or '·'.");
            }

            scalarCount++;
            if (scalarCount > MaxClanNameLength)
            {
                throw ValidationFailure(
                    ClanValidationField.ClanName,
                    ClanValidationError.TooLong,
                    $"{fieldName} exceeds {MaxClanNameLength} Unicode characters.");
            }
            index += scalarLength;
        }

        if (previousWasSeparator)
        {
            throw ValidationFailure(
                ClanValidationField.ClanName,
                ClanValidationError.TrailingSeparator,
                $"{fieldName} cannot end with a separator.");
        }
        if (!hasLetterOrNumber)
        {
            throw ValidationFailure(
                ClanValidationField.ClanName,
                ClanValidationError.MissingLetterOrNumber,
                $"{fieldName} must contain a letter or number.");
        }
        return name;
    }

    public static string RequireOptionalClanName(string? value, string fieldName = "clan name")
    {
        return string.IsNullOrWhiteSpace(value) ? "" : RequireClanName(value, fieldName);
    }

    public static string ReadClanName(ZPackage package, string fieldName = "clan name")
    {
        return RequireClanName(package.ReadString(), fieldName);
    }

    public static string ReadOptionalClanName(ZPackage package, string fieldName = "clan name")
    {
        return RequireOptionalClanName(package.ReadString(), fieldName);
    }

    public static void WriteClanName(
        ZPackage package,
        string? value,
        string fieldName = "clan name")
    {
        package.Write(RequireClanName(value, fieldName));
    }

    public static void WriteOptionalClanName(
        ZPackage package,
        string? value,
        string fieldName = "clan name")
    {
        package.Write(RequireOptionalClanName(value, fieldName));
    }

    public static string ReadText(
        ZPackage package,
        int maxLength,
        string fieldName,
        bool allowEmpty = true,
        bool allowLineBreaks = false)
    {
        return RequireText(package.ReadString(), maxLength, fieldName, allowEmpty, allowLineBreaks);
    }

    public static void WriteText(
        ZPackage package,
        string? value,
        int maxLength,
        string fieldName,
        bool allowEmpty = true,
        bool allowLineBreaks = false)
    {
        package.Write(RequireText(value, maxLength, fieldName, allowEmpty, allowLineBreaks));
    }

    public static string RequirePlayerKey(string? value, string fieldName = "player key")
    {
        string key = RequireText(value, MaxPlayerKeyLength, fieldName, allowEmpty: false);
        if (!TryParsePlayerKey(key, out _, out _))
        {
            throw new InvalidDataException($"{fieldName} is invalid.");
        }
        return key;
    }

    public static string ReadPlayerKey(ZPackage package, string fieldName = "player key")
    {
        return RequirePlayerKey(package.ReadString(), fieldName);
    }

    public static void WritePlayerKey(ZPackage package, string? value, string fieldName = "player key")
    {
        package.Write(RequirePlayerKey(value, fieldName));
    }

    public static string RequirePlatformId(string? value, string fieldName = "platform id")
    {
        return RequireText(value, MaxPlatformIdLength, fieldName, allowEmpty: false);
    }

    public static long RequireCharacterPlayerId(long value, string fieldName = "character player id")
    {
        if (value == 0L)
        {
            throw new InvalidDataException($"{fieldName} must be non-zero.");
        }
        return value;
    }

    public static string BuildPlayerKey(string? platformId, long playerId)
    {
        string normalizedPlatformId = RequirePlatformId(platformId);
        long normalizedPlayerId = RequireCharacterPlayerId(playerId);
        return normalizedPlatformId.Length.ToString(CultureInfo.InvariantCulture) +
               ":" +
               normalizedPlatformId +
               ":" +
               normalizedPlayerId.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParsePlayerKey(
        string? value,
        out string platformId,
        out long playerId)
    {
        platformId = "";
        playerId = 0L;
        string key = value ?? "";
        int lengthSeparator = key.IndexOf(':');
        if (lengthSeparator <= 0 ||
            !int.TryParse(
                key.Substring(0, lengthSeparator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int platformLength) ||
            platformLength <= 0 ||
            platformLength > MaxPlatformIdLength)
        {
            return false;
        }

        int platformStart = lengthSeparator + 1;
        int playerSeparator = platformStart + platformLength;
        if (playerSeparator >= key.Length || key[playerSeparator] != ':')
        {
            return false;
        }

        string parsedPlatformId = key.Substring(platformStart, platformLength);
        string playerText = key.Substring(playerSeparator + 1);
        if (!long.TryParse(
                playerText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsedPlayerId) ||
            parsedPlayerId == 0L ||
            NormalizePlatformId(parsedPlatformId).Length == 0)
        {
            return false;
        }

        string canonical;
        try
        {
            canonical = BuildPlayerKey(parsedPlatformId, parsedPlayerId);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        if (!StringComparer.Ordinal.Equals(canonical, key))
        {
            return false;
        }

        platformId = parsedPlatformId;
        playerId = parsedPlayerId;
        return true;
    }

    public static string ReadPlayerName(ZPackage package, string fieldName = "player name")
    {
        return ReadText(package, MaxPlayerNameLength, fieldName);
    }

    public static void WritePlayerName(ZPackage package, string? value, string fieldName = "player name")
    {
        WriteText(package, value, MaxPlayerNameLength, fieldName);
    }

    public static string RequireClanId(string? value, string fieldName = "clan id")
    {
        string text = RequireText(value, ClanIdLength, fieldName, allowEmpty: false);
        if (text.Length != ClanIdLength ||
            !Guid.TryParseExact(text, "N", out Guid parsed) ||
            parsed == Guid.Empty ||
            !StringComparer.Ordinal.Equals(parsed.ToString("N"), text))
        {
            throw new InvalidDataException($"{fieldName} is invalid.");
        }
        return text;
    }

    public static string RequireOptionalClanId(string? value, string fieldName = "clan id")
    {
        return string.IsNullOrWhiteSpace(value) ? "" : RequireClanId(value, fieldName);
    }

    public static string ReadClanId(ZPackage package, string fieldName = "clan id")
    {
        return RequireClanId(package.ReadString(), fieldName);
    }

    public static string ReadOptionalClanId(ZPackage package, string fieldName = "clan id")
    {
        return RequireOptionalClanId(package.ReadString(), fieldName);
    }

    public static void WriteClanId(ZPackage package, string? value, string fieldName = "clan id")
    {
        package.Write(RequireClanId(value, fieldName));
    }

    public static void WriteOptionalClanId(
        ZPackage package,
        string? value,
        string fieldName = "clan id")
    {
        package.Write(RequireOptionalClanId(value, fieldName));
    }

    public static string RequireClanEmblemKey(string? value, string fieldName = "clan emblem key")
    {
        string key = RequireText(
            value,
            MaxClanEmblemKeyLength,
            fieldName,
            validationField: ClanValidationField.ClanEmblemKey);
        if (key.Length == 0)
        {
            return key;
        }

        if (!IsLowerAsciiLetterOrDigit(key[0]) ||
            !IsLowerAsciiLetterOrDigit(key[key.Length - 1]))
        {
            throw ValidationFailure(
                ClanValidationField.ClanEmblemKey,
                ClanValidationError.InvalidStartOrEnd,
                $"{fieldName} must start and end with a lowercase ASCII letter or digit.");
        }

        foreach (char character in key)
        {
            if (!IsLowerAsciiLetterOrDigit(character) && character != '_' && character != '-')
            {
                throw ValidationFailure(
                    ClanValidationField.ClanEmblemKey,
                    ClanValidationError.InvalidCharacters,
                    $"{fieldName} can contain only lowercase ASCII letters, digits, '_' or '-'.");
            }
        }
        return key;
    }

    public static string ReadClanEmblemKey(ZPackage package, string fieldName = "clan emblem key")
    {
        return RequireClanEmblemKey(package.ReadString(), fieldName);
    }

    public static void WriteClanEmblemKey(
        ZPackage package,
        string? value,
        string fieldName = "clan emblem key")
    {
        package.Write(RequireClanEmblemKey(value, fieldName));
    }

    public static T RequireEnum<T>(T value, string fieldName) where T : struct, Enum
    {
        if (!Enum.IsDefined(typeof(T), value))
        {
            throw new InvalidDataException($"{fieldName} has an unknown value ({Convert.ToInt32(value)}).");
        }
        return value;
    }

    public static T ReadEnum<T>(ZPackage package, string fieldName) where T : struct, Enum
    {
        int rawValue = package.ReadInt();
        if (!Enum.IsDefined(typeof(T), rawValue))
        {
            throw new InvalidDataException($"{fieldName} has an unknown value ({rawValue}).");
        }
        return (T)Enum.ToObject(typeof(T), rawValue);
    }

    public static ClanRole RequireAssignableRole(ClanRole role, string fieldName)
    {
        RequireEnum(role, fieldName);
        if (role is not ClanRole.Officer and not ClanRole.Member and not ClanRole.Guest)
        {
            throw new InvalidDataException($"{fieldName} must be Officer, Member, or Guest.");
        }
        return role;
    }

    public static ClanRole ReadAssignableRole(ZPackage package, string fieldName)
    {
        return RequireAssignableRole(ReadEnum<ClanRole>(package, fieldName), fieldName);
    }

    public static int GetRolePower(ClanRole role)
    {
        return RequireEnum(role, "clan role") switch
        {
            ClanRole.Leader => 3,
            ClanRole.Officer => 2,
            ClanRole.Member => 1,
            ClanRole.Guest => 0,
            _ => throw new InvalidDataException("Clan role is unsupported.")
        };
    }

    public static int RequireRange(int value, int minimum, int maximum, string fieldName)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"{fieldName} is outside the supported range.");
        }
        return value;
    }

    public static long RequireRequestId(long value, string fieldName = "request id")
    {
        if (value <= 0L)
        {
            throw new InvalidDataException($"{fieldName} must be positive.");
        }
        return value;
    }

    public static long RequireOptionalRequestId(
        long value,
        string fieldName = "response request id")
    {
        if (value < 0L)
        {
            throw new InvalidDataException($"{fieldName} cannot be negative.");
        }
        return value;
    }

    public static int ReadCount(ZPackage package, int maximum, string fieldName)
    {
        return RequireRange(package.ReadInt(), 0, maximum, fieldName + " count");
    }

    public static void RequireCount(int count, int maximum, string fieldName)
    {
        _ = RequireRange(count, 0, maximum, fieldName + " count");
    }

    public static Vector3 ReadFiniteVector(ZPackage package, string fieldName)
    {
        Vector3 value = package.ReadVector3();
        RequireFiniteVector(value, fieldName);
        return value;
    }

    public static void RequireFiniteVector(Vector3 value, string fieldName)
    {
        if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
        {
            throw new InvalidDataException($"{fieldName} must contain finite coordinates.");
        }
    }

    public static string NormalizePlayerKey(string? value)
    {
        try
        {
            return RequirePlayerKey(value);
        }
        catch (InvalidDataException)
        {
            return "";
        }
    }

    public static string NormalizePlatformId(string? value)
    {
        try
        {
            return RequirePlatformId(value);
        }
        catch (InvalidDataException)
        {
            return "";
        }
    }

    public static string SanitizePlayerName(string? value)
    {
        string text = (value ?? "").Replace("<", " ").Replace(">", " ");
        StringBuilder safe = new(Math.Min(text.Length, MaxPlayerNameLength));
        for (int index = 0; index < text.Length && safe.Length < MaxPlayerNameLength; index++)
        {
            char character = text[index];
            if (char.IsControl(character))
            {
                continue;
            }

            bool isSurrogatePair = char.IsHighSurrogate(character) &&
                                   index + 1 < text.Length &&
                                   char.IsLowSurrogate(text[index + 1]);
            if (char.IsSurrogate(character) && !isSurrogatePair)
            {
                continue;
            }

            int characterLength = isSurrogatePair ? 2 : 1;
            if (safe.Length > MaxPlayerNameLength - characterLength)
            {
                break;
            }

            safe.Append(character);
            if (isSurrogatePair)
            {
                safe.Append(text[++index]);
            }
        }
        return safe.ToString().Trim();
    }

    public static bool TryGetValidationError(
        InvalidDataException exception,
        out ClanValidationField field,
        out ClanValidationError error)
    {
        if (exception.Data[ValidationErrorDataKey] is
            ValueTuple<ClanValidationField, ClanValidationError> metadata)
        {
            field = metadata.Item1;
            error = metadata.Item2;
            return true;
        }

        field = ClanValidationField.None;
        error = default;
        return false;
    }

    private static InvalidDataException ValidationFailure(
        ClanValidationField field,
        ClanValidationError error,
        string message,
        Exception? innerException = null)
    {
        InvalidDataException exception = new(message, innerException);
        if (field != ClanValidationField.None)
        {
            exception.Data[ValidationErrorDataKey] = (field, error);
        }
        return exception;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsLowerAsciiLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}

internal readonly struct ClanPlayerRef : IEquatable<ClanPlayerRef>
{
    public readonly string Id;
    public readonly string PlatformId;
    public readonly long CharacterPlayerId;
    public readonly string Name;

    public ClanPlayerRef(string? platformId, long playerId, string? name)
    {
        PlatformId = ClanDataRules.NormalizePlatformId(platformId);
        CharacterPlayerId = playerId;
        Name = ClanDataRules.SanitizePlayerName(name);
        Id = PlatformId.Length == 0 || CharacterPlayerId == 0L
            ? ""
            : ClanDataRules.BuildPlayerKey(PlatformId, CharacterPlayerId);
    }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PlatformId) &&
        CharacterPlayerId != 0L &&
        !string.IsNullOrWhiteSpace(Id);

    public static ClanPlayerRef Local()
    {
        IDistributionPlatform? platform = PlatformManager.DistributionPlatform;
        string platformId =
            platform?.LocalUser != null ? platform.LocalUser.PlatformUserID.ToString() : "";
        long playerId = Player.m_localPlayer?.GetPlayerID() ??
                        Game.instance?.GetPlayerProfile()?.GetPlayerID() ??
                        0L;
        string name = Game.instance?.GetPlayerProfile()?.GetName() ?? "Unknown";
        return new ClanPlayerRef(platformId, playerId, name);
    }

    public void Write(ZPackage package)
    {
        ClanDataRules.WriteText(
            package,
            PlatformId,
            ClanDataRules.MaxPlatformIdLength,
            "platform id",
            allowEmpty: false);
        package.Write(ClanDataRules.RequireCharacterPlayerId(CharacterPlayerId));
        ClanDataRules.WritePlayerName(package, Name);
    }

    public static ClanPlayerRef Read(ZPackage package)
    {
        return new ClanPlayerRef(
            ClanDataRules.ReadText(
                package,
                ClanDataRules.MaxPlatformIdLength,
                "platform id",
                allowEmpty: false),
            ClanDataRules.RequireCharacterPlayerId(package.ReadLong()),
            ClanDataRules.ReadPlayerName(package));
    }

    public bool Equals(ClanPlayerRef other)
    {
        return StringComparer.Ordinal.Equals(Id, other.Id);
    }

    public override bool Equals(object? obj)
    {
        return obj is ClanPlayerRef other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Id == null ? 0 : StringComparer.Ordinal.GetHashCode(Id);
    }

    public override string ToString()
    {
        string identity = IsValid ? $"{PlatformId}/{CharacterPlayerId}" : "";
        return string.IsNullOrWhiteSpace(Name) ? identity : $"{Name} ({identity})";
    }

    public static bool operator ==(ClanPlayerRef left, ClanPlayerRef right) => left.Equals(right);
    public static bool operator !=(ClanPlayerRef left, ClanPlayerRef right) => !left.Equals(right);
}

internal sealed class ClanMember
{
    public ClanPlayerRef Player;
    public ClanRole Role = ClanRole.Member;
    public float LastClanChatTime = float.NegativeInfinity;
    public float LastClanPingTime = float.NegativeInfinity;
}

internal sealed class ClanInvite
{
    public string InviteId = Guid.NewGuid().ToString("N");
    public string ClanId = "";
    public string FromName = "";
    public ClanPlayerRef Target;
}

internal sealed class ClanState
{
    public string ClanId { get; }
    public long CreationOrder;
    public string Name = "";
    public string Description = "";
    public string EmblemKey = "";
    public readonly Dictionary<string, ClanMember> Members = new(StringComparer.Ordinal);
    public readonly Dictionary<string, ClanPlayerRef> Applications = new(StringComparer.Ordinal);

    public ClanState(string clanId)
    {
        ClanId = ClanDataRules.RequireClanId(clanId);
    }

    public bool IsLeader(ClanPlayerRef player) =>
        player.IsValid &&
        Members.TryGetValue(player.Id, out ClanMember member) &&
        member.Role == ClanRole.Leader;
}

internal sealed class ClanRequest
{
    public ClanRequestType Type;
    public string ClanId = "";
    public string ClanName = "";
    public string TargetId = "";
    public string InviteId = "";
    public string Message = "";
    public string Description = "";
    public string EmblemKey = "";
    public ClanRole Role = ClanRole.Member;
    public long RequestId;
    public long HudSelectionRevision;
    public long HudStateRevision;
    public Vector3 Position = Vector3.zero;

    public static ClanRequest Simple(ClanRequestType type) => new() { Type = type };

    public void Write(ZPackage package)
    {
        ClanDataRules.RequireEnum(Type, "clan request type");
        package.Write((int)Type);
        switch (Type)
        {
            case ClanRequestType.RequestSnapshot:
            case ClanRequestType.CancelApplication:
                return;
            case ClanRequestType.LeaveClan:
                ClanDataRules.WriteClanId(package, ClanId);
                return;
            case ClanRequestType.RequestHud:
                ClanDataRules.WriteClanId(package, ClanId);
                package.Write(ClanDataRules.RequireOptionalRequestId(
                    HudSelectionRevision,
                    "HUD selection revision"));
                package.Write(ClanDataRules.RequireOptionalRequestId(
                    HudStateRevision,
                    "HUD state revision"));
                return;
            case ClanRequestType.RequestDirectory:
                package.Write(ClanDataRules.RequireRequestId(RequestId));
                return;
            case ClanRequestType.CreateClan:
                package.Write(ClanDataRules.RequireRequestId(RequestId));
                ClanDataRules.WriteClanName(package, ClanName);
                ClanDataRules.WriteClanDescription(package, Description);
                ClanDataRules.WriteClanEmblemKey(package, EmblemKey);
                return;
            case ClanRequestType.Invite:
            case ClanRequestType.AcceptApplication:
            case ClanRequestType.RejectApplication:
            case ClanRequestType.KickPlayer:
            case ClanRequestType.TransferLeadership:
                ClanDataRules.WriteClanId(package, ClanId);
                ClanDataRules.WritePlayerKey(package, TargetId, "target player key");
                return;
            case ClanRequestType.Apply:
                ClanDataRules.WriteClanId(package, ClanId);
                return;
            case ClanRequestType.AcceptInvite:
            case ClanRequestType.DeclineInvite:
                ClanDataRules.WriteText(
                    package, InviteId, ClanDataRules.MaxInviteIdLength, "invite id", allowEmpty: false);
                return;
            case ClanRequestType.SetRole:
                ClanDataRules.WriteClanId(package, ClanId);
                ClanDataRules.WritePlayerKey(package, TargetId, "target player key");
                package.Write((int)ClanDataRules.RequireAssignableRole(Role, "clan role"));
                return;
            case ClanRequestType.UpdateClanProfile:
                package.Write(ClanDataRules.RequireRequestId(RequestId));
                ClanDataRules.WriteClanId(package, ClanId);
                ClanDataRules.WriteClanName(package, ClanName);
                ClanDataRules.WriteClanDescription(package, Description);
                ClanDataRules.WriteClanEmblemKey(package, EmblemKey);
                return;
            case ClanRequestType.RenameClan:
                package.Write(ClanDataRules.RequireRequestId(RequestId));
                ClanDataRules.WriteClanId(package, ClanId);
                ClanDataRules.WriteClanName(package, ClanName);
                return;
            case ClanRequestType.SendClanChat:
                ClanDataRules.WriteClanId(package, ClanId);
                ClanDataRules.WriteText(
                    package, Message, ClanDataRules.MaxChatMessageLength,
                    "clan chat message", allowEmpty: false);
                return;
            case ClanRequestType.SendClanPing:
            case ClanRequestType.UpdatePosition:
                ClanDataRules.WriteClanId(package, ClanId);
                ClanDataRules.RequireFiniteVector(Position, "position");
                package.Write(Position);
                return;
            default:
                throw new InvalidDataException($"Unknown clan request type ({(int)Type}).");
        }
    }

    public static ClanRequest Read(ZPackage package)
    {
        ClanRequest request = new()
        {
            Type = ClanDataRules.ReadEnum<ClanRequestType>(package, "clan request type")
        };
        switch (request.Type)
        {
            case ClanRequestType.RequestSnapshot:
            case ClanRequestType.CancelApplication:
                break;
            case ClanRequestType.LeaveClan:
                request.ClanId = ClanDataRules.ReadClanId(package);
                break;
            case ClanRequestType.RequestHud:
                request.ClanId = ClanDataRules.ReadClanId(package);
                request.HudSelectionRevision = ClanDataRules.RequireOptionalRequestId(
                    package.ReadLong(),
                    "HUD selection revision");
                request.HudStateRevision = ClanDataRules.RequireOptionalRequestId(
                    package.ReadLong(),
                    "HUD state revision");
                break;
            case ClanRequestType.RequestDirectory:
                request.RequestId = ClanDataRules.RequireRequestId(package.ReadLong());
                break;
            case ClanRequestType.CreateClan:
                request.RequestId = ClanDataRules.RequireRequestId(package.ReadLong());
                request.ClanName = ClanDataRules.ReadClanName(package);
                request.Description = ClanDataRules.ReadClanDescription(package);
                request.EmblemKey = ClanDataRules.ReadClanEmblemKey(package);
                break;
            case ClanRequestType.Invite:
            case ClanRequestType.AcceptApplication:
            case ClanRequestType.RejectApplication:
            case ClanRequestType.KickPlayer:
            case ClanRequestType.TransferLeadership:
                request.ClanId = ClanDataRules.ReadClanId(package);
                request.TargetId = ClanDataRules.ReadPlayerKey(package, "target player key");
                break;
            case ClanRequestType.Apply:
                request.ClanId = ClanDataRules.ReadClanId(package);
                break;
            case ClanRequestType.AcceptInvite:
            case ClanRequestType.DeclineInvite:
                request.InviteId = ClanDataRules.ReadText(
                    package, ClanDataRules.MaxInviteIdLength, "invite id", allowEmpty: false);
                break;
            case ClanRequestType.SetRole:
                request.ClanId = ClanDataRules.ReadClanId(package);
                request.TargetId = ClanDataRules.ReadPlayerKey(package, "target player key");
                request.Role = ClanDataRules.ReadAssignableRole(package, "clan role");
                break;
            case ClanRequestType.UpdateClanProfile:
                request.RequestId = ClanDataRules.RequireRequestId(package.ReadLong());
                request.ClanId = ClanDataRules.ReadClanId(package);
                request.ClanName = ClanDataRules.ReadClanName(package);
                request.Description = ClanDataRules.ReadClanDescription(package);
                request.EmblemKey = ClanDataRules.ReadClanEmblemKey(package);
                break;
            case ClanRequestType.RenameClan:
                request.RequestId = ClanDataRules.RequireRequestId(package.ReadLong());
                request.ClanId = ClanDataRules.ReadClanId(package);
                request.ClanName = ClanDataRules.ReadClanName(package);
                break;
            case ClanRequestType.SendClanChat:
                request.ClanId = ClanDataRules.ReadClanId(package);
                request.Message = ClanDataRules.ReadText(
                    package, ClanDataRules.MaxChatMessageLength,
                    "clan chat message", allowEmpty: false);
                break;
            case ClanRequestType.SendClanPing:
            case ClanRequestType.UpdatePosition:
                request.ClanId = ClanDataRules.ReadClanId(package);
                request.Position = ClanDataRules.ReadFiniteVector(package, "position");
                break;
        }

        if (package.GetPos() != package.Size())
        {
            throw new InvalidDataException("Clan request contains unexpected trailing data.");
        }
        return request;
    }
}

internal sealed class ClanPlayerSummary
{
    public ClanPlayerRef Player;
    public ClanRole Role = ClanRole.Member;
    public bool IsSelf;
    public bool IsOnline;

    public string Id => Player.Id;
    public string Name => Player.Name;

    public void Write(ZPackage package)
    {
        if (!Player.IsValid)
        {
            throw new InvalidDataException("roster player identity is invalid.");
        }

        Player.Write(package);
        package.Write((int)ClanDataRules.RequireEnum(Role, "clan role"));
        package.Write(IsSelf);
        package.Write(IsOnline);
    }

    public static ClanPlayerSummary Read(ZPackage package) => new()
    {
        Player = ClanPlayerRef.Read(package),
        Role = ClanDataRules.ReadEnum<ClanRole>(package, "clan role"),
        IsSelf = package.ReadBool(),
        IsOnline = package.ReadBool()
    };
}

internal sealed class ClanHudPlayerSummary
{
    public string PlayerId = "";
    public bool HasHealth;
    public float CurrentHealth;
    public float MaxHealth;

    public void Write(ZPackage package)
    {
        ClanDataRules.WritePlayerKey(package, PlayerId, "HUD player key");
        package.Write(HasHealth);
        if (!HasHealth)
        {
            return;
        }

        ValidateHealth(CurrentHealth, MaxHealth);
        package.Write(CurrentHealth);
        package.Write(MaxHealth);
    }

    public static ClanHudPlayerSummary Read(ZPackage package)
    {
        ClanHudPlayerSummary player = new()
        {
            PlayerId = ClanDataRules.ReadPlayerKey(package, "HUD player key"),
            HasHealth = package.ReadBool()
        };
        if (player.HasHealth)
        {
            player.CurrentHealth = package.ReadSingle();
            player.MaxHealth = package.ReadSingle();
            ValidateHealth(player.CurrentHealth, player.MaxHealth);
        }
        return player;
    }

    private static void ValidateHealth(float currentHealth, float maxHealth)
    {
        if (float.IsNaN(currentHealth) ||
            float.IsInfinity(currentHealth) ||
            float.IsNaN(maxHealth) ||
            float.IsInfinity(maxHealth) ||
            maxHealth <= 0f ||
            maxHealth > ClanDataRules.MaxHudHealth ||
            currentHealth < 0f ||
            currentHealth > maxHealth)
        {
            throw new InvalidDataException("HUD health is outside the supported range.");
        }
    }
}

internal sealed class ClanHudSnapshot
{
    public string ClanId = "";
    public long SelectionRevision;
    public long StateRevision;
    public bool ReplaceSelection;
    public readonly List<ClanHudPlayerSummary> Players = new();

    public void Write(ZPackage package)
    {
        ClanDataRules.WriteOptionalClanId(package, ClanId, "HUD clan id");
        package.Write(ClanDataRules.RequireOptionalRequestId(
            SelectionRevision,
            "HUD selection revision"));
        package.Write(ClanDataRules.RequireOptionalRequestId(
            StateRevision,
            "HUD state revision"));
        package.Write(ReplaceSelection);
        ClanDataRules.RequireCount(Players.Count, ClanDataRules.MaxHudPlayers, "HUD player");
        package.Write(Players.Count);
        HashSet<string> playerIds = new(StringComparer.Ordinal);
        foreach (ClanHudPlayerSummary player in Players)
        {
            if (!playerIds.Add(player.PlayerId))
            {
                throw new InvalidDataException("HUD snapshot contains a duplicate player.");
            }
            player.Write(package);
        }
    }

    public static ClanHudSnapshot Read(ZPackage package)
    {
        ClanHudSnapshot snapshot = new()
        {
            ClanId = ClanDataRules.ReadOptionalClanId(package, "HUD clan id"),
            SelectionRevision = ClanDataRules.RequireOptionalRequestId(
                package.ReadLong(),
                "HUD selection revision"),
            StateRevision = ClanDataRules.RequireOptionalRequestId(
                package.ReadLong(),
                "HUD state revision"),
            ReplaceSelection = package.ReadBool()
        };
        int count = ClanDataRules.ReadCount(
            package,
            ClanDataRules.MaxHudPlayers,
            "HUD player");
        HashSet<string> playerIds = new(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            ClanHudPlayerSummary player = ClanHudPlayerSummary.Read(package);
            if (!playerIds.Add(player.PlayerId))
            {
                throw new InvalidDataException("HUD snapshot contains a duplicate player.");
            }
            snapshot.Players.Add(player);
        }
        return snapshot;
    }
}

internal sealed class ClanPublicSummary
{
    public string ClanId = "";
    public string Name = "";
    public string Description = "";
    public string EmblemKey = "";
    public string LeaderName = "";
    public int MemberCount;

    public void Write(ZPackage package)
    {
        ClanDataRules.WriteClanId(package, ClanId);
        ClanDataRules.WriteClanName(package, Name);
        ClanDataRules.WriteClanDescription(package, Description);
        ClanDataRules.WriteClanEmblemKey(package, EmblemKey);
        ClanDataRules.WritePlayerName(package, LeaderName, "clan leader name");
        package.Write(ClanDataRules.RequireRange(
            MemberCount, 0, ClanDataRules.MaxMembersPerClan, "clan member count"));
    }

    public static ClanPublicSummary Read(ZPackage package) => new()
    {
        ClanId = ClanDataRules.ReadClanId(package),
        Name = ClanDataRules.ReadClanName(package),
        Description = ClanDataRules.ReadClanDescription(package),
        EmblemKey = ClanDataRules.ReadClanEmblemKey(package),
        LeaderName = ClanDataRules.ReadPlayerName(package, "clan leader name"),
        MemberCount = ClanDataRules.RequireRange(
            package.ReadInt(), 0, ClanDataRules.MaxMembersPerClan, "clan member count")
    };
}

internal sealed class ClanApplicationSummary
{
    public string PlayerId = "";
    public string PlayerName = "";

    public void Write(ZPackage package)
    {
        ClanDataRules.WritePlayerKey(package, PlayerId, "applicant key");
        ClanDataRules.WritePlayerName(package, PlayerName, "applicant name");
    }

    public static ClanApplicationSummary Read(ZPackage package) => new()
    {
        PlayerId = ClanDataRules.ReadPlayerKey(package, "applicant key"),
        PlayerName = ClanDataRules.ReadPlayerName(package, "applicant name")
    };
}

internal sealed class ClanInviteSummary
{
    public string InviteId = "";
    public string ClanId = "";
    public string ClanName = "";
    public string FromName = "";

    public void Write(ZPackage package)
    {
        ClanDataRules.WriteText(
            package, InviteId, ClanDataRules.MaxInviteIdLength, "invite id", allowEmpty: false);
        ClanDataRules.WriteClanId(package, ClanId);
        ClanDataRules.WriteClanName(package, ClanName);
        ClanDataRules.WritePlayerName(package, FromName, "inviter name");
    }

    public static ClanInviteSummary Read(ZPackage package) => new()
    {
        InviteId = ClanDataRules.ReadText(
            package, ClanDataRules.MaxInviteIdLength, "invite id", allowEmpty: false),
        ClanId = ClanDataRules.ReadClanId(package),
        ClanName = ClanDataRules.ReadClanName(package),
        FromName = ClanDataRules.ReadPlayerName(package, "inviter name")
    };
}

internal sealed class ClanClientSnapshot
{
    public string Status = "";
    public long ResponseRequestId;
    public ClanOperationResultCode ResponseResultCode;
    public string ClanId = "";
    public string ClanName = "";
    public string ClanDescription = "";
    public string ClanEmblemKey = "";
    public ClanRole SelfRole = ClanRole.Member;
    public string PrimaryClanId = "";
    public string PrimaryClanName = "";
    public ClanRole PrimaryRole = ClanRole.Member;
    public string GuestClanId = "";
    public string GuestClanName = "";
    public readonly List<ClanPlayerSummary> Roster = new();
    public readonly List<ClanApplicationSummary> Applications = new();
    public ClanInviteSummary? Invite;
    public string OwnApplicationClanId = "";
    public string OwnApplicationClanName = "";

    public bool HasClan => !string.IsNullOrWhiteSpace(ClanId);
    public bool IsLeader =>
        HasClan &&
        SelfRole == ClanRole.Leader;
    public bool CanModerate =>
        HasClan &&
        SelfRole is ClanRole.Leader or ClanRole.Officer;
    public bool HasPrimaryClan => !string.IsNullOrWhiteSpace(PrimaryClanId);
    public bool HasGuestClan => !string.IsNullOrWhiteSpace(GuestClanId);
    public bool HasAnyClan => HasPrimaryClan || HasGuestClan;
    public bool HasOwnApplication => !string.IsNullOrWhiteSpace(OwnApplicationClanId);

    public bool IsConnectedToClan(string clanId)
    {
        return !string.IsNullOrWhiteSpace(clanId) &&
               (StringComparer.Ordinal.Equals(PrimaryClanId, clanId) ||
                StringComparer.Ordinal.Equals(GuestClanId, clanId));
    }

    public ClanRole? GetRoleForClan(string clanId)
    {
        if (!string.IsNullOrWhiteSpace(clanId) &&
            StringComparer.Ordinal.Equals(PrimaryClanId, clanId))
        {
            return PrimaryRole;
        }
        if (!string.IsNullOrWhiteSpace(clanId) &&
            StringComparer.Ordinal.Equals(GuestClanId, clanId))
        {
            return ClanRole.Guest;
        }
        return null;
    }

    public void Write(ZPackage package)
    {
        ClanDataRules.RequireCount(Roster.Count, ClanDataRules.MaxMembersPerClan, "roster");
        ClanDataRules.RequireCount(Applications.Count, ClanDataRules.MaxApplicationsPerClan, "application");

        ClanDataRules.WriteText(
            package, Status, ClanDataRules.MaxStatusLength, "snapshot status", allowLineBreaks: true);
        package.Write(ClanDataRules.RequireOptionalRequestId(ResponseRequestId));
        package.Write((int)ClanDataRules.RequireEnum(ResponseResultCode, "operation result"));
        ClanDataRules.WriteOptionalClanId(package, ClanId);
        ClanDataRules.WriteOptionalClanName(package, ClanName);
        ClanDataRules.WriteClanDescription(package, ClanDescription);
        ClanDataRules.WriteClanEmblemKey(package, ClanEmblemKey);
        package.Write((int)ClanDataRules.RequireEnum(SelfRole, "self role"));
        ClanDataRules.WriteOptionalClanId(package, PrimaryClanId, "primary clan id");
        ClanDataRules.WriteOptionalClanName(package, PrimaryClanName, "primary clan name");
        package.Write((int)ClanDataRules.RequireEnum(PrimaryRole, "primary clan role"));
        ClanDataRules.WriteOptionalClanId(package, GuestClanId, "guest clan id");
        ClanDataRules.WriteOptionalClanName(package, GuestClanName, "guest clan name");
        package.Write(Roster.Count);
        foreach (ClanPlayerSummary player in Roster)
        {
            player.Write(package);
        }

        package.Write(Applications.Count);
        foreach (ClanApplicationSummary application in Applications)
        {
            application.Write(package);
        }

        package.Write(Invite != null);
        Invite?.Write(package);
        package.Write(HasOwnApplication);
        if (HasOwnApplication)
        {
            ClanDataRules.WriteClanId(
                package,
                OwnApplicationClanId,
                "own application clan id");
            ClanDataRules.WriteClanName(
                package,
                OwnApplicationClanName,
                "own application clan name");
        }
    }

    public static ClanClientSnapshot Read(ZPackage package)
    {
        ClanClientSnapshot snapshot = new()
        {
            Status = ClanDataRules.ReadText(
                package, ClanDataRules.MaxStatusLength, "snapshot status", allowLineBreaks: true),
            ResponseRequestId = ClanDataRules.RequireOptionalRequestId(package.ReadLong()),
            ResponseResultCode = ClanDataRules.ReadEnum<ClanOperationResultCode>(
                package,
                "operation result"),
            ClanId = ClanDataRules.ReadOptionalClanId(package),
            ClanName = ClanDataRules.ReadOptionalClanName(package),
            ClanDescription = ClanDataRules.ReadClanDescription(package),
            ClanEmblemKey = ClanDataRules.ReadClanEmblemKey(package),
            SelfRole = ClanDataRules.ReadEnum<ClanRole>(package, "self role"),
            PrimaryClanId = ClanDataRules.ReadOptionalClanId(package, "primary clan id"),
            PrimaryClanName = ClanDataRules.ReadOptionalClanName(package, "primary clan name"),
            PrimaryRole = ClanDataRules.ReadEnum<ClanRole>(package, "primary clan role"),
            GuestClanId = ClanDataRules.ReadOptionalClanId(package, "guest clan id"),
            GuestClanName = ClanDataRules.ReadOptionalClanName(package, "guest clan name")
        };

        ReadList(
            package, snapshot.Roster, ClanDataRules.MaxMembersPerClan,
            "roster", ClanPlayerSummary.Read);
        ReadList(
            package, snapshot.Applications, ClanDataRules.MaxApplicationsPerClan,
            "application", ClanApplicationSummary.Read);
        if (package.ReadBool())
        {
            snapshot.Invite = ClanInviteSummary.Read(package);
        }
        if (package.ReadBool())
        {
            snapshot.OwnApplicationClanId = ClanDataRules.ReadClanId(
                package,
                "own application clan id");
            snapshot.OwnApplicationClanName = ClanDataRules.ReadClanName(
                package,
                "own application clan name");
        }
        return snapshot;
    }

    public bool ContainsClanPlayer(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return false;
        }

        foreach (ClanPlayerSummary member in Roster)
        {
            if (member.IsOnline &&
                StringComparer.Ordinal.Equals(member.Id, player.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static void ReadList<T>(
        ZPackage package,
        List<T> items,
        int maximum,
        string fieldName,
        Func<ZPackage, T> read)
    {
        int count = ClanDataRules.ReadCount(package, maximum, fieldName);
        for (int i = 0; i < count; i++)
        {
            items.Add(read(package));
        }
    }
}

internal sealed class ClanDirectoryPlayerSummary
{
    public string PlayerId = "";
    public string PlayerName = "";
    public ClanDirectoryPlayerState State;
    public string ClanName = "";
    public bool IsOnline;
    public bool IsSelf;
    public bool CanInvite;
    public bool CanResolveApplication;
    public long LastSeenUtcTicks;

    public void Write(ZPackage package)
    {
        ClanDataRules.WritePlayerKey(package, PlayerId, "directory player key");
        ClanDataRules.WritePlayerName(package, PlayerName, "directory player name");
        package.Write((int)ClanDataRules.RequireEnum(State, "directory player state"));
        ClanDataRules.WriteOptionalClanName(package, ClanName, "directory clan name");
        package.Write(IsOnline);
        package.Write(IsSelf);
        package.Write(CanInvite);
        package.Write(CanResolveApplication);
        if (LastSeenUtcTicks < 0L || LastSeenUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException("directory last-seen timestamp is invalid.");
        }
        package.Write(LastSeenUtcTicks);
    }

    public static ClanDirectoryPlayerSummary Read(ZPackage package)
    {
        ClanDirectoryPlayerSummary player = new()
        {
            PlayerId = ClanDataRules.ReadPlayerKey(package, "directory player key"),
            PlayerName = ClanDataRules.ReadPlayerName(package, "directory player name"),
            State = ClanDataRules.ReadEnum<ClanDirectoryPlayerState>(
                package,
                "directory player state"),
            ClanName = ClanDataRules.ReadOptionalClanName(package, "directory clan name"),
            IsOnline = package.ReadBool(),
            IsSelf = package.ReadBool(),
            CanInvite = package.ReadBool(),
            CanResolveApplication = package.ReadBool()
        };
        player.LastSeenUtcTicks = package.ReadLong();
        if (player.LastSeenUtcTicks < 0L || player.LastSeenUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException("directory last-seen timestamp is invalid.");
        }
        return player;
    }
}

internal sealed class ClanDirectorySnapshot
{
    public long RequestId;
    public ClanOperationResultCode ResultCode;
    public string Status = "";
    public bool IsTruncated;
    public readonly List<ClanPublicSummary> PublicClans = new();
    public readonly List<ClanDirectoryPlayerSummary> Players = new();

    internal void Write(ZPackage package, int publicClanCount, int playerCount)
    {
        publicClanCount = ClanDataRules.RequireRange(
            publicClanCount,
            0,
            Math.Min(PublicClans.Count, ClanDataRules.MaxClans),
            "directory clan count");
        playerCount = ClanDataRules.RequireRange(
            playerCount,
            0,
            Math.Min(Players.Count, ClanDataRules.MaxDirectoryPlayers),
            "directory player count");
        package.Write(ClanDataRules.RequireRequestId(RequestId));
        package.Write((int)ClanDataRules.RequireEnum(ResultCode, "directory result"));
        ClanDataRules.WriteText(
            package,
            Status,
            ClanDataRules.MaxStatusLength,
            "directory status",
            allowLineBreaks: true);
        package.Write(IsTruncated);
        package.Write(publicClanCount);
        for (int index = 0; index < publicClanCount; index++)
        {
            PublicClans[index].Write(package);
        }
        package.Write(playerCount);
        for (int index = 0; index < playerCount; index++)
        {
            Players[index].Write(package);
        }
    }

    public static ClanDirectorySnapshot Read(ZPackage package)
    {
        ClanDirectorySnapshot snapshot = new()
        {
            RequestId = ClanDataRules.RequireRequestId(package.ReadLong()),
            ResultCode = ClanDataRules.ReadEnum<ClanOperationResultCode>(
                package,
                "directory result"),
            Status = ClanDataRules.ReadText(
                package,
                ClanDataRules.MaxStatusLength,
                "directory status",
                allowLineBreaks: true),
            IsTruncated = package.ReadBool()
        };

        int clanCount = ClanDataRules.ReadCount(
            package,
            ClanDataRules.MaxClans,
            "directory clan");
        for (int index = 0; index < clanCount; index++)
        {
            snapshot.PublicClans.Add(ClanPublicSummary.Read(package));
        }

        int playerCount = ClanDataRules.ReadCount(
            package,
            ClanDataRules.MaxDirectoryPlayers,
            "directory player");
        for (int index = 0; index < playerCount; index++)
        {
            snapshot.Players.Add(ClanDirectoryPlayerSummary.Read(package));
        }
        return snapshot;
    }
}
