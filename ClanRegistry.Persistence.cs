using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.Serialization;

namespace Clan;

internal static partial class ClanRegistry
{
    private const int SaveFormatVersion = 6;
    private const int MaximumSaveBytes = 64 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .DisableAliases()
        .WithIndentedSequences()
        .Build();
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .Build();

    private static string? _loadedSaveFile;

    private static void EnsureLoaded()
    {
        string saveFile = ResolveSaveFile();
        if (string.Equals(_loadedSaveFile, saveFile, StringComparison.Ordinal))
        {
            return;
        }

        RegistryData loaded = Load(saveFile);
        SwapState(loaded);
        LastPositionUpdateByPlayer.Clear();
        AnnouncedOnlinePlayerIds.Clear();
        HudCacheByViewerId.Clear();
        _loadedSaveFile = saveFile;
    }

    private static string ResolveSaveFile()
    {
        return Path.Combine(ClanPlugin.DataDirectory, "clans.yml");
    }

    private static RegistryData Load(string saveFile)
    {
        string backupFile = saveFile + ".bak";
        if (TryLoadSave(
                saveFile,
                out _,
                out RegistryData? primary,
                out Exception? primaryError))
        {
            ClanPlugin.ClanLogger.LogInfo(
                $"Loaded {primary!.ClansById.Count} clans from {saveFile}.");
            return primary!;
        }

        if (primaryError == null)
        {
            if (!TryLoadSave(
                    backupFile,
                    out byte[]? backupBytes,
                    out RegistryData? recovered,
                    out Exception? backupError))
            {
                if (backupError != null)
                {
                    string quarantinedBackup = QuarantineUnsupportedSave(backupFile);
                    ClanPlugin.ClanLogger.LogWarning(
                        $"Clan save was missing and its backup was unsupported or invalid. " +
                        $"The backup was moved to {quarantinedBackup}; the clan registry will start empty: " +
                        backupError.Message);
                }

                return new RegistryData();
            }

            WriteAtomically(saveFile, backupBytes!);
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was missing. Restored {recovered!.ClansById.Count} clans from " +
                $"the validated backup {backupFile} to {saveFile}.");
            return recovered!;
        }

