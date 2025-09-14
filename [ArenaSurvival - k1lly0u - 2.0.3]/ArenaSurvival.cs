// Requires: Arena
using Newtonsoft.Json;
using Oxide.Plugins.ArenaEx;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Arena Survival", "k1lly0u", "2.0.3"), Description("Last man standing event mode for Arena")]
    class ArenaSurvival : RustPlugin, IEventPlugin
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
        public string EventName => "Survival";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<LastManStandingEvent>(this, config);

        public bool CanUseClassSelector => true;

        public bool RequireTimeLimit => true;

        public bool RequireScoreLimit => false;

        public bool UseScoreLimit => false;

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
            score1 = string.Empty;
            score2 = string.Format(Message("Score.Kills", langUserId), scoreEntry.value2);
        }

        public List<Arena.EventParameter> AdditionalParameters { get; } = null;

        public string ParameterIsValid(string fieldName, object value) => null;
        #endregion

        #region Functions
        private static string ToOrdinal(int i) => (i + "th").Replace("1th", "1st").Replace("2th", "2nd").Replace("3th", "3rd");
        #endregion

        #region Event Classes
        public class LastManStandingEvent : Arena.BaseEventGame
        {
            public Arena.BaseEventPlayer winner;

            protected override Arena.BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.AddComponent<SurvivalPlayer>();

            //internal override ArenaAI.HumanAI CreateAIPlayer(Vector3 position, ArenaAI.Settings settings) => ArenaAI.SpawnNPC<NPCSurvivalPlayer>(position, settings);

            protected override void StartEvent()
            {
                base.StartEvent();
                CloseEvent();
            }

            protected override void StartNextRound()
            {
                winner = null;
                base.StartNextRound();
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

                    if (GetAlivePlayerCount() <= 1)
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
                if (!winner)
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

            #region Scoreboards
            protected override void BuildScoreboard()
            {
                scoreContainer = Arena.CreateScoreboardBase(this);

                int index = -1;

                if (Config.RoundsToPlay > 0)
                    Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Round.Limit", 0UL), RoundNumber, Config.RoundsToPlay), index += 1);

                if (Config.ScoreLimit > 0)
                    Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.Remaining", 0UL), eventPlayers.Count), index += 1);

                Arena.CreateScoreEntry(scoreContainer, string.Empty, string.Empty, "K", index += 1);

                for (int i = 0; i < Mathf.Min(scoreData.Count, 15); i++)
                {
                    Arena.ScoreEntry score = scoreData[i];
                    Arena.CreateScoreEntry(scoreContainer, score.displayName, string.Empty, ((int)score.value2).ToString(), i + index + 1);
                }
            }

            protected override float GetFirstScoreValue(Arena.BaseEventPlayer eventPlayer) => 0;

            protected override float GetSecondScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Kills;

            protected override void SortScores(ref List<Arena.ScoreEntry> list)
            {
                list.Sort(delegate (Arena.ScoreEntry a, Arena.ScoreEntry b)
                {
                    return a.value2.CompareTo(b.value2) * -1;
                });
            }
            #endregion
        }

        private class SurvivalPlayer : Arena.BaseEventPlayer
        {
            public override void OnPlayerDeath(Arena.BaseEventPlayer attacker = null, float respawnTime = 5, HitInfo hitInfo = null)
            {
                AddPlayerDeath(attacker);

                DestroyUI();

                int position = Event.GetAlivePlayerCount();

                string message = attacker != null ? string.Format(GetMessage("Death.Killed", Player.userID), attacker.Player.displayName, ToOrdinal(position + 1), position) :
                                 IsOutOfBounds ? string.Format(GetMessage("Death.OOB", Player.userID), ToOrdinal(position + 1), position) :
                                 string.Format(GetMessage("Death.Suicide", Player.userID), ToOrdinal(position + 1), position);

                Arena.DisplayDeathScreen(this, message, false);
            }
        }

        //private class NPCSurvivalPlayer : ArenaAI.HumanAI
        //{
        //    internal override void OnPlayerDeath(Arena.BaseEventPlayer attacker = null, float respawnTime = 5)
        //    {
        //        AddPlayerDeath(attacker);
        //    }
        //}
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
                EventIcon = "https://www.rustedit.io/images/arena/arena_survival.png",
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
            ["Score.Name"] = "Kills",
            ["Score.Remaining"] = "Players Remaining : {0}",
            ["Death.Killed"] = "You were killed by {0}\nYou placed {1}\n{2} players remain",
            ["Death.Suicide"] = "You died...\nYou placed {0}\n{1} players remain",
            ["Death.OOB"] = "You left the playable area\nYou placed {0}\n{1} players remain",
            ["Round.Limit"] = "Round : {0} / {1}",
        };
        #endregion
    }
}
