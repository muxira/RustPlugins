// Requires: Arena

using Facepunch;
using Network;
using Newtonsoft.Json;
using Oxide.Ext.Chaos;
using Oxide.Plugins.ArenaEx;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Arena Slasher", "k1lly0u", "2.0.12"), Description("Slasher event mode for Arena")]
    class ArenaSlasher : RustPlugin, IEventPlugin
    {
        private string[] _torchItems = new string[] { "torch", "flashlight.held" };
        private string[] _validWeapons;

        private long _midnightTime;
        private long _middayTime;

        private const string WEAPON_FLASHLIGHT_ITEM = "weapon.mod.flashlight";

        private static EnvSync _envSync;

        private static readonly Hash<BasePlayer, SlasherEvent> SlasherPlayers = new Hash<BasePlayer, SlasherEvent>();

        #region Oxide Hooks
        private void Loaded()
        {
            _middayTime = new DateTime().AddHours(12).ToBinary();
            _midnightTime = new DateTime().ToBinary();

            Unsubscribe(nameof(CanNetworkTo));
        }

        private void OnServerInitialized()
        {
            Arena.RegisterEvent(EventName, this);

            GetMessage = Message;

            _envSync = UnityEngine.Object.FindObjectOfType<EnvSync>();

            FindValidWeapons();
        }

        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private object CanNetworkTo(EnvSync env, BasePlayer player)
        {
            if (!SlasherPlayers.TryGetValue(player, out SlasherEvent slasherEvent) || !slasherEvent)
                return null;
            
            NetWrite netWrite = Net.sv.StartWrite();
            netWrite.PacketID(Network.Message.Type.Entities);
            Connection connection = player.net.connection;
            connection.validate.entityUpdates = connection.validate.entityUpdates + 1;
            BaseNetworkable.SaveInfo saveInfo = new BaseNetworkable.SaveInfo
            {
                forConnection = player.net.connection,
                forDisk = false
            };

            netWrite.UInt32(player.net.connection.validate.entityUpdates);

            using (saveInfo.msg = Pool.Get<ProtoBuf.Entity>())
            {
                env.Save(saveInfo);
                if (slasherEvent.IsPlayingRound)
                    saveInfo.msg.environment.dateTime = _midnightTime;
                else saveInfo.msg.environment.dateTime = _middayTime;
                //saveInfo.msg.ToProto(netWrite);
                saveInfo.msg.WriteToStream(netWrite);
                netWrite.Send(new SendInfo(player.net.connection));
            }

            return false;
        }

        private void Unload()
        {
            SlasherPlayers.Clear();

            if (!Arena.IsUnloading)
                Arena.UnregisterEvent(EventName);

            Configuration = null;
        }
        #endregion

        #region Functions
        private void FindValidWeapons()
        {
            List<string> list = Facepunch.Pool.Get<List<string>>();

            foreach (ItemDefinition itemDefinition in ItemManager.itemList)
            {
                if (itemDefinition.category is ItemCategory.Weapon or ItemCategory.Tool)
                {
                    if (!itemDefinition.isHoldable)
                        continue;

                    AttackEntity attackEntity = itemDefinition.GetComponent<ItemModEntity>()?.entityPrefab?.Get()?.GetComponent<AttackEntity>();
                    if (attackEntity && attackEntity is BaseMelee or BaseProjectile)
                        list.Add(itemDefinition.shortname);
                }
            }

            list.Sort();

            _validWeapons = list.ToArray();

            Facepunch.Pool.FreeUnmanaged(ref list);
        }

        private string[] GetSlasherWeapons() => _validWeapons;

        private string[] GetSlasherTorches() => _torchItems;
        #endregion

        #region Event Checks
        public string EventName => "Slasher";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<SlasherEvent>(this, config);

        public bool CanUseClassSelector => false;

        public bool RequireTimeLimit => false;

        public bool RequireScoreLimit => false;

        public bool UseScoreLimit => false;

        public bool UseTimeLimit => false;

        public bool IsTeamEvent => false;

        public bool CanSelectTeam => false;

        public bool CanUseRustTeams => false;

        public bool IsRoundBased => true;

        public bool ProcessWinnersBetweenRounds => false;
        
        public bool CanUseBots => false;

        public string TeamAName => "Team A";

        public string TeamBName => "Team B";

        public void FormatScoreEntry(Arena.ScoreEntry scoreEntry, ulong langUserId, out string score1, out string score2)
        {
            score1 = string.Format(Message("Score.Kills", langUserId), scoreEntry.value1);
            score2 = string.Format(Message("Score.Deaths", langUserId), scoreEntry.value2);
        }

        public List<Arena.EventParameter> AdditionalParameters { get; } = new List<Arena.EventParameter>
        {
            new Arena.EventParameter
            {
                DataType = "string",
                Field = "torchItem",
                Input = Arena.EventParameter.InputType.Selector,
                SelectMultiple = false,
                SelectorHook = "GetSlasherTorches",
                IsRequired = true,
                DefaultValue = "flashlight.held",
                Name = "Torch Item"
            },
            new Arena.EventParameter
            {
                DataType = "string",
                Field = "slasherWeapon",
                Input = Arena.EventParameter.InputType.Selector,
                SelectMultiple = false,
                SelectorHook = "GetSlasherWeapons",
                IsRequired = false,
                Name = "Slasher Weapon",
                DefaultValue = "shotgun.pump"
            },
            new Arena.EventParameter
            {
                DataType = "string",
                Field = "slasherClothing",
                Input = Arena.EventParameter.InputType.Selector,
                SelectMultiple = false,
                SelectorHook = "GetAllKits",
                IsRequired = false,
                Name = "Slasher Clothing"
            },
            new Arena.EventParameter
            {
                DataType = "int",
                Field = "slasherTime",
                Input = Arena.EventParameter.InputType.InputField,
                IsRequired = true,
                DefaultValue = 180,
                Name = "Slasher Timer (seconds)"
            },
            new Arena.EventParameter
            {
                DataType = "int",
                Field = "playerTime",
                Input = Arena.EventParameter.InputType.InputField,
                IsRequired = true,
                DefaultValue = 120,
                Name = "Player Timer (seconds)"
            },
        };

        public string ParameterIsValid(string fieldName, object value)
        {
            switch (fieldName)
            {
                case "slasherWeapon":
                    {
                        if (ItemManager.FindItemDefinition((string)value) == null)
                            return "Unable to find a weapon with the specified shortname";

                        return null;
                    }
                case "slasherClothing":
                    {
                        object success = Arena.Instance.ValidateKit((string)value);
                        if (success != null)
                            return (string)success;

                        return null;
                    }
                default:
                    return null;
            }
        }
        #endregion

        #region Event Classes
        public class SlasherEvent : Arena.BaseEventGame
        {
            private ItemDefinition torchItem;
            private ItemDefinition slasherWeapon;
            private string slasherKit;

            private int slasherTime;
            private int playerTime;

            private Arena.BaseEventPlayer slasherPlayer;

            private List<Arena.BaseEventPlayer> remainingSlashers;

            public bool IsPlayingRound { get; private set; }

            public override void InitializeEvent(IEventPlugin plugin, Arena.EventConfig config)
            {
                torchItem = ItemManager.FindItemDefinition(config.GetParameter<string>("torchItem"));
                slasherWeapon = ItemManager.FindItemDefinition(config.GetParameter<string>("slasherWeapon"));

                slasherKit = config.GetParameter<string>("slasherClothing");

                slasherTime = config.GetParameter<int>("slasherTime");
                playerTime = config.GetParameter<int>("playerTime");

                remainingSlashers = Facepunch.Pool.Get<List<Arena.BaseEventPlayer>>();

                base.InitializeEvent(plugin, config);
            }

            protected override void OnDestroy()
            {
                Facepunch.Pool.FreeUnmanaged(ref remainingSlashers);

                SlasherPlayers.Clear();

                base.OnDestroy();
            }

            protected override void StartEvent()
            {
                Config.RoundsToPlay = eventPlayers.Count;

                remainingSlashers.Clear();

                remainingSlashers.AddRange(eventPlayers);

                eventPlayers.ForEach((Arena.BaseEventPlayer slasherPlayer) => SlasherPlayers.Add(slasherPlayer.Player, this));

                base.StartEvent();

                CloseEvent();
            }

            protected override bool CanEnterBetweenRounds() => false;

            protected override Arena.BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.AddComponent<SlasherPlayer>();

            protected override void CreateEventPlayer(BasePlayer player, Arena.Team team = Arena.Team.None)
            {
                base.CreateEventPlayer(player, team);

                Arena.LockClothingSlots(player);
            }

            public override void LeaveEvent(BasePlayer player)
            {
                bool isSlasher = slasherPlayer && player.GetComponent<SlasherPlayer>() == slasherPlayer;

                SlasherPlayers.Remove(player);
                base.LeaveEvent(player);

                if (isSlasher && Status != Arena.EventStatus.Finished)
                    EndRound();
            }

            #region Event Items
            protected override void OnKitGiven(Arena.BaseEventPlayer eventPlayer)
            {
                List<Item> list = Facepunch.Pool.Get<List<Item>>();
                eventPlayer.Player.inventory.GetAllItems(list);

                bool isSlasher = eventPlayer == slasherPlayer;

                for (int i = 0; i < list.Count; i++)
                {
                    Item item = list[i];

                    if (!isSlasher && item.info.category == ItemCategory.Attire)
                        continue;

                    item.RemoveFromContainer();
                    item.Remove();
                }

                if (isSlasher)
                {
                    Arena.GiveKit(eventPlayer.Player, slasherKit);
                    GiveSlasherWeapon(eventPlayer);
                }
                else GiveTorch(eventPlayer);

                eventPlayer.Player.inventory.SendUpdatedInventory(PlayerInventory.Type.Belt, eventPlayer.Player.inventory.containerBelt);
            }

            private void GiveSlasherWeapon(Arena.BaseEventPlayer eventPlayer)
            {
                Item item = ItemManager.Create(slasherWeapon);

                if (item.contents != null && item.contents.availableSlots.Count > 0)
                {
                    Item attachment = ItemManager.CreateByName(WEAPON_FLASHLIGHT_ITEM);
                    if (!attachment.MoveToContainer(item.contents))
                        attachment.Remove();
                    else item.GetHeldEntity()?.SendMessage("SetLightsOn", true, SendMessageOptions.DontRequireReceiver);
                }

                item.MoveToContainer(eventPlayer.Player.inventory.containerBelt);

                BaseProjectile baseProjectile = item.GetHeldEntity() as BaseProjectile;
                if (baseProjectile)
                {
                    Item ammo = ItemManager.Create(baseProjectile.primaryMagazine.ammoType, baseProjectile.primaryMagazine.capacity * 5);
                    ammo.MoveToContainer(eventPlayer.Player.inventory.containerMain);
                }
            }

            private void GiveTorch(Arena.BaseEventPlayer eventPlayer)
            {
                Item item = ItemManager.Create(torchItem);
                item.MoveToContainer(eventPlayer.Player.inventory.containerBelt);
                item.GetHeldEntity()?.SendMessage("SetLightsOn", true, SendMessageOptions.DontRequireReceiver);
            }

            protected override bool CanDropAmmo() => false;

            protected override bool CanDropBackpack() => false;

            protected override bool CanDropCorpse() => false;

            protected override bool CanDropWeapon() => false;
            #endregion

            public override void OnEventPlayerDeath(Arena.BaseEventPlayer victim, Arena.BaseEventPlayer attacker = null, HitInfo info = null)
            {
                if (!victim)
                    return;

                if (attacker)
                    attacker.OnKilledPlayer(info);

                if (victim == slasherPlayer || GetAlivePlayerCount() <= 1)
                {
                    victim.AddPlayerDeath();

                    if (victim == slasherPlayer)
                        BroadcastToPlayers(GetMessage, "Notification.HuntedWin");
                    else BroadcastToPlayers(GetMessage, "Notification.SlasherWin");

                    InvokeHandler.Invoke(this, EndRound, 0.1f);
                    return;
                }

                victim.OnPlayerDeath(attacker, 0f);

                UpdateScoreboard();

                base.OnEventPlayerDeath(victim, attacker, info);
            }

            protected override void RebuildSpectateTargets()
            {
                spectateTargets.Clear();

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    Arena.BaseEventPlayer eventPlayer = eventPlayers[i];

                    if (eventPlayer == null || eventPlayer == slasherPlayer || eventPlayer.IsDead || eventPlayer.Player.IsSpectating())
                        continue;

                    spectateTargets.Add(eventPlayer);
                }
            }

            #region Rounds
            protected override void StartNextRound()
            {
                base.StartNextRound();

                IsPlayingRound = true;

                _envSync.SendNetworkUpdateImmediate();

                _roundNumber += 1;

                slasherPlayer = GetRandomSlasher();

                StartCoroutine(ResetPlayers());

                Timer.StartTimer(slasherTime, GetMessage("Timer.Slasher", 0UL), OnSlasherTimerExpired);

                BroadcastToPlayers(GetMessage, "Notification.RoundStarted", slasherPlayer.Player.displayName);

                UpdateScoreboard();
            }

            protected override void EndRound()
            {
                slasherPlayer = null;

                Timer.StopTimer();

                IsPlayingRound = false;

                GodmodeEnabled = true;

                _envSync.SendNetworkUpdateImmediate();

                StartCoroutine(ResetPlayers());

                base.EndRound();                
            }

            private void OnSlasherTimerExpired()
            {
                Timer.StartTimer(playerTime, GetMessage("Timer.Hunted", 0UL), EndRound);

                StartCoroutine(GiveSlasherWeapons());

                BroadcastToPlayers(GetMessage, "Notification.HuntersTurn");
            }

            private Arena.BaseEventPlayer GetRandomSlasher()
            {
                Arena.BaseEventPlayer nextSlasher = remainingSlashers.GetRandom();

                remainingSlashers.Remove(nextSlasher);

                if (remainingSlashers.Count == 0)
                    remainingSlashers.AddRange(eventPlayers);

                if (nextSlasher == null)
                    return GetRandomSlasher();
                return nextSlasher;
            }

            private IEnumerator ResetPlayers()
            {
                List<Arena.BaseEventPlayer> currentPlayers = Facepunch.Pool.Get<List<Arena.BaseEventPlayer>>();
                currentPlayers.AddRange(eventPlayers);

                for (int i = 0; i < currentPlayers.Count; i++)
                {
                    Arena.BaseEventPlayer eventPlayer = currentPlayers[i];
                    if (eventPlayer)
                    {
                        if (eventPlayer.IsDead)
                        {
                            Arena.ResetPlayer(eventPlayer.Player);
                            OnPlayerRespawn(eventPlayer);
                        }
                        else
                        {
                            eventPlayer.Player.StripInventory();
                            eventPlayer.Player.ResetMetabolism();

                            yield return CoroutineEx.waitForEndOfFrame;
                            yield return CoroutineEx.waitForEndOfFrame;

                            Arena.GiveKit(eventPlayer.Player, eventPlayer.Kit);

                            yield return CoroutineEx.waitForEndOfFrame;

                            OnKitGiven(eventPlayer);
                        }
                    }

                    yield return CoroutineEx.waitForEndOfFrame;
                    yield return CoroutineEx.waitForEndOfFrame;
                }

                Facepunch.Pool.FreeUnmanaged(ref currentPlayers);
            }

            private IEnumerator GiveSlasherWeapons()
            {
                for (int i = eventPlayers.Count - 1; i >= 0; i--)
                {
                    Arena.BaseEventPlayer eventPlayer = eventPlayers[i];

                    if (eventPlayer.IsDead || eventPlayer == slasherPlayer)
                        continue;

                    GiveSlasherWeapon(eventPlayer);

                    yield return CoroutineEx.waitForEndOfFrame;
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }
            #endregion

            protected override void GetWinningPlayers(ref List<Arena.BaseEventPlayer> winners)
            {
                Arena.BaseEventPlayer winner = null;

                if (eventPlayers.Count > 0)
                {
                    int kills = 0;
                    int deaths = 0;

                    for (int i = 0; i < eventPlayers.Count; i++)
                    {
                        Arena.BaseEventPlayer eventPlayer = eventPlayers[i];
                        if (!eventPlayer)
                            continue;

                        if (eventPlayer.Kills > kills)
                        {
                            winner = eventPlayer;
                            kills = eventPlayer.Kills;
                            deaths = eventPlayer.Deaths;
                        }
                        else if (eventPlayer.Kills == kills)
                        {
                            if (eventPlayer.Deaths < deaths)
                            {
                                winner = eventPlayer;
                                kills = eventPlayer.Kills;
                                deaths = eventPlayer.Deaths;
                            }
                        }
                    }
                }

                if (winner != null)
                    winners.Add(winner);
            }

            #region Scoreboards
            protected override void BuildScoreboard()
            {
                scoreContainer = Arena.CreateScoreboardBase(this);

                int index = -1;
                Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.RoundNumber", 0UL), RoundNumber, Config.RoundsToPlay), index += 1);

                Arena.CreateScoreEntry(scoreContainer, string.Empty, "K", "D", index += 1);

                for (int i = 0; i < Mathf.Min(scoreData.Count, 15); i++)
                {
                    Arena.ScoreEntry score = scoreData[i];
                    Arena.CreateScoreEntry(scoreContainer, score.displayName, ((int)score.value1).ToString(), ((int)score.value2).ToString(), i + index + 1);
                }
            }

            protected override float GetFirstScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Kills;

            protected override float GetSecondScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Deaths;

            protected override void SortScores(ref List<Arena.ScoreEntry> list)
            {
                list.Sort(delegate (Arena.ScoreEntry a, Arena.ScoreEntry b)
                {
                    int primaryScore = a.value1.CompareTo(b.value1) * -1;

                    if (primaryScore == 0)
                        return a.value2.CompareTo(b.value2);

                    return primaryScore;
                });
            }
            #endregion
        }

        private class SlasherPlayer : Arena.BaseEventPlayer
        {
            public override void OnPlayerDeath(Arena.BaseEventPlayer attacker = null, float respawnTime = 5, HitInfo hitInfo = null)
            {
                AddPlayerDeath(attacker);

                DestroyUI();

                BeginSpectating();
            }
        }
        #endregion

        #region Config        
        private static ConfigData Configuration;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Amount of time between rounds (seconds)")]
            public int TimeBetweenRounds { get; set; }

            public string EventIcon { get; set; }

            public Oxide.Core.VersionNumber Version { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Configuration = Config.ReadObject<ConfigData>();

            if (Configuration.Version < Version)
                UpdateConfigValues();

            Config.WriteObject(Configuration, true);
        }

        protected override void LoadDefaultConfig() => Configuration = GetBaseConfig();

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                EventIcon = "https://www.rustedit.io/images/arena/arena_slasher.png",
                TimeBetweenRounds = 10,
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            Configuration.Version = Version;
            PrintWarning("Config update completed!");
        }

        #endregion

        #region Localization
        public string Message(string key, ulong playerId = 0U) => lang.GetMessage(key, this, playerId != 0U ? playerId.ToString() : null);

        private static Func<string, ulong, string> GetMessage;

        private readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["Score.Kills"] = "Kills: {0}",
            ["Score.Deaths"] = "Deaths: {0}",
            ["Score.Name"] = "Kills",
            ["Score.Limit"] = "Score Limit : {0}",
            ["Score.RoundNumber"] = "Round {0} / {1}",
            ["Timer.Slasher"] = "Slasher Time",
            ["Timer.Hunted"] = "Hunted Time",
            ["Notification.RoundStartsIn"] = "The next round starts in <color=#007acc>{0}</color> seconds",
            ["Notification.RoundStarted"] = "<color=#007acc>{0}</color> is the slasher. Hide from them!",
            ["Notification.HuntersTurn"] = "The hunted have become the hunters!",
            ["Notification.HuntedWin"] = "The hunted have won this round!",
            ["Notification.SlasherWin"] = "The slasher has won this round!"
        };
        #endregion
    }
}