        byte[]? recoveryBytes = null;
        RegistryData? recovery = null;
        Exception? recoveryError = null;
        if (TryLoadSave(
                backupFile,
                out recoveryBytes,
                out recovery,
                out recoveryError))
        {
            string quarantinedPrimary = QuarantineUnsupportedSave(saveFile);
            WriteAtomically(saveFile, recoveryBytes!);
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was unsupported or invalid and was moved to {quarantinedPrimary}: " +
                $"{primaryError.Message} Restored {recovery!.ClansById.Count} clans from " +
                $"the validated same-version backup {backupFile}.");
            return recovery!;
        }

        string quarantinedPrimaryWithoutRecovery = QuarantineUnsupportedSave(saveFile);
        if (recoveryError != null)
        {
            string quarantinedBackup = QuarantineUnsupportedSave(backupFile);
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was unsupported or invalid and was moved to {quarantinedPrimaryWithoutRecovery}: " +
                $"{primaryError.Message} Its backup was also unsupported or invalid and was moved " +
                $"to {quarantinedBackup}: {recoveryError.Message} The clan registry will start empty.");
        }
        else
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was unsupported or invalid and was moved to {quarantinedPrimaryWithoutRecovery}: " +
                $"{primaryError.Message} No backup was available; the clan registry will start empty.");
        }

        return new RegistryData();
    }

    private static bool TryLoadSave(
        string saveFile,
        out byte[]? bytes,
        out RegistryData? data,
        out Exception? invalidContent)
    {
        bytes = null;
        data = null;
        invalidContent = null;
        try
        {
            bytes = ReadSaveBytes(saveFile);
            data = ParseSave(bytes);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (IsInvalidSaveContent(error))
        {
            invalidContent = error;
            return false;
        }
    }

    private static bool IsInvalidSaveContent(Exception error)
    {
        return error is InvalidDataException or
            DecoderFallbackException or
            YamlDotNet.Core.YamlException;
    }

    private static RegistryData ParseSave(byte[] bytes)
    {
        string yaml = DecodeStrictUtf8(bytes);
        YamlRegistryDocument? document = YamlDeserializer.Deserialize<YamlRegistryDocument>(yaml);
        if (document == null)
        {
            throw new InvalidDataException("Clan save is empty.");
        }
        if (document.FormatVersion != SaveFormatVersion)
        {
            throw new InvalidDataException(
                $"Save format version {document.FormatVersion} is unsupported; " +
                $"only version {SaveFormatVersion} is accepted.");
        }
        if (document.Clans == null)
        {
            throw new InvalidDataException("Clan save is missing the clans sequence.");
        }
        if (document.PendingInvites == null)
        {
            throw new InvalidDataException("Clan save is missing the pending_invites sequence.");
        }

        RegistryData data = new();
        HashSet<long> creationOrders = new();
        HashSet<string> applicantIds = new(StringComparer.Ordinal);
        Dictionary<string, ClanState> applicationClanByPlayer = new(StringComparer.Ordinal);
        ClanDataRules.RequireCount(document.Clans.Count, ClanDataRules.MaxClans, "clan");
        foreach (YamlClanDto? serializedClan in document.Clans)
        {
            ClanState clan = ReadClan(serializedClan);
            if (!creationOrders.Add(clan.CreationOrder))
            {
                throw new InvalidDataException(
                    $"Duplicate clan creation order '{clan.CreationOrder}'.");
            }
            if (data.ClansById.ContainsKey(clan.ClanId))
            {
                throw new InvalidDataException($"Duplicate clan id '{clan.ClanId}'.");
            }
            if (data.ClansByName.ContainsKey(clan.Name))
            {
                throw new InvalidDataException($"Duplicate clan name '{clan.Name}'.");
            }
            data.ClansById.Add(clan.ClanId, clan);
            data.ClansByName.Add(clan.Name, clan);

            foreach (KeyValuePair<string, ClanMember> memberPair in clan.Members)
            {
                Dictionary<string, ClanState> index = memberPair.Value.Role == ClanRole.Guest
                    ? data.GuestClanByPlayerId
                    : data.PrimaryClanByPlayerId;
                if (index.ContainsKey(memberPair.Key))
                {
                    string slotName = memberPair.Value.Role == ClanRole.Guest
                        ? "Guest"
                        : "primary";
                    throw new InvalidDataException(
                        $"Player '{memberPair.Key}' belongs to more than one {slotName} clan.");
                }
                index.Add(memberPair.Key, clan);
            }

            foreach (string applicantId in clan.Applications.Keys)
            {
                if (!applicantIds.Add(applicantId))
                {
                    throw new InvalidDataException($"Player '{applicantId}' has more than one application.");
                }
                applicationClanByPlayer.Add(applicantId, clan);
            }
        }

        foreach (string applicantId in applicantIds)
        {
            ClanState applicationClan = applicationClanByPlayer[applicantId];
            if (data.GuestClanByPlayerId.ContainsKey(applicantId))
            {
                throw new InvalidDataException(
                    $"Player '{applicantId}' cannot have a Guest clan and a pending application.");
            }
            if (applicationClan.Members.ContainsKey(applicantId))
            {
                throw new InvalidDataException(
                    $"Player '{applicantId}' cannot apply to a clan they already belong to.");
            }
        }

        ClanDataRules.RequireCount(
            document.PendingInvites.Count,
            ClanDataRules.MaxInvites,
            "invite");
        HashSet<string> inviteIds = new(StringComparer.Ordinal);
        foreach (YamlInviteDto? serializedInvite in document.PendingInvites)
        {
            ClanInvite invite = ReadInvite(serializedInvite);
            if (!inviteIds.Add(invite.InviteId))
            {
                throw new InvalidDataException($"Duplicate invite id '{invite.InviteId}'.");
            }

            if (!data.ClansById.TryGetValue(invite.ClanId, out ClanState inviteClan))
            {
                throw new InvalidDataException($"Invite references missing clan '{invite.ClanId}'.");
            }

            if (data.GuestClanByPlayerId.ContainsKey(invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Invite target '{invite.Target.Id}' already has a Guest clan.");
            }
            if (inviteClan.Members.ContainsKey(invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Invite target '{invite.Target.Id}' already belongs to the inviting clan.");
            }

            if (data.PendingInvitesByTarget.ContainsKey(invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Player '{invite.Target.Id}' has more than one pending invite.");
            }
            data.PendingInvitesByTarget.Add(invite.Target.Id, invite);
        }

        return data;
    }

    private static string DecodeStrictUtf8(byte[] bytes)
    {
        int offset = bytes.Length >= 3 &&
                     bytes[0] == 0xEF &&
                     bytes[1] == 0xBB &&
                     bytes[2] == 0xBF
            ? 3
            : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static ClanState ReadClan(YamlClanDto? source)
    {
        if (source == null)
        {
            throw new InvalidDataException("Clan save contains a null clan entry.");
        }

        ClanState clan = new(ClanDataRules.RequireClanId(source.ClanId))
        {
            CreationOrder = RequireClanCreationOrder(source.CreationOrder),
            Name = ClanDataRules.RequireClanName(source.Name),
            Description = ClanDataRules.RequireClanDescription(source.Description),
            EmblemKey = ClanDataRules.RequireClanEmblemKey(source.EmblemKey)
        };

        if (source.Members == null)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' is missing the members sequence.");
        }
        ClanDataRules.RequireCount(
            source.Members.Count,
            ClanDataRules.MaxMembersPerClan,
            "clan member");
        if (source.Members.Count == 0)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' has no members.");
        }

        int leaderCount = 0;
        foreach (YamlMemberDto? serializedMember in source.Members)
        {
            if (serializedMember == null)
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains a null member entry.");
            }

            ClanPlayerRef player = ReadPlayer(serializedMember.Player, "clan member");
            ClanRole role = ParseRole(serializedMember.Role, "clan role");
            if (role == ClanRole.Leader)
            {
                leaderCount++;
            }

            if (clan.Members.ContainsKey(player.Id))
            {
                throw new InvalidDataException($"Duplicate member '{player.Id}' in clan '{clan.Name}'.");
            }
            clan.Members.Add(player.Id, new ClanMember
            {
                Player = player,
                Role = role
            });
        }

        if (leaderCount != 1)
        {
            throw new InvalidDataException(
                $"Clan '{clan.Name}' must have exactly one leader; found {leaderCount}.");
        }

        if (source.Applications == null)
        {
            throw new InvalidDataException(
                $"Clan '{clan.Name}' is missing the applications sequence.");
        }
        ClanDataRules.RequireCount(
            source.Applications.Count,
            ClanDataRules.MaxApplicationsPerClan,
            "application");
        foreach (YamlApplicationDto? serializedApplication in source.Applications)
        {
            if (serializedApplication == null)
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains a null application entry.");
            }

            ClanPlayerRef applicant = ReadPlayer(
                serializedApplication.Player,
                "application player");
            if (clan.Applications.ContainsKey(applicant.Id))
            {
                throw new InvalidDataException(
                    $"Duplicate application from '{applicant.Id}' in clan '{clan.Name}'.");
            }
            clan.Applications.Add(applicant.Id, applicant);
        }

        return clan;
    }

    private static ClanInvite ReadInvite(YamlInviteDto? source)
    {
        if (source == null)
        {
            throw new InvalidDataException("Clan save contains a null invite entry.");
        }

        return new ClanInvite
        {
            InviteId = ClanDataRules.RequireText(
                source.InviteId,
                ClanDataRules.MaxInviteIdLength,
                "invite id",
                allowEmpty: false),
            ClanId = ClanDataRules.RequireClanId(source.ClanId, "invite clan id"),
            FromName = ClanDataRules.RequireText(
                source.FromName,
                ClanDataRules.MaxPlayerNameLength,
                "invite sender name"),
            Target = ReadPlayer(source.Target, "invite target")
        };
    }

    private static ClanPlayerRef ReadPlayer(YamlPlayerDto? source, string fieldName)
    {
        if (source == null)
        {
            throw new InvalidDataException($"{fieldName} is missing.");
        }

        string platformId = ClanDataRules.RequirePlatformId(
            source.PlatformId,
            fieldName + " platform id");
        long playerId = ClanDataRules.RequireCharacterPlayerId(
            source.CharacterPlayerId,
            fieldName + " character player id");
        string name = ClanDataRules.RequireText(
            source.Name,
            ClanDataRules.MaxPlayerNameLength,
            fieldName + " name");
        ClanPlayerRef player = new(platformId, playerId, name);
        if (!player.IsValid)
        {
            throw new InvalidDataException($"{fieldName} identity is invalid.");
        }
        return player;
    }

    private static ClanRole ParseRole(string? value, string fieldName)
    {
        string name = ClanDataRules.RequireText(
            value,
            16,
            fieldName,
            allowEmpty: false);
        return name switch
        {
            "leader" => ClanRole.Leader,
            "officer" => ClanRole.Officer,
            "member" => ClanRole.Member,
            "guest" => ClanRole.Guest,
            _ => throw new InvalidDataException($"{fieldName} has unknown value '{name}'.")
        };
    }

    private static ClanState CommitClanProfile(
        ClanState clan,
        string clanName,
        string description,
        string emblemKey)
    {
        if (!ClansById.TryGetValue(clan.ClanId, out ClanState indexedClan) ||
            !ReferenceEquals(indexedClan, clan) ||
            !ClansByName.TryGetValue(clan.Name, out ClanState namedClan) ||
            !ReferenceEquals(namedClan, clan))
        {
            throw new InvalidOperationException(
                $"Clan '{clan.ClanId}' is inconsistent with the registry indexes.");
        }

        string saveFile = ResolveSaveFile();
        if (!string.Equals(_loadedSaveFile, saveFile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The clan registry storage changed while clan state was being updated.");
        }

        ValidateClanIndexes();
        YamlRegistryDocument document = CreateSaveDocument();
        YamlClanDto? target = document.Clans?
            .SingleOrDefault(candidate =>
                candidate != null &&
                StringComparer.Ordinal.Equals(candidate.ClanId, clan.ClanId));
        if (target == null)
        {
            throw new InvalidOperationException(
                $"Clan '{clan.ClanId}' is missing from the save candidate.");
        }

        target.Name = ClanDataRules.RequireClanName(clanName);
        target.Description = ClanDataRules.RequireClanDescription(description);
        target.EmblemKey = ClanDataRules.RequireClanEmblemKey(emblemKey);

        byte[] bytes = SerializeSave(document);
        if (bytes.Length > MaximumSaveBytes)
        {
            throw new InvalidDataException(
                $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
        }
        RegistryData candidate = ParseSave(bytes);
        PreserveRuntimeMemberState(candidate);

        try
        {
            WriteAtomically(saveFile, bytes);
        }
        catch
        {
            RecoverPersistedStateAfterSaveFailure();
            throw;
        }

        SwapState(candidate);
        _directoryInvalidationPending = true;
        ClanApi.NotifyRegistryChanged();
        return ClansById[clan.ClanId];
    }

    private static void Save(bool wardAuthorizationChanged = false)
    {
        try
        {
            string saveFile = ResolveSaveFile();
            if (!string.Equals(_loadedSaveFile, saveFile, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The clan registry storage changed while clan state was being updated.");
            }

            ValidateClanIndexes();
            ClanDataRules.RequireCount(ClansById.Count, ClanDataRules.MaxClans, "clan");
            ClanDataRules.RequireCount(
                PendingInvitesByTarget.Count,
                ClanDataRules.MaxInvites,
                "invite");

            YamlRegistryDocument document = CreateSaveDocument();
            byte[] bytes = SerializeSave(document);
            if (bytes.Length > MaximumSaveBytes)
            {
                throw new InvalidDataException(
                    $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
            }

            // Never replace a known-good save with output that this exact reader cannot consume.
            _ = ParseSave(bytes);
            WriteAtomically(saveFile, bytes);
            _directoryInvalidationPending = true;
        }
        catch
        {
            RecoverPersistedStateAfterSaveFailure();
            throw;
        }

        if (wardAuthorizationChanged)
        {
            ClanApi.NotifyRegistryChanged();
        }
    }

    private static void RecoverPersistedStateAfterSaveFailure()
    {
        if (RestorePersistedState())
        {
            return;
        }

        _loadedSaveFile = null;
        LastPositionUpdateByPlayer.Clear();
        SwapState(new RegistryData());
        ClanPlugin.ClanLogger.LogError(
            "Clan registry was invalidated after both saving and restoring the persisted " +
            "state failed. The next registry access must reload clans.yml.");
    }

    private static void PreserveRuntimeMemberState(RegistryData candidate)
    {
        foreach (KeyValuePair<string, ClanState> clanPair in ClansById)
        {
            if (!candidate.ClansById.TryGetValue(clanPair.Key, out ClanState candidateClan))
            {
                continue;
            }

            foreach (KeyValuePair<string, ClanMember> memberPair in clanPair.Value.Members)
            {
                if (!candidateClan.Members.TryGetValue(
                        memberPair.Key,
                        out ClanMember candidateMember))
                {
                    continue;
                }

                candidateMember.LastClanChatTime = memberPair.Value.LastClanChatTime;
                candidateMember.LastClanPingTime = memberPair.Value.LastClanPingTime;
            }
        }
    }

    private static void ValidateClanIndexes()
    {
        if (ClansById.Count != ClansByName.Count)
        {
            throw new InvalidDataException("Clan id and name indexes contain different counts.");
        }

        foreach (KeyValuePair<string, ClanState> namePair in ClansByName)
        {
            string canonicalName = ClanDataRules.RequireClanName(namePair.Key);
            if (!StringComparer.Ordinal.Equals(namePair.Key, canonicalName) ||
                !StringComparer.Ordinal.Equals(namePair.Value.Name, namePair.Key) ||
                !ClansById.TryGetValue(namePair.Value.ClanId, out ClanState idClan) ||
                !ReferenceEquals(idClan, namePair.Value))
            {
                throw new InvalidDataException(
                    $"Clan name index '{namePair.Key}' is not canonical or points to the wrong clan.");
            }
        }

        int primaryMemberCount = 0;
        int guestMemberCount = 0;
        HashSet<long> creationOrders = new();
        HashSet<string> primaryMemberIds = new(StringComparer.Ordinal);
        HashSet<string> guestMemberIds = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ClanState> pair in ClansById)
        {
            ClanState clan = pair.Value;
            long creationOrder = RequireClanCreationOrder(clan.CreationOrder);
            if (!creationOrders.Add(creationOrder))
            {
                throw new InvalidDataException(
                    $"Clan '{pair.Key}' has duplicate creation order '{creationOrder}'.");
            }
            if (!StringComparer.Ordinal.Equals(pair.Key, clan.ClanId) ||
                !ClansByName.TryGetValue(clan.Name, out ClanState namedClan) ||
                !ReferenceEquals(namedClan, clan))
            {
                throw new InvalidDataException(
                    $"Clan '{pair.Key}' is inconsistent with the registry indexes.");
            }

            foreach (KeyValuePair<string, ClanMember> memberPair in clan.Members)
            {
                ClanMember member = memberPair.Value;
                if (member == null ||
                    !member.Player.IsValid ||
                    !StringComparer.Ordinal.Equals(memberPair.Key, member.Player.Id))
                {
                    throw new InvalidDataException(
                        $"Clan '{pair.Key}' has an inconsistent member index for '{memberPair.Key}'.");
                }

                ClanRole role = ClanDataRules.RequireEnum(member.Role, "clan role");
                if (role == ClanRole.Guest)
                {
                    if (!guestMemberIds.Add(memberPair.Key) ||
                        !GuestClanByPlayerId.TryGetValue(
                            memberPair.Key,
                            out ClanState indexedGuestClan) ||
                        !ReferenceEquals(indexedGuestClan, clan))
                    {
                        throw new InvalidDataException(
                            $"Clan '{pair.Key}' has an inconsistent Guest index for '{memberPair.Key}'.");
                    }
                    guestMemberCount++;
                }
                else
                {
                    if (!primaryMemberIds.Add(memberPair.Key) ||
                        !PrimaryClanByPlayerId.TryGetValue(
                            memberPair.Key,
                            out ClanState indexedPrimaryClan) ||
                        !ReferenceEquals(indexedPrimaryClan, clan))
                    {
                        throw new InvalidDataException(
                            $"Clan '{pair.Key}' has an inconsistent primary index for '{memberPair.Key}'.");
                    }
                    primaryMemberCount++;
                }
            }
        }

        if (PrimaryClanByPlayerId.Count != primaryMemberCount ||
            GuestClanByPlayerId.Count != guestMemberCount)
        {
            throw new InvalidDataException(
                "The role-aware player-to-clan indexes contain a different number of members than the clans.");
        }
    }

    private static bool RestorePersistedState()
    {
        string? loadedSaveFile = _loadedSaveFile;
        if (loadedSaveFile == null || string.IsNullOrWhiteSpace(loadedSaveFile))
        {
            return false;
        }

        try
        {
            RegistryData persisted = Load(loadedSaveFile);
            SwapState(persisted);
            LastPositionUpdateByPlayer.Clear();
            return true;
        }
        catch (Exception restoreError)
        {
            ClanPlugin.ClanLogger.LogError(
                $"Failed to restore clan state after a save error: {restoreError}");
            return false;
        }
    }

    private static YamlRegistryDocument CreateSaveDocument()
    {
        List<YamlClanDto?> clans = new();
        List<YamlInviteDto?> pendingInvites = new();
        YamlRegistryDocument document = new()
        {
            FormatVersion = SaveFormatVersion,
            Clans = clans,
            PendingInvites = pendingInvites
        };

        foreach (ClanState clan in ClansById.Values.OrderBy(
                     clan => clan.ClanId,
                     StringComparer.Ordinal))
        {
            clans.Add(CreateClanDto(clan));
        }

        foreach (KeyValuePair<string, ClanInvite> pair in PendingInvitesByTarget.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            ClanInvite invite = pair.Value;
            if (invite == null ||
                !invite.Target.IsValid ||
                !StringComparer.Ordinal.Equals(pair.Key, invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Pending invite target index '{pair.Key}' is inconsistent.");
            }
            pendingInvites.Add(CreateInviteDto(invite));
        }

        return document;
    }

    private static byte[] SerializeSave(YamlRegistryDocument document)
    {
        string yaml = YamlSerializer.Serialize(document)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        if (!yaml.EndsWith("\n", StringComparison.Ordinal))
        {
            yaml += "\n";
        }
        return StrictUtf8.GetBytes(yaml);
    }

    private static YamlClanDto CreateClanDto(ClanState clan)
    {
        ClanDataRules.RequireCount(
            clan.Members.Count,
            ClanDataRules.MaxMembersPerClan,
            "clan member");
        ClanDataRules.RequireCount(
            clan.Applications.Count,
            ClanDataRules.MaxApplicationsPerClan,
            "application");
        if (clan.Members.Count == 0)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' has no members.");
        }

        List<YamlMemberDto?> members = new();
        List<YamlApplicationDto?> applications = new();
        YamlClanDto serialized = new()
        {
            ClanId = ClanDataRules.RequireClanId(clan.ClanId),
            CreationOrder = RequireClanCreationOrder(clan.CreationOrder),
            Name = ClanDataRules.RequireClanName(clan.Name),
            Description = ClanDataRules.RequireClanDescription(clan.Description),
            EmblemKey = ClanDataRules.RequireClanEmblemKey(clan.EmblemKey),
            Members = members,
            Applications = applications
        };

        int leaderCount = 0;
        foreach (KeyValuePair<string, ClanMember> pair in clan.Members.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            ClanMember member = pair.Value;
            if (member == null ||
                !member.Player.IsValid ||
                !StringComparer.Ordinal.Equals(pair.Key, member.Player.Id))
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains an inconsistent member '{pair.Key}'.");
            }

            ClanRole role = ClanDataRules.RequireEnum(member.Role, "clan role");
            if (role == ClanRole.Leader)
            {
                leaderCount++;
            }

            members.Add(new YamlMemberDto
            {
                Player = CreatePlayerDto(member.Player, "clan member"),
                Role = FormatRole(role)
            });
        }
        if (leaderCount != 1)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' must have exactly one leader.");
        }

        foreach (KeyValuePair<string, ClanPlayerRef> pair in clan.Applications.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            ClanPlayerRef applicant = pair.Value;
            if (!applicant.IsValid ||
                !StringComparer.Ordinal.Equals(pair.Key, applicant.Id))
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains an inconsistent application '{pair.Key}'.");
            }

            applications.Add(new YamlApplicationDto
            {
                Player = CreatePlayerDto(applicant, "application player")
            });
        }

        return serialized;
    }

    private static YamlInviteDto CreateInviteDto(ClanInvite invite)
    {
        return new YamlInviteDto
        {
            InviteId = ClanDataRules.RequireText(
                invite.InviteId,
                ClanDataRules.MaxInviteIdLength,
                "invite id",
                allowEmpty: false,
                allowLineBreaks: false),
            ClanId = ClanDataRules.RequireClanId(invite.ClanId, "invite clan id"),
            FromName = ClanDataRules.RequireText(
                invite.FromName,
                ClanDataRules.MaxPlayerNameLength,
                "invite sender name"),
            Target = CreatePlayerDto(invite.Target, "invite target")
        };
    }

    private static YamlPlayerDto CreatePlayerDto(ClanPlayerRef player, string fieldName)
    {
        if (!player.IsValid)
        {
            throw new InvalidDataException($"{fieldName} identity is invalid.");
        }

        return new YamlPlayerDto
        {
            PlatformId = ClanDataRules.RequirePlatformId(
                player.PlatformId,
                fieldName + " platform id"),
            CharacterPlayerId = ClanDataRules.RequireCharacterPlayerId(
                player.CharacterPlayerId,
                fieldName + " character player id"),
            Name = ClanDataRules.RequireText(
                player.Name,
                ClanDataRules.MaxPlayerNameLength,
                fieldName + " name")
        };
    }

    private static string FormatRole(ClanRole role)
    {
        return ClanDataRules.RequireEnum(role, "clan role") switch
        {
            ClanRole.Leader => "leader",
            ClanRole.Officer => "officer",
            ClanRole.Member => "member",
            ClanRole.Guest => "guest",
            _ => throw new InvalidDataException("Clan role is unsupported.")
        };
    }

    private static void WriteAtomically(string saveFile, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(saveFile);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Clan save directory is invalid.");
        }

        Directory.CreateDirectory(directory);
        string tempFile = saveFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(
                       tempFile,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(saveFile))
            {
                string backupFile = saveFile + ".bak";
                File.Replace(tempFile, saveFile, backupFile, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempFile, saveFile);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception cleanupError)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Failed to clean temporary clan save '{tempFile}': {cleanupError.Message}");
            }

            throw;
        }
    }

    private static byte[] ReadSaveBytes(string saveFile)
    {
        FileInfo info = new(saveFile);
        if (info.Length < 0L || info.Length > MaximumSaveBytes)
        {
            throw new InvalidDataException(
                $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
        }

        byte[] bytes = File.ReadAllBytes(saveFile);
        if (bytes.Length > MaximumSaveBytes)
        {
            throw new InvalidDataException(
                $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
        }

        return bytes;
    }

    private static string QuarantineUnsupportedSave(string saveFile)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string quarantineFile = saveFile +
                                    ".unsupported-or-corrupt-" +
                                    timestamp +
                                    "-" +
                                    Guid.NewGuid().ToString("N");
            if (File.Exists(quarantineFile))
            {
                continue;
            }

            try
            {
                File.Move(saveFile, quarantineFile);
                return quarantineFile;
            }
            catch (IOException) when (File.Exists(saveFile) && File.Exists(quarantineFile))
            {
                // Another writer claimed this generated destination. Retry with a new unique name.
            }
        }

        throw new IOException($"Could not allocate a unique quarantine file for '{saveFile}'.");
    }

    private sealed class YamlRegistryDocument
    {
        public YamlRegistryDocument()
        {
        }

        [YamlMember(Alias = "format_version", ApplyNamingConventions = false, Order = 1)]
        public int FormatVersion { get; set; }

        [YamlMember(Alias = "clans", ApplyNamingConventions = false, Order = 2)]
        public List<YamlClanDto?>? Clans { get; set; }

        [YamlMember(Alias = "pending_invites", ApplyNamingConventions = false, Order = 3)]
        public List<YamlInviteDto?>? PendingInvites { get; set; }
    }

    private sealed class YamlClanDto
    {
        public YamlClanDto()
        {
        }

        [YamlMember(Alias = "clan_id", ApplyNamingConventions = false, Order = 1)]
        public string? ClanId { get; set; }

        [YamlMember(Alias = "creation_order", ApplyNamingConventions = false, Order = 2)]
        public long CreationOrder { get; set; }

        [YamlMember(Alias = "name", ApplyNamingConventions = false, Order = 3)]
        public string? Name { get; set; }

        [YamlMember(Alias = "description", ApplyNamingConventions = false, Order = 4)]
        public string? Description { get; set; }

        [YamlMember(Alias = "emblem_key", ApplyNamingConventions = false, Order = 5)]
        public string? EmblemKey { get; set; }

        [YamlMember(Alias = "members", ApplyNamingConventions = false, Order = 6)]
        public List<YamlMemberDto?>? Members { get; set; }

        [YamlMember(Alias = "applications", ApplyNamingConventions = false, Order = 7)]
        public List<YamlApplicationDto?>? Applications { get; set; }
    }

    private sealed class YamlMemberDto
    {
        public YamlMemberDto()
        {
        }

        [YamlMember(Alias = "player", ApplyNamingConventions = false, Order = 1)]
        public YamlPlayerDto? Player { get; set; }

        [YamlMember(Alias = "role", ApplyNamingConventions = false, Order = 2)]
        public string? Role { get; set; }
    }

    private sealed class YamlApplicationDto
    {
        public YamlApplicationDto()
        {
        }

        [YamlMember(Alias = "player", ApplyNamingConventions = false, Order = 1)]
        public YamlPlayerDto? Player { get; set; }
    }

    private sealed class YamlInviteDto
    {
        public YamlInviteDto()
        {
        }

        [YamlMember(Alias = "invite_id", ApplyNamingConventions = false, Order = 1)]
        public string? InviteId { get; set; }

        [YamlMember(Alias = "clan_id", ApplyNamingConventions = false, Order = 2)]
        public string? ClanId { get; set; }

        [YamlMember(Alias = "from_name", ApplyNamingConventions = false, Order = 3)]
        public string? FromName { get; set; }

        [YamlMember(Alias = "target", ApplyNamingConventions = false, Order = 4)]
        public YamlPlayerDto? Target { get; set; }
    }

    private sealed class YamlPlayerDto
    {
        public YamlPlayerDto()
        {
        }

        [YamlMember(Alias = "platform_id", ApplyNamingConventions = false, Order = 1)]
        public string? PlatformId { get; set; }

        [YamlMember(Alias = "player_id", ApplyNamingConventions = false, Order = 2)]
        public long CharacterPlayerId { get; set; }

        [YamlMember(Alias = "name", ApplyNamingConventions = false, Order = 3)]
        public string? Name { get; set; }
    }

}
