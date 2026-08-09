// 小格子铺 | Latticeshop
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using CustomStartCode.Common;

namespace CustomStartCode.Config;

/// <summary>
/// 卡牌库选择Tab - 用于浏览和选择卡牌添加到自定义卡组
/// </summary>
internal class CardLibraryTab
{
    private const int CardsPerPage = 24;
    private const int Columns = 6;

    // NCard 基础尺寸约 300x422（参考 FreeLoadout）。Scale 只影响绘制不影响布局，
    // 因此必须用 clip(ClipContents) 包裹并手动居中，否则格子会被卡牌的自然最小尺寸撑大。
    // ★ 手动调参入口（改这两处即可）：
    //   CardCellScale    → 卡片缩放倍率（绘制尺寸 = 300x422 × 此值，越大卡片越大，建议 0.35~0.5）
    //   CardCellMinHeight→ 每个卡格的固定最小高度（控制格子高度，需 ≥ 422×Scale）
    private const float CardBaseWidth = 300f;
    private const float CardBaseHeight = 422f;
    private const float CardCellScale = 0.40f;
    private const float CardCellMinHeight = 190f;

    private static string _searchText = string.Empty;
    private static readonly Dictionary<CardType, bool> _typeFilter = new()
    {
        [CardType.Attack] = true,
        [CardType.Skill] = true,
        [CardType.Power] = true,
        [CardType.Status] = false,
        [CardType.Curse] = false,
    };
    // 角色筛选（角色Id -> 是否显示），默认均不选中，未选任何项时不过滤
    private static readonly Dictionary<string, bool> _characterFilter = new(StringComparer.OrdinalIgnoreCase);
    // 未归属任何角色的卡牌（无色/诅咒/衍生等）是否显示
    private static bool _includeGeneralCards;
    // 懒构建：卡牌Id -> 归属角色Id集合（一卡可属多角色）
    private static Dictionary<string, HashSet<string>>? _cardCharacterMap;
    private static int _pageIndex;

    private readonly CharacterConfig _config;
    private readonly Action _onChanged;
    private CanvasLayer? _layer;
    private ScrollContainer? _scrollContainer;
    private VBoxContainer? _contentContainer;

