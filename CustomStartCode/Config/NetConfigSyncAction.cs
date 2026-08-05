// 小格子铺 | Latticeshop
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace CustomStartCode.Config;

/// <summary>
/// 配置同步网络动作：携带某个玩家的角色配置，同步到所有端后按 NetId 独立保存。
/// </summary>
public struct NetConfigSyncAction : INetAction, IPacketSerializable
{
    public string characterId;
    public bool enableCustomDeck;
    public List<string> customDeckCardTypes;
    public bool enableCustomRelics;
    public List<string> startingRelicTypes;

    public GameAction ToGameAction(Player player)
    {
        return new ConfigSyncGameAction(
            player,
            characterId,
            enableCustomDeck,
            customDeckCardTypes ?? new List<string>(),
            enableCustomRelics,
            startingRelicTypes ?? new List<string>());
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(characterId);
        writer.WriteBool(enableCustomDeck);
        writer.WriteInt(customDeckCardTypes?.Count ?? 0);
        if (customDeckCardTypes != null)
        {
            foreach (var cardType in customDeckCardTypes)
            {
                writer.WriteString(cardType);
            }
        }
        writer.WriteBool(enableCustomRelics);
        writer.WriteInt(startingRelicTypes?.Count ?? 0);
        if (startingRelicTypes != null)
        {
            foreach (var relicType in startingRelicTypes)
            {
                writer.WriteString(relicType);
            }
        }
    }

    public void Deserialize(PacketReader reader)
    {
        characterId = reader.ReadString();
        enableCustomDeck = reader.ReadBool();
        int count = reader.ReadInt();
        customDeckCardTypes = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            customDeckCardTypes.Add(reader.ReadString());
        }
        enableCustomRelics = reader.ReadBool();
        int relicCount = reader.ReadInt();
        startingRelicTypes = new List<string>(relicCount);
        for (int i = 0; i < relicCount; i++)
        {
            startingRelicTypes.Add(reader.ReadString());
        }
    }

    public override string ToString()
    {
        return $"NetConfigSyncAction character={characterId}";
    }
}
