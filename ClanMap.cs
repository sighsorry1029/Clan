using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Splatform;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Clan;

internal static class ClanMap
{
    private const float PositionSendInterval = 2f;
    private const float PositionMoveThreshold = 1f;
    private const float PositionHeartbeatInterval = 10f;
    private const string ClanPingHintName = "ClanPing";
    private const string PingHintName = "PingPanel";

    private static ConditionalWeakTable<Chat.WorldTextInstance, object> ClanPingTexts = new();
    private static readonly Dictionary<string, Vector3> ForcedPositions =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> ClanPlayerIds = new(StringComparer.Ordinal);
    private static readonly List<string> StalePositionIds = new();
    private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>>? _pingPinsField =
        CreateMinimapFieldAccessor<List<Minimap.PinData>>("m_pingPins");
    private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>>? _playerPinsField =
        CreateMinimapFieldAccessor<List<Minimap.PinData>>("m_playerPins");
    private static AccessTools.FieldRef<Minimap, List<Chat.WorldTextInstance>>?
        _tempShoutsField =
            CreateMinimapFieldAccessor<List<Chat.WorldTextInstance>>("m_tempShouts");
    private static AccessTools.FieldRef<Minimap, List<ZNet.PlayerInfo>>?
        _tempPlayerInfoField =
            CreateMinimapFieldAccessor<List<ZNet.PlayerInfo>>("m_tempPlayerInfo");

    private static Sprite? _clanPlayerIcon;
    private static Sprite? _clanPingIcon;
    private static Minimap? _clanPingHintOwner;
    private static GameObject? _clanPingHint;
    private static TMP_Text? _clanPingHintLabel;
    private static float _nextPositionSendTime;
    private static Vector3 _lastSentPosition = Vector3.positiveInfinity;
    private static float _lastPositionSentTime = float.NegativeInfinity;
    private static string _effectiveClanId = "";
    private static bool _positionSharingActive;

    private static readonly Color ClanPingColor = new(1f, 0.78f, 0.25f, 1f);

    private static AccessTools.FieldRef<Minimap, T>? CreateMinimapFieldAccessor<T>(
        string fieldName)
        where T : class
    {
        try
        {
            return AccessTools.FieldRefAccess<Minimap, T>(fieldName);
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan map integration for {fieldName} is unavailable: {ex.Message}");
            return null;
        }
    }

    private static bool TryReadMinimapField<T>(
        Minimap minimap,
        ref AccessTools.FieldRef<Minimap, T>? accessor,
        string fieldName,
        out T value)
        where T : class
    {
        value = null!;
        AccessTools.FieldRef<Minimap, T>? current = accessor;
        if (current == null)
        {
            return false;
        }

        try
        {
            value = current(minimap);
            if (value != null)
            {
                return true;
            }

            accessor = null;
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan map integration for {fieldName} was disabled because the field was null.");
            return false;
        }
        catch (Exception ex)
        {
            accessor = null;
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan map integration for {fieldName} was disabled: {ex.Message}");
            return false;
        }
    }

    public static void ResetSession()
    {
        ForcedPositions.Clear();
        ClanPlayerIds.Clear();
        RestoreClanIcons();
        DestroyClanPingHint();
        DestroyGeneratedSprite(ref _clanPlayerIcon);
        DestroyGeneratedSprite(ref _clanPingIcon);
        ClanPingTexts = new ConditionalWeakTable<Chat.WorldTextInstance, object>();
        _effectiveClanId = "";
        _positionSharingActive = false;
        ResetPositionTimer();
    }

