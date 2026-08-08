// 小格子铺 | Latticeshop
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace CustomStartCode.Config;

/// <summary>
/// 配置同步动作：把某玩家的角色配置按 NetId 记录到 ConfigManager，
/// 使多人模式下每个玩家能按自己的配置开局（同步后由各端统一应用）。
/// </summary>
public class ConfigSyncGameAction : GameAction
{
    public override ulong OwnerId => Sender.NetId;

    public override GameActionType ActionType => GameActionType.Any;

    public Player Sender { get; }

    public string CharacterId { get; }

    public bool EnableCustomDeck { get; }

    public List<string> CustomDeckCardTypes { get; }

    public bool EnableCustomRelics { get; }

    public List<string> StartingRelicTypes { get; }

    public int StartingGold { get; }

    public int MaxHp { get; }

    public ConfigSyncGameAction(Player sender, CharacterConfig config)
    {
        Sender = sender;
        CharacterId = config.CharacterId;
        EnableCustomDeck = config.EnableCustomDeck;
        CustomDeckCardTypes = new List<string>(config.CustomDeckCardTypes);
        EnableCustomRelics = config.EnableCustomRelics;
        StartingRelicTypes = new List<string>(config.StartingRelicTypes);
        StartingGold = config.StartingGold;
        MaxHp = config.MaxHp;
    }

    public ConfigSyncGameAction(
        Player sender,
        string characterId,
        bool enableCustomDeck,
        List<string> customDeckCardTypes,
        bool enableCustomRelics,
        List<string> startingRelicTypes,
        int startingGold = 0,
        int maxHp = 0)
    {
        Sender = sender;
        CharacterId = characterId;
        EnableCustomDeck = enableCustomDeck;
        CustomDeckCardTypes = customDeckCardTypes ?? new List<string>();
        EnableCustomRelics = enableCustomRelics;
        StartingRelicTypes = startingRelicTypes ?? new List<string>();
        StartingGold = startingGold;
        MaxHp = maxHp;
    }

    protected override async Task ExecuteAction()
    {
        var config = new CharacterConfig
        {
            CharacterId = CharacterId,
            EnableCustomDeck = EnableCustomDeck,
            CustomDeckCardTypes = new List<string>(CustomDeckCardTypes),
            EnableCustomRelics = EnableCustomRelics,
            StartingRelicTypes = new List<string>(StartingRelicTypes),
            StartingGold = StartingGold,
            MaxHp = MaxHp,
        };
        ConfigManager.SetRemoteCharacterConfig(Sender.NetId, config);
        await Task.CompletedTask;
    }

    public override INetAction ToNetAction()
    {
        return new NetConfigSyncAction
        {
            characterId = CharacterId,
            enableCustomDeck = EnableCustomDeck,
            customDeckCardTypes = CustomDeckCardTypes,
            enableCustomRelics = EnableCustomRelics,
            startingRelicTypes = StartingRelicTypes,
            startingGold = StartingGold,
            maxHp = MaxHp,
        };
    }

    public override string ToString()
    {
        return $"ConfigSyncGameAction sender={Sender.NetId} character={CharacterId}";
    }
}
