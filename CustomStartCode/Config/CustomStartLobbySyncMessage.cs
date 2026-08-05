// 小格子铺 | Latticeshop
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace CustomStartCode.Config;

/// <summary>
/// 联机大厅同步消息：房主把“强制全部应用房主配置”开关状态广播给所有客机。
/// </summary>
public struct CustomStartLobbySyncMessage : INetMessage, IPacketSerializable
{
    public bool forceHostConfigEnabled;

    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(forceHostConfigEnabled);
    }

    public void Deserialize(PacketReader reader)
    {
        forceHostConfigEnabled = reader.ReadBool();
    }

    public override string ToString()
    {
        return $"CustomStartLobbySyncMessage forceHostConfig={forceHostConfigEnabled}";
    }
}
