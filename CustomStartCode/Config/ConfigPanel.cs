// 小格子铺 | Latticeshop
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Characters;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using CustomStartCode.Common;

namespace CustomStartCode.Config;

/// <summary>
/// 定制开局配置面板
/// </summary>
internal static class ConfigPanel
{
    private const string LocTable = "characters";
    private const string OverlayName = "CustomStartConfigOverlay";
    private const int OverlayZIndex = 1000;
    private static readonly string[] FeatureIds = { "deck_config", "relic_config", "resource_config", "preset_config" };
    private static string[] FeatureNames => new[]
    {
        L("CONFIG_FEATURE_DECK"),
        L("CONFIG_FEATURE_RELIC"),
        L("CONFIG_FEATURE_RESOURCE"),
        L("CONFIG_FEATURE_PRESET"),
    };
    private static readonly MegaCrit.Sts2.Core.Logging.Logger Logger = new("CustomStartConfigPanel", MegaCrit.Sts2.Core.Logging.LogType.Generic);

    private static Control? _layer;
    private static HBoxContainer? _charIconRow;
    private static ScrollContainer? _scrollContainer;
    private static VBoxContainer? _contentContainer;
    private static Button[]? _sideBarButtons;
    private static Control? _charInfoHeader;
    private static Label? _charNameLabel;

    private static int _activeFeature;

    private static string? _selectedCharacterId;
    private static CharacterConfig? _currentConfig;
    private static CardLibraryTab? _cardLibraryTab;
    private static RelicLibraryTab? _relicLibraryTab;
    private static bool _wasVisibleBeforeInspect;

    private static readonly Dictionary<string, Texture2D> _iconCache = new();

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

    internal static bool IsOpen => _layer is { Visible: true };

