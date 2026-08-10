// 小格子铺 | Latticeshop
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Runs;

namespace CustomStartCode.Config;

/// <summary>
/// 已分发但拾取效果（AfterObtained）尚未完成的初始遗物标记。
///
/// 开局首次存档（EnterMapPointInternal → SaveRun）发生在进入首个房间之前，
/// 而选择面板要等房间落定后才弹出，因此“立即保存退出→继续游戏”的存档里
/// 遗物已存在但效果未执行。原版加载存档不会重跑 AfterObtained（不幂等），
/// 本标记随配置同目录落盘，继续游戏时按 seed + 角色列表匹配后重新入队执行。
/// </summary>
internal sealed class PendingRelicMarker
{
    private const string FileName = "CustomStartPendingPickups.json";

    private static readonly MegaCrit.Sts2.Core.Logging.Logger Logger =
        new("CustomStartMarker", MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public string Seed { get; set; } = string.Empty;
    public List<string> Players { get; set; } = new();
    public List<Entry> Pending { get; set; } = new();

    public sealed class Entry
    {
        public int PlayerIndex { get; set; }
        public string RelicTypeName { get; set; } = string.Empty;
        /// <summary>
        /// 同类遗物（同类型名）在玩家遗物列表中的第几个（0 起）。
        /// 用于区分多个同名遗物，避免“三个星盘”时第一条完成就误删全部。
        /// </summary>
        public int OccurrenceIndex { get; set; }
    }

    private static string MarkerPath
    {
        get
        {
            string dir = Path.GetDirectoryName(ConfigManager.ConfigPath) ?? ".";
            return Path.Combine(dir, FileName);
        }
    }

    public static PendingRelicMarker? Load()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            string json = File.ReadAllText(MarkerPath);
            return JsonSerializer.Deserialize<PendingRelicMarker>(json);
        }
        catch { return null; }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(MarkerPath, json);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CustomStart] 保存遗物拾取效果标记失败: {ex.Message}");
        }
    }

    public static void DeleteIfExists()
    {
        try
        {
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        catch { }
    }

    /// <summary>
    /// 标记是否属于当前局（seed + 按序角色列表一致）。
    /// </summary>
    public bool Matches(RunState state)
    {
        if (state?.Rng == null) return false;
        if (string.IsNullOrEmpty(Seed) || !string.Equals(Seed, state.Rng.StringSeed, StringComparison.Ordinal))
        {
            return false;
        }
        var chars = state.Players.Select(p => p.Character?.Id?.Entry ?? string.Empty).ToList();
        if (chars.Count != Players.Count) return false;
        for (int i = 0; i < chars.Count; i++)
        {
            if (!string.Equals(chars[i], Players[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// 合并新分发的遗物到标记：seed 不符视为新局，整份替换；否则仅追加缺失条目。
    /// </summary>
    public static void Merge(RunState state, IEnumerable<(int PlayerIndex, string RelicTypeName, int OccurrenceIndex)> entries)
    {
        try
        {
            if (state?.Rng == null) return;
            var marker = Load();
            if (marker == null || !marker.Matches(state))
            {
                marker = new PendingRelicMarker
                {
                    Seed = state.Rng.StringSeed,
                    Players = state.Players.Select(p => p.Character?.Id?.Entry ?? string.Empty).ToList(),
                };
            }
            foreach (var (idx, name, occurrence) in entries)
            {
                if (string.IsNullOrEmpty(name) || idx < 0) continue;
                if (marker.Pending.Any(e =>
                    e.PlayerIndex == idx && e.RelicTypeName == name && e.OccurrenceIndex == occurrence)) continue;
                marker.Pending.Add(new Entry { PlayerIndex = idx, RelicTypeName = name, OccurrenceIndex = occurrence });
            }
            marker.Save();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CustomStart] 记录遗物拾取效果标记失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 效果完成后移除标记条目；全部完成则删除标记文件。
    /// </summary>
    /// <returns>true=标记已清空（全部效果完成）；false=仍有未完成条目。</returns>
    public static bool RemoveCompleted(RunState state, int playerIndex, string relicTypeName, int occurrenceIndex)
    {
        try
        {
            var marker = Load();
            if (marker == null || !marker.Matches(state)) return false;
            marker.Pending.RemoveAll(e =>
                e.PlayerIndex == playerIndex
                && e.RelicTypeName == relicTypeName
                && e.OccurrenceIndex == occurrenceIndex);
            if (marker.Pending.Count == 0)
            {
                DeleteIfExists();
                return true;
            }
            marker.Save();
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CustomStart] 更新遗物拾取效果标记失败: {ex.Message}");
            return false;
        }
    }
}
