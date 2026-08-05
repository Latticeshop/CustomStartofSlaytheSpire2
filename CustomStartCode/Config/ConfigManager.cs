// 小格子铺 | Latticeshop
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CustomStartCode.Config;

/// <summary>
/// 角色配置数据（纯配置：自定义初始卡组 + 自定义初始遗物）
/// </summary>
public class CharacterConfig
{
    /// <summary>
    /// 角色ID
    /// </summary>
    public string CharacterId { get; set; } = string.Empty;

    /// <summary>
    /// 自定义初始卡组（卡牌类型名列表），为空则使用默认
    /// </summary>
    public List<string> CustomDeckCardTypes { get; set; } = new();

    /// <summary>
    /// 是否启用自定义初始卡组
    /// </summary>
    public bool EnableCustomDeck { get; set; }

    /// <summary>
    /// 是否启用自定义初始遗物
    /// </summary>
    public bool EnableCustomRelics { get; set; }

    /// <summary>
    /// 自定义初始遗物（遗物类型名列表），为空则使用默认
    /// </summary>
    public List<string> StartingRelicTypes { get; set; } = new();
}

/// <summary>
/// 配置管理器 - 负责配置的保存、加载和读取
/// </summary>
public static class ConfigManager
{
    private const string ConfigFileName = "CustomStartConfig.json";
    private const string LocTable = "characters";
    /// <summary>
    /// 自定义卡组条目中升级卡牌的标记后缀（如 "Strike:U"）。
    /// </summary>
    public const string UpgradedMarker = ":U";
    private static readonly MegaCrit.Sts2.Core.Logging.Logger Logger = new("CustomStart", LogType.Generic);

    private static Dictionary<string, CharacterConfig>? _configs;
    private static bool _initialized;
    // 多人模式下按玩家 NetId 同步过来的配置（用于按玩家独立应用）
    private static readonly Dictionary<ulong, CharacterConfig> _remoteConfigs = new();
    // 当前局玩家列表（开局时缓存，供配置广播定位本地玩家）
    private static IReadOnlyList<Player>? _currentPlayers;
    // 是否处于多人联机会话（大厅初始化时置位，离开大厅后复位）
    private static bool _isMultiplayerSession;
    // “强制全部应用房主配置”开关（由房主在大厅切换并广播到各端）
    private static bool _forceHostConfigEnabled;
    // 房主整套配置的临时副本（本局生效，不写入各端配置存储）
    private static Dictionary<string, CharacterConfig>? _forcedHostConfigs;

    /// <summary>
    /// 配置文件路径
    /// </summary>
    public static string ConfigPath { get; private set; } = string.Empty;

