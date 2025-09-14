// Requires: Arena
using Newtonsoft.Json;
using Oxide.Plugins.ArenaEx;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Arena FFA", "k1lly0u", "2.0.3"), Description("Deathmatch event mode for Arena")]
    class ArenaFFA : RustPlugin, IEventPlugin
    {
        #region Oxide Hooks
        private void OnServerInitialized()
        {
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

        #region Event Checks
        public string EventName => "Free For All";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<DeathmatchEvent>(this, config);

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
        
        public bool CanUseBots => true;

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
                DataType = "bool",
                Field = "closeOnStart",
                Input = Arena.EventParameter.InputType.Toggle,
                IsRequired = false,
                Name = "Close Event On Start",
                DefaultValue = false
            }
        };

        public string ParameterIsValid(string fieldName, object value) => null;
        #endregion

        #region Event Classes
        public class DeathmatchEvent : Arena.BaseEventGame
        {
            public Arena.BaseEventPlayer winner;

            private bool closeOnStart;

            public override void InitializeEvent(IEventPlugin plugin, Arena.EventConfig config)
            {
                closeOnStart = config.GetParameter<bool>("closeEventOnStart");
                base.InitializeEvent(plugin, config);
            }

            protected override void StartEvent()
            {
                base.StartEvent();

                if (closeOnStart)
                    CloseEvent();
            }

            protected override void StartNextRound()
            {
                winner = null;
                base.StartNextRound();
            }

            protected override void EndRound()
            {
                if (winner == null)
                {
                     
                }
                base.EndRound();
            }

            public override bool CanDropActiveItem() => true;

            public override void OnEventPlayerDeath(Arena.BaseEventPlayer victim, Arena.BaseEventPlayer attacker = null, HitInfo info = null)
            {
                if (!victim)
                    return;

                victim.OnPlayerDeath(attacker, Configuration.RespawnTime);

                if (attacker && victim != attacker)
                {
                    attacker.OnKilledPlayer(info);

                    if (Config.ScoreLimit > 0 && attacker.Kills >= Config.ScoreLimit)
                    {
                        winner = attacker;
                        InvokeHandler.Invoke(this, EndRound, 0.1f);
                        return;
                    }
                }

                UpdateScoreboard();
                base.OnEventPlayerDeath(victim, attacker, info);
            }

            protected override void GetWinningPlayers(ref List<Arena.BaseEventPlayer> winners)
            {
                if (winner == null)
                {
                    if (eventPlayers.Count > 0)
                    {
                        int kills = 0;
                        int deaths = 0;

                        for (int i = 0; i < eventPlayers.Count; i++)
                        {
                            Arena.BaseEventPlayer eventPlayer = eventPlayers[i];
                            if (eventPlayer == null)
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
                }

                if (winner != null)
                    winners.Add(winner);
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
                EventIcon = "https://www.rustedit.io/images/arena/arena_ffa.png",
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