    internal static void Toggle(Node root)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer))
        {
            Build();
            RemoveExistingOverlay(root);
            root.AddChild(_layer!);
            _layer!.Visible = true;
            return;
        }

        if (_layer.GetParent() == null)
        {
            root.AddChild(_layer);
        }

        _layer.Visible = !_layer.Visible;
    }

    internal static void Show(Node root)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer))
        {
            Build();
        }

        RemoveExistingOverlay(root);

        // 若 _layer 已挂载到场景树，不要重复 AddChild（否则报 "already has a parent"）
        if (_layer!.GetParent() == null)
        {
            root.AddChild(_layer);
        }
        _layer!.Visible = true;
    }

    private static void RemoveExistingOverlay(Node root)
    {
        var existing = root.GetNodeOrNull<Control>(OverlayName);
        // 仅释放旧的、非当前 _layer 的残留覆盖层，避免误删自身导致重复 AddChild 报错
        if (existing != null && GodotObject.IsInstanceValid(existing) && !ReferenceEquals(existing, _layer))
        {
            existing.QueueFree();
        }
        if (_layer != null && !GodotObject.IsInstanceValid(_layer))
        {
            _layer = null;
        }
    }

    internal static void Hide()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            _layer.Visible = false;
        }
    }

    internal static void RequestRefresh()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer) && _layer.Visible)
        {
            RefreshContent();
        }
    }

    private static void Build()
    {
        _layer = new Control
        {
            Name = OverlayName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = OverlayZIndex,
        };
        _layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var backstop = new ColorRect();
        backstop.Color = new Color(0f, 0f, 0f, 0.6f);
        backstop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        backstop.MouseFilter = Control.MouseFilterEnum.Stop;
        backstop.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                Hide();
                backstop.GetViewport()?.SetInputAsHandled();
            }
        };
        _layer.AddChild(backstop);

        var panel = new PanelContainer();
        panel.AnchorLeft = 0.04f;
        panel.AnchorRight = 0.96f;
        panel.AnchorTop = 0.04f;
        panel.AnchorBottom = 0.96f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.MouseFilter = Control.MouseFilterEnum.Stop;

        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0.08f, 0.06f, 0.10f, 0.97f);
        panelStyle.SetBorderWidthAll(2);
        panelStyle.BorderColor = new Color("B89840");
        panelStyle.SetCornerRadiusAll(8);
        panelStyle.SetContentMarginAll(0);
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        _layer.AddChild(panel);

        var rootVBox = new VBoxContainer();
        rootVBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rootVBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        rootVBox.AddThemeConstantOverride("separation", 0);
        panel.AddChild(rootVBox);

        var titleMargin = new MarginContainer();
        titleMargin.AddThemeConstantOverride("margin_left", 16);
        titleMargin.AddThemeConstantOverride("margin_right", 16);
        titleMargin.AddThemeConstantOverride("margin_top", 10);
        titleMargin.AddThemeConstantOverride("margin_bottom", 6);
        titleMargin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titleMargin.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        rootVBox.AddChild(titleMargin);

        var titleBar = new HBoxContainer();
        titleBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titleBar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        titleBar.AddThemeConstantOverride("separation", 8);
        titleBar.Alignment = BoxContainer.AlignmentMode.Center;
        titleMargin.AddChild(titleBar);

        var titleLabel = new Label();
        titleLabel.Text = L("CONFIG_TITLE");
        titleLabel.AddThemeFontSizeOverride("font_size", 22);
        titleLabel.AddThemeColorOverride("font_color", StsColors.gold);
        titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titleLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        titleBar.AddChild(titleLabel);

        var closeBtn = new Button();
        closeBtn.Text = "✕";
        closeBtn.CustomMinimumSize = new Vector2(36, 36);
        closeBtn.AddThemeFontSizeOverride("font_size", 16);
        closeBtn.AddThemeColorOverride("font_color", StsColors.cream);
        closeBtn.AddThemeColorOverride("font_hover_color", StsColors.red);
        ApplyFlatStyle(closeBtn);
        closeBtn.Pressed += Hide;
        titleBar.AddChild(closeBtn);

        var titleDivider = new ColorRect();
        titleDivider.CustomMinimumSize = new Vector2(0, 2);
        titleDivider.Color = new Color(0.91f, 0.86f, 0.75f, 0.25f);
        titleDivider.MouseFilter = Control.MouseFilterEnum.Ignore;
        rootVBox.AddChild(titleDivider);

        var mainHBox = new HBoxContainer();
        mainHBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        mainHBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        rootVBox.AddChild(mainHBox);

        BuildSidebar(mainHBox);
        BuildContentArea(mainHBox);

        _activeFeature = 0;
        UpdateSidebarHighlights();

        if (_selectedCharacterId == null)
        {
            var firstChar = GetAllCharacters().FirstOrDefault();
            if (firstChar != null)
            {
                _selectedCharacterId = firstChar.Id.Entry;
            }
        }

        RefreshContent();
    }

    private static void BuildSidebar(HBoxContainer parent)
    {
        var sidebar = new PanelContainer();
        sidebar.CustomMinimumSize = new Vector2(200, 0);
        sidebar.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var sideStyle = new StyleBoxFlat();
        sideStyle.BgColor = new Color(0.06f, 0.05f, 0.08f, 0.95f);
        sideStyle.SetBorderWidthAll(1);
        sideStyle.BorderColor = new Color(0.55f, 0.45f, 0.25f, 0.35f);
        sideStyle.SetCornerRadiusAll(4);
        sidebar.AddThemeStyleboxOverride("panel", sideStyle);
        parent.AddChild(sidebar);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        sidebar.AddChild(margin);

        var list = new VBoxContainer();
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        list.AddThemeConstantOverride("separation", 4);
        margin.AddChild(list);

        var heading = new Label();
        heading.Text = L("CONFIG_SIDEBAR_TITLE");
        heading.AddThemeFontSizeOverride("font_size", 14);
        heading.AddThemeColorOverride("font_color", StsColors.gold);
        list.AddChild(heading);

        var sep = new ColorRect();
        sep.CustomMinimumSize = new Vector2(0, 1);
        sep.Color = new Color(0.55f, 0.45f, 0.25f, 0.4f);
        sep.MouseFilter = Control.MouseFilterEnum.Ignore;
        list.AddChild(sep);

        _sideBarButtons = new Button[FeatureIds.Length];
        for (int i = 0; i < FeatureIds.Length; i++)
        {
            int idx = i;
            var btn = CreateSidebarButton(FeatureNames[i]);
            btn.Pressed += () => SwitchFeature(idx);
            list.AddChild(btn);
            _sideBarButtons[i] = btn;
        }
    }

    private static void BuildContentArea(HBoxContainer parent)
    {
        var contentMargin = new MarginContainer();
        contentMargin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        contentMargin.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        contentMargin.AddThemeConstantOverride("margin_left", 12);
        contentMargin.AddThemeConstantOverride("margin_right", 12);
        contentMargin.AddThemeConstantOverride("margin_top", 8);
        contentMargin.AddThemeConstantOverride("margin_bottom", 8);
        parent.AddChild(contentMargin);

        var contentVBox = new VBoxContainer();
        contentVBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        contentVBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        contentVBox.AddThemeConstantOverride("separation", 8);
        contentMargin.AddChild(contentVBox);

        // Character icon row at the top
        var iconRowContainer = new PanelContainer();
        iconRowContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var iconRowStyle = new StyleBoxFlat();
        iconRowStyle.BgColor = new Color(0.06f, 0.05f, 0.08f, 0.95f);
        iconRowStyle.SetBorderWidthAll(1);
        iconRowStyle.BorderColor = new Color(0.55f, 0.45f, 0.25f, 0.35f);
        iconRowStyle.SetCornerRadiusAll(4);
        iconRowStyle.SetContentMarginAll(8);
        iconRowContainer.AddThemeStyleboxOverride("panel", iconRowStyle);
        contentVBox.AddChild(iconRowContainer);

        _charIconRow = new HBoxContainer();
        _charIconRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _charIconRow.AddThemeConstantOverride("separation", 16);
        _charIconRow.Alignment = BoxContainer.AlignmentMode.Center;
        iconRowContainer.AddChild(_charIconRow);

        var allCharacters = GetAllCharacters();
        if (allCharacters.Count == 0)
        {
            var emptyLabel = new Label();
            emptyLabel.Text = L("CONFIG_NO_CHARACTERS");
            emptyLabel.AddThemeFontSizeOverride("font_size", 14);
            emptyLabel.AddThemeColorOverride("font_color", StsColors.gray);
            emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _charIconRow.AddChild(emptyLabel);
        }
        else
        {
            foreach (var character in allCharacters)
            {
                _charIconRow.AddChild(CreateCharacterIconButton(character));
            }
        }

        // Character info section (name)
        _charInfoHeader = new VBoxContainer();
        _charInfoHeader.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _charInfoHeader.AddThemeConstantOverride("separation", 2);
        contentVBox.AddChild(_charInfoHeader);

        _charNameLabel = new Label();
        _charNameLabel.AddThemeFontSizeOverride("font_size", 18);
        _charNameLabel.AddThemeColorOverride("font_color", StsColors.gold);
        _charInfoHeader.AddChild(_charNameLabel);

        // Scroll container for dynamic content
        _scrollContainer = new ScrollContainer();
        _scrollContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scrollContainer.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        contentVBox.AddChild(_scrollContainer);

        _contentContainer = new VBoxContainer();
        _contentContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _contentContainer.AddThemeConstantOverride("separation", 6);
        _scrollContainer.AddChild(_contentContainer);
    }

    private static Button CreateSidebarButton(string text)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(180, 44);
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_color", StsColors.cream);
        btn.AddThemeColorOverride("font_hover_color", StsColors.gold);
        btn.AddThemeColorOverride("font_pressed_color", StsColors.gray);
        ApplyFlatStyle(btn);
        return btn;
    }

    private static void SwitchFeature(int index)
    {
        _activeFeature = index;
        UpdateSidebarHighlights();
        RefreshContent();
    }

    private static void UpdateSidebarHighlights()
    {
        if (_sideBarButtons == null) return;
        for (int i = 0; i < _sideBarButtons.Length; i++)
        {
            var btn = _sideBarButtons[i];
            bool active = i == _activeFeature;
            btn.AddThemeColorOverride("font_color", active ? StsColors.gold : StsColors.cream);
            btn.AddThemeStyleboxOverride("normal", CreateStyleBox(
                active ? new Color(0.18f, 0.15f, 0.08f, 0.95f) : new Color(0.12f, 0.10f, 0.15f, 0.85f),
                active ? StsColors.gold : new Color(0.35f, 0.30f, 0.25f, 0.5f)));
        }
    }

    private static void RefreshContent()
    {
        if (_contentContainer == null) return;
        ClearChildren(_contentContainer);

        // Update character info header
        UpdateCharacterInfo();

        // 开局方案页与具体角色无关，无需先选角色
        if (_activeFeature == 3)
        {
            UpdateCharacterIconHighlights();
            BuildPresetConfigContent(_contentContainer);
            return;
        }

        if (string.IsNullOrEmpty(_selectedCharacterId))
        {
            var warnLabel = new Label();
            warnLabel.Text = L("CONFIG_WARNING_SELECT_CHAR");
            warnLabel.AddThemeFontSizeOverride("font_size", 16);
            warnLabel.AddThemeColorOverride("font_color", StsColors.cream);
            _contentContainer.AddChild(warnLabel);
            return;
        }

        var config = ConfigManager.GetCharacterConfig(_selectedCharacterId);
        _currentConfig = config;

        // Update icon highlights
        UpdateCharacterIconHighlights();

        switch (_activeFeature)
        {
            case 0:
                BuildCardPoolSection(_contentContainer, config);
                break;
            case 1:
                BuildRelicConfigContent(_contentContainer, config);
                break;
            case 2:
                BuildResourceConfigContent(_contentContainer, config);
                break;
        }
    }

    private static void UpdateCharacterInfo()
    {
        if (_charNameLabel == null) return;

        if (string.IsNullOrEmpty(_selectedCharacterId))
        {
            _charNameLabel.Text = L("CONFIG_SELECT_CHAR_PROMPT");
            return;
        }

        _charNameLabel.Text = GetCharacterDisplayName(_selectedCharacterId);
    }

    private static string GetCharacterDisplayName(string characterId)
    {
        try
        {
            var model = ModelDb.AllCharacters.FirstOrDefault(c =>
            {
                try { return c.Id.Entry == characterId; }
                catch { return false; }
            });
            if (model != null)
            {
                try { return model.Title?.GetFormattedText() ?? characterId; }
                catch { return characterId; }
            }
        }
        catch { }
        return characterId;
    }

    private static void UpdateCharacterIconHighlights()
    {
        if (_charIconRow == null) return;

        foreach (var child in _charIconRow.GetChildren())
        {
            if (child is VBoxContainer wrapper && wrapper.HasMeta("char_id"))
            {
                string charId = wrapper.GetMeta("char_id").AsString();
                bool isSelected = charId == _selectedCharacterId;

                foreach (var c in wrapper.GetChildren())
                {
                    if (c is Button btn && btn.CustomMinimumSize.X == 80 && btn.CustomMinimumSize.Y == 80)
                    {
                        // 重建 StyleBox（新实例触发重绘），保证当前角色长期显示黄色方框
                        ApplyCharacterIconStyle(btn, isSelected);
                    }
                    if (c is Label nameLabel)
                    {
                        nameLabel.AddThemeColorOverride("font_color", isSelected ? StsColors.gold : StsColors.cream);
                    }
                }
            }
        }
    }

    private static void BuildCardPoolSection(VBoxContainer container, CharacterConfig config)
    {
        var poolHeader = CreateSectionHeader(L("CONFIG_CARD_POOL"));
        container.AddChild(poolHeader);

        var enableRow = new HBoxContainer();
        enableRow.AddThemeConstantOverride("separation", 12);
        container.AddChild(enableRow);

        var enableLabel = new Label();
        enableLabel.Text = L("CONFIG_ENABLE_CUSTOM_DECK");
        enableLabel.AddThemeFontSizeOverride("font_size", 14);
        enableLabel.AddThemeColorOverride("font_color", StsColors.cream);
        enableRow.AddChild(enableLabel);

        var enableBtn = CreateToggleButton(config.EnableCustomDeck,
            config.EnableCustomDeck ? L("CONFIG_ENABLED") : L("CONFIG_DISABLED"));
        enableBtn.Pressed += () =>
        {
            config.EnableCustomDeck = !config.EnableCustomDeck;
            ConfigManager.UpdateCharacterConfig(config);
            RefreshContent();
        };
        enableRow.AddChild(enableBtn);

        AddDivider(container);

        if (!config.EnableCustomDeck)
        {
            var defaultHeader = CreateSectionHeader(L("CONFIG_DEFAULT_DECK"));
            container.AddChild(defaultHeader);
            ShowDefaultDeckInfo(container, _selectedCharacterId!);
        }
        else
        {
            var customHeader = CreateSectionHeader(L("CONFIG_CUSTOM_DECK"));
            container.AddChild(customHeader);
            ShowCustomDeckEditor(container, config);
        }

        AddDivider(container);

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", 12);
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        container.AddChild(buttonRow);

        var resetBtn = CreateActionButton(L("CONFIG_RESET_DEFAULT"), StsColors.cream);
        resetBtn.CustomMinimumSize = new Vector2(120, 36);
        resetBtn.Pressed += () =>
        {
            ShowConfirmDialog(
                L("CONFIG_CONFIRM_RESET_TITLE"),
                L("CONFIG_CONFIRM_RESET_DESC"),
                () =>
                {
                    ConfigManager.ResetCharacterConfig(_selectedCharacterId!);
                    RefreshContent();
                });
        };
        buttonRow.AddChild(resetBtn);

        var saveBtn = CreateActionButton(L("CONFIG_SAVE"), StsColors.green);
        saveBtn.CustomMinimumSize = new Vector2(120, 36);
        saveBtn.Pressed += () =>
        {
            ShowConfirmDialog(
                L("CONFIG_CONFIRM_SAVE_TITLE"),
                L("CONFIG_CONFIRM_SAVE_DESC"),
                () =>
                {
                    ConfigManager.Save();
                    ShowNotification(L("CONFIG_SAVED"));
                });
        };
        buttonRow.AddChild(saveBtn);
    }

    private static Control CreateCharacterIconButton(CharacterModel character)
    {
        var wrapper = new VBoxContainer();
        wrapper.CustomMinimumSize = new Vector2(100, 120);
        wrapper.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        wrapper.AddThemeConstantOverride("separation", 4);
        wrapper.Alignment = BoxContainer.AlignmentMode.Center;
        wrapper.SetMeta("char_id", character.Id.Entry);

        bool isSelected = _selectedCharacterId == character.Id.Entry;

        var iconBtn = new Button();
        iconBtn.CustomMinimumSize = new Vector2(80, 80);
        // 非 Flat 才会绘制 normal 边框；四态统一覆写，避免默认装饰
        iconBtn.Flat = false;
        // 去掉点击后的白色焦点框，只保留常驻的黄色选中方框
        iconBtn.FocusMode = Control.FocusModeEnum.None;
        iconBtn.MouseFilter = Control.MouseFilterEnum.Stop;

        ApplyCharacterIconStyle(iconBtn, isSelected);

        var iconTexture = LoadCharacterIcon(character);
        if (iconTexture != null)
        {
            var textureRect = new TextureRect();
            textureRect.Texture = iconTexture;
            textureRect.AnchorRight = 1;
            textureRect.AnchorBottom = 1;
            textureRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            textureRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            textureRect.MouseFilter = Control.MouseFilterEnum.Ignore;
            iconBtn.AddChild(textureRect);
        }
        else
        {
            var colorRect = new ColorRect();
            colorRect.Color = new Color(0.3f, 0.25f, 0.35f, 0.8f);
            colorRect.AnchorRight = 1;
            colorRect.AnchorBottom = 1;
            colorRect.MouseFilter = Control.MouseFilterEnum.Ignore;
            iconBtn.AddChild(colorRect);

            try
            {
                var initialLabel = new Label();
                initialLabel.Text = character.Id.Entry.Substring(0, 1).ToUpper();
                initialLabel.AddThemeFontSizeOverride("font_size", 28);
                initialLabel.AddThemeColorOverride("font_color", StsColors.gold);
                initialLabel.HorizontalAlignment = HorizontalAlignment.Center;
                initialLabel.VerticalAlignment = VerticalAlignment.Center;
                initialLabel.AnchorRight = 1;
                initialLabel.AnchorBottom = 1;
                initialLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                iconBtn.AddChild(initialLabel);
            }
            catch { }
        }

        string charId = character.Id.Entry;
        iconBtn.Pressed += () =>
        {
            _selectedCharacterId = charId;
            RefreshContent();
        };

        wrapper.AddChild(iconBtn);

        var nameLabel = new Label();
        try
        {
            nameLabel.Text = character.Title?.GetFormattedText() ?? character.Id.Entry;
        }
        catch
        {
            nameLabel.Text = character.Id.Entry;
        }
        nameLabel.AddThemeFontSizeOverride("font_size", 12);
        nameLabel.AddThemeColorOverride("font_color", isSelected ? StsColors.gold : StsColors.cream);
        nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        wrapper.AddChild(nameLabel);

        return wrapper;
    }

    private static void ApplyCharacterIconStyle(Button btn, bool isSelected)
    {
        var bg = new StyleBoxFlat();
        bg.BgColor = isSelected
            ? new Color(0.30f, 0.25f, 0.10f, 0.95f)
            : new Color(0.15f, 0.12f, 0.18f, 0.9f);
        bg.SetBorderWidthAll(2);
        bg.BorderColor = isSelected ? StsColors.gold : new Color(0.45f, 0.40f, 0.30f, 0.5f);
        bg.SetCornerRadiusAll(8);
        // normal/hover/pressed/focus 统一，避免默认焦点框与悬停装饰
        btn.AddThemeStyleboxOverride("normal", bg);
        btn.AddThemeStyleboxOverride("hover", bg);
        btn.AddThemeStyleboxOverride("pressed", bg);
        btn.AddThemeStyleboxOverride("focus", bg);
    }

    private static Texture2D? LoadCharacterIcon(CharacterModel character)
    {
        string charId = character.Id.Entry;
        if (_iconCache.TryGetValue(charId, out var cached))
        {
            if (GodotObject.IsInstanceValid(cached))
                return cached;
            _iconCache.Remove(charId);
        }

        try
        {
            var tex = character.IconTexture;
            if (tex != null && GodotObject.IsInstanceValid(tex))
            {
                _iconCache[charId] = tex;
                return tex;
            }
        }
        catch { }

        try
        {
            var selectIcon = character.CharacterSelectIcon;
            if (selectIcon != null && GodotObject.IsInstanceValid(selectIcon))
            {
                _iconCache[charId] = selectIcon;
                return selectIcon;
            }
        }
        catch { }

        return null;
    }

    // ============ 数据辅助方法 ============

    private static void ShowDefaultDeckInfo(VBoxContainer container, string characterId)
    {
        try
        {
            var characterModel = ModelDb.AllCharacters.FirstOrDefault(c =>
            {
                try { return c.Id.Entry == characterId; }
                catch { return false; }
            });

            if (characterModel == null)
            {
                var noModelLabel = new Label();
                noModelLabel.Text = L("CONFIG_NO_CHARACTER_MODEL");
                noModelLabel.AddThemeFontSizeOverride("font_size", 14);
                noModelLabel.AddThemeColorOverride("font_color", StsColors.gray);
                container.AddChild(noModelLabel);
                return;
            }

            // 官方属性 CharacterModel.StartingDeck（RitsuLib 已将 StartingDeckEntries 映射到此处）
            List<CardModel> startingCards;
            try
            {
                startingCards = characterModel.StartingDeck?.ToList() ?? new List<CardModel>();
            }
            catch
            {
                startingCards = new List<CardModel>();
            }

            if (startingCards.Count == 0)
            {
                var emptyLabel = new Label();
                emptyLabel.Text = L("CONFIG_DECK_EMPTY");
                emptyLabel.AddThemeFontSizeOverride("font_size", 14);
                emptyLabel.AddThemeColorOverride("font_color", StsColors.gray);
                container.AddChild(emptyLabel);
                return;
            }

            var infoLabel = new Label();
            infoLabel.Text = L("CONFIG_DEFAULT_DECK_INFO", startingCards.Count);
            infoLabel.AddThemeFontSizeOverride("font_size", 14);
            infoLabel.AddThemeColorOverride("font_color", StsColors.gray);
            container.AddChild(infoLabel);

            // 按卡牌标题分组统计数量（直接读取 CardModel.Title，兼容原版与mod卡牌）
            var counts = new Dictionary<string, int>();
            foreach (var card in startingCards)
            {
                string name;
                try { name = card.Title; }
                catch { name = card.GetType().Name; }
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }

            var deckListLabel = new Label();
            deckListLabel.Text = string.Join("，", counts.Select(kv => $"{kv.Key} × {kv.Value}"));
            deckListLabel.AddThemeFontSizeOverride("font_size", 12);
            deckListLabel.AddThemeColorOverride("font_color", StsColors.gray);
            deckListLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            container.AddChild(deckListLabel);
        }
        catch
        {
            var infoLabel = new Label();
            infoLabel.Text = L("CONFIG_NO_DECK_INFO");
            infoLabel.AddThemeFontSizeOverride("font_size", 14);
            infoLabel.AddThemeColorOverride("font_color", StsColors.gray);
            container.AddChild(infoLabel);
        }
    }

    private static void BuildRelicConfigContent(VBoxContainer container, CharacterConfig config)
    {
        var header = CreateSectionHeader(L("CONFIG_RELIC_TITLE"));
        container.AddChild(header);

        var desc = new Label();
        desc.Text = L("CONFIG_RELIC_DESC");
        desc.AddThemeFontSizeOverride("font_size", 13);
        desc.AddThemeColorOverride("font_color", StsColors.gray);
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        container.AddChild(desc);

        var toggleRow = new HBoxContainer();
        toggleRow.AddThemeConstantOverride("separation", 12);
        container.AddChild(toggleRow);

        var toggleLabel = new Label();
        toggleLabel.Text = L("CONFIG_RELIC_ENABLE");
        toggleLabel.AddThemeFontSizeOverride("font_size", 15);
        toggleLabel.AddThemeColorOverride("font_color", StsColors.cream);
        toggleRow.AddChild(toggleLabel);

        var enableBtn = CreateToggleButton(config.EnableCustomRelics,
            config.EnableCustomRelics ? L("CONFIG_ENABLED") : L("CONFIG_DISABLED"));
        enableBtn.Pressed += () =>
        {
            config.EnableCustomRelics = !config.EnableCustomRelics;
            ConfigManager.UpdateCharacterConfig(config);
            RefreshContent();
        };
        toggleRow.AddChild(enableBtn);

        AddDivider(container);

        var currentLabel = new Label();
        currentLabel.Text = L("CONFIG_RELIC_CURRENT", config.StartingRelicTypes.Count);
        currentLabel.AddThemeFontSizeOverride("font_size", 14);
        currentLabel.AddThemeColorOverride("font_color", StsColors.gold);
        container.AddChild(currentLabel);

        if (config.StartingRelicTypes.Count == 0)
        {
            var emptyLabel = new Label();
            emptyLabel.Text = L("CONFIG_RELIC_EMPTY");
            emptyLabel.AddThemeFontSizeOverride("font_size", 13);
            emptyLabel.AddThemeColorOverride("font_color", StsColors.gray);
            container.AddChild(emptyLabel);
        }
        else
        {
            var relicCounts = new Dictionary<string, int>();
            foreach (var relicType in config.StartingRelicTypes)
            {
                relicCounts.TryGetValue(relicType, out int count);
                relicCounts[relicType] = count + 1;
            }

            var flow = new FlowContainer();
            flow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            flow.AddThemeConstantOverride("h_separation", 6);
            flow.AddThemeConstantOverride("v_separation", 6);
            container.AddChild(flow);

            foreach (var (relicTypeName, count) in relicCounts)
            {
                flow.AddChild(CreateRelicConfigTile(config, relicTypeName, count));
            }
        }

        var hintLabel = new Label();
        hintLabel.Text = L("CONFIG_RELIC_REMOVE_HINT");
        hintLabel.AddThemeFontSizeOverride("font_size", 11);
        hintLabel.AddThemeColorOverride("font_color", StsColors.gray);
        container.AddChild(hintLabel);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 10);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        container.AddChild(btnRow);

        var libraryBtn = CreateActionButton(L("CONFIG_RELIC_LIBRARY"), StsColors.gold);
        libraryBtn.CustomMinimumSize = new Vector2(150, 36);
        libraryBtn.Pressed += () => OpenRelicLibrary(config);
        btnRow.AddChild(libraryBtn);

        if (config.StartingRelicTypes.Count > 0)
        {
            var clearBtn = CreateActionButton(L("CONFIG_CLEAR_RELICS"), StsColors.red);
            clearBtn.CustomMinimumSize = new Vector2(120, 36);
            clearBtn.Pressed += () =>
            {
                config.StartingRelicTypes.Clear();
                ConfigManager.UpdateCharacterConfig(config);
                RefreshContent();
            };
            btnRow.AddChild(clearBtn);
        }

        var saveBtn = CreateActionButton(L("CONFIG_SAVE"), StsColors.green);
        saveBtn.CustomMinimumSize = new Vector2(120, 36);
        saveBtn.Pressed += () =>
        {
            ShowConfirmDialog(
                L("CONFIG_CONFIRM_SAVE_TITLE"),
                L("CONFIG_CONFIRM_SAVE_DESC"),
                () =>
                {
                    ConfigManager.Save();
                    ShowNotification(L("CONFIG_SAVED"));
                });
        };
        btnRow.AddChild(saveBtn);
    }

    /// <summary>
    /// 初始资源配置页：金币与血量上限（0 = 使用角色默认值）。
    /// </summary>
    private static void BuildResourceConfigContent(VBoxContainer container, CharacterConfig config)
    {
        var header = CreateSectionHeader(L("CONFIG_RESOURCE_TITLE"));
        container.AddChild(header);

        var desc = new Label();
        desc.Text = L("CONFIG_RESOURCE_DESC");
        desc.AddThemeFontSizeOverride("font_size", 13);
        desc.AddThemeColorOverride("font_color", StsColors.gray);
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        container.AddChild(desc);

        int defaultGold = 0;
        int defaultHp = 0;
        try
        {
            var model = ModelDb.AllCharacters.FirstOrDefault(c =>
            {
                try { return c.Id.Entry == _selectedCharacterId; }
                catch { return false; }
            });
            if (model != null)
            {
                defaultGold = model.StartingGold;
                defaultHp = model.StartingHp;
            }
        }
        catch { }

        AddDivider(container);

        var goldRow = new HBoxContainer();
        goldRow.AddThemeConstantOverride("separation", 12);
        goldRow.Alignment = BoxContainer.AlignmentMode.Center;
        container.AddChild(goldRow);

        var goldLabel = new Label();
        goldLabel.Text = L("CONFIG_RESOURCE_GOLD");
        goldLabel.AddThemeFontSizeOverride("font_size", 15);
        goldLabel.AddThemeColorOverride("font_color", StsColors.cream);
        goldLabel.CustomMinimumSize = new Vector2(120, 0);
        goldRow.AddChild(goldLabel);

        var goldSpin = new SpinBox();
        goldSpin.MinValue = 0;
        goldSpin.MaxValue = 99999;
        goldSpin.Step = 1;
        goldSpin.Value = config.StartingGold;
        goldSpin.CustomMinimumSize = new Vector2(180, 36);
        goldSpin.AddThemeFontSizeOverride("font_size", 14);
        goldSpin.ValueChanged += v =>
        {
            config.StartingGold = (int)v;
            ConfigManager.UpdateCharacterConfig(config);
        };
        goldRow.AddChild(goldSpin);

        var goldDefault = new Label();
        goldDefault.Text = L("CONFIG_RESOURCE_DEFAULT", defaultGold);
        goldDefault.AddThemeFontSizeOverride("font_size", 12);
        goldDefault.AddThemeColorOverride("font_color", StsColors.gray);
        goldRow.AddChild(goldDefault);

        var hpRow = new HBoxContainer();
        hpRow.AddThemeConstantOverride("separation", 12);
        hpRow.Alignment = BoxContainer.AlignmentMode.Center;
        container.AddChild(hpRow);

        var hpLabel = new Label();
        hpLabel.Text = L("CONFIG_RESOURCE_HP");
        hpLabel.AddThemeFontSizeOverride("font_size", 15);
        hpLabel.AddThemeColorOverride("font_color", StsColors.cream);
        hpLabel.CustomMinimumSize = new Vector2(120, 0);
        hpRow.AddChild(hpLabel);

        var hpSpin = new SpinBox();
        hpSpin.MinValue = 0;
        hpSpin.MaxValue = 99999;
        hpSpin.Step = 1;
        hpSpin.Value = config.MaxHp;
        hpSpin.CustomMinimumSize = new Vector2(180, 36);
        hpSpin.AddThemeFontSizeOverride("font_size", 14);
        hpSpin.ValueChanged += v =>
        {
            config.MaxHp = (int)v;
            ConfigManager.UpdateCharacterConfig(config);
        };
        hpRow.AddChild(hpSpin);

        var hpDefault = new Label();
        hpDefault.Text = L("CONFIG_RESOURCE_DEFAULT", defaultHp);
        hpDefault.AddThemeFontSizeOverride("font_size", 12);
        hpDefault.AddThemeColorOverride("font_color", StsColors.gray);
        hpRow.AddChild(hpDefault);

        var zeroHint = new Label();
        zeroHint.Text = L("CONFIG_RESOURCE_ZERO_HINT");
        zeroHint.AddThemeFontSizeOverride("font_size", 11);
        zeroHint.AddThemeColorOverride("font_color", StsColors.gray);
        container.AddChild(zeroHint);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 10);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        container.AddChild(btnRow);

        var resetBtn = CreateActionButton(L("CONFIG_RESOURCE_RESET"), StsColors.red);
        resetBtn.CustomMinimumSize = new Vector2(140, 36);
        resetBtn.Pressed += () =>
        {
            config.StartingGold = 0;
            config.MaxHp = 0;
            ConfigManager.UpdateCharacterConfig(config);
            RefreshContent();
        };
        btnRow.AddChild(resetBtn);

        var saveBtn = CreateActionButton(L("CONFIG_SAVE"), StsColors.green);
        saveBtn.CustomMinimumSize = new Vector2(120, 36);
        saveBtn.Pressed += () =>
        {
            ShowConfirmDialog(
                L("CONFIG_CONFIRM_SAVE_TITLE"),
                L("CONFIG_CONFIRM_SAVE_DESC"),
                () =>
                {
                    ConfigManager.Save();
                    ShowNotification(L("CONFIG_SAVED"));
                });
        };
        btnRow.AddChild(saveBtn);
    }

    private static Control CreateRelicConfigTile(CharacterConfig config, string relicTypeName, int count)
    {
        var tile = new Control();
        tile.CustomMinimumSize = new Vector2(56, 56);
        tile.MouseFilter = Control.MouseFilterEnum.Stop;
        tile.MouseDefaultCursorShape = Control.CursorShape.PointingHand;

        var relicModel = GetRelicModelByTypeName(relicTypeName);
        if (relicModel != null)
        {
            try
            {
                var icon = new TextureRect();
                icon.Texture = relicModel.Icon;
                icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                icon.MouseFilter = Control.MouseFilterEnum.Ignore;
                tile.AddChild(icon);
            }
            catch { }
        }

        // 边框
        var frame = new PanelContainer();
        frame.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        frame.MouseFilter = Control.MouseFilterEnum.Ignore;
        var style = new StyleBoxFlat();
        style.BgColor = new Color(0, 0, 0, 0f);
        style.SetBorderWidthAll(2);
        style.BorderColor = new Color(0.35f, 0.30f, 0.25f, 0.5f);
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

        // 右上角小 ✕ 删除按钮（与卡牌模型一致）
        var removeBtn = new Button();
        removeBtn.Text = "✕";
        removeBtn.AnchorLeft = 1f;
        removeBtn.AnchorRight = 1f;
        removeBtn.AnchorTop = 0f;
        removeBtn.AnchorBottom = 0f;
        removeBtn.OffsetLeft = -13;
        removeBtn.OffsetRight = -3;
        removeBtn.OffsetTop = 2;
        removeBtn.OffsetBottom = 12;
        removeBtn.AddThemeFontSizeOverride("font_size", 8);
        removeBtn.AddThemeColorOverride("font_color", StsColors.gray);
        removeBtn.AddThemeColorOverride("font_hover_color", StsColors.red);
        removeBtn.FocusMode = Control.FocusModeEnum.None;
        removeBtn.AddThemeStyleboxOverride("normal", CreateStyleBox(
            new Color(0.08f, 0.06f, 0.10f, 0.85f), new Color(0.35f, 0.30f, 0.25f, 0.5f)));
        removeBtn.AddThemeStyleboxOverride("hover", CreateStyleBox(
            new Color(0.25f, 0.10f, 0.12f, 0.95f), StsColors.red));
        removeBtn.Pressed += () => RemoveRelicFromConfig(config, relicTypeName);
        tile.AddChild(removeBtn);

        // 左键查看详情，右键移除
        tile.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                tile.GetViewport()?.SetInputAsHandled();
                ShowRelicDetails(relicTypeName);
            }
            else if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                tile.GetViewport()?.SetInputAsHandled();
                RemoveRelicFromConfig(config, relicTypeName);
            }
        };

        // 悬停显示遗物详情
        tile.MouseEntered += () =>
        {
            try
            {
                var relic = GetRelicModelByTypeName(relicTypeName);
                if (relic != null) ShowRelicHoverTips(tile, relic);
            }
            catch { }
        };
        tile.MouseExited += () => NHoverTipSet.Remove(tile);

        return tile;
    }

    private static void ShowRelicHoverTips(Control owner, RelicModel relic)
    {
        try
        {
            var tipSet = NHoverTipSet.CreateAndShow(owner, relic.HoverTips, HoverTipAlignment.Left);
            if (tipSet != null && GodotObject.IsInstanceValid(tipSet))
            {
                tipSet.GetParent()?.RemoveChild(tipSet);
                UiLayers.GetHoverTipLayer().AddChild(tipSet);
            }
        }
        catch { }
    }

    private static void RemoveRelicFromConfig(CharacterConfig config, string relicTypeName)
    {
        string? firstMatch = config.StartingRelicTypes.FirstOrDefault(t => t == relicTypeName);
        if (firstMatch != null)
        {
            config.StartingRelicTypes.Remove(firstMatch);
            ConfigManager.UpdateCharacterConfig(config);
            RefreshContent();
        }
    }

    private static void OpenRelicLibrary(CharacterConfig config)
    {
        _relicLibraryTab = new RelicLibraryTab(config, () => RefreshContent());
        _relicLibraryTab.Show();
    }

    private static string GetRelicDisplayName(string relicTypeName)
    {
        try
        {
            var relicModel = GetRelicModelByTypeName(relicTypeName);
            if (relicModel != null)
            {
                try { return relicModel.Title.GetFormattedText(); }
                catch { }
            }
        }
        catch { }
        return relicTypeName;
    }

    private static RelicModel? GetRelicModelByTypeName(string relicTypeName)
    {
        try
        {
            var type = FindTypeInAllAssemblies(relicTypeName);
            if (type != null)
            {
                var relicMethod = typeof(ModelDb).GetMethods()
                    .FirstOrDefault(m => m.Name == "Relic" && m.IsGenericMethodDefinition);
                if (relicMethod != null)
                {
                    var genericMethod = relicMethod.MakeGenericMethod(type);
                    return genericMethod.Invoke(null, null) as RelicModel;
                }
            }
        }
        catch { }
        return null;
    }

    private static void ShowRelicDetails(string relicTypeName)
    {
        try
        {
            var relic = GetRelicModelByTypeName(relicTypeName);
            if (relic == null) return;
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
            Logger.Error($"[ModConfig] ShowRelicDetails 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 遗物检视页打开时，临时降低配置面板层级，避免盖住详情页。
    /// </summary>
    public static void OnInspectScreenOpened()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            // 记录打开详情前的可见状态，仅当配置面板本来就在显示时才在关闭后恢复
            _wasVisibleBeforeInspect = _layer.Visible;
            _layer.ZIndex = -1;
            // 隐藏配置面板，让点击任意位置都落到详情页背板，点击即可关闭
            _layer.Visible = false;
        }
    }

    /// <summary>
    /// 遗物检视页关闭后恢复配置面板层级。
    /// </summary>
    public static void OnInspectScreenClosed()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            _layer.ZIndex = OverlayZIndex;
            _layer.Visible = _wasVisibleBeforeInspect;
        }
    }

    private static void ShowCustomDeckEditor(VBoxContainer container, CharacterConfig config)
    {
        var currentDeckLabel = new Label();
        currentDeckLabel.Text = L("CONFIG_CUSTOM_DECK_COUNT", config.CustomDeckCardTypes.Count);
        currentDeckLabel.AddThemeFontSizeOverride("font_size", 14);
        currentDeckLabel.AddThemeColorOverride("font_color", StsColors.gold);
        container.AddChild(currentDeckLabel);

        if (config.CustomDeckCardTypes.Count == 0)
        {
            var emptyLabel = new Label();
            emptyLabel.Text = L("CONFIG_CUSTOM_DECK_EMPTY");
            emptyLabel.AddThemeFontSizeOverride("font_size", 14);
            emptyLabel.AddThemeColorOverride("font_color", StsColors.gray);
            container.AddChild(emptyLabel);
        }
        else
        {
            // 升级卡与未升级同名卡分开统计（各自独立叠加）
            var cardCounts = new Dictionary<(string TypeName, bool Upgraded), int>();
            foreach (var entry in config.CustomDeckCardTypes)
            {
                string typeName = ConfigManager.DecodeCardType(entry, out bool upgraded);
                var key = (typeName, upgraded);
                cardCounts.TryGetValue(key, out int count);
                cardCounts[key] = count + 1;
            }

            var flowContainer = new VBoxContainer();
            flowContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            flowContainer.AddThemeConstantOverride("separation", 6);
            container.AddChild(flowContainer);

            // 卡牌按容器宽度自动换行（不再固定每行5张）
            var cardsFlow = new FlowContainer();
            cardsFlow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            cardsFlow.AddThemeConstantOverride("h_separation", 8);
            cardsFlow.AddThemeConstantOverride("v_separation", 6);
            flowContainer.AddChild(cardsFlow);

            foreach (var ((typeName, upgraded), count) in cardCounts.OrderByDescending(kv => kv.Value))
            {
                string displayName = GetCardDisplayName(typeName) + (upgraded ? " +" : "");
                var cardModel = GetCardModelByTypeName(typeName);

                var cardThumb = new VBoxContainer();
                cardThumb.Alignment = BoxContainer.AlignmentMode.Center;
                cardThumb.AddThemeConstantOverride("separation", 2);
                cardThumb.CustomMinimumSize = new Vector2(110, 160);

                // 用 clip 包裹卡牌，避免卡片自然最小尺寸把格子撑大（与 CardLibraryTab 相同方案）
                var clip = new Control();
                clip.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                clip.CustomMinimumSize = new Vector2(0, 150);
                clip.ClipContents = true;
                clip.MouseFilter = Control.MouseFilterEnum.Ignore;
                cardThumb.AddChild(clip);

                if (cardModel != null)
                {
                    try
                    {
                        var displayCard = cardModel.IsMutable ? cardModel : cardModel.ToMutable();
                        if (upgraded && !displayCard.IsUpgraded)
                        {
                            CardCmd.Upgrade(displayCard);
                        }
                        var nCard = NCard.Create(displayCard);
                        if (nCard != null)
                        {
                            nCard.Scale = new Vector2(0.32f, 0.32f);
                            nCard.MouseFilter = Control.MouseFilterEnum.Ignore;
                            clip.AddChild(nCard);

                            NCard capturedCard = nCard;
                            Control capturedClip = clip;
                            // 复用池中的 NCard 可能已 Ready：Ready 信号不会再次触发，
                            // 必须立即刷新视觉，否则会显示上一张卡残留的文案/模型
                            if (nCard.IsNodeReady())
                            {
                                nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                                CenterThumb(capturedClip, capturedCard, 0.32f);
                            }
                            else
                            {
                                nCard.Ready += () =>
                                {
                                    if (GodotObject.IsInstanceValid(capturedCard))
                                        capturedCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                                    Callable.From(() =>
                                    {
                                        if (GodotObject.IsInstanceValid(capturedClip) && GodotObject.IsInstanceValid(capturedCard))
                                            CenterThumb(capturedClip, capturedCard, 0.32f);
                                    }).CallDeferred();
                                };
                            }
                            clip.Resized += () =>
                            {
                                if (GodotObject.IsInstanceValid(capturedClip) && GodotObject.IsInstanceValid(capturedCard))
                                    CenterThumb(capturedClip, capturedCard, 0.32f);
                            };
                        }
                    }
                    catch { }
                }

                // 卡片右上角 ✕ 按钮：点击移除一张（与右键逻辑一致）
                var removeBtn = new Button();
                removeBtn.Text = "✕";
                removeBtn.AnchorLeft = 1f;
                removeBtn.AnchorRight = 1f;
                removeBtn.AnchorTop = 0f;
                removeBtn.AnchorBottom = 0f;
                removeBtn.OffsetLeft = -21;
                removeBtn.OffsetRight = -11;
                removeBtn.OffsetTop = 3;
                removeBtn.OffsetBottom = 13;
                removeBtn.AddThemeFontSizeOverride("font_size", 9);
                // 与遗物 X 按钮一致：未聚焦灰色，聚焦红色高亮
                removeBtn.AddThemeColorOverride("font_color", StsColors.gray);
                removeBtn.AddThemeColorOverride("font_hover_color", StsColors.red);
                removeBtn.AddThemeStyleboxOverride("normal", CreateStyleBox(
                    new Color(0.08f, 0.06f, 0.10f, 0.85f), new Color(0.35f, 0.30f, 0.25f, 0.5f)));
                removeBtn.AddThemeStyleboxOverride("hover", CreateStyleBox(
                    new Color(0.25f, 0.10f, 0.12f, 0.95f), StsColors.red));
                removeBtn.AddThemeStyleboxOverride("pressed", CreateStyleBox(
                    new Color(0.08f, 0.06f, 0.10f, 0.95f), new Color("B89840")));
                removeBtn.FocusMode = Control.FocusModeEnum.None;
                string encodedEntry = ConfigManager.EncodeCardType(typeName, upgraded);
                string capturedRemoveType = encodedEntry;
                removeBtn.Pressed += () => RemoveDeckCard(config, capturedRemoveType);
                clip.AddChild(removeBtn);

                var infoRow = new HBoxContainer();
                infoRow.Alignment = BoxContainer.AlignmentMode.Center;
                infoRow.AddThemeConstantOverride("separation", 3);

                var nameLabel = new Label();
                nameLabel.Text = displayName;
                nameLabel.AddThemeFontSizeOverride("font_size", 10);
                nameLabel.AddThemeColorOverride("font_color", StsColors.cream);
                infoRow.AddChild(nameLabel);

                var countLbl = new Label();
                countLbl.Text = $"×{count}";
                countLbl.AddThemeFontSizeOverride("font_size", 10);
                countLbl.AddThemeColorOverride("font_color", StsColors.gold);
                infoRow.AddChild(countLbl);

                cardThumb.AddChild(infoRow);

                string capturedType = encodedEntry;
                cardThumb.GuiInput += (InputEvent ev) =>
                {
                    if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
                    {
                        cardThumb.GetViewport()?.SetInputAsHandled();
                        RemoveDeckCard(config, capturedType);
                    }
                };

                cardsFlow.AddChild(cardThumb);
            }

            var hintLabel = new Label();
            hintLabel.Text = L("CONFIG_CARD_REMOVE_HINT");
            hintLabel.AddThemeFontSizeOverride("font_size", 11);
            hintLabel.AddThemeColorOverride("font_color", StsColors.gray);
            flowContainer.AddChild(hintLabel);
        }

        AddDivider(container);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 10);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        container.AddChild(btnRow);

        var addBtn = CreateActionButton(L("CONFIG_OPEN_CARD_LIBRARY"), StsColors.gold);
        addBtn.CustomMinimumSize = new Vector2(150, 36);
        addBtn.Pressed += () => OpenCardLibrary(config);
        btnRow.AddChild(addBtn);

        if (config.CustomDeckCardTypes.Count > 0)
        {
            var clearBtn = CreateActionButton(L("CONFIG_CLEAR_DECK"), StsColors.red);
            clearBtn.CustomMinimumSize = new Vector2(120, 36);
            clearBtn.Pressed += () =>
            {
                config.CustomDeckCardTypes.Clear();
                ConfigManager.UpdateCharacterConfig(config);
                RefreshContent();
            };
            btnRow.AddChild(clearBtn);
        }
    }

    /// <summary>
    /// 从自定义卡组移除一张指定卡牌（每次移除 1 张，与原有右键逻辑一致）。
    /// </summary>
    private static void RemoveDeckCard(CharacterConfig config, string cardType)
    {
        string? firstMatch = config.CustomDeckCardTypes.FirstOrDefault(t => t == cardType);
        if (firstMatch != null)
        {
            config.CustomDeckCardTypes.Remove(firstMatch);
            ConfigManager.UpdateCharacterConfig(config);
            RefreshContent();
        }
    }

    /// <summary>
    /// 将卡牌缩略图居中到 clip 内（NCard 原点在卡牌中心，因此把原点放到 clip 中心）。
    /// </summary>
    private static void CenterThumb(Control clip, Control card, float scale)
    {
        if (!GodotObject.IsInstanceValid(clip) || !GodotObject.IsInstanceValid(card)) return;
        // NCard 的原点在卡牌中心（card.tscn 中卡面偏移 -150..150 × -211..211），不是左上角，
        // 所以把原点放到 clip 中心即可让整张卡居中（绘制尺寸 = 300×422×scale）。
        // 原实现把原点当左上角，用 (clip.Size - drawn) / 2 计算，导致卡牌整体向左上偏移、
        // 顶部（卡名/费用/插画）和左侧（描述开头）被 clip 裁掉。
        card.Position = new Vector2(clip.Size.X / 2f, clip.Size.Y / 2f);
    }

    /// <summary>
    /// 在所有已加载程序集中按名称查找类型（兼容本mod与原版卡牌，如 Wound）。
    /// </summary>
    private static Type? FindTypeInAllAssemblies(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetType(typeName);
                if (type != null) return type;
            }
            catch { }
        }

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

    private static string GetCardDisplayName(string typeName)
    {
        try
        {
            var type = FindTypeInAllAssemblies(typeName);
            if (type != null)
            {
                var cardModel = GetCardModelByType(type);
                if (cardModel != null)
                {
                    try { return cardModel.Title; } catch { }
                }
            }
        }
        catch { }
        return typeName;
    }

    private static CardModel? GetCardModelByTypeName(string typeName)
    {
        try
        {
            var type = FindTypeInAllAssemblies(typeName);
            if (type != null)
            {
                return GetCardModelByType(type);
            }
        }
        catch { }
        return null;
    }

    private static CardModel? GetCardModelByType(Type cardType)
    {
        try
        {
            var cardMethod = typeof(ModelDb).GetMethods()
                .FirstOrDefault(m => m.Name == "Card" && m.IsGenericMethodDefinition);
            if (cardMethod != null)
            {
                var genericMethod = cardMethod.MakeGenericMethod(cardType);
                return genericMethod.Invoke(null, null) as CardModel;
            }
        }
        catch { }
        return null;
    }

    private static void OpenCardLibrary(CharacterConfig config)
    {
        _cardLibraryTab = new CardLibraryTab(config, () => RefreshContent());
        _cardLibraryTab.Show();
    }

    internal static void AddCardToDeck(CharacterConfig config, string cardTypeName, bool upgraded = false)
    {
        config.CustomDeckCardTypes.Add(ConfigManager.EncodeCardType(cardTypeName, upgraded));
        ConfigManager.UpdateCharacterConfig(config);
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

    // ============ 开局方案存储 ============

    /// <summary>
    /// 开局方案存储页：动态槽位卡片，高亮 = 当前方案，单击不高亮不切换，双击直接切换。
    /// </summary>
    private static void BuildPresetConfigContent(VBoxContainer container)
    {
        var header = CreateSectionHeader(L("CONFIG_PRESET_PAGE_TITLE"));
        container.AddChild(header);

        var desc = new Label();
        desc.Text = L("CONFIG_PRESET_PAGE_DESC");
        desc.AddThemeFontSizeOverride("font_size", 13);
        desc.AddThemeColorOverride("font_color", StsColors.gray);
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        container.AddChild(desc);

        AddDivider(container);

        var grid = new GridContainer();
        grid.Columns = 2;
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);
        container.AddChild(grid);

        for (int i = 0; i < ConfigManager.GetPresetCount(); i++)
        {
            grid.AddChild(CreatePresetSlotCard(i));
        }
    }

    private static Control CreatePresetSlotCard(int index)
    {
        var preset = ConfigManager.GetPreset(index);
        bool filled = ConfigManager.HasPreset(index);
        bool isActive = index == ConfigManager.ActivePresetIndex;
        string name = preset?.Name ?? L("CONFIG_PRESET_EMPTY");

        var card = new PanelContainer();
        card.CustomMinimumSize = new Vector2(320, 0);
        card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var style = new StyleBoxFlat();
        if (isActive)
        {
            style.BgColor = new Color(0.18f, 0.15f, 0.08f, 0.95f);
            style.SetBorderWidthAll(2);
            style.BorderColor = StsColors.gold;
        }
        else
        {
            style.BgColor = new Color(0.08f, 0.07f, 0.10f, 0.9f);
            style.SetBorderWidthAll(1);
            style.BorderColor = new Color(0.35f, 0.30f, 0.25f, 0.5f);
        }
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(10);
        card.AddThemeStyleboxOverride("panel", style);

        // 双击卡片空白处直接切换（空槽/当前槽无效，切换不再弹窗确认）
        card.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left, DoubleClick: true }
                && filled && !isActive)
            {
                card.GetViewport()?.SetInputAsHandled();
                SwitchToPreset(index);
            }
        };

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        card.AddChild(vbox);

        // 名称行：编辑按钮（左上角）+ 名字 + 当前标记 + 删除按钮（右上角）
        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 6);
        nameRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(nameRow);

        var editBtn = new Button();
        editBtn.Text = L("CONFIG_PRESET_EDIT_NAME");
        editBtn.CustomMinimumSize = new Vector2(30, 26);
        editBtn.TooltipText = L("CONFIG_PRESET_RENAME_TOOLTIP");
        editBtn.AddThemeFontSizeOverride("font_size", 12);
        editBtn.AddThemeColorOverride("font_hover_color", StsColors.gold);
        editBtn.AddThemeColorOverride("font_pressed_color", StsColors.gray);
        // 空位（未命名空槽）不允许命名
        editBtn.Disabled = preset == null;
        ApplyFlatStyle(editBtn);
        int capturedIndex = index;
        editBtn.Pressed += () => ShowRenamePresetDialog(capturedIndex);
        nameRow.AddChild(editBtn);

        var nameLabel = new Label();
        nameLabel.Text = name;
        nameLabel.AddThemeFontSizeOverride("font_size", 16);
        nameLabel.AddThemeColorOverride("font_color",
            isActive ? StsColors.gold : filled ? StsColors.cream : StsColors.gray);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameRow.AddChild(nameLabel);

        if (isActive)
        {
            var activeTag = new Label();
            activeTag.Text = L("CONFIG_PRESET_CURRENT");
            activeTag.AddThemeFontSizeOverride("font_size", 11);
            activeTag.AddThemeColorOverride("font_color", StsColors.green);
            nameRow.AddChild(activeTag);
        }

        // 删除按钮（右上角）：任何已存在的槽（含当前槽）均可删除，空槽占位不显示
        if (ConfigManager.GetPreset(index) != null)
        {
            var deleteBtn = new Button();
            deleteBtn.Text = "✕";
            deleteBtn.CustomMinimumSize = new Vector2(30, 26);
            deleteBtn.TooltipText = L("CONFIG_PRESET_DELETE_TOOLTIP");
            deleteBtn.AddThemeFontSizeOverride("font_size", 12);
            deleteBtn.AddThemeColorOverride("font_color", StsColors.gray);
            deleteBtn.AddThemeColorOverride("font_hover_color", StsColors.red);
            deleteBtn.AddThemeColorOverride("font_pressed_color", StsColors.red);
            ApplyFlatStyle(deleteBtn);
            deleteBtn.Pressed += () => ConfirmDeletePreset(capturedIndex);
            nameRow.AddChild(deleteBtn);
        }

        // 状态行：已保存角色数 / 空位
        var stateLabel = new Label();
        stateLabel.Text = isActive
            ? L("CONFIG_PRESET_SUMMARY", preset?.Characters.Count ?? 0)
            : filled
            ? L("CONFIG_PRESET_SUMMARY", preset!.Characters.Count)
            : L("CONFIG_PRESET_EMPTY");
        stateLabel.AddThemeFontSizeOverride("font_size", 12);
        stateLabel.AddThemeColorOverride("font_color", isActive ? StsColors.gold : StsColors.gray);
        vbox.AddChild(stateLabel);

        // 按钮行：保存 / 切换
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(btnRow);

        var saveBtn = CreateActionButton(L("CONFIG_PRESET_SAVE_BTN"), StsColors.green);
        saveBtn.CustomMinimumSize = new Vector2(110, 34);
        saveBtn.Pressed += () => ConfirmSavePreset(capturedIndex);
        btnRow.AddChild(saveBtn);

        var switchBtn = CreateActionButton(L("CONFIG_PRESET_SWITCH_BTN"), StsColors.gold);
        switchBtn.CustomMinimumSize = new Vector2(110, 34);
        switchBtn.Disabled = !filled || isActive;
        switchBtn.Pressed += () => SwitchToPreset(capturedIndex);
        btnRow.AddChild(switchBtn);

        return card;
    }

    private static void ConfirmSavePreset(int index)
    {
        if (index == ConfigManager.ActivePresetIndex)
        {
            ShowConfirmDialog(
                L("CONFIG_PRESET_SAVE_TITLE"),
                L("CONFIG_PRESET_SAVE_CURRENT_DESC"),
                () =>
                {
                    ConfigManager.SavePreset(index);
                    RefreshContent();
                    ShowNotification(L("CONFIG_PRESET_SAVED_CURRENT"));
                });
        }
        else if (ConfigManager.HasPreset(index))
        {
            ShowConfirmDialog(
                L("CONFIG_PRESET_OVERWRITE_TITLE"),
                L("CONFIG_PRESET_OVERWRITE_DESC", index + 1),
                () =>
                {
                    ConfigManager.SavePreset(index);
                    RefreshContent();
                    ShowNotification(L("CONFIG_PRESET_SAVED", index + 1));
                });
        }
        else
        {
            ShowConfirmDialog(
                L("CONFIG_PRESET_NEW_TITLE"),
                L("CONFIG_PRESET_NEW_DESC"),
                () =>
                {
                    ConfigManager.SavePreset(index);
                    RefreshContent();
                    ShowNotification(L("CONFIG_PRESET_NEW_SAVED"));
                });
        }
    }

    /// <summary>
    /// 直接切换方案：不覆盖任何槽内容、无确认弹窗（切换只移动高亮，随时可切回）。
    /// </summary>
    private static void SwitchToPreset(int index)
    {
        if (!ConfigManager.HasPreset(index)) return;
        string presetName = ConfigManager.GetPreset(index)?.Name ?? L("CONFIG_PRESET_SLOT", index);
        if (ConfigManager.LoadPreset(index))
        {
            RefreshContent();
            ShowNotification(L("CONFIG_PRESET_LOADED", presetName));
        }
    }

    private static void ConfirmDeletePreset(int index)
    {
        var preset = ConfigManager.GetPreset(index);
        if (preset == null) return;
        string presetName = preset.Name;
        ShowConfirmDialog(
            L("CONFIG_PRESET_DELETE_TITLE"),
            L("CONFIG_PRESET_DELETE_DESC", presetName),
            () =>
            {
                if (ConfigManager.DeletePreset(index))
                {
                    RefreshContent();
                    ShowNotification(L("CONFIG_PRESET_DELETED", presetName));
                }
            });
    }

    /// <summary>
    /// 方案命名对话框：输入框 + 确认/取消。
    /// </summary>
    private static void ShowRenamePresetDialog(int index)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;

        var dialogMask = new ColorRect();
        dialogMask.Color = new Color(0f, 0f, 0f, 0.5f);
        dialogMask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        dialogMask.MouseFilter = Control.MouseFilterEnum.Stop;
        _layer.AddChild(dialogMask);

        var dialog = new PanelContainer();
        dialog.AnchorLeft = 0.5f;
        dialog.AnchorRight = 0.5f;
        dialog.AnchorTop = 0.5f;
        dialog.AnchorBottom = 0.5f;
        dialog.OffsetLeft = -180;
        dialog.OffsetRight = 180;
        dialog.GrowHorizontal = Control.GrowDirection.Both;
        dialog.GrowVertical = Control.GrowDirection.Both;
        dialog.MouseFilter = Control.MouseFilterEnum.Stop;

        var dialogStyle = new StyleBoxFlat();
        dialogStyle.BgColor = new Color(0.10f, 0.08f, 0.12f, 0.98f);
        dialogStyle.SetBorderWidthAll(2);
        dialogStyle.BorderColor = StsColors.gold;
        dialogStyle.SetCornerRadiusAll(8);
        dialogStyle.SetContentMargin(Side.Left, 14);
        dialogStyle.SetContentMargin(Side.Right, 14);
        dialogStyle.SetContentMargin(Side.Top, 16);
        dialogStyle.SetContentMargin(Side.Bottom, 10);
        dialog.AddThemeStyleboxOverride("panel", dialogStyle);
        _layer.AddChild(dialog);

        var vbox = new VBoxContainer();
        vbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddThemeConstantOverride("separation", 10);
        dialog.AddChild(vbox);

        var titleLabel = new Label();
        titleLabel.Text = L("CONFIG_PRESET_RENAME_TITLE");
        titleLabel.AddThemeFontSizeOverride("font_size", 18);
        titleLabel.AddThemeColorOverride("font_color", StsColors.gold);
        titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddChild(titleLabel);

        var descLabel = new Label();
        descLabel.Text = L("CONFIG_PRESET_RENAME_DESC", index + 1);
        descLabel.AddThemeFontSizeOverride("font_size", 13);
        descLabel.AddThemeColorOverride("font_color", StsColors.cream);
        descLabel.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(descLabel);

        var lineEdit = new LineEdit();
        var existing = ConfigManager.GetPreset(index);
        lineEdit.Text = existing?.Name ?? L("CONFIG_PRESET_SLOT", index + 1);
        lineEdit.MaxLength = 20;
        lineEdit.CustomMinimumSize = new Vector2(0, 34);
        lineEdit.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(lineEdit);

        void CloseDialog()
        {
            if (GodotObject.IsInstanceValid(dialogMask)) dialogMask.QueueFree();
            if (GodotObject.IsInstanceValid(dialog)) dialog.QueueFree();
        }

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 16);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(btnRow);

        var cancelBtn = CreateActionButton(L("CONFIG_CANCEL"), StsColors.cream);
        cancelBtn.CustomMinimumSize = new Vector2(100, 36);
        cancelBtn.Pressed += CloseDialog;
        btnRow.AddChild(cancelBtn);

        var confirmBtn = CreateActionButton(L("CONFIG_CONFIRM"), StsColors.gold);
        confirmBtn.CustomMinimumSize = new Vector2(100, 36);
        confirmBtn.Pressed += () =>
        {
            string newName = lineEdit.Text;
            CloseDialog();
            ConfigManager.RenamePreset(index, newName);
            RefreshContent();
            ShowNotification(L("CONFIG_PRESET_RENAMED", index + 1));
        };
        btnRow.AddChild(confirmBtn);
    }

    private static void ShowNotification(string message)
    {
        GD.Print($"[ModConfig] {message}");
    }

    private static void ShowConfirmDialog(string title, string desc, Action onConfirm)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;

        var dialogMask = new ColorRect();
        dialogMask.Color = new Color(0f, 0f, 0f, 0.5f);
        dialogMask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        dialogMask.MouseFilter = Control.MouseFilterEnum.Stop;
        _layer.AddChild(dialogMask);

        var dialog = new PanelContainer();
        dialog.AnchorLeft = 0.5f;
        dialog.AnchorRight = 0.5f;
        dialog.AnchorTop = 0.5f;
        dialog.AnchorBottom = 0.5f;
        dialog.OffsetLeft = -160;
        dialog.OffsetRight = 160;
        // 高度由内容自适应（宽度仍固定 320），配合下方上大下小的内边距，
        // 让标题不贴顶、底部留白紧凑
        dialog.OffsetTop = 0;
        dialog.OffsetBottom = 0;
        dialog.GrowHorizontal = Control.GrowDirection.Both;
        dialog.GrowVertical = Control.GrowDirection.Both;
        dialog.MouseFilter = Control.MouseFilterEnum.Stop;

        var dialogStyle = new StyleBoxFlat();
        dialogStyle.BgColor = new Color(0.10f, 0.08f, 0.12f, 0.98f);
        dialogStyle.SetBorderWidthAll(2);
        dialogStyle.BorderColor = StsColors.gold;
        dialogStyle.SetCornerRadiusAll(8);
        dialogStyle.SetContentMargin(Side.Left, 14);
        dialogStyle.SetContentMargin(Side.Right, 14);
        dialogStyle.SetContentMargin(Side.Top, 16);
        dialogStyle.SetContentMargin(Side.Bottom, 10);
        dialog.AddThemeStyleboxOverride("panel", dialogStyle);
        _layer.AddChild(dialog);

        var vbox = new VBoxContainer();
        vbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // 让弹窗内容按实际高度垂直居中，避免内容贴顶、底部留白过大
        vbox.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        vbox.AddThemeConstantOverride("separation", 10);
        dialog.AddChild(vbox);

        var titleLabel = new Label();
        titleLabel.Text = title;
        titleLabel.AddThemeFontSizeOverride("font_size", 18);
        titleLabel.AddThemeColorOverride("font_color", StsColors.gold);
        titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddChild(titleLabel);

        var descLabel = new Label();
        descLabel.Text = desc;
        descLabel.AddThemeFontSizeOverride("font_size", 13);
        descLabel.AddThemeColorOverride("font_color", StsColors.cream);
        descLabel.HorizontalAlignment = HorizontalAlignment.Center;
        descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        descLabel.CustomMinimumSize = new Vector2(280, 0);
        descLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddChild(descLabel);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 16);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(btnRow);

        void CloseDialog()
        {
            if (GodotObject.IsInstanceValid(dialogMask))
                dialogMask.QueueFree();
            if (GodotObject.IsInstanceValid(dialog))
                dialog.QueueFree();
        }

        var cancelBtn = CreateActionButton(L("CONFIG_CANCEL"), StsColors.cream);
        cancelBtn.CustomMinimumSize = new Vector2(100, 36);
        cancelBtn.Pressed += CloseDialog;
        btnRow.AddChild(cancelBtn);

        var confirmBtn = CreateActionButton(L("CONFIG_CONFIRM"), StsColors.gold);
        confirmBtn.CustomMinimumSize = new Vector2(100, 36);
        confirmBtn.Pressed += () =>
        {
            CloseDialog();
            onConfirm?.Invoke();
        };
        btnRow.AddChild(confirmBtn);
    }

    // ============ UI辅助方法 ============

    private static Label CreateSectionHeader(string text)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeFontSizeOverride("font_size", 16);
        label.AddThemeColorOverride("font_color", StsColors.gold);
        return label;
    }

    private static Button CreateActionButton(string text, Color? fontColor = null)
    {
        var btn = new Button();
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_color", fontColor ?? StsColors.cream);
        btn.AddThemeColorOverride("font_hover_color", StsColors.gold);
        btn.AddThemeColorOverride("font_pressed_color", StsColors.gray);
        ApplyFlatStyle(btn);
        return btn;
    }

    private static Button CreateToggleButton(bool enabled, string text)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(80, 32);
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_hover_color", StsColors.gold);
        btn.AddThemeColorOverride("font_pressed_color", StsColors.gray);
        UpdateToggleStyle(btn, enabled);
        return btn;
    }

    private static void UpdateToggleStyle(Button btn, bool enabled)
    {
        btn.AddThemeColorOverride("font_color", enabled ? StsColors.green : StsColors.cream);
        if (enabled)
        {
            btn.AddThemeStyleboxOverride("normal", CreateStyleBox(
                new Color(0.15f, 0.25f, 0.15f, 0.9f),
                new Color(0.3f, 0.6f, 0.3f, 0.7f)));
        }
        else
        {
            btn.AddThemeStyleboxOverride("normal", CreateStyleBox(
                new Color(0.12f, 0.10f, 0.15f, 0.85f),
                new Color(0.35f, 0.30f, 0.25f, 0.5f)));
        }
    }

    private static void ApplyFlatStyle(Button btn)
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

    private static StyleBoxFlat CreateStyleBox(Color bg, Color border)
    {
        var sb = new StyleBoxFlat();
        sb.BgColor = bg;
        sb.BorderColor = border;
        sb.SetBorderWidthAll(2);
        sb.SetCornerRadiusAll(6);
        sb.SetContentMarginAll(6);
        return sb;
    }

    private static void AddDivider(VBoxContainer container)
    {
        var divider = new ColorRect();
        divider.CustomMinimumSize = new Vector2(0, 1);
        divider.Color = new Color(0.91f, 0.86f, 0.75f, 0.1f);
        divider.MouseFilter = Control.MouseFilterEnum.Ignore;
        container.AddChild(divider);
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