    public static void OnSnapshotChanged(ClanClientSnapshot snapshot)
    {
        ClanPlayerIds.Clear();
        foreach (ClanPlayerSummary member in snapshot.Roster)
        {
            if (member.IsOnline)
            {
                ClanPlayerIds.Add(member.Id);
            }
        }

        bool clanChanged = !string.Equals(_effectiveClanId, snapshot.ClanId, StringComparison.Ordinal);
        if (!ClanPlugin.ShareClanPositions.Value.IsOn() || !snapshot.HasClan || clanChanged)
        {
            ForcedPositions.Clear();
            if (clanChanged)
            {
                RestoreClanPlayerIcons();
            }
            ResetPositionTimer();
        }

        _effectiveClanId = snapshot.HasClan ? snapshot.ClanId : "";
        if (!ClanPlugin.ShareClanPositions.Value.IsOn() || !snapshot.HasClan)
        {
            RestoreClanPlayerIcons();
            _positionSharingActive = false;
            return;
        }

        _positionSharingActive = true;
        StalePositionIds.Clear();
        foreach (string playerId in ForcedPositions.Keys)
        {
            if (!ClanPlayerIds.Contains(playerId))
            {
                StalePositionIds.Add(playerId);
            }
        }

        foreach (string playerId in StalePositionIds)
        {
            ForcedPositions.Remove(playerId);
        }
        StalePositionIds.Clear();
    }

    public static void Tick()
    {
        UpdateClanPingHint();

        if (!ClanPlugin.ShareClanPositions.Value.IsOn() ||
            !ClanRpc.CurrentSnapshot.HasClan ||
            Player.m_localPlayer == null)
        {
            if (ForcedPositions.Count != 0)
            {
                ForcedPositions.Clear();
            }
            if (_positionSharingActive)
            {
                RestoreClanPlayerIcons();
            }
            _positionSharingActive = false;
            ResetPositionTimer();
            return;
        }

        _positionSharingActive = true;
        if (Time.time < _nextPositionSendTime)
        {
            return;
        }

        Vector3 position = Player.m_localPlayer.transform.position;
        bool moved = Vector3.Distance(position, _lastSentPosition) >= PositionMoveThreshold;
        bool heartbeatDue = Time.time < _lastPositionSentTime ||
                            Time.time - _lastPositionSentTime >= PositionHeartbeatInterval;
        if (!moved && !heartbeatDue)
        {
            _nextPositionSendTime = Time.time + PositionSendInterval;
            return;
        }

        _lastSentPosition = position;
        _lastPositionSentTime = Time.time;
        _nextPositionSendTime = Time.time + PositionSendInterval;
        ClanRpc.Send(new ClanRequest
        {
            Type = ClanRequestType.UpdatePosition,
            ClanId = ClanRpc.CurrentSnapshot.ClanId,
            Position = position
        });
    }

    private static void UpdateClanPingHint()
    {
        Minimap? minimap = Minimap.instance;
        if (minimap == null || minimap.m_largeRoot == null)
        {
            return;
        }

        KeyboardShortcut shortcut = ClanPlugin.ClanPingModifierKey.Value;
        KeyCode modifierKey = shortcut.MainKey;
        bool visible = ClanRpc.CurrentSnapshot.HasClan &&
                       modifierKey != KeyCode.None &&
                       PlatformPrefs.GetInt("KeyHints", 1) == 1 &&
                       minimap.m_largeRoot.activeInHierarchy;
        if (!visible)
        {
            if (_clanPingHint != null && _clanPingHint.activeSelf)
            {
                _clanPingHint.SetActive(false);
                MarkClanPingHintLayoutForRebuild();
            }
            return;
        }

        if (_clanPingHintOwner != minimap || _clanPingHint == null)
        {
            BuildClanPingHint(minimap);
        }

        if (_clanPingHint == null)
        {
            return;
        }

        if (!_clanPingHint.activeSelf)
        {
            _clanPingHint.SetActive(true);
            MarkClanPingHintLayoutForRebuild();
        }

        if (_clanPingHintLabel == null)
        {
            return;
        }

        string text = ClanLocalization.Format(
            "clan_map_ping_hint",
            FormatHintShortcut(shortcut));
        if (!string.Equals(_clanPingHintLabel.text, text, StringComparison.Ordinal))
        {
            _clanPingHintLabel.text = text;
            MarkClanPingHintLayoutForRebuild();
        }
    }

