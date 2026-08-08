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

    /// <summary>
    /// 初始金币数量（0 = 使用角色默认值）
    /// </summary>
    public int StartingGold { get; set; }

    /// <summary>
    /// 初始血量上限（0 = 使用角色默认值）
    /// </summary>
    public int MaxHp { get; set; }

    /// <summary>
    /// 深拷贝当前配置（供方案快照使用，避免引用共享导致方案被后续编辑污染）。
    /// </summary>
    public CharacterConfig Clone()
    {
        return new CharacterConfig
        {
            CharacterId = CharacterId,
            CustomDeckCardTypes = new List<string>(CustomDeckCardTypes),
            EnableCustomDeck = EnableCustomDeck,
            StartingRelicTypes = new List<string>(StartingRelicTypes),
            EnableCustomRelics = EnableCustomRelics,
            StartingGold = StartingGold,
            MaxHp = MaxHp,
        };
    }
}

/// <summary>
/// 定制开局配置方案 - 保存全部角色的配置快照
/// </summary>
public class DeckConfigPreset
{
    /// <summary>
    /// 方案名称（默认“方案N”）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 方案内容：全部角色的配置
    /// </summary>
    public Dictionary<string, CharacterConfig> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    // 动态方案槽位列表：高亮槽 = 当前方案（工作配置同步回该槽），
    // 末尾始终保持一个 null 空槽用于“保存即新建”，数量不设上限。
    private static readonly List<DeckConfigPreset?> _presets = new();
    // 当前方案槽位索引（切换只移动高亮，不覆盖任何槽内容）
    private static int _activePresetIndex = 0;
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
        _presets.Clear();
        _activePresetIndex = 0;
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Logger.Info($"[CustomStart] 配置文件不存在，创建默认配置: {ConfigPath}");
                RecoverActiveSlot();
                EnsureTrailingEmptySlot();
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