    /// <summary>
    /// 初始化配置管理器
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        ConfigPath = GetConfigPath();
        Load();
        Logger.Info($"[CustomStart] 初始化完成，配置文件: {ConfigPath}");
    }

    /// <summary>
    /// 加载配置
    /// </summary>
    public static void Load()
    {
        _configs = new Dictionary<string, CharacterConfig>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Logger.Info($"[CustomStart] 配置文件不存在，创建默认配置: {ConfigPath}");
                Save();
                return;
            }

            string json = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.TryGetProperty("characters", out JsonElement chars))
            {
                foreach (var prop in chars.EnumerateObject())
                {
                    var config = ParseCharacterConfig(prop.Name, prop.Value);
                    _configs[config.CharacterId] = config;
                }
            }
            Logger.Info($"[CustomStart] 加载配置成功，共 {_configs.Count} 个角色配置");
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 加载配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    public static void Save()
    {
        if (_configs == null) _configs = new Dictionary<string, CharacterConfig>();
        try
        {
            var config = new Dictionary<string, object>
            {
                ["_readme"] = "定制开局 Mod 配置文件。请勿手动修改。",
                ["version"] = "1.0",
                ["characters"] = _configs.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        customDeckCardTypes = kv.Value.CustomDeckCardTypes,
                        enableCustomDeck = kv.Value.EnableCustomDeck,
                        startingRelicTypes = kv.Value.StartingRelicTypes,
                        enableCustomRelics = kv.Value.EnableCustomRelics
                    }
                )
            };

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(ConfigPath, json);
            Logger.Info($"[CustomStart] 配置已保存: {ConfigPath}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 保存配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取指定角色的配置。
    /// 多人模式下传入远端玩家 NetId 时，返回该玩家同步过来的配置；
    /// 本机玩家始终使用本机最新配置。
    /// </summary>
    public static CharacterConfig GetCharacterConfig(string characterId, ulong? netId = null)
    {
        if (_configs == null) Load();

        if (netId.HasValue && !IsLocalNetId(netId.Value) && _remoteConfigs.TryGetValue(netId.Value, out var remoteConfig))
        {
            return remoteConfig;
        }

        if (_configs!.TryGetValue(characterId, out var config))
        {
            return config;
        }

        var newConfig = new CharacterConfig { CharacterId = characterId };
        _configs[characterId] = newConfig;
        return newConfig;
    }

    /// <summary>
    /// 获取玩家本局应使用的配置：
    /// 强制房主配置开启时优先使用房主对对应角色的配置；
    /// 否则本机玩家用本机配置，远端玩家用同步配置。
    /// </summary>
    public static CharacterConfig? GetConfigForPlayer(Player player)
    {
        try
        {
            if (player?.Character == null) return null;
            string? characterId = player.Character?.Id?.Entry;
            if (string.IsNullOrEmpty(characterId)) return null;

            if (_forceHostConfigEnabled)
            {
                if (TryGetForcedConfig(characterId, out var forced))
                {
                    return forced;
                }
                // 房主未配置该角色：本局按默认开局（与房主行为一致），不采用玩家自身配置
                return new CharacterConfig { CharacterId = characterId };
            }

            return MultiplayerSyncHelper.IsLocalPlayer(player)
                ? GetCharacterConfig(characterId)
                : GetCharacterConfig(characterId, player.NetId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取所有角色配置
    /// </summary>
    public static Dictionary<string, CharacterConfig> GetAllConfigs()
    {
        if (_configs == null) Load();
        return new Dictionary<string, CharacterConfig>(_configs!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 更新角色配置
    /// </summary>
    public static void UpdateCharacterConfig(CharacterConfig config)
    {
        if (_configs == null) Load();
        _configs![config.CharacterId] = config;
        Save();
        BroadcastConfig(config);
    }

    /// <summary>
    /// 记录其他玩家（NetId）同步过来的配置，用于多人模式下按玩家独立应用。
    /// </summary>
    public static void SetRemoteCharacterConfig(ulong netId, CharacterConfig config)
    {
        if (config == null) return;
        _remoteConfigs[netId] = config;
        Logger.Info($"[CustomStart] 已记录玩家 {netId} 的配置（角色: {config.CharacterId}）");
    }

    /// <summary>
    /// 缓存当前局玩家列表（开局时由 RunStartPatch 调用）。
    /// </summary>
    public static void SetRunPlayers(IReadOnlyList<Player>? players)
    {
        _currentPlayers = players;
    }

    /// <summary>
    /// 是否处于多人联机会话（大厅阶段即置位，离开大厅后复位）。
    /// </summary>
    public static bool IsMultiplayerSession => _isMultiplayerSession;

    /// <summary>
    /// 设置多人联机会话标记（由大厅初始化/清理补丁调用）。
    /// </summary>
    public static void SetMultiplayerSession(bool value)
    {
        _isMultiplayerSession = value;
        if (!value)
        {
            _remoteConfigs.Clear();
            _currentPlayers = null;
            _forceHostConfigEnabled = false;
            _forcedHostConfigs = null;
        }
    }

    /// <summary>
    /// “强制全部应用房主配置”开关状态。
    /// </summary>
    public static bool IsForceHostConfigEnabled => _forceHostConfigEnabled;

    /// <summary>
    /// 设置强制房主配置开关（大厅面板/消息调用；不写入配置文件）。
    /// </summary>
    public static void SetForceHostConfig(bool enabled)
    {
        if (_forceHostConfigEnabled == enabled) return;
        _forceHostConfigEnabled = enabled;
        Logger.Info($"[CustomStart] 强制全部应用房主配置: {(enabled ? "开启" : "关闭")}");
    }

    /// <summary>
    /// 保存房主整套配置的本局临时副本（由开局时的同步动作在所有端执行）。
    /// </summary>
    public static void SetForcedHostConfigs(Dictionary<string, CharacterConfig>? configs)
    {
        _forcedHostConfigs = configs == null
            ? null
            : new Dictionary<string, CharacterConfig>(configs, StringComparer.OrdinalIgnoreCase);
        Logger.Info($"[CustomStart] 已接收房主整套配置（{_forcedHostConfigs?.Count ?? 0} 个角色）");
    }

    /// <summary>
    /// 强制模式下是否已收到房主整套配置。
    /// </summary>
    public static bool HasForcedHostConfigs()
    {
        // 房主可能一个角色都没配置（即全默认），此时也算已就绪
        return _forceHostConfigEnabled && _forcedHostConfigs != null;
    }

    /// <summary>
    /// 尝试获取房主对指定角色的强制配置。
    /// </summary>
    public static bool TryGetForcedConfig(string characterId, out CharacterConfig config)
    {
        if (_forcedHostConfigs != null && _forcedHostConfigs.TryGetValue(characterId, out var forced))
        {
            config = forced;
            return true;
        }
        config = null!;
        return false;
    }

    /// <summary>
    /// 单个玩家配置是否就绪（本地玩家始终可用本机配置，远端玩家需等同步到达）。
    /// </summary>
    public static bool HasConfigForPlayer(Player player)
    {
        if (player == null) return false;
        try
        {
            if (MultiplayerSyncHelper.IsLocalPlayer(player)) return true;
            return _remoteConfigs.ContainsKey(player.NetId);
        }
        catch { return false; }
    }

    /// <summary>
    /// 多人开局时：把本机所有本地玩家的配置广播给主机（同步后全端应用）。
    /// </summary>
    public static void BroadcastAllLocalConfigs()
    {
        try
        {
            if (!MultiplayerSyncHelper.IsMultiplayerGame()) return;
            if (_currentPlayers == null) return;
            foreach (var player in _currentPlayers)
            {
                try
                {
                    if (!MultiplayerSyncHelper.IsLocalPlayer(player)) continue;
                    string? characterId = player.Character?.Id?.Entry;
                    if (string.IsNullOrEmpty(characterId)) continue;
                    BroadcastConfig(GetCharacterConfig(characterId));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 广播本地配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将本地配置广播（多人模式下）；不在多人运行环境中时静默跳过。
    /// </summary>
    public static void BroadcastConfig(CharacterConfig config)
    {
        try
        {
            if (config == null) return;
            if (!MultiplayerSyncHelper.IsMultiplayerGame()) return;
            if (_currentPlayers == null) return;

            Player? local = _currentPlayers.FirstOrDefault(p =>
            {
                try { return MultiplayerSyncHelper.IsLocalPlayer(p) && p.Character?.Id?.Entry == config.CharacterId; }
                catch { return false; }
            });
            if (local == null) return;

            RunManager.Instance?.ActionQueueSynchronizer?.RequestEnqueue(new ConfigSyncGameAction(local, config));
            Logger.Info($"[CustomStart] 已广播配置（角色: {config.CharacterId}）");
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 广播配置失败: {ex.Message}");
        }
    }

    private static bool IsLocalNetId(ulong netId)
    {
        try
        {
            return RunManager.Instance?.NetService != null && RunManager.Instance.NetService.NetId == netId;
        }
        catch { return false; }
    }

    /// <summary>
    /// 重置指定角色为默认配置
    /// </summary>
    public static void ResetCharacterConfig(string characterId)
    {
        if (_configs == null) Load();
        _configs![characterId] = new CharacterConfig { CharacterId = characterId };
        Save();
    }

    /// <summary>
    /// 获取角色的自定义卡组卡牌类型列表
    /// </summary>
    public static List<string> GetCustomDeckCardTypes(string characterId)
    {
        var config = GetCharacterConfig(characterId);
        return config.EnableCustomDeck ? config.CustomDeckCardTypes : new List<string>();
    }

    /// <summary>
    /// 编码卡组条目：升级卡附加 ":U" 后缀，与未升级同名卡区分（各自独立叠加）。
    /// </summary>
    public static string EncodeCardType(string typeName, bool upgraded)
    {
        return upgraded ? typeName + UpgradedMarker : typeName;
    }

    /// <summary>
    /// 解码卡组条目：去掉升级标记，返回真实卡牌类型名。
    /// </summary>
    public static string DecodeCardType(string entry, out bool upgraded)
    {
        upgraded = false;
        if (!string.IsNullOrEmpty(entry) && entry.EndsWith(UpgradedMarker, StringComparison.Ordinal))
        {
            upgraded = true;
            return entry.Substring(0, entry.Length - UpgradedMarker.Length);
        }
        return entry ?? string.Empty;
    }

    /// <summary>
    /// 读取 characters 本地化表中的文案（供卡牌库/遗物库等配置模块 UI 使用）。
    /// </summary>
    public static string L(string key, params object[] args)
    {
        try
        {
            string text = new LocString(LocTable, key).GetRawText();
            if (args.Length > 0)
                text = string.Format(text, args);
            return text;
        }
        catch
        {
            return key;
        }
    }

    private static CharacterConfig ParseCharacterConfig(string characterId, JsonElement element)
    {
        var config = new CharacterConfig { CharacterId = characterId };

        if (element.TryGetProperty("enableCustomDeck", out var enableCustomDeck))
        {
            config.EnableCustomDeck = enableCustomDeck.GetBoolean();
        }

        if (element.TryGetProperty("customDeckCardTypes", out var cardTypes))
        {
            var list = new List<string>();
            foreach (var item in cardTypes.EnumerateArray())
            {
                string? val = item.GetString();
                if (!string.IsNullOrEmpty(val))
                    list.Add(val);
            }
            config.CustomDeckCardTypes = list;
        }

        if (element.TryGetProperty("startingRelicTypes", out var relicTypes))
        {
            var list = new List<string>();
            foreach (var item in relicTypes.EnumerateArray())
            {
                string? val = item.GetString();
                if (!string.IsNullOrEmpty(val))
                    list.Add(val);
            }
            config.StartingRelicTypes = list;
        }

        if (element.TryGetProperty("enableCustomRelics", out var enableCustomRelics))
        {
            config.EnableCustomRelics = enableCustomRelics.GetBoolean();
        }

        return config;
    }

    private static string GetConfigPath()
    {
        try
        {
            string? location = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(location))
            {
                string? dir = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(dir))
                {
                    string modDir = Path.Combine(dir, "CustomStart");
                    if (Directory.Exists(modDir))
                    {
                        return Path.Combine(modDir, ConfigFileName);
                    }
                    return Path.Combine(dir, ConfigFileName);
                }
            }
        }
        catch { }

        try
        {
            string? exeDir = Path.GetDirectoryName(OS.GetExecutablePath());
            if (!string.IsNullOrEmpty(exeDir))
                return Path.Combine(exeDir, "mods", "CustomStart", ConfigFileName);
        }
        catch { }

        return ConfigFileName;
    }
}
