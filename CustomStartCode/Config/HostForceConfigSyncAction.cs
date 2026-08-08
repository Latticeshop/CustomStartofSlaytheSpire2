// 小格子铺 | Latticeshop
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace CustomStartCode.Config;

/// <summary>
/// 房主配置强制同步动作：房主在开局时广播自己保存的整套角色配置，
/// 所有端（含房主自身）将其保存为“本局临时强制配置”，不覆盖各端自己的配置存储。
/// </summary>
public class HostForceConfigSyncGameAction : GameAction
{
    public override ulong OwnerId => Sender.NetId;

    public override GameActionType ActionType => GameActionType.Any;

    public Player Sender { get; }

    public Dictionary<string, CharacterConfig> Configs { get; }

    public HostForceConfigSyncGameAction(Player sender, Dictionary<string, CharacterConfig> configs)
    {
        Sender = sender;
        Configs = new Dictionary<string, CharacterConfig>(configs, StringComparer.OrdinalIgnoreCase);
    }

    public HostForceConfigSyncGameAction(Player sender, List<NetCharacterConfig> entries)
    {
        Sender = sender;
        Configs = new Dictionary<string, CharacterConfig>(StringComparer.OrdinalIgnoreCase);
        if (entries != null)
        {
            foreach (var entry in entries)
            {
                var config = entry.ToConfig();
                if (!string.IsNullOrEmpty(config.CharacterId))
                {
                    Configs[config.CharacterId] = config;
                }
            }
        }
    }

    protected override async Task ExecuteAction()
    {
        ConfigManager.SetForcedHostConfigs(Configs);
        await Task.CompletedTask;
    }

    public override INetAction ToNetAction()
    {
        var entries = new List<NetCharacterConfig>();
        foreach (var config in Configs.Values)
        {
            entries.Add(new NetCharacterConfig(config));
        }
        return new NetHostForceConfigSyncAction { entries = entries };
    }

    public override string ToString()
    {
        return $"HostForceConfigSyncGameAction sender={Sender.NetId} configs={Configs.Count}";
    }
}

/// <summary>
/// 单个角色配置的网络传输结构（用于房主整套配置的序列化）。
/// </summary>
public struct NetCharacterConfig : MegaCrit.Sts2.Core.Multiplayer.Serialization.IPacketSerializable
{
    public string characterId;
    public bool enableCustomDeck;
    public List<string> customDeckCardTypes;
    public bool enableCustomRelics;
    public List<string> startingRelicTypes;
    public int startingGold;
    public int maxHp;

    public NetCharacterConfig(CharacterConfig config)
    {
        characterId = config.CharacterId;
        enableCustomDeck = config.EnableCustomDeck;
        customDeckCardTypes = new List<string>(config.CustomDeckCardTypes);
        enableCustomRelics = config.EnableCustomRelics;
        startingRelicTypes = new List<string>(config.StartingRelicTypes);
        startingGold = config.StartingGold;
        maxHp = config.MaxHp;
    }

    public CharacterConfig ToConfig()
    {
        return new CharacterConfig
        {
            CharacterId = characterId,
            EnableCustomDeck = enableCustomDeck,
            CustomDeckCardTypes = new List<string>(customDeckCardTypes ?? new List<string>()),
            EnableCustomRelics = enableCustomRelics,
            StartingRelicTypes = new List<string>(startingRelicTypes ?? new List<string>()),
            StartingGold = startingGold,
            MaxHp = maxHp,
        };
    }

    public void Serialize(MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter writer)
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
        writer.WriteInt(startingGold);
        writer.WriteInt(maxHp);
    }

    public void Deserialize(MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader reader)
    {
        characterId = reader.ReadString();
        enableCustomDeck = reader.ReadBool();
        int deckCount = reader.ReadInt();
        customDeckCardTypes = new List<string>(deckCount);
        for (int i = 0; i < deckCount; i++)
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
        startingGold = reader.ReadInt();
        maxHp = reader.ReadInt();
    }
}

/// <summary>
/// 房主配置强制同步网络动作。
/// </summary>
public struct NetHostForceConfigSyncAction : INetAction, MegaCrit.Sts2.Core.Multiplayer.Serialization.IPacketSerializable
{
    public List<NetCharacterConfig> entries;

    public GameAction ToGameAction(Player player)
    {
        return new HostForceConfigSyncGameAction(player, entries);
    }

    public void Serialize(MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter writer)
    {
        writer.WriteInt(entries?.Count ?? 0);
        if (entries != null)
        {
            foreach (var entry in entries)
            {
                entry.Serialize(writer);
            }
        }
    }

    public void Deserialize(MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader reader)
    {
        int count = reader.ReadInt();
        entries = new List<NetCharacterConfig>(count);
        for (int i = 0; i < count; i++)
        {
            var entry = new NetCharacterConfig();
            entry.Deserialize(reader);
            entries.Add(entry);
        }
    }

    public override string ToString()
    {
        return $"NetHostForceConfigSyncAction entries={entries?.Count ?? 0}";
    }
}