            if (doc.RootElement.TryGetProperty("presets", out JsonElement presetsEl) &&
                presetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in presetsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var preset = new DeckConfigPreset();
                    if (item.TryGetProperty("name", out var nameEl))
                    {
                        preset.Name = nameEl.GetString() ?? string.Empty;
                    }
                    if (item.TryGetProperty("characters", out var charsEl))
                    {
                        foreach (var prop in charsEl.EnumerateObject())
                        {
                            var config = ParseCharacterConfig(prop.Name, prop.Value);
                            preset.Characters[config.CharacterId] = config;
                        }
                    }
                    _presets.Add(preset);
                }
            }

            if (doc.RootElement.TryGetProperty("activePresetIndex", out JsonElement activeEl) &&
                activeEl.ValueKind == JsonValueKind.Number)
            {
                int idx = activeEl.GetInt32();
                if (idx >= 0) _activePresetIndex = idx;
            }

            RecoverActiveSlot();
            EnsureTrailingEmptySlot();
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
            // 当前方案绑定：工作配置始终同步回高亮槽（名字保留）
            RecoverActiveSlot();
            _presets[_activePresetIndex].Characters = SnapshotConfigs(_configs);
            EnsureTrailingEmptySlot();

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
                        enableCustomRelics = kv.Value.EnableCustomRelics,
                        startingGold = kv.Value.StartingGold,
                        maxHp = kv.Value.MaxHp
                    }
                ),
                ["presets"] = BuildPresetsJson(),
                ["activePresetIndex"] = _activePresetIndex
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
    /// 当前方案槽位索引：高亮跟随当前方案，切换只改高亮、不覆盖任何槽内容。
    /// </summary>
    public static int ActivePresetIndex => _activePresetIndex;

    /// <summary>
    /// 当前槽位总数（含末尾空槽）。
    /// </summary>
    public static int GetPresetCount()
    {
        return _presets.Count;
    }

    /// <summary>
    /// 获取指定槽位的方案（null = 空位）。
    /// </summary>
    public static DeckConfigPreset? GetPreset(int index)
    {
        return index >= 0 && index < _presets.Count ? _presets[index] : null;
    }

    /// <summary>
    /// 指定槽位是否已保存过方案（有角色配置内容）。
    /// </summary>
    public static bool HasPreset(int index)
    {
        var preset = GetPreset(index);
        return preset != null && preset.Characters.Count > 0;
    }

    /// <summary>
    /// 将当前全部角色配置快照保存到指定槽位：
    /// 空槽 = 新建方案（自动追加新的末尾空槽）；已保存槽 = 覆盖内容（保留名字）；当前槽 = 重写快照。
    /// </summary>
    public static void SavePreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return;
        if (_configs == null) Load();

        var snapshot = BuildFullConfigSnapshot();
        if (index == _activePresetIndex)
        {
            _presets[index].Characters = snapshot;
            Logger.Info($"[CustomStart] 已保存当前方案（{snapshot.Count} 个角色）");
        }
        else
        {
            var existing = _presets[index];
            if (existing == null || existing.Characters.Count == 0)
            {
                var newPreset = new DeckConfigPreset
                {
                    Name = existing?.Name ?? GenerateNextPresetName(),
                    Characters = snapshot,
                };
                _presets[index] = newPreset;
                Logger.Info($"[CustomStart] 已新建方案（{snapshot.Count} 个角色）");
            }
            else
            {
                existing.Characters = snapshot;
                Logger.Info($"[CustomStart] 已保存方案（{snapshot.Count} 个角色）");
            }
        }
        Save();
    }

    /// <summary>
    /// 重命名指定槽位的方案。
    /// </summary>
    public static void RenamePreset(int index, string name)
    {
        if (index < 0 || index >= _presets.Count) return;
        var preset = _presets[index];
        if (preset == null)
        {
            preset = new DeckConfigPreset();
            _presets[index] = preset;
        }
        preset.Name = string.IsNullOrWhiteSpace(name) ? GenerateNextPresetName() : name.Trim();
        Save();
        Logger.Info($"[CustomStart] 已重命名方案 {index}: {preset.Name}");
    }

    /// <summary>
    /// 加载指定槽位的方案为当前方案：只移动高亮（active index），不覆盖任何槽内容。
    /// </summary>
    public static bool LoadPreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return false;
        var preset = _presets[index];
        if (preset == null || preset.Characters.Count == 0) return false;

        _configs = SnapshotConfigs(preset.Characters);
        _activePresetIndex = index;
        Save();
        BroadcastAllLocalConfigs();
        Logger.Info($"[CustomStart] 已切换到方案 {index}（{_configs.Count} 个角色）");
        return true;
    }

    /// <summary>
    /// 删除指定槽位的方案（可删当前槽，删除后高亮自动回退到最近槽；弹窗确认由 UI 负责）。
    /// </summary>
    public static bool DeletePreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return false;
        if (_presets[index] == null) return false;
        _presets.RemoveAt(index);
        RecoverActiveSlot();
        EnsureTrailingEmptySlot();
        Save();
        Logger.Info($"[CustomStart] 已删除方案 {index}");
        return true;
    }

    /// <summary>
    /// 对配置字典做深拷贝快照。
    /// </summary>
    private static Dictionary<string, CharacterConfig> SnapshotConfigs(Dictionary<string, CharacterConfig> source)
    {
        var result = new Dictionary<string, CharacterConfig>(StringComparer.OrdinalIgnoreCase);
        if (source == null) return result;
        foreach (var kv in source)
        {
            result[kv.Key] = kv.Value.Clone();
        }
        return result;
    }

    /// <summary>
    /// 生成 presets 数组的 JSON 载荷：已保存/已命名槽序列化；空槽不序列化（加载时自动重建）。
    /// </summary>
    private static List<object?> BuildPresetsJson()
    {
        var result = new List<object?>();
        foreach (var preset in _presets)
        {
            if (preset == null) continue;
            var characters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in preset.Characters)
            {
                characters[kv.Key] = new
                {
                    customDeckCardTypes = kv.Value.CustomDeckCardTypes,
                    enableCustomDeck = kv.Value.EnableCustomDeck,
                    startingRelicTypes = kv.Value.StartingRelicTypes,
                    enableCustomRelics = kv.Value.EnableCustomRelics,
                    startingGold = kv.Value.StartingGold,
                    maxHp = kv.Value.MaxHp,
                };
            }
            result.Add(new Dictionary<string, object>
            {
                ["name"] = preset.Name,
                ["characters"] = characters,
            });
        }
        return result;
    }

    /// <summary>
    /// 生成“方案N”默认名（N = 当前已保存槽数量 + 1，不含当前槽与空槽）。
    /// </summary>
    private static string GenerateNextPresetName()
    {
        int savedCount = 0;
        for (int i = 0; i < _presets.Count; i++)
        {
            if (i == _activePresetIndex) continue;
            var p = _presets[i];
            if (p != null && p.Characters.Count > 0) savedCount++;
        }
        return L("CONFIG_PRESET_SLOT", savedCount + 1);
    }

    /// <summary>
    /// 物化全部角色的配置快照（未配置角色自动补默认项）。
    /// </summary>
    private static Dictionary<string, CharacterConfig> BuildFullConfigSnapshot()
    {
        var snapshot = new Dictionary<string, CharacterConfig>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var character in ModelDb.AllCharacters)
            {
                try
                {
                    string id = character.Id.Entry;
                    if (string.IsNullOrEmpty(id)) continue;
                    snapshot[id] = GetCharacterConfig(id).Clone();
                }
                catch { }
            }
        }
        catch { }
        if (snapshot.Count == 0 && _configs != null)
        {
            foreach (var kv in _configs)
            {
                snapshot[kv.Key] = kv.Value.Clone();
            }
        }
        return snapshot;
    }

    /// <summary>
    /// 保证槽位列表末尾始终有一个空槽（用于“保存即新建”）。
    /// </summary>
    private static void EnsureTrailingEmptySlot()
    {
        if (_presets.Count == 0)
        {
            _presets.Add(null);
            return;
        }
        int lastIdx = _presets.Count - 1;
        var last = _presets[lastIdx];
        if (last == null) return;
        if (lastIdx == _activePresetIndex || last.Characters.Count > 0)
        {
            _presets.Add(null);
        }
    }

    /// <summary>
    /// 恢复当前方案槽：越界或指向空槽时回退到最近的槽，全部为空则新建当前槽。
    /// </summary>
    private static void RecoverActiveSlot()
    {
        if (_presets.Count == 0)
        {
            _presets.Add(new DeckConfigPreset { Name = L("CONFIG_PRESET_CURRENT_DEFAULT") });
            _activePresetIndex = 0;
            return;
        }
        if (_activePresetIndex >= _presets.Count) _activePresetIndex = _presets.Count - 1;
        if (_activePresetIndex < 0) _activePresetIndex = 0;
        if (_presets[_activePresetIndex] != null) return;

        for (int j = _activePresetIndex - 1; j >= 0; j--)
        {
            if (_presets[j] != null) { _activePresetIndex = j; return; }
        }
        for (int j = _activePresetIndex + 1; j < _presets.Count; j++)
        {
            if (_presets[j] != null) { _activePresetIndex = j; return; }
        }
        _presets[_activePresetIndex] = new DeckConfigPreset { Name = L("CONFIG_PRESET_CURRENT_DEFAULT") };
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

        if (element.TryGetProperty("startingGold", out var startingGold))
        {
            config.StartingGold = startingGold.GetInt32();
        }

        if (element.TryGetProperty("maxHp", out var maxHp))
        {
            config.MaxHp = maxHp.GetInt32();
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
