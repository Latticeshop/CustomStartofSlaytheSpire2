// 小格子铺 | Latticeshop
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CustomStartCode.Config;

/// <summary>
/// 自定义初始遗物的“拾取生效”延迟队列。
///
/// 星盘/美味饼干/召唤铃铛等遗物会在 AfterObtained 里打开选择面板等待玩家输入，
/// 而原版 RunManager.FinalizeStartingRelics 在 NRun 场景创建之前、LocalContext.NetId
/// 赋值之前就会逐个调用 AfterObtained。此时 ShouldSelectLocalCard 判定玩家不是本地玩家，
/// 单机会直接抛 “Cannot wait for remote choice in singleplayer!”，导致开局卡死/被打回主菜单。
///
/// 本队列把所有自定义初始遗物的 AddRelicInternal + AfterObtained 推迟到 NRun UI 就绪后
/// 统一执行，并在面板期间监听开局是否已结束，避免面板未完成时退出造成任务悬挂。
/// </summary>
internal static class StartingRelicPickupQueue
{
    private static readonly MegaCrit.Sts2.Core.Logging.Logger Logger =
        new("CustomStartRelicPickup", MegaCrit.Sts2.Core.Logging.LogType.Generic);

    private sealed record PendingRelic(Player Player, RelicModel Relic);

    private static readonly List<PendingRelic> Pending = new();
    private static bool _runnerRunning;

    public static void Enqueue(Player player, RelicModel relic)
    {
        if (player == null || relic == null) return;
        // 同一遗物实例不重复入队（防止恢复标记时同名/同实例重复）
        if (Pending.Any(p => ReferenceEquals(p.Relic, relic))) return;
        Pending.Add(new PendingRelic(player, relic));
        if (!_runnerRunning)
        {
            _runnerRunning = true;
            TaskHelper.RunSafely(ProcessPendingAsync());
        }
    }

    /// <summary>
    /// 继续游戏（LoadRun）后调用：读取本地“未完成拾取效果”标记，
    /// 把仍在该局玩家身上的遗物重新入队，恢复选择面板。
    /// </summary>
    public static void CheckPendingPickupsAfterLoad(RunState runState)
    {
        try
        {
            if (runState == null) return;
            TaskHelper.RunSafely(RestorePendingAfterLoadAsync(runState));
        }
        catch { }
    }

