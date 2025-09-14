// Requires: Arena
using Newtonsoft.Json;
using Oxide.Plugins.ArenaEx;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Arena One In The Chamber", "k1lly0u", "2.0.7"), Description("One In The Chamber event mode for Arena")]
    class ArenaOITC : RustPlugin, IEventPlugin
    {
        private string[] _validPrimaryWeapons;
        private string[] _validSecondaryWeapons;

        #region Oxide Hooks
        private void OnServerInitialized()
        {
            FindValidWeapons();

            Arena.RegisterEvent(EventName, this);

            GetMessage = Message;
        }

        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private void Unload()
        {
            if (!Arena.IsUnloading)
                Arena.UnregisterEvent(EventName);

            Configuration = null;
        }
        #endregion

        #region Functions
        private void FindValidWeapons()
        {
            List<string> primary = Facepunch.Pool.Get<List<string>>();
            List<string> secondary = Facepunch.Pool.Get<List<string>>();

            foreach (ItemDefinition itemDefinition in ItemManager.itemList)
            {
                if (itemDefinition.category is ItemCategory.Weapon or ItemCategory.Tool)
                {
                    if (!itemDefinition.isHoldable)
                        continue;

                    AttackEntity attackEntity = itemDefinition.GetComponent<ItemModEntity>()?.entityPrefab?.Get()?.GetComponent<AttackEntity>();
                    if (attackEntity != null)
                    {
                        if (attackEntity is BaseMelee)
                            secondary.Add(itemDefinition.shortname);
                        else if (attackEntity is BaseProjectile)
                            primary.Add(itemDefinition.shortname);
                    }
                }
            }

            primary.Sort();
            secondary.Sort();

            _validPrimaryWeapons = primary.ToArray();
            _validSecondaryWeapons = secondary.ToArray();

            Facepunch.Pool.FreeUnmanaged(ref primary);
            Facepunch.Pool.FreeUnmanaged(ref secondary);
        }

        private string[] GetValidPrimaryWeapons() => _validPrimaryWeapons;

        private string[] GetValidSecondaryWeapons() => _validSecondaryWeapons;

        private static string ToOrdinal(int i) => (i + "th").Replace("1th", "1st").Replace("2th", "2nd").Replace("3th", "3rd");
        #endregion

        #region Event Checks
        public string EventName => "One In The Chamber";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<OneInTheChamberEvent>(this, config);

        public bool CanUseClassSelector => true;

        public bool RequireTimeLimit => true;

        public bool RequireScoreLimit => false;

        public bool UseScoreLimit => true;

        public bool UseTimeLimit => true;

        public bool IsTeamEvent => false;

        public bool CanSelectTeam => false;

        public bool CanUseRustTeams => false;

        public bool IsRoundBased => false;

        public bool ProcessWinnersBetweenRounds => true;
        
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
                DataType = "int",
                Field = "playerLives",
                Input = Arena.EventParameter.InputType.InputField,
                IsRequired = true,
                Name = "Player Lives",
                DefaultValue = 5
            },
            new Arena.EventParameter
            {
                DataType = "string",
                Field = "primaryWeapon",
                Input = Arena.EventParameter.InputType.Selector,
                IsRequired = true,
                DefaultValue = "pistol.python",
                Name = "Primary Weapon",
                SelectorHook = "GetValidPrimaryWeapons"
            },
            new Arena.EventParameter
            {
                DataType = "string",
                Field = "secondaryWeapon",
                Input = Arena.EventParameter.InputType.Selector,
                IsRequired = true,
                DefaultValue = "machete",
                Name = "Secondary Weapon",
                SelectorHook = "GetValidSecondaryWeapons"
            },
        };

        public string ParameterIsValid(string fieldName, object value) => null;
        #endregion

        #region Event Classes
        public class OneInTheChamberEvent : Arena.BaseEventGame
        {
            public Arena.BaseEventPlayer winner;

            private ItemDefinition primaryWeapon;

            private ItemDefinition secondaryWeapon;

            internal int playerLives;

            protected override Arena.BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.AddComponent<OITCPlayer>();

            public override void InitializeEvent(IEventPlugin plugin, Arena.EventConfig config)
            {
                playerLives = config.GetParameter<int>("playerLives");
                primaryWeapon = ItemManager.FindItemDefinition(config.GetParameter<string>("primaryWeapon"));
                secondaryWeapon = ItemManager.FindItemDefinition(config.GetParameter<string>("secondaryWeapon"));

                base.InitializeEvent(plugin, config);
            }

            protected override void StartEvent()
            {
                base.StartEvent();
                CloseEvent();
            }

            protected override void StartNextRound()
            {
                winner = null;

                eventPlayers.ForEach((Arena.BaseEventPlayer eventPlayer) => (eventPlayer as OITCPlayer).RemainingLives = playerLives);

                base.StartNextRound();
            }

            protected override float GetDamageModifier(Arena.BaseEventPlayer eventPlayer, Arena.BaseEventPlayer attackerPlayer) => attackerPlayer != null ? 100f : 1f;

            public override void OnEventPlayerDeath(Arena.BaseEventPlayer victim, Arena.BaseEventPlayer attacker = null, HitInfo info = null)
            {
                if (!victim)
                    return;

                victim.OnPlayerDeath(attacker, Configuration.RespawnTime);
                
                if (attacker && victim != attacker)
                {
                    attacker.OnKilledPlayer(info);

                    Item primaryItem = attacker.Player.inventory.containerBelt.FindItemByItemID(primaryWeapon.itemid);
                    if (primaryItem != null)
                    {
                        BaseProjectile baseProjectile = primaryItem.GetHeldEntity() as BaseProjectile;
                        if (baseProjectile && ((info != null && info.Weapon && info.Weapon.GetItem() == primaryItem) || baseProjectile.primaryMagazine.contents == 0))
                        {
                            baseProjectile.primaryMagazine.contents = 1;
                            baseProjectile.SendNetworkUpdate();
                        }
                    }
                }

                if (GetRemainingPlayers() <= 1)
                {
                    winner = attacker;
                    InvokeHandler.Invoke(this, EndRound, 0.1f);
                    return;
                }

                UpdateScoreboard();
                base.OnEventPlayerDeath(victim, attacker, info);
            }

            protected override bool CanDropAmmo() => false;

            protected override bool CanDropBackpack() => false;

            protected override bool CanDropCorpse() => false;

            protected override bool CanDropWeapon() => false;

            protected override void OnKitGiven(Arena.BaseEventPlayer eventPlayer)
            {
                CreateWeapon(eventPlayer.Player, primaryWeapon);
                CreateWeapon(eventPlayer.Player, secondaryWeapon);
            }

            private void CreateWeapon(BasePlayer player, ItemDefinition itemDefinition)
            {
                Item item = ItemManager.Create(itemDefinition);

                BaseProjectile baseProjectile = item.GetHeldEntity() as BaseProjectile;
                if (baseProjectile != null)
                    baseProjectile.primaryMagazine.contents = 1;

                item.MoveToContainer(player.inventory.containerBelt);
            }

            protected override void GetWinningPlayers(ref List<Arena.BaseEventPlayer> winners)
            {
                if (winner == null)
                {
                    if (eventPlayers.Count > 0)
                    {
                        int kills = 0;

                        for (int i = 0; i < eventPlayers.Count; i++)
                        {
                            Arena.BaseEventPlayer eventPlayer = eventPlayers[i];
                            if (eventPlayer == null)
                                continue;

                            if (eventPlayer.Kills > kills)
                            {
                                winner = eventPlayer;
                                kills = eventPlayer.Kills;
                            }                            
                        }
                    }
                }

                if (winner != null)
                    winners.Add(winner);
            }

            private int GetRemainingPlayers()
            {
                int count = 0;
                eventPlayers.ForEach((Arena.BaseEventPlayer eventPlayer) =>
                {
                    if ((eventPlayer as OITCPlayer).RemainingLives >= 1)
                        count++;
                });

                return count;
            }

            #region Scoreboards
            protected override void BuildScoreboard()
            {
                scoreContainer = Arena.CreateScoreboardBase(this);

                int index = -1;

                if (Config.RoundsToPlay > 0)
                    Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Round.Limit", 0UL), RoundNumber, Config.RoundsToPlay), index += 1);

                if (Config.ScoreLimit > 0)
                    Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.Limit", 0UL), Config.ScoreLimit), index += 1);

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

        private class OITCPlayer : Arena.BaseEventPlayer
        {
            internal int RemainingLives { get; set; }

            public override void OnPlayerDeath(Arena.BaseEventPlayer attacker = null, float respawnTime = 5, HitInfo hitInfo = null)
            {
                RemainingLives--;

                if (RemainingLives <= 0)
                {
                    AddPlayerDeath(attacker);

                    DestroyUI();

                    int position = Event.GetAlivePlayerCount();

                    string message = attacker != null ? string.Format(GetMessage("Death.Killed", Player.userID), attacker.Player.displayName, ToOrdinal(position + 1), position) :
                                     IsOutOfBounds ? string.Format(GetMessage("Death.OOB", Player.userID), ToOrdinal(position + 1), position) :
                                     string.Format(GetMessage("Death.Suicide", Player.userID), ToOrdinal(position + 1), position);

                    Arena.DisplayDeathScreen(this, message, false);
                }
                else base.OnPlayerDeath(attacker, respawnTime);
            }
        }
        #endregion

        #region Config        
        private static ConfigData Configuration;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Respawn time (seconds)")]
            public int RespawnTime { get; set; }

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
                EventIcon = "https://www.rustedit.io/images/arena/arena_oitc.png",
                RespawnTime = 5,
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
            ["Round.Limit"] = "Round : {0} / {1}"
        };
        #endregion
    }
}
