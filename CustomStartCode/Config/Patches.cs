// 小格子铺 | Latticeshop
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using CustomStartCode.Common;

namespace CustomStartCode.Config;

/// <summary>
/// 定制开局补丁 - 在游戏流程中应用配置（自定义初始卡组 + 自定义初始遗物；
/// 支持联机：按玩家同步各自配置，或强制全队应用房主配置）
/// </summary>
public static class Patches
{
    private static readonly MegaCrit.Sts2.Core.Logging.Logger Logger = new("CustomStartPatches", MegaCrit.Sts2.Core.Logging.LogType.Generic);

    private const string MenuButtonName = "CustomStartConfigButton";
    private const int NativeDuplicateFlags = 14;
    private const string LocTable = "characters";

    private static readonly Type? MainMenuType = FindType("NMainMenu", "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu");
    private static readonly Type? MainMenuTextButtonType = FindType("NMainMenuTextButton", "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu");
    private static readonly Type? ClickableControlType = FindType("NClickableControl", "MegaCrit.Sts2.Core.Nodes");
    private static readonly Type? SignalNameType = FindSignalNameType();
    private static readonly string? FocusedSignalName = GetSignalName("Focused");
    private static readonly string? UnfocusedSignalName = GetSignalName("Unfocused");
    private static readonly string ReleasedSignalName = GetSignalName("Released") ?? "pressed";
    private static readonly FieldInfo? LastHitButtonField = MainMenuType != null
        ? AccessTools.Field(MainMenuType, "_lastHitButton")
        : null;
    private static readonly FieldInfo? LocStringField = MainMenuTextButtonType != null
        ? AccessTools.Field(MainMenuTextButtonType, "_locString")
        : null;
    private static readonly MethodInfo? ButtonFocusedMethod = MainMenuType != null && MainMenuTextButtonType != null
        ? AccessTools.Method(MainMenuType, "MainMenuButtonFocused", new[] { MainMenuTextButtonType! })
        : null;
    private static readonly MethodInfo? ButtonUnfocusedMethod = MainMenuType != null && MainMenuTextButtonType != null
        ? AccessTools.Method(MainMenuType, "MainMenuButtonUnfocused", new[] { MainMenuTextButtonType! })
        : null;

    private static Type? FindSignalNameType()
    {
        if (ClickableControlType == null) return null;
        return ClickableControlType.GetNestedTypes()
            .FirstOrDefault(t => t.Name == "SignalName");
    }

    private static string? GetSignalName(string fieldName)
    {
        if (SignalNameType == null) return null;
        var field = SignalNameType.GetField(fieldName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field == null) return null;
        var value = field.GetValue(null);
        if (value is string str) return str;
        if (value is StringName sn) return sn.ToString();
        return null;
    }

    private static string L(string key, params object[] args)
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

