using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace CustomStartCode.Common;

/// <summary>
/// 共享 UI 层级辅助：悬停提示等需要显示在 mod 配置面板 / 遗物库之上的元素。
/// </summary>
internal static class UiLayers
{
    public const int HoverTipLayerIndex = 105;
    private static CanvasLayer? _hoverTipLayer;

    public static CanvasLayer GetHoverTipLayer()
    {
        if (_hoverTipLayer == null || !GodotObject.IsInstanceValid(_hoverTipLayer))
        {
            _hoverTipLayer = new CanvasLayer { Layer = HoverTipLayerIndex, Name = "CustomStartHoverTipLayer" };
            NGame.Instance?.AddChild(_hoverTipLayer);
        }
        return _hoverTipLayer;
    }
}