    public CardLibraryTab(CharacterConfig config, Action onChanged)
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
    }

    private void Build()
    {
        _layer = new CanvasLayer();
        _layer.Layer = 102;
        _layer.Name = "CardLibraryTab";

        // 背景遮罩
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

        // 主面板
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

        // 标题
        var titleLabel = new Label();
        titleLabel.Text = ConfigManager.L("CONFIG_CARD_LIB_TITLE");
        titleLabel.AddThemeFontSizeOverride("font_size", 20);
        titleLabel.AddThemeColorOverride("font_color", StsColors.gold);
        titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        mainVBox.AddChild(titleLabel);

        // 搜索栏
        BuildSearchBar(mainVBox);

        // 角色筛选
        BuildCharacterFilterBar(mainVBox);

        // 类型筛选
        BuildTypeFilterBar(mainVBox);

        // 分隔符
        var divider = new ColorRect();
        divider.CustomMinimumSize = new Vector2(0, 2);
        divider.Color = new Color(0.91f, 0.86f, 0.75f, 0.25f);
        divider.MouseFilter = Control.MouseFilterEnum.Ignore;
        mainVBox.AddChild(divider);

        // 滚动容器
        _scrollContainer = new ScrollContainer();
        _scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scrollContainer.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        mainVBox.AddChild(_scrollContainer);

        _contentContainer = new VBoxContainer();
        _contentContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _contentContainer.AddThemeConstantOverride("separation", 6);
        _scrollContainer.AddChild(_contentContainer);

        // 底部按钮
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

    private void BuildSearchBar(VBoxContainer container)
    {
        var searchBox = new HBoxContainer();
        searchBox.AddThemeConstantOverride("separation", 6);

        var searchLabel = new Label();
        searchLabel.Text = ConfigManager.L("CONFIG_SEARCH");
        searchLabel.AddThemeFontSizeOverride("font_size", 14);
        searchLabel.AddThemeColorOverride("font_color", StsColors.cream);
        searchBox.AddChild(searchLabel);

        var searchInput = new LineEdit();
        searchInput.Text = _searchText;
        searchInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        searchInput.CustomMinimumSize = new Vector2(200, 32);
        searchInput.AddThemeFontSizeOverride("font_size", 14);
        searchInput.TextChanged += (string newText) =>
        {
            _searchText = newText;
            _pageIndex = 0;
            RefreshContent();
        };
        searchBox.AddChild(searchInput);

        if (!string.IsNullOrEmpty(_searchText))
        {
            var clearBtn = CreateActionButton(ConfigManager.L("CONFIG_CLEAR"), StsColors.red);
            clearBtn.Pressed += () =>
            {
                _searchText = string.Empty;
                _pageIndex = 0;
                RefreshContent();
            };
            searchBox.AddChild(clearBtn);
        }

        container.AddChild(searchBox);
    }

    private void BuildTypeFilterBar(VBoxContainer container)
    {
        var filterBox = new HBoxContainer();
        filterBox.AddThemeConstantOverride("separation", 4);
        container.AddChild(filterBox);

        var filterLabel = new Label();
        filterLabel.Text = ConfigManager.L("CONFIG_TYPE");
        filterLabel.AddThemeFontSizeOverride("font_size", 14);
        filterLabel.AddThemeColorOverride("font_color", StsColors.cream);
        filterLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        filterBox.AddChild(filterLabel);

        (CardType type, string name)[] types =
        [
            (CardType.Attack, ConfigManager.L("CONFIG_TYPE_ATTACK")),
            (CardType.Skill, ConfigManager.L("CONFIG_TYPE_SKILL")),
            (CardType.Power, ConfigManager.L("CONFIG_TYPE_POWER")),
            (CardType.Status, ConfigManager.L("CONFIG_TYPE_STATUS")),
            (CardType.Curse, ConfigManager.L("CONFIG_TYPE_CURSE")),
        ];

        foreach (var (type, name) in types)
        {
            bool enabled = _typeFilter.GetValueOrDefault(type, false);
            var btn = CreateFilterButton(name, enabled);
            btn.Pressed += () =>
            {
                _typeFilter[type] = !_typeFilter.GetValueOrDefault(type, true);
                UpdateFilterButtonStyle(btn, _typeFilter[type]);
                RefreshContent();
            };
            filterBox.AddChild(btn);
        }
    }

    private void BuildCharacterFilterBar(VBoxContainer container)
    {
        var filterBox = new FlowContainer();
        filterBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        filterBox.AddThemeConstantOverride("h_separation", 4);
        filterBox.AddThemeConstantOverride("v_separation", 4);
        container.AddChild(filterBox);

        var filterLabel = new Label();
        filterLabel.Text = ConfigManager.L("CONFIG_CHARACTER");
        filterLabel.AddThemeFontSizeOverride("font_size", 14);
        filterLabel.AddThemeColorOverride("font_color", StsColors.cream);
        filterLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        filterBox.AddChild(filterLabel);

        foreach (var character in GetAllCharacters())
        {
            string charId = character.Id.Entry;
            bool enabled = _characterFilter.GetValueOrDefault(charId, false);
            _characterFilter[charId] = enabled;

            var btn = CreateFilterButton(GetCharacterName(character), enabled);
            btn.Pressed += () =>
            {
                _characterFilter[charId] = !_characterFilter.GetValueOrDefault(charId, true);
                UpdateFilterButtonStyle(btn, _characterFilter[charId]);
                RefreshContent();
            };
            filterBox.AddChild(btn);
        }

        var generalBtn = CreateFilterButton(ConfigManager.L("CONFIG_GENERAL"), _includeGeneralCards);
        generalBtn.Pressed += () =>
        {
            _includeGeneralCards = !_includeGeneralCards;
            UpdateFilterButtonStyle(generalBtn, _includeGeneralCards);
            RefreshContent();
        };
        filterBox.AddChild(generalBtn);
    }

    private static List<CharacterModel> GetAllCharacters()
    {
        var characters = new List<CharacterModel>();
        try
        {
            foreach (var character in ModelDb.AllCharacters)
            {
                characters.Add(character);
            }
        }
        catch { }
        return characters;
    }

    private static string GetCharacterName(CharacterModel character)
    {
        try
        {
            string? name = character.Title?.GetFormattedText();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { }
        return character.Id.Entry;
    }

    private void RefreshContent()
    {
        if (_contentContainer == null) return;
        ClearChildren(_contentContainer);

        // 获取所有卡牌并筛选
        var allCards = GetFilteredCards();
        int totalPages = Math.Max(1, (allCards.Count + CardsPerPage - 1) / CardsPerPage);
        _pageIndex = Math.Clamp(_pageIndex, 0, totalPages - 1);

        var pageCards = allCards.Skip(_pageIndex * CardsPerPage).Take(CardsPerPage).ToList();

        if (pageCards.Count == 0)
        {
            bool noFilterSelected =
                !_typeFilter.Values.Any(v => v)
                && !_characterFilter.Values.Any(v => v)
                && !_includeGeneralCards;
            var emptyLabel = new Label();
            emptyLabel.Text = noFilterSelected
                ? ConfigManager.L("CONFIG_CARD_LIB_EMPTY_FILTER")
                : (string.IsNullOrEmpty(_searchText) ? ConfigManager.L("CONFIG_CARD_LIB_EMPTY") : ConfigManager.L("CONFIG_CARD_LIB_NO_MATCH"));
            emptyLabel.AddThemeFontSizeOverride("font_size", 14);
            emptyLabel.AddThemeColorOverride("font_color", StsColors.gray);
            _contentContainer.AddChild(emptyLabel);
            return;
        }

        // 卡牌网格
        var grid = new GridContainer { Columns = Columns };
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 16);
        _contentContainer.AddChild(grid);

        foreach (var card in pageCards)
        {
            grid.AddChild(CreateCardCell(card));
        }

        // 分页导航
        if (totalPages > 1)
        {
            BuildPageNav(_contentContainer, totalPages, allCards.Count);
        }
    }

    private List<CardModel> GetFilteredCards()
    {
        var cards = new List<CardModel>();
        try
        {
            foreach (var card in ModelDb.AllCards)
            {
                if (!_typeFilter.GetValueOrDefault(card.Type, false)) continue;
                if (!IsCharacterFilteredIn(card)) continue;

                if (!string.IsNullOrEmpty(_searchText))
                {
                    string search = _searchText.ToLowerInvariant();
                    bool matches = false;
                    try
                    {
                        if (card.Title.ToLowerInvariant().Contains(search)) matches = true;
                    }
                    catch { }
                    if (card.Id.Entry.ToLowerInvariant().Contains(search)) matches = true;
                    if (!matches) continue;
                }

                cards.Add(card);
            }
        }
        catch { }

        return cards.Distinct().OrderBy(c => c.Id.Entry).ToList();
    }

    /// <summary>
    /// 判断卡牌是否通过角色筛选；未归属任何角色的卡牌视为“通用”。
    /// 一张卡可归属多个角色（如箱子卡同时注册在盟军/苏军卡池），任一归属角色处于开启状态即显示。
    /// </summary>
    private static bool IsCharacterFilteredIn(CardModel card)
    {
        var owners = GetCardCharacterIds(card);
        if (owners == null || owners.Count == 0) return _includeGeneralCards;
        foreach (var owner in owners)
        {
            if (_characterFilter.GetValueOrDefault(owner, false)) return true;
        }
        return false;
    }

    /// <summary>
    /// 懒构建 卡牌Id -> 归属角色Id集合 映射。
    /// </summary>
    private static HashSet<string>? GetCardCharacterIds(CardModel card)
    {
        if (_cardCharacterMap == null)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var character in ModelDb.AllCharacters)
                {
                    var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var id in character.CardPool.AllCardIds)
                        {
                            if (!string.IsNullOrEmpty(id.Entry)) entries.Add(id.Entry);
                        }
                    }
                    catch { }
                    try
                    {
                        foreach (var c in character.StartingDeck)
                        {
                            if (c != null && !string.IsNullOrEmpty(c.Id.Entry)) entries.Add(c.Id.Entry);
                        }
                    }
                    catch { }
                    foreach (var entry in entries)
                    {
                        if (!map.TryGetValue(entry, out var owners))
                        {
                            owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            map[entry] = owners;
                        }
                        owners.Add(character.Id.Entry);
                    }
                }
            }
            catch { }
            _cardCharacterMap = map;
        }
        return _cardCharacterMap.TryGetValue(card.Id.Entry, out var resultOwners) ? resultOwners : null;
    }

    private Control CreateCardCell(CardModel card)
    {
        var cell = new VBoxContainer();
        cell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        cell.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        cell.AddThemeConstantOverride("separation", 4);
        cell.Alignment = BoxContainer.AlignmentMode.Center;

        // clip 容器：ClipContents + 固定最小高度，卡牌不参与容器布局（避免自然最小尺寸把格子撑大）
        var clip = new Control();
        clip.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        clip.CustomMinimumSize = new Vector2(0, CardCellMinHeight);
        clip.ClipContents = true;
        clip.MouseFilter = Control.MouseFilterEnum.Stop;
        clip.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        cell.AddChild(clip);

        NCard? nCard = null;
        try
        {
            var displayCard = card.IsMutable ? card : card.ToMutable();
            nCard = NCard.Create(displayCard);
            if (nCard != null)
            {
                nCard.Scale = new Vector2(CardCellScale, CardCellScale);
                nCard.MouseFilter = Control.MouseFilterEnum.Ignore;
                clip.AddChild(nCard);

                // 复用池中的 NCard 可能已 Ready：Ready 信号不会再次触发，
                // 必须立即刷新视觉，否则会显示上一张卡残留的文案/模型
                if (nCard.IsNodeReady())
                {
                    nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                    CenterCardInClip(clip, nCard);
                }
                else
                {
                    nCard.Ready += () =>
                    {
                        if (GodotObject.IsInstanceValid(nCard))
                            nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                        // 延迟到本帧布局完成后居中（此时 clip.Size 已就绪）
                        Callable.From(() =>
                        {
                            if (GodotObject.IsInstanceValid(clip) && GodotObject.IsInstanceValid(nCard))
                                CenterCardInClip(clip, nCard);
                        }).CallDeferred();
                    };
                }
            }
        }
        catch { }

        // 居中卡片：clip 尺寸变化时按绘制后的尺寸（基础尺寸 x 缩放）重新定位
        NCard? capturedCard = nCard;
        clip.Resized += () => CenterCardInClip(clip, capturedCard);

        // 悬停提示
        clip.MouseEntered += () =>
        {
            try { ShowHoverTips(clip, card.HoverTips, HoverTip.GetHoverTipAlignment(clip)); }
            catch { }
        };
        clip.MouseExited += () => NHoverTipSet.Remove(clip);

        // 点击添加
        clip.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                clip.GetViewport()?.SetInputAsHandled();
                AddCardToDeck(card, false);
            }
        };

        // 添加 / 升级 两个按钮并排（各占一半）
        var addRow = new HBoxContainer();
        addRow.AddThemeConstantOverride("separation", 4);
        addRow.Alignment = BoxContainer.AlignmentMode.Center;
        cell.AddChild(addRow);

        var addBtn = CreateActionButton(ConfigManager.L("CONFIG_ADD"), StsColors.gold);
        addBtn.CustomMinimumSize = new Vector2(72, 28);
        addBtn.Pressed += () => AddCardToDeck(card, false);
        addRow.AddChild(addBtn);

        var upgradeBtn = CreateActionButton(ConfigManager.L("CONFIG_UPGRADE"), StsColors.gold);
        upgradeBtn.CustomMinimumSize = new Vector2(72, 28);
        upgradeBtn.Pressed += () => AddCardToDeck(card, true);
        addRow.AddChild(upgradeBtn);

        return cell;
    }

    /// <summary>
    /// 将 NCard 居中到 clip 内（按绘制后的尺寸 基础尺寸x缩放 计算偏移）。
    /// </summary>
    private static void CenterCardInClip(Control clip, Control? card)
    {
        if (card == null || !GodotObject.IsInstanceValid(clip) || !GodotObject.IsInstanceValid(card)) return;
        // NCard 的原点在卡牌中心（card.tscn 中卡面偏移 -150..150 × -211..211），不是左上角，
        // 所以把原点放到 clip 中心即可让整张卡居中（绘制尺寸 = 基础尺寸 × 缩放）。
        // 原实现把原点当左上角，用 (clip.Size - drawn) / 2 计算，导致卡牌整体向左上偏移、
        // 顶部（卡名/费用/插画）和左侧（描述开头）被 clip 裁掉。
        card.Position = new Vector2(clip.Size.X / 2f, clip.Size.Y / 2f);
    }

    private void AddCardToDeck(CardModel card, bool upgraded)
    {
        string typeName = card.GetType().Name;
        ConfigPanel.AddCardToDeck(_config, typeName, upgraded);
        ShowNotification($"已添加: {GetCardTitle(card)}" + (upgraded ? "（升级）" : ""));
        RefreshContent();
    }

    private string GetCardTitle(CardModel card)
    {
        try { return card.Title; }
        catch { return card.Id.Entry; }
    }

    private void BuildPageNav(VBoxContainer container, int totalPages, int totalCount)
    {
        var nav = new HBoxContainer();
        nav.AddThemeConstantOverride("separation", 6);

        if (_pageIndex > 0)
        {
            var prevBtn = CreateActionButton(ConfigManager.L("CONFIG_PREV_PAGE"), StsColors.cream);
            prevBtn.Pressed += () =>
            {
                _pageIndex--;
                RefreshContent();
            };
            nav.AddChild(prevBtn);
        }

        var pageLabel = new Label();
        pageLabel.Text = ConfigManager.L("CONFIG_PAGE_INFO", _pageIndex + 1, totalPages, totalCount);
        pageLabel.AddThemeFontSizeOverride("font_size", 13);
        pageLabel.AddThemeColorOverride("font_color", StsColors.cream);
        nav.AddChild(pageLabel);

        if (_pageIndex < totalPages - 1)
        {
            var nextBtn = CreateActionButton(ConfigManager.L("CONFIG_NEXT_PAGE"), StsColors.cream);
            nextBtn.Pressed += () =>
            {
                _pageIndex++;
                RefreshContent();
            };
            nav.AddChild(nextBtn);
        }

        container.AddChild(nav);
    }

    // ============ UI辅助方法 ============

    private Button CreateActionButton(string text, Color? fontColor = null)
    {
        var btn = new Button();
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 13);
        btn.AddThemeColorOverride("font_color", fontColor ?? StsColors.cream);
        btn.AddThemeColorOverride("font_hover_color", StsColors.gold);
        btn.AddThemeColorOverride("font_pressed_color", StsColors.gray);
        ApplyFlatStyle(btn);
        return btn;
    }

    private Button CreateFilterButton(string text, bool enabled)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(70, 28);
        btn.AddThemeFontSizeOverride("font_size", 13);
        // 筛选按钮是即时切换开关：去掉焦点选中框，点击后底色/字体色立即变化
        btn.FocusMode = Control.FocusModeEnum.None;
        UpdateFilterButtonStyle(btn, enabled);
        btn.AddThemeStyleboxOverride("hover", CreateStyleBox(
            new Color(0.18f, 0.15f, 0.22f, 0.92f),
            StsColors.gold));
        btn.AddThemeStyleboxOverride("pressed", CreateStyleBox(
            new Color(0.08f, 0.06f, 0.10f, 0.95f),
            new Color("B89840")));
        return btn;
    }

    /// <summary>
    /// 更新筛选按钮的开关状态样式：开启=绿底绿字，关闭=灰底灰字。
    /// </summary>
    private void UpdateFilterButtonStyle(Button btn, bool enabled)
    {
        btn.AddThemeColorOverride("font_color", enabled ? StsColors.green : StsColors.gray);
        btn.AddThemeStyleboxOverride("normal", CreateStyleBox(
            enabled ? new Color(0.13f, 0.24f, 0.13f, 0.92f) : new Color(0.12f, 0.10f, 0.15f, 0.85f),
            enabled ? new Color(0.30f, 0.60f, 0.30f, 0.75f) : new Color(0.35f, 0.30f, 0.25f, 0.5f)));
    }

    private void ApplyFlatStyle(Button btn)
    {
        btn.AddThemeStyleboxOverride("normal", CreateStyleBox(
            new Color(0.12f, 0.10f, 0.15f, 0.85f),
            new Color(0.35f, 0.30f, 0.25f, 0.5f)));
        btn.AddThemeStyleboxOverride("hover", CreateStyleBox(
            new Color(0.18f, 0.15f, 0.22f, 0.92f),
            StsColors.gold));
        btn.AddThemeStyleboxOverride("pressed", CreateStyleBox(
            new Color(0.08f, 0.06f, 0.10f, 0.95f),
            new Color("B89840")));
        btn.AddThemeStyleboxOverride("focus", CreateStyleBox(
            new Color(0.18f, 0.15f, 0.22f, 0.92f),
            StsColors.gold));
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

    private void ShowHoverTips(Control owner, IEnumerable<IHoverTip> tips, HoverTipAlignment alignment)
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

    private void ShowNotification(string message)
    {
        GD.Print($"[CardLibrary] {message}");
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
