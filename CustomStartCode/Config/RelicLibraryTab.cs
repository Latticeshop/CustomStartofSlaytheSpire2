// 小格子铺 | Latticeshop
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using CustomStartCode.Common;

namespace CustomStartCode.Config;

/// <summary>
/// 遗物库选择Tab - 浏览全部遗物并添加到自定义初始遗物
/// 分组参考原版遗物百科：初始/普通/罕见/稀有/商店/先古/事件 + 其他 mod 专属栏
/// </summary>
internal class RelicLibraryTab
{
    private const int DefaultLayer = 103;
    private static CanvasLayer? _currentLayer;

    private readonly CharacterConfig _config;
    private readonly Action _onChanged;
    private CanvasLayer? _layer;
    private ScrollContainer? _scrollContainer;
    private VBoxContainer? _contentContainer;

    public RelicLibraryTab(CharacterConfig config, Action onChanged)
    {
        _config = config;
        _onChanged = onChanged;
    }

    public void Show()
    {
        Build();
    }

    public void Close()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            _layer.QueueFree();
        }
        _layer = null;
        if (ReferenceEquals(_currentLayer, _layer)) _currentLayer = null;
    }

    private void Build()
    {
        _layer = new CanvasLayer();
        _layer.Layer = DefaultLayer;
        _currentLayer = _layer;
        _layer.Name = "RelicLibraryTab";

        var backstop = new ColorRect();
        backstop.Color = new Color(0f, 0f, 0f, 0.7f);
        backstop.AnchorRight = 1;
        backstop.AnchorBottom = 1;
        backstop.MouseFilter = Control.MouseFilterEnum.Stop;
        backstop.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                Close();
                backstop.GetViewport()?.SetInputAsHandled();
                _onChanged();
            }
        };
        _layer.AddChild(backstop);

        var panel = new PanelContainer();
        panel.AnchorLeft = 0.05f;
        panel.AnchorRight = 0.95f;
        panel.AnchorTop = 0.05f;
        panel.AnchorBottom = 0.95f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.MouseFilter = Control.MouseFilterEnum.Stop;

        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0.06f, 0.05f, 0.08f, 0.97f);
        panelStyle.SetBorderWidthAll(2);
        panelStyle.BorderColor = new Color("B89840");
        panelStyle.SetCornerRadiusAll(8);
        panelStyle.SetContentMarginAll(12);
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        _layer.AddChild(panel);

        var mainVBox = new VBoxContainer();
        mainVBox.AnchorRight = 1;
        mainVBox.AnchorBottom = 1;
        mainVBox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(mainVBox);

        var titleLabel = new Label();
        titleLabel.Text = ConfigManager.L("CONFIG_RELIC_LIB_TITLE");
        titleLabel.AddThemeFontSizeOverride("font_size", 20);
        titleLabel.AddThemeColorOverride("font_color", StsColors.gold);
        titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        mainVBox.AddChild(titleLabel);

        var hintLabel = new Label();
        hintLabel.Text = ConfigManager.L("CONFIG_RELIC_LIB_HINT");
        hintLabel.AddThemeFontSizeOverride("font_size", 11);
        hintLabel.AddThemeColorOverride("font_color", StsColors.gray);
        hintLabel.HorizontalAlignment = HorizontalAlignment.Center;
        mainVBox.AddChild(hintLabel);

        var divider = new ColorRect();
        divider.CustomMinimumSize = new Vector2(0, 2);
        divider.Color = new Color(0.91f, 0.86f, 0.75f, 0.25f);
        divider.MouseFilter = Control.MouseFilterEnum.Ignore;
        mainVBox.AddChild(divider);

        _scrollContainer = new ScrollContainer();
        _scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scrollContainer.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        mainVBox.AddChild(_scrollContainer);

        _contentContainer = new VBoxContainer();
        _contentContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _contentContainer.AddThemeConstantOverride("separation", 6);
        _scrollContainer.AddChild(_contentContainer);

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", 12);
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        mainVBox.AddChild(buttonRow);

        var closeBtn = CreateActionButton(ConfigManager.L("CONFIG_LIB_CLOSE"), StsColors.red);
        closeBtn.CustomMinimumSize = new Vector2(100, 36);
        closeBtn.Pressed += () =>
        {
            Close();
            _onChanged();
        };
        buttonRow.AddChild(closeBtn);

        RefreshContent();
        NGame.Instance?.AddChild(_layer);
    }

    /// <summary>
    /// 遗物检视页打开时，临时把本库降到默认画布之下，避免盖住详情页。
    /// </summary>
    public static void OnInspectScreenOpened()
    {
        if (_currentLayer != null && GodotObject.IsInstanceValid(_currentLayer))
        {
            _currentLayer.Layer = -1;
            _currentLayer.Visible = false;
        }
    }

    /// <summary>
    /// 遗物检视页关闭后恢复本库层级。
    /// </summary>
    public static void OnInspectScreenClosed()
    {
        if (_currentLayer != null && GodotObject.IsInstanceValid(_currentLayer))
        {
            _currentLayer.Layer = DefaultLayer;
            _currentLayer.Visible = true;
        }
    }

    private void RefreshContent()
    {
        if (_contentContainer == null) return;
        ClearChildren(_contentContainer);

        foreach (var (groupName, relics) in BuildGroups())
        {
            var groupHeader = new Label();
            groupHeader.Text = groupName;
            groupHeader.AddThemeFontSizeOverride("font_size", 15);
            groupHeader.AddThemeColorOverride("font_color", StsColors.gold);
            _contentContainer.AddChild(groupHeader);

            var flow = new FlowContainer();
            flow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            flow.AddThemeConstantOverride("h_separation", 6);
            flow.AddThemeConstantOverride("v_separation", 6);
            _contentContainer.AddChild(flow);

            foreach (var relic in relics)
            {
                flow.AddChild(CreateRelicTile(relic));
            }
        }
    }

    private List<(string GroupName, List<RelicModel> Relics)> BuildGroups()
    {
        var allRelics = new List<RelicModel>();
        try
        {
            foreach (var relic in ModelDb.AllRelics)
            {
                allRelics.Add(relic);
            }
        }
        catch { }

        var groups = new List<(string, List<RelicModel>)>();
        var rarityGroups = new (RelicRarity Rarity, string Name)[]
        {
            (RelicRarity.Starter, ConfigManager.L("CONFIG_RELIC_GROUP_STARTER")),
            (RelicRarity.Common, ConfigManager.L("CONFIG_RELIC_GROUP_COMMON")),
            (RelicRarity.Uncommon, ConfigManager.L("CONFIG_RELIC_GROUP_UNCOMMON")),
            (RelicRarity.Rare, ConfigManager.L("CONFIG_RELIC_GROUP_RARE")),
            (RelicRarity.Shop, ConfigManager.L("CONFIG_RELIC_GROUP_SHOP")),
            (RelicRarity.Ancient, ConfigManager.L("CONFIG_RELIC_GROUP_ANCIENT")),
            (RelicRarity.Event, ConfigManager.L("CONFIG_RELIC_GROUP_EVENT")),
        };

        var handled = new HashSet<RelicModel>();
        foreach (var (rarity, name) in rarityGroups)
        {
            var list = allRelics.Where(r =>
            {
                try { return r.Rarity == rarity; }
                catch { return false; }
            }).ToList();
            if (list.Count > 0)
            {
                groups.Add((name, list));
            }
            foreach (var r in list) handled.Add(r);
        }

        // 其他 mod / 本 mod 专属遗物：按遗物池标题分组
        var exclusive = allRelics.Where(r => !handled.Contains(r)).ToList();
        if (exclusive.Count > 0)
        {
            foreach (var poolGroup in exclusive.GroupBy(r =>
            {
                try { return r.Pool?.GetType().Name ?? ConfigManager.L("CONFIG_RELIC_GROUP_EXCLUSIVE"); }
                catch { return ConfigManager.L("CONFIG_RELIC_GROUP_EXCLUSIVE"); }
            }))
            {
                groups.Add((ConfigManager.L("CONFIG_RELIC_GROUP_EXCLUSIVE_PREFIX", poolGroup.Key), poolGroup.ToList()));
            }
        }

        return groups;
    }

    private Control CreateRelicTile(RelicModel relic)
    {
        var tile = new Control();
        tile.CustomMinimumSize = new Vector2(56, 56);
        tile.MouseFilter = Control.MouseFilterEnum.Stop;
        tile.MouseDefaultCursorShape = Control.CursorShape.PointingHand;

        int count = _config.StartingRelicTypes.Count(t => t == relic.GetType().Name);
        bool added = count > 0;

        try
        {
            var icon = new TextureRect();
            icon.Texture = relic.Icon;
            icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            icon.MouseFilter = Control.MouseFilterEnum.Ignore;
            tile.AddChild(icon);
        }
        catch { }

        // 已添加标记：金色边框 + 数量角标
        var frame = new PanelContainer();
        frame.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        frame.MouseFilter = Control.MouseFilterEnum.Ignore;
        var style = new StyleBoxFlat();
        style.BgColor = new Color(0, 0, 0, 0f);
        style.SetBorderWidthAll(2);
        style.BorderColor = added ? StsColors.gold : new Color(0.35f, 0.30f, 0.25f, 0.5f);
        style.SetCornerRadiusAll(4);
        frame.AddThemeStyleboxOverride("panel", style);
        tile.AddChild(frame);

        if (count > 0)
        {
            var countLabel = new Label();
            countLabel.Text = $"×{count}";
            countLabel.AddThemeFontSizeOverride("font_size", 9);
            countLabel.AddThemeColorOverride("font_color", StsColors.gold);
            countLabel.AnchorLeft = 1f;
            countLabel.AnchorRight = 1f;
            countLabel.AnchorTop = 1f;
            countLabel.AnchorBottom = 1f;
            countLabel.OffsetLeft = -22;
            countLabel.OffsetTop = -13;
            countLabel.OffsetRight = -3;
            countLabel.OffsetBottom = -3;
            countLabel.HorizontalAlignment = HorizontalAlignment.Right;
            tile.AddChild(countLabel);
        }

        // 左键重复点击可添加多个副本；右键减少一个（与自定义卡牌逻辑一致）
        tile.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                tile.GetViewport()?.SetInputAsHandled();
                AddRelicCopy(relic);
                RefreshContent();
            }
            else if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                tile.GetViewport()?.SetInputAsHandled();
                RemoveRelicCopy(relic);
                RefreshContent();
            }
        };

        // 悬停显示遗物详情
        tile.MouseEntered += () =>
        {
            try { ShowHoverTips(tile, relic.HoverTips, HoverTipAlignment.Left); }
            catch { }
        };
        tile.MouseExited += () => NHoverTipSet.Remove(tile);

        return tile;
    }

    private static void ShowHoverTips(Control owner, IEnumerable<IHoverTip> tips, HoverTipAlignment alignment)
    {
        try
        {
            var tipSet = NHoverTipSet.CreateAndShow(owner, tips, alignment);
            if (tipSet != null && GodotObject.IsInstanceValid(tipSet))
            {
                tipSet.GetParent()?.RemoveChild(tipSet);
                UiLayers.GetHoverTipLayer().AddChild(tipSet);
            }
        }
        catch { }
    }

    private void AddRelicCopy(RelicModel relic)
    {
        _config.StartingRelicTypes.Add(relic.GetType().Name);
        ConfigManager.UpdateCharacterConfig(_config);
    }

    private void RemoveRelicCopy(RelicModel relic)
    {
        string typeName = relic.GetType().Name;
        string? firstMatch = _config.StartingRelicTypes.FirstOrDefault(t => t == typeName);
        if (firstMatch == null) return;
        _config.StartingRelicTypes.Remove(firstMatch);
        ConfigManager.UpdateCharacterConfig(_config);
    }

    private void ShowRelicDetails(RelicModel relic)
    {
        try
        {
            var allRelics = new List<RelicModel>();
            try
            {
                foreach (var r in ModelDb.AllRelics) allRelics.Add(r);
            }
            catch { }
            if (allRelics.Count == 0) allRelics.Add(relic);
            NGame.Instance?.GetInspectRelicScreen()?.Open(allRelics, relic);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RelicLibrary] 打开遗物详情失败: {ex}");
        }
    }

    private static string GetRelicTitle(RelicModel relic)
    {
        try { return relic.Title.GetFormattedText(); }
        catch { return relic.Id.Entry; }
    }

    private Button CreateActionButton(string text, Color? fontColor = null)
    {
        var btn = new Button();
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 13);
        btn.AddThemeColorOverride("font_color", fontColor ?? StsColors.cream);
        btn.AddThemeColorOverride("font_hover_color", StsColors.gold);
        btn.AddThemeColorOverride("font_pressed_color", StsColors.gray);
        btn.AddThemeStyleboxOverride("normal", CreateStyleBox(
            new Color(0.12f, 0.10f, 0.15f, 0.85f),
            new Color(0.35f, 0.30f, 0.25f, 0.5f)));
        btn.AddThemeStyleboxOverride("hover", CreateStyleBox(
            new Color(0.18f, 0.15f, 0.22f, 0.92f),
            StsColors.gold));
        btn.AddThemeStyleboxOverride("pressed", CreateStyleBox(
            new Color(0.08f, 0.06f, 0.10f, 0.95f),
            new Color("B89840")));
        return btn;
    }

    private StyleBoxFlat CreateStyleBox(Color bg, Color border)
    {
        var sb = new StyleBoxFlat();
        sb.BgColor = bg;
        sb.BorderColor = border;
        sb.SetBorderWidthAll(2);
        sb.SetCornerRadiusAll(6);
        sb.SetContentMarginAll(6);
        return sb;
    }

    private static void ClearChildren(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }
}