    private static async Task ProcessPendingAsync()
    {
        try
        {
            while (true)
            {
                List<PendingRelic> batch = TakePending();
                if (batch.Count == 0) break;

                // 阶段1：等 NRun UI 就绪后立刻“分发”（把遗物加入玩家）。
                // 必须在开局首次存档（EnterMapPointInternal → SaveRun）之前完成：
                // 该存档发生在进入首个房间之前，若遗物此时还未加入玩家，
                // “立即保存退出→继续游戏”会丢失全部自定义遗物。
                if (!await WaitUntilRunUiReadyAsync(batch, requireRoomSettled: false))
                {
                    Logger.Warn($"[CustomStart] 开局遗物分发等待超时或开局已中断，跳过 {batch.Count} 个遗物效果");
                    break;
                }
                // 等待 UI 期间新入队的遗物合并进本批。
                // 各端入队顺序一致（state.Players 顺序），无论在哪一帧合并，
                // 拼接结果都等于完整入队顺序，保证全端执行顺序相同。
                batch.AddRange(TakePending());
                Logger.Info($"[CustomStart] 队列批次开始分发: 遗物数={batch.Count}, 遗物=[{string.Join(",", batch.Select(b => b.Relic.GetType().Name))}]");

                foreach (PendingRelic pending in batch)
                {
                    try
                    {
                        if (!IsCurrentRun(pending.Player)) continue;
                        TryAddRelic(pending);
                        Patches.InitialDeckPatch.RevealLocalRelicInventoryIcons(pending.Player);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[CustomStart] 分发遗物失败 {pending.Relic.GetType().Name}: {ex.Message}");
                    }
                }

                // 记录“已分发但效果未完成”的遗物：SL 后按标记恢复面板。
                // 必须与分发同步落盘（早于开局首次存档）。
                try
                {
                    var state = RunManager.Instance?.DebugOnlyGetState();
                    if (state != null)
                    {
                        var playerList = state.Players.ToList();
                        PendingRelicMarker.Merge(state, batch.Select(b =>
                        {
                            int idx = playerList.IndexOf(b.Player);
                            string name = b.Relic.GetType().Name;
                            int occurrence = b.Player.Relics
                                .Where(r => r.GetType().Name == name)
                                .ToList()
                                .IndexOf(b.Relic);
                            return (idx, name, occurrence);
                        }));
                    }
                }
                catch { }

                // 阶段2：等开局房间落定后再逐个执行拾取效果（选择面板不被地图/房间切换遮挡）。
                if (!await WaitUntilRunUiReadyAsync(batch, requireRoomSettled: true))
                {
                    Logger.Warn($"[CustomStart] 开局房间落定等待超时或开局已中断，遗物已分发但跳过拾取效果 ({batch.Count} 个)");
                    break;
                }

                bool reopenMapAfterSelection = false;
                try
                {
                    // 本机需要弹出选择面板（批次里含本地玩家遗物）时，若地图打开，
                    // NOverlayStack 会隐藏面板，因此先关闭地图保证可见；
                    // 处理完成后若仍处在本局开局流程中再重新打开地图（参考海克斯符文mod同款处理）。
                    bool showLocalPanels = batch.Any(p => LocalContext.IsMe(p.Player));
                    if (showLocalPanels && NMapScreen.Instance is { IsOpen: true } && NGame.Instance != null)
                    {
                        NMapScreen.Instance.Close(animateOut: false);
                        reopenMapAfterSelection = true;
                        await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
                    }

                    // 按同一顺序逐个执行拾取效果。
                    // 星盘/沉重石板等效果消耗全队共享的 RNG 流（RunState.Rng.Niche），
                    // 执行顺序一旦各端不一致就会进入战斗时 checksum 分叉，因此绝不能按“本机优先”乱序。
                    foreach (PendingRelic pending in batch)
                    {
                        try
                        {
                            if (!IsCurrentRun(pending.Player))
                            {
                                Logger.Warn($"[CustomStart] 开局遗物拾取效果跳过：开局已切换/结束 ({pending.Relic.GetType().Name})");
                                continue;
                            }

                            if (CombatManager.Instance is { IsInProgress: true })
                            {
                                Logger.Warn($"[CustomStart] 战斗已开始，跳过 {pending.Relic.GetType().Name} 的拾取选择面板（遗物已分发）");
                                continue;
                            }

                            Logger.Info($"[CustomStart] 开始执行遗物拾取效果: {pending.Relic.GetType().Name}");
                            bool completed = await ProcessRelicWithAbandonGuardAsync(pending);
                            Logger.Info($"[CustomStart] 遗物拾取效果完成: {pending.Relic.GetType().Name}");
                            if (completed)
                            {
                                try
                                {
                                    var state = RunManager.Instance?.DebugOnlyGetState();
                                    if (state != null)
                                    {
                                        int idx = state.Players.ToList().IndexOf(pending.Player);
                                        string name = pending.Relic.GetType().Name;
                                        int occurrence = pending.Player.Relics
                                            .Where(r => r.GetType().Name == name)
                                            .ToList()
                                            .IndexOf(pending.Relic);
                                        bool allDone = PendingRelicMarker.RemoveCompleted(state, idx, name, occurrence);
                                        // 全部拾取效果完成且标记清空时立即落盘，
                                        // 避免“完成面板后立即保存退出”留下效果前的旧存档。
                                        if (allDone)
                                        {
                                            try { TaskHelper.RunSafely(SaveManager.Instance.SaveRun(null)); }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[CustomStart] 开局遗物拾取效果失败 {pending.Relic.GetType().Name}: {ex}");
                        }
                    }
                }
                finally
                {
                    if (reopenMapAfterSelection
                        && IsRunActive()
                        && NMapScreen.Instance != null
                        && !NMapScreen.Instance.IsOpen)
                    {
                        NMapScreen.Instance.Open();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 开局遗物拾取效果队列执行失败: {ex.Message}");
        }
        finally
        {
            _runnerRunning = false;
        }
    }

    private static void TryAddRelic(PendingRelic pending)
    {
        if (pending.Player.Relics.Contains(pending.Relic)) return;
        pending.Relic.FloorAddedToDeck = 1;
        pending.Player.AddRelicInternal(pending.Relic, -1, false);
    }

    /// <summary>
    /// 执行单个遗物的 AfterObtained；若其选择面板尚未完成时本局已经结束/切换，
    /// 则放弃剩余效果而不是永久悬挂（防止队列被卡死影响下一局）。
    /// </summary>
    /// <returns>true=效果已执行完成；false=中途放弃（本局结束/切换）。</returns>
    private static async Task<bool> ProcessRelicWithAbandonGuardAsync(PendingRelic pending)
    {
        // 注意：不能以 HasUponPickupEffect 判断是否调用 AfterObtained——
        // 华美手镯/大抱抱等遗物重写了 AfterObtained 但没有重写该属性（基类默认为 false），
        // 原版 FinalizeStartingRelics 与 RelicCmd.Obtain 都是无条件调用，基类实现本身是空操作。
        using var cts = new System.Threading.CancellationTokenSource();
        Task pickup = pending.Relic.AfterObtained();
        Task runEnded = WaitUntilRunEndsAsync(pending.Player, cts.Token);
        Task done = await Task.WhenAny(pickup, runEnded);
        if (done == pickup)
        {
            cts.Cancel();
            await pickup; // 完成或抛出
            return true;
        }

        cts.Cancel();
        Logger.Warn($"[CustomStart] {pending.Relic.GetType().Name} 的选择面板未完成时本局已结束，放弃剩余拾取效果");
        return false;
    }

    private static async Task WaitUntilRunEndsAsync(Player player, System.Threading.CancellationToken token)
    {
        const int MaxFrames = 60 * 60;
        for (int frame = 0; frame < MaxFrames; frame++)
        {
            if (token.IsCancellationRequested) return;
            if (!IsCurrentRun(player) || NGame.Instance == null)
            {
                return;
            }
            await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>
    /// 继续游戏后：等 NRun 就绪且房间落定，读取标记并重新入队未完成的拾取效果。
    /// 顺序保持标记中的原分发顺序（全端一致）。
    /// </summary>
    private static async Task RestorePendingAfterLoadAsync(RunState runState)
    {
        try
        {
            if (!await WaitForLoadedRunUiReadyAsync(runState)) return;

            var marker = PendingRelicMarker.Load();
            if (marker == null || !marker.Matches(runState))
            {
                // 残留标记与当前局不符（旧局已结束/新开局），清理避免误触发
                PendingRelicMarker.DeleteIfExists();
                return;
            }

            foreach (var entry in marker.Pending.ToList())
            {
                if (entry.PlayerIndex < 0 || entry.PlayerIndex >= runState.Players.Count) continue;
                var player = runState.Players[entry.PlayerIndex];
                if (player == null) continue;
                var sameType = player.Relics
                    .Where(r => r.GetType().Name == entry.RelicTypeName)
                    .ToList();
                if (entry.OccurrenceIndex < 0 || entry.OccurrenceIndex >= sameType.Count) continue;
                var relic = sameType[entry.OccurrenceIndex];
                Enqueue(player, relic);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CustomStart] 恢复未完成的遗物拾取效果失败: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForLoadedRunUiReadyAsync(RunState runState)
    {
        const int MaxFrames = 60 * 20;
        for (int frame = 0; frame < MaxFrames; frame++)
        {
            if (IsLoadedRunUiReady(runState)) return true;
            if (NGame.Instance == null) return false;
            await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private static bool IsLoadedRunUiReady(RunState runState)
    {
        if (LocalContext.NetId == null) return false;
        NRun? nRun = NRun.Instance;
        if (nRun == null || !GodotObject.IsInstanceValid(nRun)) return false;
        if (nRun.GlobalUi?.Overlays == null || nRun.GlobalUi?.TopBar == null) return false;
        RunState? state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null || !ReferenceEquals(state, runState)) return false;
        if (NMapScreen.Instance == null) return false;
        if (NMapScreen.Instance.IsOpen) return true;
        AbstractRoom? room = state.CurrentRoom;
        return room != null && room is not MapRoom;
    }

    private static async Task<bool> WaitUntilRunUiReadyAsync(List<PendingRelic> batch, bool requireRoomSettled)
    {
        const int MaxFrames = 60 * 20;
        for (int frame = 0; frame < MaxFrames; frame++)
        {
            if (IsRunUiReady(batch, requireRoomSettled)) return true;
            if (NGame.Instance == null) return false;
            await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private static bool IsRunUiReady(List<PendingRelic> batch, bool requireRoomSettled)
    {
        if (LocalContext.NetId == null) return false;
        NRun? nRun = NRun.Instance;
        if (nRun == null || !GodotObject.IsInstanceValid(nRun)) return false;
        if (nRun.GlobalUi?.Overlays == null || nRun.GlobalUi?.TopBar == null) return false;
        RunState? state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null) return false;
        if (!batch.All(p => ReferenceEquals(p.Player.RunState, state))) return false;

        // 分发阶段只需 NRun UI 就绪即可，无需等房间落定，
        // 确保在开局首次存档之前完成遗物加入。
        if (!requireRoomSettled) return true;

        // 等开局房间落定再弹面板：EnterAct 过程中（地图未开、房间未进）若过早处理，
        // 选择面板会被随后打开的地图隐藏或与房间切换竞争。
        if (NMapScreen.Instance == null) return false;
        if (NMapScreen.Instance.IsOpen) return true;
        AbstractRoom? room = state.CurrentRoom;
        return room != null && room is not MapRoom;
    }

    private static bool IsCurrentRun(Player player)
    {
        RunState? state = RunManager.Instance?.DebugOnlyGetState();
        return state != null
            && ReferenceEquals(player.RunState, state)
            && state.Players.Contains(player);
    }

    private static bool IsRunActive()
    {
        return RunManager.Instance?.DebugOnlyGetState() != null
            && NRun.Instance != null
            && GodotObject.IsInstanceValid(NRun.Instance);
    }

    private static List<PendingRelic> TakePending()
    {
        if (Pending.Count == 0) return new List<PendingRelic>();
        List<PendingRelic> batch = Pending.ToList();
        Pending.Clear();
        return batch;
    }
}
