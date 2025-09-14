// Requires: Arena
using Newtonsoft.Json;
using Oxide.Plugins.ArenaEx;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Arena Team Survival", "k1lly0u", "2.0.5"), Description("Team Survival event mode for Arena")]
    class ArenaTeamSurvival : RustPlugin, IEventPlugin
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
        public string EventName => "Team Survival";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<TeamSurvivalEvent>(this, config);

        public bool CanUseClassSelector => true;

        public bool RequireTimeLimit => true;

        public bool RequireScoreLimit => false;

        public bool UseScoreLimit => true;

        public bool UseTimeLimit => true;

        public bool IsTeamEvent => true;

        public bool CanSelectTeam => true;

        public bool CanUseRustTeams => true;

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
        public class TeamSurvivalEvent : Arena.BaseEventGame
        {
            public Arena.Team winningTeam;

            private int teamAScore;
            private int teamBScore;

            protected override Arena.BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.AddComponent<TeamSurvivalPlayer>();           

            protected override void StartEvent()
            {
                BalanceTeams();
                base.StartEvent();

                CloseEvent();
            }

            protected override void StartNextRound()
            {
                winningTeam = Arena.Team.None;
                teamAScore = 0;
                teamBScore = 0;

                BalanceTeams();

                base.StartNextRound();
            }

            protected override Arena.Team GetPlayerTeam()
            {
                if (GetTeamCount(Arena.Team.A) > GetTeamCount(Arena.Team.B))
                    return Arena.Team.B;
                return Arena.Team.A;
            }

            protected override void CreateEventPlayer(BasePlayer player, Arena.Team team = Arena.Team.None)
            {
                base.CreateEventPlayer(player, team);

                Arena.LockClothingSlots(player);
            }

            public override bool CanDropActiveItem() => true;

            public override int GetTeamScore(Arena.Team team) => team == Arena.Team.B ? teamBScore : teamAScore;
                        
            protected override float GetDamageModifier(Arena.BaseEventPlayer eventPlayer, Arena.BaseEventPlayer attackerPlayer)
            {
                if (attackerPlayer && eventPlayer.Team == attackerPlayer.Team && Configuration.FriendlyFireModifier != 1f)
                    return Configuration.FriendlyFireModifier;

                return 1f;
            }

            public override void OnEventPlayerDeath(Arena.BaseEventPlayer victim, Arena.BaseEventPlayer attacker = null, HitInfo info = null)
            {
                if (!victim)
                    return;

                victim.OnPlayerDeath(attacker, Configuration.RespawnTime);

                if (attacker && victim != attacker && victim.Team != attacker.Team)
                {
                    if (attacker.Team == Arena.Team.B)
                        teamBScore += 1;
                    else teamAScore += 1;

                    attacker.OnKilledPlayer(info);

                    if (GetTeamAliveCount(victim.Team) == 0)
                    {
                        winningTeam = attacker.Team;
                        InvokeHandler.Invoke(this, EndRound, 0.1f);
                        return;
                    }
                }

                UpdateScoreboard();
                base.OnEventPlayerDeath(victim, attacker, info);
            }
            
            protected override void GetWinningPlayers(ref List<Arena.BaseEventPlayer> winners)
            {
                if (winningTeam < Arena.Team.None)
                {
                    if (eventPlayers.Count > 0)
                    {
                        for (int i = 0; i < eventPlayers.Count; i++)
                        {
                            Arena.BaseEventPlayer eventPlayer = eventPlayers[i];
                            if (eventPlayer == null)
                                continue;

                            if (eventPlayer.Team == winningTeam)
                                winners.Add(eventPlayer);
                        }
                    }
                }
            }

            #region Scoreboards
            protected override void BuildScoreboard()
            {
                scoreContainer = Arena.CreateScoreboardBase(this);

                int index = -1;
                if (Config.RoundsToPlay > 0)
                    Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Round.Limit", 0UL), RoundNumber, Config.RoundsToPlay), index += 1);

                Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.Team", 0UL), teamAScore, TeamA.Color, TeamB.Color, teamBScore), index += 1);

                if (Config.ScoreLimit > 0)
                    Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.Limit", 0UL), Config.ScoreLimit), index += 1);

                Arena.CreateScoreEntry(scoreContainer, string.Empty, string.Empty, "K", index += 1);

                for (int i = 0; i < Mathf.Min(scoreData.Count, 15); i++)
                {
                    Arena.ScoreEntry score = scoreData[i];
                    Arena.CreateScoreEntry(scoreContainer, score.displayName, string.Empty, ((int)score.value2).ToString(), i + index + 1);
                }
            }

            protected override float GetFirstScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Kills;

            protected override float GetSecondScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Deaths;

            protected override void SortScores(ref List<Arena.ScoreEntry> list)
            {
                list.Sort(delegate (Arena.ScoreEntry a, Arena.ScoreEntry b)
                {
                    return a.value2.CompareTo(b.value2) * -1;
                });
            }
            #endregion

            //internal override ArenaAI.HumanAI CreateAIPlayer(Vector3 position, ArenaAI.Settings settings) => ArenaAI.SpawnNPC<NPCTeamSurvivalPlayer>(position, settings);
        }

        private class TeamSurvivalPlayer : Arena.BaseEventPlayer
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

        //private class NPCTeamSurvivalPlayer : ArenaAI.HumanAI
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

            [JsonProperty(PropertyName = "Friendly fire damage modifier (0.0 is no damage, 1.0 is normal damage)")]
            public float FriendlyFireModifier { get; set; }

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
                EventIcon = "https://www.rustedit.io/images/arena/arena_teamsurvival.png",
                RespawnTime = 5,
                FriendlyFireModifier = 1.0f,
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();

            if (Configuration.Version < new Core.VersionNumber(0, 4, 1))
                Configuration.FriendlyFireModifier = baseConfig.FriendlyFireModifier;

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
            ["Score.Team"] = "{0} : <color={1}>Team A</color> | <color={2}>Team B</color> : {3}",
            ["Round.Limit"] = "Round : {0} / {1}"
        };
        #endregion
    }
}
