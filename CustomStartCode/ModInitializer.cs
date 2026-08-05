// 小格子铺 | Latticeshop
using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using CustomStartCode.Config;

namespace CustomStartCode;

[ModInitializer(nameof(Initialize))]
public static class ModInitializer
{
    public const string ModId = "CustomStart";

    /// <summary>
    /// 红警2MOD 的 manifest id。若该 mod 已启用，本模组自动停用：
    /// 红警2MOD 已内置自定义初始卡组/遗物功能，同时生效会重复。
    /// </summary>
    private const string RedAlert2ModId = "RedAlert2Mod";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        if (IsRedAlert2ModEnabled())
        {
            Logger.Info("检测到红警2MOD（RedAlert2Mod）已启用，其已内置自定义初始卡组/遗物功能，本模组（定制开局）自动停用。");
            return;
        }

        var harmony = new Harmony(ModId);
        harmony.PatchAll();

        // 初始化配置管理器
        ConfigManager.Initialize();

        // 注册配置补丁（开局应用牌组/遗物、主菜单按钮、遗物详情层级）
        CustomStartCode.Config.Patches.Install(harmony);

        Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(Assembly.GetExecutingAssembly());

        Logger.Info("定制开局 Mod 加载成功！");
    }

    /// <summary>
    /// 判断红警2MOD 是否已启用。
    /// ModManager 在加载任何 mod 的 DLL 之前就会收集全部启用 mod 的清单，
    /// 因此无论两个 mod 的加载顺序如何，这里都能可靠判断。
    /// </summary>
    private static bool IsRedAlert2ModEnabled()
    {
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                // 注意：不要对 ModManifest 使用 == 判空 —— 高版本游戏里它是 record（会生成 op_Equality），
                // 旧版本（如 v0.107）是普通 class，运行时找不到该方法会 MissingMethodException。
                // 用 is null 做引用判空，兼容所有版本。
                if (mod.manifest is null ||
                    !string.Equals(mod.manifest.id, RedAlert2ModId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 禁用/重复/运行时添加的 mod 不会真正生效
                bool willLoad = mod.state != ModLoadState.Disabled
                    && mod.state != ModLoadState.DisabledDuplicate
                    && mod.state != ModLoadState.AddedAtRuntime;
                if (willLoad)
                {
                    Logger.Info($"[CustomStart] 发现红警2MOD: path={mod.path}, state={mod.state}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CustomStart] 检测 ModManager.Mods 失败，回退到程序集检测: {ex.Message}");
        }

        // 兜底：红警2MOD 的 DLL 可能已被加载（例如其初始化顺序在本模组之前）
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, "RedAlert2Mod", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        return false;
    }
}