    private static void BuildClanPingHint(Minimap minimap)
    {
        DestroyClanPingHint();

        Transform? keyboardHints =
            minimap.m_largeRoot.transform.Find("KeyHints/keyboard_hints");
        Transform? pingHint = keyboardHints?.Find(PingHintName);
        if (keyboardHints == null || pingHint == null)
        {
            return;
        }

        Transform? existing = keyboardHints.Find(ClanPingHintName);
        GameObject hint = existing != null
            ? existing.gameObject
            : UnityEngine.Object.Instantiate(pingHint.gameObject, keyboardHints, false);
        hint.name = ClanPingHintName;
        hint.transform.SetSiblingIndex(pingHint.GetSiblingIndex());
        hint.SetActive(true);
        HorizontalLayoutGroup? hintLayout = hint.GetComponent<HorizontalLayoutGroup>();
        if (hintLayout != null)
        {
            hintLayout.spacing = -4f;
        }

        TMP_Text? label = hint.transform.Find("Label")?.GetComponent<TMP_Text>() ??
                          hint.GetComponentInChildren<TMP_Text>(includeInactive: true);
        if (label == null)
        {
            if (existing == null)
            {
                UnityEngine.Object.Destroy(hint);
            }
            return;
        }

        _clanPingHintOwner = minimap;
        _clanPingHint = hint;
        _clanPingHintLabel = label;
        MarkClanPingHintLayoutForRebuild();
    }

    private static void MarkClanPingHintLayoutForRebuild()
    {
        if (_clanPingHint?.transform.parent is not RectTransform parent)
        {
            return;
        }
        LayoutRebuilder.MarkLayoutForRebuild(parent);
    }

    private static string FormatHintShortcut(KeyboardShortcut shortcut)
    {
        return shortcut.ToString()
            .Replace("LeftShift", "Shift")
            .Replace("RightShift", "Shift")
            .Replace("LeftControl", "Ctrl")
            .Replace("RightControl", "Ctrl")
            .Replace("LeftAlt", "Alt")
            .Replace("RightAlt", "Alt");
    }

    private static void DestroyClanPingHint()
    {
        if (_clanPingHint != null)
        {
            UnityEngine.Object.Destroy(_clanPingHint);
        }
        _clanPingHintOwner = null;
        _clanPingHint = null;
        _clanPingHintLabel = null;
    }

    public static void OnMapPing(ClanPlayerRef sender, Vector3 position)
    {
        Chat? chat = Chat.instance;
        if (chat == null ||
            !sender.IsValid ||
            !IsClanPlayer(sender))
        {
            return;
        }

        EnsureSprites();
        long senderId = FindTalkerId(sender);
        UserInfo userInfo = new()
        {
            Name = string.IsNullOrWhiteSpace(sender.Name)
                ? ClanLocalization.Text("clan_name_fallback")
                : sender.Name,
            UserId = new PlatformUserID(sender.PlatformId)
        };

        chat.OnNewChatMessage(null, senderId, position, Talker.Type.Ping, userInfo, "");
        Chat.WorldTextInstance? worldText = FindWorldText(chat, senderId);
        if (worldText == null)
        {
            return;
        }

        ((Graphic)worldText.m_textMeshField).color = ClanPingColor;
        ClanPingTexts.Remove(worldText);
        ClanPingTexts.Add(worldText, Array.Empty<object>());
    }

    private static Chat.WorldTextInstance? FindWorldText(Chat chat, long talkerId)
    {
        foreach (Chat.WorldTextInstance worldText in chat.WorldTexts)
        {
            if (worldText.m_talkerID == talkerId)
            {
                return worldText;
            }
        }

        return null;
    }

    public static void OnPositionUpdate(ClanPlayerRef player, Vector3 position)
    {
        if (!ClanPlugin.ShareClanPositions.Value.IsOn() ||
            !ClanRpc.CurrentSnapshot.HasClan ||
            !player.IsValid ||
            player == ClanPlayerRef.Local() ||
            !IsClanPlayer(player))
        {
            return;
        }

        ForcedPositions[player.Id] = position;
    }

