// Requires: Arena
using Newtonsoft.Json;
using Oxide.Plugins.ArenaEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Arena Capture The Flag", "k1lly0u", "2.0.5"), Description("Capture the flag event mode for Arena")]
    class ArenaCTF : RustPlugin, IEventPlugin
    {
        #region Oxide Hooks
        private void OnServerInitialized()
        {
            Arena.RegisterEvent(EventName, this);

            GetMessage = Message;
        }

        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private object CanUpdateSign(BasePlayer player, Signage sign) => sign.GetComponentInParent<FlagController>() != null ? (object)true : null;

        private void Unload()
        {
            if (!Arena.IsUnloading)
                Arena.UnregisterEvent(EventName);

            Configuration = null;
        }
        #endregion

        #region Event Checks
        public string EventName => "Capture The Flag";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<CaptureTheFlagEvent>(this, config);

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
            score1 = string.Format(Message("Score.FlagCaptures", langUserId), scoreEntry.value1);
            score2 = string.Format(Message("Score.Kills", langUserId), scoreEntry.value2);
        }

        public List<Arena.EventParameter> AdditionalParameters { get; } = new List<Arena.EventParameter>
        {
            new Arena.EventParameter
            {
                DataType = "int",
                Field = "flagRespawnTimer",
                Input = Arena.EventParameter.InputType.InputField,
                IsRequired = true,
                Name = "Flag Reset Time",
                DefaultValue = 30
            },
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
        public class CaptureTheFlagEvent : Arena.BaseEventGame
        {
            public Arena.Team winningTeam;

            internal int flagRespawnTime;

            private int teamAScore;
            private int teamBScore;

            private bool closeOnStart;

            internal FlagController TeamAFlag { get; private set; }

            internal FlagController TeamBFlag { get; private set; }

            public override void InitializeEvent(IEventPlugin plugin, Arena.EventConfig config)
            {
                flagRespawnTime = config.GetParameter<int>("flagRespawnTimer");
                closeOnStart = config.GetParameter<bool>("closeEventOnStart");

                base.InitializeEvent(plugin, config);

                TeamAFlag = FlagController.Create(this, Arena.Team.A, TeamA.Spawns.ReserveSpawnPoint(0));
                TeamBFlag = FlagController.Create(this, Arena.Team.B, TeamB.Spawns.ReserveSpawnPoint(0));
            }

            protected override void StartEvent()
            {                
                BalanceTeams();
                base.StartEvent();

                if (closeOnStart)
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

            public override void EndEvent()
            {
                TeamAFlag.DropFlag(false);
                TeamBFlag.DropFlag(false);

                base.EndEvent();
            }

            public override bool CanDropActiveItem() => true;

            protected override void OnDestroy()
            {
                Destroy(TeamAFlag);
                Destroy(TeamBFlag);

                base.OnDestroy();
            }

            protected override Arena.BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.AddComponent<CaptureTheFlagPlayer>();

            //internal override ArenaAI.HumanAI CreateAIPlayer(Vector3 position, ArenaAI.Settings settings) => ArenaAI.SpawnNPC<CaptureTheFlagAIPlayer>(position, settings);
            
            protected override void CreateEventPlayer(BasePlayer player, Arena.Team team = Arena.Team.None)
            {
                base.CreateEventPlayer(player, team);

                Arena.LockClothingSlots(player);
            }

            protected override Arena.Team GetPlayerTeam()
            {
                if (GetTeamCount(Arena.Team.A) > GetTeamCount(Arena.Team.B))
                    return Arena.Team.B;
                return Arena.Team.A;
            }

            //protected override Arena.Team GetAIPlayerTeam() => GetPlayerTeam();

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

                if (victim is ICaptureTheFlagPlayer { IsCarryingFlag: true })
                {
                    FlagController flagController = victim.Team == Arena.Team.B ? TeamAFlag : TeamBFlag;
                    if (flagController.FlagHolder == victim)
                    {
                        flagController.DropFlag(true);
                        BroadcastToPlayers(GetMessage, "Notification.FlagDropped", victim.Player.displayName, flagController.Team, GetTeamColor(victim.Team), GetTeamColor(flagController.Team));
                    }
                }

                victim.OnPlayerDeath(attacker, Configuration.RespawnTime);

                if (attacker && victim != attacker && victim.Team != attacker.Team)
                    attacker.OnKilledPlayer(info);

                UpdateScoreboard();
                base.OnEventPlayerDeath(victim, attacker, info);
            }

            protected override void GetWinningPlayers(ref List<Arena.BaseEventPlayer> winners)
            {
                if (winningTeam != Arena.Team.None)
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

            internal void OnFlagCaptured(Arena.BaseEventPlayer eventPlayer, Arena.Team team)
            {
                int flagCaptures;

                if (eventPlayer.Team == Arena.Team.B)
                    flagCaptures = teamBScore += 1;
                else flagCaptures = teamAScore += 1;

                (eventPlayer as ICaptureTheFlagPlayer).FlagCaptures += 1;

                BroadcastToPlayers(GetMessage, "Notification.FlagCaptured", eventPlayer.Player.displayName, team, GetTeamColor(eventPlayer.Team), GetTeamColor(team));

                UpdateScoreboard();

                if (flagCaptures >= Config.ScoreLimit)
                {
                    winningTeam = eventPlayer.Team;
                    InvokeHandler.Invoke(this, EndRound, 0.1f);
                }
            }

            internal string GetTeamColor(Arena.Team team) => team == Arena.Team.B ? TeamB.Color : TeamA.Color;

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

                Arena.CreateScoreEntry(scoreContainer, string.Empty, "C", "K", index += 1);

                for (int i = 0; i < Mathf.Min(scoreData.Count, 15); i++)
                {
                    Arena.ScoreEntry score = scoreData[i];
                    Arena.CreateScoreEntry(scoreContainer, $"<color={(score.team == Arena.Team.A ? TeamA.Color : TeamB.Color)}>{score.displayName}</color>", ((int)score.value1).ToString(), ((int)score.value2).ToString(), i + index + 1);
                }
            }

            protected override float GetFirstScoreValue(Arena.BaseEventPlayer eventPlayer) => (eventPlayer as ICaptureTheFlagPlayer).FlagCaptures;

            protected override float GetSecondScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Kills;

            protected override void SortScores(ref List<Arena.ScoreEntry> list)
            {
                list.Sort(delegate (Arena.ScoreEntry a, Arena.ScoreEntry b)
                {
                    int primaryScore = a.value1.CompareTo(b.value1) * -1;

                    if (primaryScore == 0)
                        return a.value2.CompareTo(b.value2) * -1;

                    return primaryScore;
                });
            }
            #endregion
        }

        internal class CaptureTheFlagPlayer : Arena.BaseEventPlayer, ICaptureTheFlagPlayer
        {
            public int FlagCaptures { get; set; }

            public bool IsCarryingFlag { get; set; }

            public override void ResetStatistics()
            {
                FlagCaptures = 0;
                IsCarryingFlag = false;
            }
        }

        //internal class CaptureTheFlagAIPlayer : ArenaAI.HumanAI, ICaptureTheFlagPlayer
        //{
        //    public int FlagCaptures { get; set; }

        //    public bool IsCarryingFlag { get; set; }
            
        //    internal FlagController TeamFlag
        //    {
        //        get
        //        {
        //            if (Team == Arena.Team.None || Event == null)
        //                return null;

        //            if (_teamFlag == null)
        //                _teamFlag = Team == Arena.Team.A ? (Event as CaptureTheFlagEvent).TeamAFlag : (Event as CaptureTheFlagEvent).TeamBFlag;

        //            return _teamFlag;
        //        }
        //    }

        //    internal FlagController EnemyFlag
        //    {
        //        get
        //        {
        //            if (Team == Arena.Team.None || Event == null)
        //                return null;

        //            if (_enemyFlag == null)
        //                _enemyFlag = Team == Arena.Team.A ? (Event as CaptureTheFlagEvent).TeamBFlag : (Event as CaptureTheFlagEvent).TeamAFlag;

        //            return _enemyFlag;
        //        }
        //    }

        //    private FlagController _teamFlag;

        //    private FlagController _enemyFlag;


        //    protected override void CreateAIBrain()
        //    {
        //        Brain = new ArenaAI.EventAIBrain<ArenaAI.HumanAI>(this, 7);

        //        Brain.AddState(new ArenaAI.IdleState(), 0);
        //        Brain.AddState(new ArenaAI.ChaseState(), 1);
        //        Brain.AddState(new ArenaAI.CombatState(), 2);
        //        Brain.AddState(new TryGetEnemyFlagState(), 3);
        //        Brain.AddState(new TryCaptureEnemyFlagState(), 4);
        //        Brain.AddState(new TryReturnTeamFlagState(), 5);
        //        Brain.AddState(new MoveTowardsEnemyWithFlagState(), 6);

        //        Brain.DoThink();
        //    }

        //    internal class TryGetEnemyFlagState : ArenaAI.EventAIState<ArenaAI.HumanAI>
        //    {
        //        internal override float GetWeight()
        //        {
        //            if ((AI as CaptureTheFlagAIPlayer).EnemyFlag == null || (AI as CaptureTheFlagAIPlayer).EnemyFlag.FlagHolder != null)
        //                return 0f;

        //            return 5f;
        //        }

        //        internal override void StateEnter()
        //        {
        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //            AI.Entity.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);

        //            AI.Entity.IsDormant = false;

        //            base.StateEnter();
        //        }

        //        internal override void StateThink(float delta)
        //        {
        //            base.StateThink(delta);

        //            AI.SetDestination((AI as CaptureTheFlagAIPlayer).EnemyFlag.Transform.position);

        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //        }
        //    }

        //    internal class TryCaptureEnemyFlagState : ArenaAI.EventAIState<ArenaAI.HumanAI>
        //    {
        //        internal override float GetWeight()
        //        {                    
        //            if ((AI as CaptureTheFlagAIPlayer).IsCarryingFlag)
        //                return 10f;

        //            return 0f;
        //        }

        //        internal override void StateEnter()
        //        {
        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //            AI.Entity.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);

        //            AI.Entity.IsDormant = false;

        //            base.StateEnter();
        //        }

        //        internal override void StateThink(float delta)
        //        {
        //            base.StateThink(delta);

        //            AI.SetDestination((AI as CaptureTheFlagAIPlayer).TeamFlag.HomePosition);

        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //        }
        //    }

        //    internal class TryReturnTeamFlagState : ArenaAI.EventAIState<ArenaAI.HumanAI>
        //    {
        //        internal override float GetWeight()
        //        {
        //            if ((AI as CaptureTheFlagAIPlayer).TeamFlag == null || (AI as CaptureTheFlagAIPlayer).TeamFlag.IsAtBase)
        //                return 0f;

        //            if ((AI as CaptureTheFlagAIPlayer).TeamFlag.FlagHolder == null)
        //                return 5f;

        //            return 0f;
        //        }

        //        internal override void StateEnter()
        //        {
        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //            AI.Entity.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);

        //            AI.Entity.IsDormant = false;

        //            base.StateEnter();
        //        }

        //        internal override void StateThink(float delta)
        //        {
        //            base.StateThink(delta);

        //            AI.SetDestination((AI as CaptureTheFlagAIPlayer).TeamFlag.Transform.position);

        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //        }
        //    }

        //    internal class MoveTowardsEnemyWithFlagState : ArenaAI.EventAIState<ArenaAI.HumanAI>
        //    {
        //        internal override float GetWeight()
        //        {
        //            if ((AI as CaptureTheFlagAIPlayer).TeamFlag == null || (AI as CaptureTheFlagAIPlayer).TeamFlag.IsAtBase)
        //                return 0f;

        //            if ((AI as CaptureTheFlagAIPlayer).TeamFlag.FlagHolder != null)
        //                return 4f;

        //            return 0f;
        //        }

        //        internal override void StateEnter()
        //        {
        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //            AI.Entity.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);

        //            AI.Entity.IsDormant = false;

        //            base.StateEnter();
        //        }

        //        internal override void StateThink(float delta)
        //        {
        //            base.StateThink(delta);

        //            AI.SetDestination((AI as CaptureTheFlagAIPlayer).TeamFlag.Transform.position);

        //            AI.Entity.SetDesiredSpeed(global::HumanNPC.SpeedType.Sprint);
        //        }
        //    }
        //}

        internal interface ICaptureTheFlagPlayer
        {
            int FlagCaptures { get; set; }
            
            bool IsCarryingFlag { get; set; }
        }

        internal class FlagController : MonoBehaviour
        {
            private Signage primary;
            private Signage secondary;

            private Vector3 basePosition;
            private BoxCollider boxCollider;

            private uint signImageCRC = 0;

            private CaptureTheFlagEvent captureTheFlagEvent;

            internal Transform Transform { get; private set; }

            internal Arena.Team Team { get; set; }

            internal Arena.BaseEventPlayer FlagHolder { get; private set; }

            internal bool IsAtBase { get; private set; } = true;

            internal Vector3 HomePosition => basePosition;

            private const string SIGN_PREFAB = "assets/prefabs/deployable/signs/sign.post.single.prefab";

            private const float ROTATE_SPEED = 48f;

            internal static FlagController Create(CaptureTheFlagEvent captureTheFlagEvent, Arena.Team team, Vector3 position)
            {
                Signage signage = Spawn(position);
                FlagController flagController = signage.gameObject.AddComponent<FlagController>();

                flagController.captureTheFlagEvent = captureTheFlagEvent;
                flagController.Team = team;
                flagController.basePosition = position;

                return flagController;
            }

            private static Signage Spawn(Vector3 position)
            {
                Signage signage = GameManager.server.CreateEntity(SIGN_PREFAB, position) as Signage;
                signage.enableSaving = false;
                signage.Spawn();

                Destroy(signage.GetComponent<MeshCollider>());
                Destroy(signage.GetComponent<DestroyOnGroundMissing>());
                Destroy(signage.GetComponent<GroundWatch>());

                return signage;
            }

            private void Awake()
            {
                primary = GetComponent<Signage>();
                Transform = primary.transform;
            }

            private void Start()
            {
                secondary = Spawn(Transform.position);

                secondary.SetParent(primary);
                secondary.transform.localPosition = Vector3.zero;
                secondary.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

                SetSignImages(primary);
                SetSignImages(secondary);

                primary.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                secondary.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);

                primary.gameObject.layer = (int)Rust.Layer.Reserved1;

                boxCollider = primary.gameObject.AddComponent<BoxCollider>();
                boxCollider.size = new Vector3(1.2f, 2f, 1f);
                boxCollider.center = new Vector3(0f, 1.1f, 0f);
                boxCollider.isTrigger = true;
            }

            private void Update()
            {
                Transform.RotateAround(Transform.position, Transform.up, Time.deltaTime * ROTATE_SPEED);
                primary.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
            }

            private void OnDestroy()
            {
                Destroy(boxCollider);

                if (secondary != null && !secondary.IsDestroyed)
                {
                    secondary.SetParent(null);
                    secondary.Kill(BaseNetworkable.DestroyMode.None);
                }

                if (primary != null && !primary.IsDestroyed)
                {
                    primary.SetParent(null);
                    primary.Kill();
                }
            }

            private void SetSignImages(Signage signage)
            {
                string hex = Team == Arena.Team.B ? captureTheFlagEvent.TeamB.Color : captureTheFlagEvent.TeamA.Color;

                if (signImageCRC == 0)
                {
                    hex = hex.TrimStart('#');

                    int red = int.Parse(hex.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                    int green = int.Parse(hex.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                    int blue = int.Parse(hex.Substring(4, 2), NumberStyles.AllowHexSpecifier);

                    Color color = new Color((float)red / 255f, (float)green / 255f, (float)blue / 255f);

                    Color[] array = new Color[256 * 256];
                    for (int i = 0; i < array.Length; i++)
                        array[i] = color;

                    Texture2D texture2D = new Texture2D(256, 256);
                    texture2D.SetPixels(array);
                    byte[] bytes = texture2D.EncodeToPNG();

                    Destroy(texture2D);

                    signImageCRC = FileStorage.server.Store(bytes, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                }

                Array.Resize<uint>(ref signage.textureIDs, 1);

                signage.textureIDs[0] = signImageCRC;
                signage.SetFlag(BaseEntity.Flags.Locked, true);
            }

            private void OnTriggerEnter(Collider col)
            {
                if (captureTheFlagEvent.Status != Arena.EventStatus.Started)
                    return;

                Arena.BaseEventPlayer eventPlayer = col.GetComponent<Arena.BaseEventPlayer>();
                if (eventPlayer == null || eventPlayer.IsDead)
                    return;

                if (IsAtBase)
                {
                    if (eventPlayer.Team != Team)
                        PickupFlag(eventPlayer);
                    else
                    {
                        if ((eventPlayer as ICaptureTheFlagPlayer).IsCarryingFlag)
                        {
                            FlagController enemyFlag = Team == Arena.Team.A ? captureTheFlagEvent.TeamBFlag : captureTheFlagEvent.TeamAFlag;
                            enemyFlag.CaptureFlag(eventPlayer);
                        }
                    }
                }
                else
                {
                    if (FlagHolder == null)
                    {
                        if (eventPlayer.Team != Team)
                            PickupFlag(eventPlayer);
                        else
                        {
                            ResetFlag();
                            captureTheFlagEvent.BroadcastToPlayers(GetMessage, "Notification.FlagReset", eventPlayer.Team, captureTheFlagEvent.GetTeamColor(eventPlayer.Team));
                        }
                    }
                }
            }

            private void PickupFlag(Arena.BaseEventPlayer eventPlayer)
            {
                FlagHolder = eventPlayer;
                (eventPlayer as ICaptureTheFlagPlayer).IsCarryingFlag = true;

                IsAtBase = false;
                InvokeHandler.CancelInvoke(this, DroppedTimeExpired);

                primary.SetParent(eventPlayer.Player);
                Transform.localPosition = new Vector3(0f, 0.25f, -0.75f);

                captureTheFlagEvent.BroadcastToPlayers(GetMessage, "Notification.FlagPickedUp", eventPlayer.Player.displayName, Team, captureTheFlagEvent.GetTeamColor(eventPlayer.Team), captureTheFlagEvent.GetTeamColor(Team));
            }

            internal void DropFlag(bool resetToBase)
            {
                primary.SetParent(null, true);

                if (FlagHolder != null)
                {
                    (FlagHolder as ICaptureTheFlagPlayer).IsCarryingFlag = false;
                    FlagHolder = null;
                }

                primary.UpdateNetworkGroup();
                primary.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);

                if (resetToBase)
                    InvokeHandler.Invoke(this, DroppedTimeExpired, captureTheFlagEvent.flagRespawnTime);
            }

            private void CaptureFlag(Arena.BaseEventPlayer eventPlayer)
            {
                ResetFlag();
                captureTheFlagEvent.OnFlagCaptured(eventPlayer, Team);
            }

            private void DroppedTimeExpired()
            {
                captureTheFlagEvent.BroadcastToPlayers(GetMessage, "Notification.FlagReset", Team, captureTheFlagEvent.GetTeamColor(Team));
                ResetFlag();
            }

            private void ResetFlag()
            {
                if (FlagHolder != null)
                {
                    (FlagHolder as ICaptureTheFlagPlayer).IsCarryingFlag = false;
                    FlagHolder = null;
                }

                InvokeHandler.CancelInvoke(this, DroppedTimeExpired);

                primary.SetParent(null);

                Transform.position = basePosition;

                primary.UpdateNetworkGroup();
                primary.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);

                IsAtBase = true;
            }
        }
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
                EventIcon = "https://www.rustedit.io/images/arena/arena_ctf.png",
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
            ["Score.FlagCaptures"] = "Flag Captures: {0}",
            ["Score.Kills"] = "Kills: {0}",
            ["Score.Name"] = "Kills",
            ["Score.Limit"] = "Score Limit : {0}",
            ["Score.Team"] = "{0} : <color={1}>Team A</color> | <color={2}>Team B</color> : {3}",
            ["Notification.FlagPickedUp"] = "<color={2}>{0}</color> has picked up <color={3}>Team {1}</color>'s flag",
            ["Notification.FlagReset"] = "<color={1}>Team {0}</color>'s flag has been returned to base",
            ["Notification.FlagCaptured"] = "<color={2}>{0}</color> has captured <color={3}>Team {1}</color>'s flag",
            ["Notification.FlagDropped"] = "<color={2}>{0}</color> has dropped <color={3}>Team {1}</color>'s flag"

        };
        #endregion
    }
}