    /// <summary>
    /// 安装所有配置补丁
    /// </summary>
    public static void Install(HarmonyLib.Harmony harmony)
    {
        // 补丁1: 拦截初始卡组/遗物创建
        // 真实初始牌组在 Player.CreateForNewRun -> PopulateStartingInventory() 中依次生成 牌组/遗物/药水。
        // 在 Postfix 中替换牌组/遗物，此时所有 PopulateStarting* 已执行完毕。
        try
        {
            var populateInventoryMethod = AccessTools.Method(typeof(Player), "PopulateStartingInventory");
            if (populateInventoryMethod != null)
            {
                harmony.Patch(
                    original: populateInventoryMethod,
                    postfix: new HarmonyMethod(typeof(InitialDeckPatch), nameof(InitialDeckPatch.Postfix))
                );
                Logger.Info("[CustomStart] 初始卡组/遗物补丁安装成功 (Player.PopulateStartingInventory)");
            }
            else
            {
                Logger.Warn("[CustomStart] 找不到 Player.PopulateStartingInventory 方法");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 初始卡组补丁安装失败: {ex.Message}");
        }

        // 补丁2: 在主菜单添加入口按钮（参考海克斯符文mod实现方式）
        try
        {
            if (MainMenuType != null)
            {
                var readyMethod = AccessTools.Method(MainMenuType, "_Ready");
                if (readyMethod != null)
                {
                    harmony.Patch(
                        original: readyMethod,
                        postfix: new HarmonyMethod(typeof(MainMenuPatch), nameof(MainMenuPatch.Postfix))
                    );
                    Logger.Info("[CustomStart] 主菜单补丁安装成功");
                }
            }
            else
            {
                Logger.Warn("[CustomStart] 找不到 NMainMenu 类型");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 主菜单补丁安装失败: {ex.Message}");
        }

        // 补丁3: 多人开局设置 - 缓存玩家列表、广播本机配置；
        // 开启“强制全部应用房主配置”时由房主广播整套配置，同步完成后各端统一应用。
        try
        {
            var setupMultiplayerMethod = AccessTools.Method(typeof(RunManager), "SetUpNewMultiplayer");
            if (setupMultiplayerMethod != null)
            {
                harmony.Patch(
                    original: setupMultiplayerMethod,
                    postfix: new HarmonyMethod(typeof(RunStartPatch), nameof(RunStartPatch.Postfix))
                );
                Logger.Info("[CustomStart] 多人开局设置补丁安装成功 (RunManager.SetUpNewMultiplayer)");
            }
            else
            {
                Logger.Warn("[CustomStart] 找不到 RunManager.SetUpNewMultiplayer");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 多人开局设置补丁安装失败: {ex.Message}");
        }

        // 补丁3.5: 多人大厅初始化/关闭 - 标记多人会话，并挂载/清理“强制房主配置”面板
        try
        {
            var charSelectType = typeof(NCharacterSelectScreen);
            var hostInitMethod = AccessTools.Method(charSelectType, "InitializeMultiplayerAsHost");
            var clientInitMethod = AccessTools.Method(charSelectType, "InitializeMultiplayerAsClient");
            var closedMethod = AccessTools.Method(charSelectType, "OnSubmenuClosed");
            if (hostInitMethod != null)
            {
                harmony.Patch(hostInitMethod, postfix: new HarmonyMethod(typeof(MultiplayerLobbyPatch), nameof(MultiplayerLobbyPatch.HostPostfix)));
            }
            if (clientInitMethod != null)
            {
                harmony.Patch(clientInitMethod, postfix: new HarmonyMethod(typeof(MultiplayerLobbyPatch), nameof(MultiplayerLobbyPatch.ClientPostfix)));
            }
            if (closedMethod != null)
            {
                harmony.Patch(closedMethod, postfix: new HarmonyMethod(typeof(MultiplayerLobbyPatch), nameof(MultiplayerLobbyPatch.LobbyClosedPostfix)));
            }
            Logger.Info("[CustomStart] 多人大厅会话标记补丁安装成功");
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 多人大厅会话标记补丁安装失败: {ex.Message}");
        }

        // 补丁3.6: 大厅面板挂载/清理 - 在所有玩家的联机大厅页展示“强制房主配置”面板
        try
        {
            var containerType = typeof(NRemoteLobbyPlayerContainer);
            var initMethod = AccessTools.Method(containerType, "Initialize", new[] { typeof(StartRunLobby), typeof(bool) });
            var cleanupMethod = AccessTools.Method(containerType, "Cleanup");
            // OnPlayerConnected 只有一个重载，按方法名查找即可（避免依赖具体参数类型名）
            var playerConnectedMethod = AccessTools.Method(containerType, "OnPlayerConnected");
            if (initMethod != null)
            {
                harmony.Patch(initMethod, postfix: new HarmonyMethod(typeof(LobbyPanelPatch), nameof(LobbyPanelPatch.InitializePostfix)));
            }
            if (cleanupMethod != null)
            {
                harmony.Patch(cleanupMethod, postfix: new HarmonyMethod(typeof(LobbyPanelPatch), nameof(LobbyPanelPatch.CleanupPostfix)));
            }
            if (playerConnectedMethod != null)
            {
                harmony.Patch(playerConnectedMethod, postfix: new HarmonyMethod(typeof(LobbyPanelPatch), nameof(LobbyPanelPatch.PlayerConnectedPostfix)));
            }
            Logger.Info("[CustomStart] 大厅面板补丁安装成功");
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 大厅面板补丁安装失败: {ex.Message}");
        }

        // 补丁4: 遗物检视页层级 - 打开时临时降低 mod 配置面板/遗物库层级，关闭后恢复
        try
        {
            var inspectOpenMethod = AccessTools.Method(typeof(NInspectRelicScreen), "Open");
            var inspectCloseMethod = AccessTools.Method(typeof(NInspectRelicScreen), "Close");
            if (inspectOpenMethod != null)
            {
                harmony.Patch(
                    original: inspectOpenMethod,
                    postfix: new HarmonyMethod(typeof(RelicInspectZOrderPatch), nameof(RelicInspectZOrderPatch.OpenPostfix))
                );
            }
            if (inspectCloseMethod != null)
            {
                harmony.Patch(
                    original: inspectCloseMethod,
                    postfix: new HarmonyMethod(typeof(RelicInspectZOrderPatch), nameof(RelicInspectZOrderPatch.ClosePostfix))
                );
            }
            if (inspectOpenMethod != null || inspectCloseMethod != null)
            {
                Logger.Info("[CustomStart] 遗物检视页层级补丁安装成功");
            }
            else
            {
                Logger.Warn("[CustomStart] 找不到 NInspectRelicScreen.Open/Close");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[CustomStart] 遗物检视页层级补丁安装失败: {ex.Message}");
        }

        Logger.Info("[CustomStart] 配置补丁安装完成");
    }

    private static Type? FindType(string name, string? ns = null)
    {
        if (ns != null)
        {
            var type = Type.GetType($"{ns}.{name}, sts2");
            if (type != null)
            {
                Logger.Info($"[CustomStart] FindType: Found {ns}.{name} via Assembly.GetType");
                return type;
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetType(name);
                if (type != null)
                {
                    Logger.Info($"[CustomStart] FindType: Found {name} in {asm.GetName().Name} (exact)");
                    return type;
                }
            }
            catch { }

            if (ns != null)
            {
                try
                {
                    var type = asm.GetType($"{ns}.{name}");
                    if (type != null)
                    {
                        Logger.Info($"[CustomStart] FindType: Found {ns}.{name} in {asm.GetName().Name}");
                        return type;
                    }
                }
                catch { }
            }
        }

        // Fallback: search all types by short name
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetTypes().FirstOrDefault(t => t.Name == name);
                if (type != null)
                {
                    Logger.Info($"[CustomStart] FindType: Found {name} via short name in {asm.GetName().Name} ({type.FullName})");
                    return type;
                }
            }
            catch { }
        }

        Logger.Warn($"[CustomStart] FindType: Type {name} not found{(ns != null ? $" in namespace {ns}" : "")}");
        return null;
    }

    /// <summary>
    /// 初始卡组补丁 - 开局时应用自定义初始卡组和自定义初始遗物
    /// </summary>
    public static class InitialDeckPatch
    {
        public static void Postfix(Player __instance)
        {
            try
            {
                if (__instance?.Character == null) return;
                // 多人联机：各端在开局时按各自配置建牌组会分叉，
                // 改为开局同步完成后由 RunStartPatch 统一调用 ApplyConfigToPlayer。
                if (ConfigManager.IsMultiplayerSession) return;
                ApplyConfigToPlayer(__instance);
            }
            catch (Exception ex)
            {
                Logger.Error($"[CustomStart] InitialDeckPatch.Postfix 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 按玩家配置应用初始牌组 / 初始遗物（单机直接调用，多人同步完成后也调用）。
        /// </summary>
        public static void ApplyConfigToPlayer(Player __instance, RunState? runState = null)
        {
            try
            {
                if (__instance?.Character == null) return;
                var config = ConfigManager.GetConfigForPlayer(__instance);
                if (config == null) return;
                string characterId = config.CharacterId;

                // 自定义初始卡组：清空默认牌组并按配置重建
                if (config.EnableCustomDeck && config.CustomDeckCardTypes.Count > 0)
                {
                    var customDeck = CreateCustomDeck(config);
                    if (customDeck.Count > 0)
                    {
                        __instance.Deck.Clear(silent: true);
                        foreach (CardModel card in customDeck)
                        {
                            card.FloorAddedToDeck = 1;
                            __instance.Deck.AddInternal(card, -1, true);
                            // 多人延迟应用时 RunState 已建立，需通过 AddCard 正规注册
                            // （设置 Owner 并加入运行状态），否则 Hook 遍历会因 Owner 为空 NRE；
                            // 单人局由 CreateShared 统一赋 Owner，不能预置。
                            if (runState != null)
                            {
                                try { runState.AddCard(card, __instance); } catch { }
                            }
                        }
                        Logger.Info($"[CustomStart] 已应用自定义卡组，角色: {characterId}, 卡牌数: {customDeck.Count}");
                    }
                }

                // 自定义初始遗物 - 用配置的遗物替换默认初始遗物
                if (config.EnableCustomRelics && config.StartingRelicTypes.Count > 0)
                {
                    ApplyCustomRelics(__instance, config);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[CustomStart] ApplyConfigToPlayer 失败: {ex.Message}");
            }
        }

        private static void ApplyCustomRelics(Player player, CharacterConfig config)
        {
            try
            {
                var newRelics = new List<RelicModel>();
                foreach (var relicTypeName in config.StartingRelicTypes)
                {
                    try
                    {
                        var relicType = FindCardType(relicTypeName);
                        if (relicType == null) continue;
                        var relic = GetRelicModel(relicType);
                        if (relic != null)
                        {
                            newRelics.Add(relic.ToMutable());
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[CustomStart] 创建遗物失败 {relicTypeName}: {ex.Message}");
                    }
                }

                if (newRelics.Count == 0)
                {
                    Logger.Warn("[CustomStart] 自定义遗物配置为空或全部无效，保留默认初始遗物");
                    return;
                }

                // 清空默认初始遗物后按配置授予。
                // 注意：必须用 silent:false 走 RelicObtained/RelicRemoved 事件，
                // 让顶部遗物栏（NRelicInventory）与玩家遗物列表保持同步；
                // 否则后续获得遗物（NEOW/事件/奖励）时 UI 索引越界崩溃。
                foreach (var old in player.Relics.ToList())
                {
                    try { player.RemoveRelicInternal(old, silent: false); } catch { }
                }
                foreach (var relic in newRelics)
                {
                    relic.FloorAddedToDeck = 1;
                    try { SaveManager.Instance.MarkRelicAsSeen(relic); } catch { }
                    player.AddRelicInternal(relic, -1, false);
                }
                // 事件添加的图标默认隐藏（startsShown=false），这里把本地玩家遗物栏图标恢复可见
                RevealLocalRelicInventoryIcons(player);
                Logger.Info($"[CustomStart] 已应用自定义初始遗物，角色: {player.Character?.Id?.Entry}, 遗物数: {newRelics.Count}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[CustomStart] ApplyCustomRelics 失败: {ex.Message}");
            }
        }

        private static void RevealLocalRelicInventoryIcons(Player player)
        {
            try
            {
                if (!LocalContext.IsMe(player)) return;
                var nRun = NRun.Instance;
                if (nRun == null || !GodotObject.IsInstanceValid(nRun)) return;
                var inventory = nRun.GlobalUi?.RelicInventory;
                if (inventory == null || !GodotObject.IsInstanceValid(inventory)) return;
                foreach (var holder in inventory.RelicNodes)
                {
                    try
                    {
                        var icon = holder.Relic?.Icon;
                        if (icon == null || !GodotObject.IsInstanceValid(icon)) continue;
                        Color modulate = icon.Modulate;
                        modulate.A = 1f;
                        icon.Modulate = modulate;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static RelicModel? GetRelicModel(Type relicType)
        {
            try
            {
                var relicMethod = typeof(ModelDb).GetMethods()
                    .FirstOrDefault(m => m.Name == "Relic" && m.IsGenericMethodDefinition);
                if (relicMethod != null)
                {
                    var genericMethod = relicMethod.MakeGenericMethod(relicType);
                    return genericMethod.Invoke(null, null) as RelicModel;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[CustomStart] 获取遗物模型失败: {ex.Message}");
            }
            return null;
        }

        private static List<CardModel> CreateCustomDeck(CharacterConfig config)
        {
            var deck = new List<CardModel>();
            var cardCounts = new Dictionary<string, int>();

            foreach (var cardTypeName in config.CustomDeckCardTypes)
            {
                cardCounts.TryGetValue(cardTypeName, out int count);
                cardCounts[cardTypeName] = count + 1;
            }

            foreach (var (cardTypeName, count) in cardCounts)
            {
                try
                {
                    string typeName = ConfigManager.DecodeCardType(cardTypeName, out bool upgraded);
                    var cardType = FindCardType(typeName);
                    if (cardType != null)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var cardModel = GetCardModel(cardType);
                            if (cardModel != null)
                            {
                                var card = cardModel.ToMutable();
                                if (upgraded && !card.IsUpgraded)
                                {
                                    CardCmd.Upgrade(card);
                                }
                                deck.Add(card);
                            }
                        }
                    }
                    else
                    {
                        Logger.Warn($"[CustomStart] 找不到卡牌类型: {typeName}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[CustomStart] 创建卡牌失败 {cardTypeName}: {ex.Message}");
                }
            }

            return deck;
        }

        private static Type? FindCardType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // 1) 尝试全名（含命名空间）
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(typeName);
                    if (type != null) return type;
                }
                catch { }
            }

            // 2) 原版卡牌命名空间
            string[] namespaces =
            {
                "MegaCrit.Sts2.Core.Models.Cards",
            };
            foreach (var ns in namespaces)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var type = asm.GetType($"{ns}.{typeName}");
                        if (type != null) return type;
                    }
                    catch { }
                }
            }

            // 3) 短名扫描（兼容原版卡牌，如 Wound）
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetTypes().FirstOrDefault(t => t.Name == typeName);
                    if (type != null) return type;
                }
                catch { }
            }

            return null;
        }

        private static CardModel? GetCardModel(Type cardType)
        {
            try
            {
                // 使用反射调用泛型方法 ModelDb.Card<T>()
                var cardMethod = typeof(ModelDb).GetMethods()
                    .FirstOrDefault(m => m.Name == "Card" && m.IsGenericMethodDefinition);

                if (cardMethod != null)
                {
                    var genericMethod = cardMethod.MakeGenericMethod(cardType);
                    return genericMethod.Invoke(null, null) as CardModel;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[CustomStart] 获取卡牌模型失败: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>
    /// 主菜单补丁 - 在主菜单添加入口按钮（参考海克斯符文mod实现）
    /// </summary>
    public static class MainMenuPatch
    {
        private const int MaxAttachAttempts = 30;

        public static void Postfix(Node __instance)
        {
            if (MainMenuType == null) return;

            // 检查是否已添加过
            if (__instance.FindChild(MenuButtonName, recursive: true, owned: false) != null)
                return;

            // 使用TaskHelper异步等待UI完全初始化
            TaskHelper.RunSafely(AttachButtonWhenReadyAsync(__instance));
        }

        private static async Task AttachButtonWhenReadyAsync(Node mainMenu)
        {
            for (int attempt = 1; attempt <= MaxAttachAttempts; attempt++)
            {
                if (!GodotObject.IsInstanceValid(mainMenu))
                    return;

                try
                {
                    if (TryAttachButton(mainMenu))
                        return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[CustomStart] 主菜单按钮安装失败: {ex.Message}");
                    return;
                }

                if (!await AwaitProcessFrameAsync(mainMenu))
                    return;
            }

            Logger.Warn("[CustomStart] 主菜单按钮安装超时");
        }

        private static bool TryAttachButton(Node host)
        {
            // 检查是否已存在
            if (host.FindChild(MenuButtonName, recursive: true, owned: false) is Node existing
                && GodotObject.IsInstanceValid(existing))
                return true;

            if (TryAttachNativeMenuButton(host))
            {
                Logger.Info("[CustomStart] 主菜单配置按钮已添加");
                return true;
            }

            Logger.Warn("[CustomStart] 主菜单按钮安装失败：找不到原生菜单按钮");
            return false;
        }

        private static bool TryAttachNativeMenuButton(Node mainMenu)
        {
            if (MainMenuTextButtonType == null || LocStringField == null)
                return false;

            // 查找主菜单按钮容器
            var buttonHost = mainMenu.GetNodeOrNull<Control>("MainMenuTextButtons");
            if (buttonHost == null)
            {
                // 尝试查找按钮容器
                buttonHost = TryFindButtonContainer(mainMenu);
                if (buttonHost == null) return false;
            }

            // 查找SettingsButton作为模板
            Node? settingsButton = null;
            if (mainMenu.GetNodeOrNull("MainMenuTextButtons/SettingsButton") is Node btn)
            {
                settingsButton = btn;
            }

            // 如果找不到SettingsButton，用容器中第一个按钮作为模板
            if (settingsButton == null && buttonHost != null)
            {
                settingsButton = buttonHost.GetChildren()
                    .FirstOrDefault(c => MainMenuTextButtonType.IsInstanceOfType(c));
            }

            if (settingsButton == null)
            {
                Logger.Warn("[CustomStart] 找不到按钮模板");
                return false;
            }

            // 使用Duplicate复制按钮
            var configBtn = settingsButton.Duplicate(NativeDuplicateFlags);
            ((Node)configBtn).Name = MenuButtonName;
            ((Node)configBtn).UniqueNameInOwner = true;

            // 添加到容器
            buttonHost.AddChild(configBtn);
            buttonHost.MoveChild(configBtn, Math.Min(settingsButton.GetIndex() + 1, buttonHost.GetChildCount() - 1));

            // 配置标签（设置本地化文本）
            ConfigureNativeMenuLabel(configBtn);

            // 配置按钮属性
            ConfigureNativeMenuButton(configBtn, settingsButton);

            // 配置焦点事件
            ConfigureNativeMenuFocus(mainMenu, configBtn);

            // 连接点击事件
            ConnectNativeMenuButton(configBtn);

            return true;
        }

        private static void ConfigureNativeMenuLabel(Node configButton)
        {
            // 清除locString以使用自定义文本
            LocStringField?.SetValue(configButton, null);

            if (configButton.GetChildCount() > 0 && configButton.GetChild(0) is Label label)
            {
                label.Text = L("CONFIG_MENU_TITLE");
                label.PivotOffset = label.Size * 0.5f;
            }

            if (configButton is Control ctrl)
            {
                ctrl.TooltipText = L("CONFIG_MENU_TITLE_TOOLTIP");
            }
        }

        private static void ConfigureNativeMenuButton(Node configButton, Node template)
        {
            if (configButton is Control control && template is Control templateCtrl)
            {
                control.MouseFilter = Control.MouseFilterEnum.Stop;
                control.FocusMode = Control.FocusModeEnum.All;
                control.MouseDefaultCursorShape = templateCtrl.MouseDefaultCursorShape;
                control.SizeFlagsHorizontal = templateCtrl.SizeFlagsHorizontal;
                control.SizeFlagsVertical = templateCtrl.SizeFlagsVertical;
                control.CustomMinimumSize = templateCtrl.CustomMinimumSize;
                control.ZIndex = templateCtrl.ZIndex;
                control.ZAsRelative = templateCtrl.ZAsRelative;
            }
        }

        private static void ConfigureNativeMenuFocus(Node mainMenu, Node configButton)
        {
            // NClickableControl.RefreshFocus 发出的 Focused/Unfocused 信号带 1 个参数（控件本身），
            // 必须用 Callable.From<T>(...) 匹配参数个数，否则报 "Expected 0 argument(s), received 1"。
            if (ButtonFocusedMethod != null && FocusedSignalName != null)
            {
                ((GodotObject)configButton).Connect(
                    FocusedSignalName,
                    Callable.From<GodotObject>(_ =>
                    {
                        ButtonFocusedMethod.Invoke(mainMenu, new[] { configButton });
                    }));
            }

            if (ButtonUnfocusedMethod != null && UnfocusedSignalName != null)
            {
                ((GodotObject)configButton).Connect(
                    UnfocusedSignalName,
                    Callable.From<GodotObject>(_ =>
                    {
                        ButtonUnfocusedMethod.Invoke(mainMenu, new[] { configButton });
                    }));
            }
        }

        private static void ConnectNativeMenuButton(Node configButton)
        {
            // Match Hextech pattern exactly: NClickableControl.SignalName.Released + Callable.From<NButton>
            // The Released signal expects an NButton parameter; using a parameterless callable
            // causes Godot to silently skip the callback in this game's signal system.
            try
            {
                ((GodotObject)configButton).Connect(
                    NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ =>
                    {
                        Logger.Info("[CustomStart] Config button clicked (Released)");

                        if (LastHitButtonField != null)
                        {
                            var mainMenu = FindAncestorByType(configButton, MainMenuType!);
                            if (mainMenu != null)
                            {
                                LastHitButtonField.SetValue(mainMenu, configButton);
                            }
                        }

                        OpenOverlay(configButton);
                    }));
                Logger.Info("[CustomStart] Connected to NClickableControl.SignalName.Released");
            }
            catch (Exception ex)
            {
                Logger.Error($"[CustomStart] Failed to connect Released signal: {ex.Message}");
            }
        }

        private static void OpenOverlay(Node source)
        {
            try
            {
                // Match Hextech's ResolveRoot pattern: use the source button's scene tree root
                Node root = source.GetTree()?.Root is Node r ? r : source;
                ConfigPanel.Show(root);
                Logger.Info("[CustomStart] Panel shown");
            }
            catch (Exception ex)
            {
                Logger.Error($"[CustomStart] Failed to show panel: {ex.Message}");
            }
        }

        private static string GetSignalName(string constantName, string fallback)
        {
            try
            {
                if (ClickableControlType != null)
                {
                    var signalNameField = ClickableControlType.GetNestedTypes()
                        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                        .FirstOrDefault(f => f.Name == constantName);

                    if (signalNameField != null)
                    {
                        var value = signalNameField.GetValue(null);
                        if (value is string str)
                            return str;
                        if (value is StringName sn)
                            return sn.ToString();
                    }
                }
            }
            catch { }

            return fallback;
        }

        private static Control? TryFindButtonContainer(Node host)
        {
            // 递归查找包含多个按钮的容器
            foreach (var child in host.GetChildren())
            {
                if (child is Control ctrl && ctrl.GetChildCount() >= 3)
                {
                    bool hasButtons = ctrl.GetChildren()
                        .Any(c => c.GetType().Name.Contains("Button"));
                    if (hasButtons)
                        return ctrl;
                }
            }

            foreach (var child in host.GetChildren())
            {
                if (child.GetChildCount() > 0)
                {
                    var found = TryFindButtonContainer(child);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static Node? FindAncestorByType(Node node, Type type)
        {
            var current = node.GetParent();
            while (current != null)
            {
                if (type.IsInstanceOfType(current))
                    return current;
                current = current.GetParent();
            }
            return null;
        }

        private static async Task<bool> AwaitProcessFrameAsync(Node node)
        {
            try
            {
                int frameCount = 0;
                while (frameCount < 60)
                {
                    await Task.Delay(16);
                    if (!GodotObject.IsInstanceValid(node))
                        return false;
                    frameCount++;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 遗物检视页层级补丁：打开详情时临时降低 mod 配置面板/遗物库层级，
    /// 关闭后恢复，保证详情页在 mod 配置页面之上。
    /// </summary>
    public static class RelicInspectZOrderPatch
    {
        public static void OpenPostfix()
        {
            ConfigPanel.OnInspectScreenOpened();
            RelicLibraryTab.OnInspectScreenOpened();
        }

        public static void ClosePostfix()
        {
            ConfigPanel.OnInspectScreenClosed();
            RelicLibraryTab.OnInspectScreenClosed();
        }
    }

    /// <summary>
    /// 开局补丁 - 多人开局时广播本机配置，并按同步结果统一应用；
    /// “强制全部应用房主配置”开启时由房主广播整套配置，所有人本局都按房主配置开局。
    /// </summary>
    public static class RunStartPatch
    {
        public static void Postfix(RunState state)
        {
            try
            {
                // SetUpNewMultiplayer 只会在多人开局时调用，这里确保会话标记置位
                // （即使大厅阶段标记缺失也能自愈）
                ConfigManager.SetMultiplayerSession(true);
                ConfigManager.SetRunPlayers(state?.Players);
                // 多人开局：把本机本地玩家的配置广播（同步后全端应用）
                ConfigManager.BroadcastAllLocalConfigs();

                if (ConfigManager.IsMultiplayerSession && state != null)
                {
                    // 强制房主配置模式：仅房主广播整套配置，避免各端用各自本地配置互相覆盖
                    if (ConfigManager.IsForceHostConfigEnabled && MultiplayerSyncHelper.IsHost())
                    {
                        Player? local = state.Players.FirstOrDefault(p =>
                        {
                            try { return MultiplayerSyncHelper.IsLocalPlayer(p); }
                            catch { return false; }
                        });
                        if (local != null)
                        {
                            RunManager.Instance?.ActionQueueSynchronizer?.RequestEnqueue(
                                new HostForceConfigSyncGameAction(local, ConfigManager.GetAllConfigs()));
                            Logger.Info("[CustomStart] 强制房主配置模式：房主已广播整套配置");
                        }
                    }

                    // 等待各玩家配置（或房主整套配置）同步到位后统一应用
                    TaskHelper.RunSafely(ApplyConfigsAfterSyncAsync(state));
                }
                Logger.Info("[CustomStart] RunStartPatch: 开局设置完成，配置已广播");
            }
            catch (Exception ex)
            {
                Logger.Error($"[CustomStart] RunStartPatch 失败: {ex.Message}");
            }
        }

        private static async Task ApplyConfigsAfterSyncAsync(RunState state)
        {
            try
            {
                var applied = new HashSet<ulong>();
                DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    bool allReady = true;
                    foreach (var player in state.Players)
                    {
                        if (player == null || applied.Contains(player.NetId)) continue;

                        // 战斗开始后不再应用，避免中途改牌组/遗物引发分叉
                        try
                        {
                            if (CombatManager.Instance != null && CombatManager.Instance.IsInProgress)
                            {
                                Logger.Info("[CustomStart] 战斗已开始，未同步配置留待下一局应用");
                                return;
                            }
                        }
                        catch { }

                        bool ready = ConfigManager.IsForceHostConfigEnabled
                            ? ConfigManager.HasForcedHostConfigs()
                            : ConfigManager.HasConfigForPlayer(player);
                        if (!ready)
                        {
                            allReady = false;
                            continue;
                        }

                        try
                        {
                            InitialDeckPatch.ApplyConfigToPlayer(player, state);
                            applied.Add(player.NetId);
                            Logger.Info($"[CustomStart] 已应用玩家配置 player={player.NetId}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[CustomStart] 应用玩家配置失败 player={player.NetId}: {ex.Message}");
                            applied.Add(player.NetId); // 避免死循环重试
                        }
                    }

                    if (allReady) break;
                    await Task.Delay(250);
                }
                Logger.Info($"[CustomStart] 开局配置应用完成（已应用 {applied.Count}/{state.Players.Count} 名玩家）");
            }
            catch (Exception ex)
            {
                Logger.Error($"[CustomStart] ApplyConfigsAfterSyncAsync 失败: {ex.Message}");
            }
            finally
            {
                // 开局配置应用结束（全部应用/超时/战斗已开始），复位会话标记，
                // 避免一局联机结束后标记残留，导致后续单机局的自定义卡组/遗物不生效。
                ConfigManager.SetMultiplayerSession(false);
            }
        }
    }

    /// <summary>
    /// 多人大厅补丁 - 标记多人会话；离开大厅时复位，避免影响后续单人局。
    /// </summary>
    public static class MultiplayerLobbyPatch
    {
        public static void HostPostfix(NCharacterSelectScreen __instance)
        {
            ConfigManager.SetMultiplayerSession(true);
            LobbyHostConfigPanel.BindLobby(__instance?.Lobby);
        }

        public static void ClientPostfix(NCharacterSelectScreen __instance)
        {
            ConfigManager.SetMultiplayerSession(true);
            LobbyHostConfigPanel.BindLobby(__instance?.Lobby);
        }

        public static void LobbyClosedPostfix()
        {
            ConfigManager.SetMultiplayerSession(false);
        }
    }

    /// <summary>
    /// 大厅面板补丁 - 在联机大厅页挂载“强制房主配置”面板，离开时清理。
    /// </summary>
    public static class LobbyPanelPatch
    {
        public static void InitializePostfix(NRemoteLobbyPlayerContainer __instance, StartRunLobby lobby)
        {
            LobbyHostConfigPanel.AttachOrRebind(__instance, lobby);
        }

        public static void CleanupPostfix()
        {
            LobbyHostConfigPanel.Cleanup();
        }

        public static void PlayerConnectedPostfix(NRemoteLobbyPlayerContainer __instance)
        {
            LobbyHostConfigPanel.OnPlayerConnected(__instance);
        }
    }
}