    private static bool TrySendClanPing(Vector3 position)
    {
        if (!ClanRpc.CurrentSnapshot.HasClan ||
            Player.m_localPlayer == null ||
            !ClanPlugin.ClanPingModifierKey.Value.IsKeyHeld())
        {
            return false;
        }

        position.y = Player.m_localPlayer.transform.position.y;
        ClanRpc.Send(new ClanRequest
        {
            Type = ClanRequestType.SendClanPing,
            ClanId = ClanRpc.CurrentSnapshot.ClanId,
            Position = position
        });
        return true;
    }

    private static void ResetPositionTimer()
    {
        _nextPositionSendTime = 0f;
        _lastSentPosition = Vector3.positiveInfinity;
        _lastPositionSentTime = float.NegativeInfinity;
    }

    private static bool IsClanPlayer(ZNet.PlayerInfo player)
    {
        return ClanPlugin.ShareClanPositions.Value.IsOn() &&
               ClanIdentity.TryMatchRosterPlayer(
                   player,
                   ClanRpc.CurrentSnapshot.Roster,
                   out ClanPlayerRef clanPlayer) &&
               ClanPlayerIds.Contains(clanPlayer.Id);
    }

    private static bool IsClanPlayer(ClanPlayerRef player)
    {
        return player.IsValid && ClanPlayerIds.Contains(player.Id);
    }

    private static void RestoreClanIcons()
    {
        RestoreClanPlayerIcons();
        Minimap? minimap = Minimap.instance;
        if (_clanPingIcon == null ||
            minimap == null ||
            !TryReadMinimapField(
                minimap,
                ref _pingPinsField,
                "m_pingPins",
                out List<Minimap.PinData> pingPins))
        {
            return;
        }

        Sprite? defaultIcon = FindMinimapSprite(minimap, Minimap.PinType.Ping);
        if (defaultIcon == null)
        {
            return;
        }

        foreach (Minimap.PinData pin in pingPins)
        {
            if (pin.m_icon != _clanPingIcon)
            {
                continue;
            }

            SetPinAppearance(pin, defaultIcon, doubleSize: false);
        }
    }

    private static void RestoreClanPlayerIcons()
    {
        Minimap? minimap = Minimap.instance;
        if (_clanPlayerIcon == null ||
            minimap == null ||
            !TryReadMinimapField(
                minimap,
                ref _playerPinsField,
                "m_playerPins",
                out List<Minimap.PinData> playerPins))
        {
            return;
        }

        Sprite? defaultIcon = FindMinimapSprite(minimap, Minimap.PinType.Player);
        if (defaultIcon == null)
        {
            return;
        }

        foreach (Minimap.PinData pin in playerPins)
        {
            if (pin.m_icon != _clanPlayerIcon)
            {
                continue;
            }

            SetPinAppearance(pin, defaultIcon, doubleSize: false);
        }
    }

    private static void SetPinAppearance(
        Minimap.PinData pin,
        Sprite? icon,
        bool doubleSize)
    {
        pin.m_icon = icon;
        pin.m_doubleSize = doubleSize;
        if (pin.m_iconElement != null)
        {
            pin.m_iconElement.sprite = icon;
        }
    }

    private static Sprite? FindMinimapSprite(Minimap minimap, Minimap.PinType type)
    {
        if (minimap.m_icons == null)
        {
            return null;
        }

        foreach (Minimap.SpriteData spriteData in minimap.m_icons)
        {
            if (spriteData.m_name == type)
            {
                return spriteData.m_icon;
            }
        }

        return null;
    }

