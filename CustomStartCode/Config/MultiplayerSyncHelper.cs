// 小格子铺 | Latticeshop
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CustomStartCode.Config;

/// <summary>
/// 多人同步辅助：判断当前是否多人游戏、本地玩家、主机等。
/// </summary>
public static class MultiplayerSyncHelper
{
    public static bool IsMultiplayerGame()
    {
        try
        {
            if (RunManager.Instance == null) return false;
            if (RunManager.Instance.NetService == null) return false;
            var type = RunManager.Instance.NetService.Type;
            return type == NetGameType.Host || type == NetGameType.Client;
        }
        catch { return false; }
    }

    public static INetGameService? GetNetService()
    {
        try
        {
            if (RunManager.Instance != null && RunManager.Instance.NetService != null)
                return RunManager.Instance.NetService;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 当前端是否为房主。
    /// </summary>
    public static bool IsHost()
    {
        try
        {
            return GetNetService()?.Type == NetGameType.Host;
        }
        catch { return false; }
    }

    /// <summary>
    /// 指定 Player 是否为本地玩家（NetId 与当前端一致）。
    /// </summary>
    public static bool IsLocalPlayer(Player player)
    {
        try
        {
            if (player == null) return false;
            var netService = GetNetService();
            if (netService == null) return false;
            ulong serviceNetId = netService.NetId;
            ulong playerNetId = player.NetId;
            return playerNetId != 0UL && playerNetId == serviceNetId;
        }
        catch { return false; }
    }
}
