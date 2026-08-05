// 小格子铺 | Latticeshop
using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace CustomStartCode.Config;

/// <summary>
/// 联机大厅“强制全部应用房主配置”面板。
/// 所有玩家都能看到开关状态；仅房主可以点击切换；客机只读。
/// 房主切换后通过 CustomStartLobbySyncMessage 广播给所有客机。
/// </summary>
internal static class LobbyHostConfigPanel
{
    private const string PanelName = "CustomStartLobbyHostConfigPanel";
    private const float PanelWidth = 400f;
    private const float PanelHeight = 84f;

    private static PanelContainer? _panel;
    private static Button? _toggleButton;
    private static Label? _statusLabel;
    private static Label? _hintLabel;
    private static StartRunLobby? _lobby;
    private static bool _dragging;
    private static Vector2 _dragGrabOffset;

    private static MessageHandlerDelegate<CustomStartLobbySyncMessage>? _handlerDelegate;
    private static INetGameService? _registeredService;

    /// <summary>
    /// 大厅初始化时提前绑定并注册消息处理（早于面板挂载，避免错过房主的状态广播）。
    /// </summary>
    public static void BindLobby(StartRunLobby lobby)
    {
        try
        {
            if (lobby == null || lobby.NetService.Type == NetGameType.Singleplayer) return;
            _lobby = lobby;
            RegisterHandler(lobby.NetService);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CustomStart] 绑定大厅消息处理失败: {ex}");
        }
    }

    public static void AttachOrRebind(NRemoteLobbyPlayerContainer container, StartRunLobby lobby)
    {
        try
        {
            if (container == null || lobby == null) return;
            if (lobby.NetService.Type == NetGameType.Singleplayer)
            {
                Cleanup();
                return;
            }

            _panel = container.GetNodeOrNull<PanelContainer>(PanelName);
            if (_panel == null || !GodotObject.IsInstanceValid(_panel))
            {
                _panel = BuildPanel();
                _panel.Name = PanelName;
                container.AddChild(_panel);
            }

            _lobby = lobby;

            // 注册大厅状态消息处理（幂等，避免重复注册）
            RegisterHandler(lobby.NetService);

            Refresh();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CustomStart] 大厅面板挂载失败: {ex}");
        }
    }

    /// <summary>
    /// 大厅有玩家加入时调用：房主重发当前开关状态，避免后加入的客机看到过期状态。
    /// </summary>
    public static void OnPlayerConnected(NRemoteLobbyPlayerContainer container)
    {
        try
        {
            if (_lobby == null || _panel == null || !GodotObject.IsInstanceValid(_panel)) return;
            if (_lobby.NetService.Type != NetGameType.Host) return;
            _lobby.NetService.SendMessage(new CustomStartLobbySyncMessage
            {
                forceHostConfigEnabled = ConfigManager.IsForceHostConfigEnabled,
            });
        }
        catch { }
    }

    /// <summary>
    /// 根据当前开关状态与房主/客机身份刷新面板显示。
    /// </summary>
    public static void Refresh()
    {
        try
        {
            if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;

            bool enabled = ConfigManager.IsForceHostConfigEnabled;
            bool isHost = _lobby != null && _lobby.NetService.Type == NetGameType.Host;

            _toggleButton!.Disabled = !isHost;
            _toggleButton.Text = ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_TOGGLE");
            _toggleButton.TooltipText = isHost
                ? ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_TOOLTIP_HOST")
                : ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_TOOLTIP_CLIENT");

            _statusLabel!.Text = enabled
                ? ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_STATUS_ON")
                : ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_STATUS_OFF");
            _statusLabel.AddThemeColorOverride("font_color", enabled ? StsColors.gold : StsColors.gray);

            _hintLabel!.Text = isHost
                ? ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_HINT_HOST")
                : ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_HINT_CLIENT");

            // 边框颜色：开启=金色高亮，关闭=灰色；客机=淡蓝边框提示只读
            if (_panel.GetThemeStylebox("panel") is StyleBoxFlat style)
            {
                style.BorderColor = enabled
                    ? StsColors.gold
                    : (isHost ? new Color(0.45f, 0.40f, 0.35f, 0.8f) : new Color(0.35f, 0.55f, 0.80f, 0.8f));
                style.BorderWidthLeft = style.BorderWidthTop = style.BorderWidthRight = style.BorderWidthBottom = 2;
            }
        }
        catch { }
    }

    /// <summary>
    /// 大厅关闭/清理：注销消息处理并释放面板。
    /// </summary>
    public static void Cleanup()
    {
        UnregisterHandler();
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
        {
            _panel.QueueFree();
        }
        _panel = null;
        _toggleButton = null;
        _statusLabel = null;
        _hintLabel = null;
        _lobby = null;
        _dragging = false;
    }

    private static void OnTogglePressed()
    {
        try
        {
            if (_lobby == null) return;
            if (_lobby.NetService.Type != NetGameType.Host) return; // 仅房主可操作

            bool next = !ConfigManager.IsForceHostConfigEnabled;
            ConfigManager.SetForceHostConfig(next);

            try
            {
                _lobby.NetService.SendMessage(new CustomStartLobbySyncMessage
                {
                    forceHostConfigEnabled = next,
                });
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[CustomStart] 广播房主强制配置状态失败: {ex}");
            }

            Refresh();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CustomStart] 切换房主强制配置失败: {ex}");
        }
    }

    private static void OnLobbySyncMessage(CustomStartLobbySyncMessage message, ulong senderId)
    {
        try
        {
            ConfigManager.SetForceHostConfig(message.forceHostConfigEnabled);
            Refresh();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CustomStart] 处理大厅状态消息失败: {ex}");
        }
    }

    private static void RegisterHandler(INetGameService netService)
    {
        if (_registeredService == netService && _handlerDelegate != null) return;
        UnregisterHandler();
        _handlerDelegate = OnLobbySyncMessage;
        _registeredService = netService;
        try
        {
            netService.RegisterMessageHandler(_handlerDelegate);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CustomStart] 注册大厅状态消息处理失败: {ex}");
        }
    }

    private static void UnregisterHandler()
    {
        if (_registeredService != null && _handlerDelegate != null)
        {
            try
            {
                _registeredService.UnregisterMessageHandler(_handlerDelegate);
            }
            catch { }
        }
        _registeredService = null;
        _handlerDelegate = null;
    }

    private static PanelContainer BuildPanel()
    {
        var panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 50,
            // 自由定位（锚定容器左上角）。默认位置 = 原位置（容器上方 -88）右移一个自身宽度、下移一个自身高度，
            // 避免面板过高顶出屏幕；支持鼠标拖拽后可按需再调整。
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 4f + PanelWidth,
            OffsetTop = -88f + PanelHeight,
            OffsetRight = 4f + PanelWidth + PanelWidth,
            OffsetBottom = -88f + PanelHeight + PanelHeight,
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.05f, 0.08f, 0.95f),
            BorderColor = new Color(0.45f, 0.40f, 0.35f, 0.8f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomRight = 6,
            CornerRadiusBottomLeft = 6,
        };
        style.SetContentMarginAll(6);
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 3);
        panel.AddChild(vbox);

        var title = new Label();
        title.Text = ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_TITLE");
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", StsColors.gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(row);

        _toggleButton = new Button();
        _toggleButton.Text = ConfigManager.L("CONFIG_LOBBY_FORCE_HOST_TOGGLE");
        _toggleButton.CustomMinimumSize = new Vector2(230, 30);
        _toggleButton.AddThemeFontSizeOverride("font_size", 13);
        _toggleButton.Pressed += OnTogglePressed;
        row.AddChild(_toggleButton);

        _statusLabel = new Label();
        _statusLabel.AddThemeFontSizeOverride("font_size", 14);
        _statusLabel.AddThemeColorOverride("font_color", StsColors.gray);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.CustomMinimumSize = new Vector2(80, 0);
        row.AddChild(_statusLabel);

        _hintLabel = new Label();
        _hintLabel.AddThemeFontSizeOverride("font_size", 10);
        _hintLabel.AddThemeColorOverride("font_color", StsColors.gray);
        _hintLabel.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(_hintLabel);

        AttachDrag(panel);
        return panel;
    }

    /// <summary>
    /// 鼠标拖拽：按住面板非按钮区域拖动，松手停止；拖拽位置限制在屏幕内，避免拖出视野。
    /// </summary>
    private static void AttachDrag(PanelContainer panel)
    {
        panel.GuiInput += (InputEvent ev) =>
        {
            try
            {
                if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
                {
                    if (mb.Pressed)
                    {
                        _dragging = true;
                        _dragGrabOffset = mb.Position;
                    }
                    else
                    {
                        _dragging = false;
                    }
                    panel.AcceptEvent();
                }
                else if (ev is InputEventMouseMotion && _dragging)
                {
                    var parent = panel.GetParent<Control>();
                    if (parent == null || !GodotObject.IsInstanceValid(parent)) return;

                    Vector2 newGlobal = panel.GetGlobalMousePosition() - _dragGrabOffset;
                    Rect2 viewport = panel.GetViewportRect();
                    newGlobal.X = Mathf.Clamp(newGlobal.X, 0f, Mathf.Max(0f, viewport.Size.X - panel.Size.X));
                    newGlobal.Y = Mathf.Clamp(newGlobal.Y, 0f, Mathf.Max(0f, viewport.Size.Y - panel.Size.Y));
                    panel.Position = newGlobal - parent.GlobalPosition;
                    panel.AcceptEvent();
                }
            }
            catch { }
        };
    }
}