    private static void DestroyGeneratedSprite(ref Sprite? sprite)
    {
        if (sprite == null)
        {
            return;
        }

        Texture2D texture = sprite.texture;
        UnityEngine.Object.Destroy(sprite);
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
        }
        sprite = null;
    }

    private static long FindTalkerId(ClanPlayerRef player)
    {
        if (ZNet.instance != null)
        {
            foreach (ZNet.PlayerInfo info in ClanIdentity.GetOnlinePlayers())
            {
                if (ClanIdentity.TryMatchRosterPlayer(
                        info,
                        ClanRpc.CurrentSnapshot.Roster,
                        out ClanPlayerRef connected) &&
                    connected == player)
                {
                    return info.m_characterID.UserID;
                }
            }
        }

        unchecked
        {
            long hash = 1469598103934665603L;
            foreach (char c in player.Id)
            {
                hash ^= c;
                hash *= 1099511628211L;
            }
            return hash switch
            {
                0 => 1,
                long.MinValue => long.MaxValue,
                _ => Math.Abs(hash)
            };
        }
    }

    private static void EnsureSprites()
    {
        if (_clanPlayerIcon != null && _clanPingIcon != null)
        {
            return;
        }
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return;
        }

        _clanPlayerIcon ??= CreateCircleSprite(
            "Clan Player Icon",
            ClanUiFactory.GetClanColor(),
            Color.white);
        _clanPingIcon ??= CreateDiamondSprite("Clan Ping Icon", ClanPingColor, Color.white);
    }

    private static Sprite CreateCircleSprite(string name, Color fill, Color accent)
    {
        const int size = 64;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            name = name + " Texture"
        };
        Color clear = new(0f, 0f, 0f, 0f);
        Vector2 center = new((size - 1) / 2f, (size - 1) / 2f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                Color color = clear;
                if (distance <= 27f)
                {
                    color = distance > 22f ? accent : fill;
                }
                if (distance <= 7f)
                {
                    color = accent;
                }
                texture.SetPixel(x, y, color);
            }
        }
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite CreateDiamondSprite(string name, Color fill, Color accent)
    {
        const int size = 64;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            name = name + " Texture"
        };
        Color clear = new(0f, 0f, 0f, 0f);
        Vector2 center = new((size - 1) / 2f, (size - 1) / 2f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float diamond = Math.Abs(x - center.x) + Math.Abs(y - center.y);
                Color color = clear;
                if (diamond <= 27f)
                {
                    color = diamond > 22f ? accent : fill;
                }
                if (diamond <= 7f)
                {
                    color = accent;
                }
                texture.SetPixel(x, y, color);
            }
        }
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.SendPing))]
    private static class SendClanPingPatch
    {
        private static bool Prefix(Vector3 position)
        {
            return !TrySendClanPing(position);
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.RPC_ChatMessage))]
    private static class ClearClanPingPatch
    {
        private static void Prefix(Chat __instance, long sender)
        {
            Minimap? minimap = Minimap.instance;
            Chat.WorldTextInstance? worldText = FindWorldText(__instance, sender);
            if (worldText == null || !ClanPingTexts.Remove(worldText) || minimap == null)
            {
                return;
            }

            if (!TryReadMinimapField(
                    minimap,
                    ref _tempShoutsField,
                    "m_tempShouts",
                    out List<Chat.WorldTextInstance> tempShouts) ||
                !TryReadMinimapField(
                    minimap,
                    ref _pingPinsField,
                    "m_pingPins",
                    out List<Minimap.PinData> pingPins))
            {
                return;
            }

            Sprite? defaultIcon = FindMinimapSprite(minimap, Minimap.PinType.Ping);
            if (defaultIcon == null)
            {
                return;
            }

            for (int i = 0; i < tempShouts.Count && i < pingPins.Count; i++)
            {
                Minimap.PinData pin = pingPins[i];
                if (tempShouts[i] == worldText)
                {
                    SetPinAppearance(pin, defaultIcon, doubleSize: false);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdatePlayerPins))]
    private static class ClanMemberPinPatch
    {
        private static void Postfix(Minimap __instance)
        {
            EnsureSprites();
            if (_clanPlayerIcon == null ||
                !TryReadMinimapField(
                    __instance,
                    ref _tempPlayerInfoField,
                    "m_tempPlayerInfo",
                    out List<ZNet.PlayerInfo> tempPlayerInfo) ||
                !TryReadMinimapField(
                    __instance,
                    ref _playerPinsField,
                    "m_playerPins",
                    out List<Minimap.PinData> playerPins))
            {
                return;
            }

            Sprite? defaultIcon = FindMinimapSprite(__instance, Minimap.PinType.Player);
            if (defaultIcon == null)
            {
                return;
            }

            for (int i = 0; i < tempPlayerInfo.Count && i < playerPins.Count; i++)
            {
                Minimap.PinData pin = playerPins[i];
                ZNet.PlayerInfo player = tempPlayerInfo[i];
                if (pin.m_name != player.m_name)
                {
                    continue;
                }

                if (IsClanPlayer(player))
                {
                    SetPinAppearance(pin, _clanPlayerIcon, doubleSize: true);
                }
                else if (pin.m_icon == _clanPlayerIcon)
                {
                    SetPinAppearance(pin, defaultIcon, doubleSize: false);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.GetOtherPublicPlayers))]
    private static class AddClanPositionsToMinimapPatch
    {
        private static void Postfix(
            ZNet __instance,
            List<ZNet.PlayerInfo> playerList)
        {
            Minimap? minimap = Minimap.instance;
            if (!ClanPlugin.ShareClanPositions.Value.IsOn() ||
                !ClanRpc.CurrentSnapshot.HasClan ||
                minimap == null ||
                !TryReadMinimapField(
                    minimap,
                    ref _tempPlayerInfoField,
                    "m_tempPlayerInfo",
                    out List<ZNet.PlayerInfo> tempPlayerInfo) ||
                !ReferenceEquals(playerList, tempPlayerInfo))
            {
                return;
            }

            for (int i = 0; i < playerList.Count; i++)
            {
                ZNet.PlayerInfo publicPlayer = playerList[i];
                if (!ClanIdentity.TryMatchRosterPlayer(
                        publicPlayer,
                        ClanRpc.CurrentSnapshot.Roster,
                        out ClanPlayerRef player) ||
                    !ForcedPositions.TryGetValue(player.Id, out Vector3 forcedPosition))
                {
                    continue;
                }

                publicPlayer.m_position = forcedPosition;
                playerList[i] = publicPlayer;
            }

            foreach (ZNet.PlayerInfo networkPlayer in __instance.GetPlayerList())
            {
                if (networkPlayer.m_publicPosition ||
                    networkPlayer.m_characterID.IsNone() ||
                    networkPlayer.m_characterID == __instance.LocalPlayerCharacterID)
                {
                    continue;
                }

                if (!ClanIdentity.TryMatchRosterPlayer(
                        networkPlayer,
                        ClanRpc.CurrentSnapshot.Roster,
                        out ClanPlayerRef player) ||
                    !ForcedPositions.TryGetValue(
                        player.Id,
                        out Vector3 forcedPosition))
                {
                    continue;
                }

                ZNet.PlayerInfo displayPlayer = networkPlayer;
                displayPlayer.m_publicPosition = true;
                displayPlayer.m_position = forcedPosition;
                playerList.Add(displayPlayer);
            }
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdatePingPins))]
    private static class ClanPingPinPatch
    {
        private static void Postfix(Minimap __instance)
        {
            EnsureSprites();
            if (_clanPingIcon == null ||
                !TryReadMinimapField(
                    __instance,
                    ref _tempShoutsField,
                    "m_tempShouts",
                    out List<Chat.WorldTextInstance> tempShouts) ||
                !TryReadMinimapField(
                    __instance,
                    ref _pingPinsField,
                    "m_pingPins",
                    out List<Minimap.PinData> pingPins))
            {
                return;
            }

            for (int i = 0; i < tempShouts.Count && i < pingPins.Count; i++)
            {
                Minimap.PinData pin = pingPins[i];
                Chat.WorldTextInstance worldText = tempShouts[i];
                if (!ClanPingTexts.TryGetValue(worldText, out _))
                {
                    continue;
                }

                SetPinAppearance(pin, _clanPingIcon, doubleSize: true);
            }
        }
    }

}
