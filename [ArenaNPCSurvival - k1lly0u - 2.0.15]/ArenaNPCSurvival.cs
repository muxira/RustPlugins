// Requires: Arena

using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Ext.Chaos;
using Oxide.Plugins.ArenaEx;
using Oxide.Ext.ChaosNPC;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Arena NPC Survival", "k1lly0u", "2.0.15"), Description("NPC Survival event mode for Arena")]
    class ArenaNPCSurvival : RustPlugin, IEventPlugin, IChaosNPCPlugin
    {
        [PluginReference]
        private Plugin Spawns;
        private string[] NPCTypes { get; } = new string[] { "HeavyScientist", "Scientist", "Scarecrow", "BanditGuard", "TunnelDweller" };

        #region Oxide Hooks
        private void OnServerInitialized()
        {
            Arena.RegisterEvent(EventName, this);

            GetMessage = Message;
        }

        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private void OnEntityDeath(NPCSurvivalBot npcSurvivalBot, HitInfo hitInfo)
        {
            if (npcSurvivalBot)
                npcSurvivalBot.npcSurvivalEvent.OnNPCDeath(npcSurvivalBot, hitInfo);
        }
                
        private void Unload()
        {
            if (!Arena.IsUnloading)
                Arena.UnregisterEvent(EventName);

            Configuration = null;
        }
        #endregion

        #region Event Checks
        public string EventName => "NPC Survival";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<NPCSurvivalEvent>(this, config);

        public bool CanUseClassSelector => true;

        public bool RequireTimeLimit => true;

        public bool RequireScoreLimit => false;

        public bool UseScoreLimit => false;

        public bool UseTimeLimit => true;

        public bool IsTeamEvent => false;

        public bool CanSelectTeam => false;

        public bool CanUseRustTeams => false;

        public bool IsRoundBased => false;
        
        public bool ProcessWinnersBetweenRounds => false;

        public bool CanUseBots => false;

        public string TeamAName => "Human";

        public string TeamBName => "NPC";

        public void FormatScoreEntry(Arena.ScoreEntry scoreEntry, ulong langUserId, out string score1, out string score2)
        {
            score1 = string.Empty;
            score2 = string.Format(Message("Score.Kills", langUserId), scoreEntry.value2);
        }

        public List<Arena.EventParameter> AdditionalParameters { get; } = new List<Arena.EventParameter>
        {
            new Arena.EventParameter
            {
                DataType = "int",
                DefaultValue = 3,
                Field = "playerLives",
                Input = Arena.EventParameter.InputType.InputField,
                IsRequired = true,
                Name = "Player Lives"
            },
            new Arena.EventParameter
            {
                DataType = "string",
                DefaultValue = "Scientist",
                SelectorHook = "GetNPCTypes",
                Field = "npcType",
                Input = Arena.EventParameter.InputType.Selector,
                IsRequired = true,
                Name = "NPC Type"                
            },
            new Arena.EventParameter
            {
                DataType = "int",
                DefaultValue = 3,
                Field = "npcsPerPlayer",
                Input = Arena.EventParameter.InputType.InputField,
                IsRequired = true,
                Name = "NPCs Per Player"                
            },
            new Arena.EventParameter
            {
                DataType = "int",
                DefaultValue = 1,
                Field = "additionalPerRound",
                Input = Arena.EventParameter.InputType.InputField,
                IsRequired = true,
                Name = "Additional NPCs Each Round"
            },
            new Arena.EventParameter
            {
                DataType = "string",
                DefaultValue = string.Empty,
                Field = "npcSpawnFile",
                Input = Arena.EventParameter.InputType.Selector,
                IsRequired = true,
                Name = "NPC Spawn File",
                SelectorHook = "GetSpawnfileNames"
            },
            new Arena.EventParameter
            {
                DataType = "List<string>",
                DefaultValue = string.Empty,
                Field = "npcKits",
                Input = Arena.EventParameter.InputType.Selector,
                IsRequired = false,
                Name = "NPC Kit(s)",
                SelectorHook = "GetAllKits",
                SelectMultiple = true
            }
        };

        public string ParameterIsValid(string fieldName, object value) => null;
        #endregion

        #region Functions
        private string[] GetNPCTypes() => NPCTypes;

        private static string ToOrdinal(int i) => (i + "th").Replace("1th", "1st").Replace("2th", "2nd").Replace("3th", "3rd");
        #endregion

        #region Event Classes

        private class NpcSpawnSelector : Arena.SpawnSelector
        {
            private static Plugin Spawns;
            
            public override int Count => _defaultSpawns.Count;

            internal NpcSpawnSelector(string spawnFile)
            {
                if (Spawns == null)
                    Spawns = Interface.Oxide.RootPluginManager.GetPlugin("Spawns");

                if (Spawns == null || !Spawns.IsLoaded)
                {
                    Debug.LogError($"[NpcSurvival] Spawns plugin is not loaded!");
                    
                    _defaultSpawns = Facepunch.Pool.Get<List<Vector3>>();
                    _availableSpawns = Facepunch.Pool.Get<List<Vector3>>();
                    return;
                }
                
                List<Vector3> spawns = Spawns.Call("LoadSpawnFile", spawnFile) as List<Vector3> ?? new List<Vector3>();

                _defaultSpawns = Facepunch.Pool.Get<List<Vector3>>();
                _availableSpawns = Facepunch.Pool.Get<List<Vector3>>();

                for (int i = 0; i < spawns.Count; i++)
                {
                    if (NavmeshSpawnPoint.Find(spawns[i], 20f, out Vector3 position))
                        _defaultSpawns.Add(position);
                }
                
                _availableSpawns.AddRange(_defaultSpawns);
            }
        }

        public class NPCSurvivalEvent : Arena.BaseEventGame
        {
            public Arena.Team winningTeam;

            private NpcSpawnSelector npcSpawns;

            private readonly List<NPCSurvivalBot> eventNPCs = new List<NPCSurvivalBot>();

            private int npcsPerPlayer;
            private int additionalPerRound;
            private string npcSpawnFile;
            internal int playerLives;

            private NPCSettings Settings { get; set; }

            public override void InitializeEvent(IEventPlugin plugin, Arena.EventConfig config)
            {
                playerLives = config.GetParameter<int>("playerLives") - 1;

                npcsPerPlayer = config.GetParameter<int>("npcsPerPlayer");
                additionalPerRound = config.GetParameter<int>("additionalPerRound");
                npcSpawnFile = config.GetParameter<string>("npcSpawnFile");

                SetupNPCSettings(config);                

                npcSpawns = new NpcSpawnSelector(npcSpawnFile);

                if (npcSpawns.Count == 0)
                    Debug.LogError($"Event {config.EventName} has no valid NPC spawn points!");

                base.InitializeEvent(plugin, config);
            }

            private void SetupNPCSettings(Arena.EventConfig config)
            {
                Settings = new NPCSettings();

                switch (config.GetParameter<string>("npcType"))
                {
                    case "Scientist":
                        Settings.Types = new NPCType[] { NPCType.Scientist };
                        break;
                    case "HeavyScientist":
                        Settings.Types = new NPCType[] { NPCType.HeavyScientist };
                        break;
                    case "Scarecrow":
                        Settings.Types = new NPCType[] { NPCType.Scarecrow };
                        break;
                    case "BanditGuard":
                        Settings.Types = new NPCType[] { NPCType.BanditGuard };
                        break;
                    default:
                        Settings.Types = new NPCType[] { NPCType.TunnelDweller };
                        break;
                }

                // Convert npcKit string param to npcKits List param. Don't save cause we are enumerating, it will be saved next time the user updates a event
                if (config.AdditionalParams.TryGetValue("npcKit", out object kitObj))
                {
                    List<string> list = new List<string>();
                    
                    if (kitObj is string kitStr)
                        list.Add(kitStr);

                    config.AdditionalParams.Remove("npcKit");
                    config.AdditionalParams["npcKits"] = list;
                }
                
                List<string> kits = config.GetParameter<List<string>>("npcKits");
                Settings.Kits = kits?.ToArray() ?? Array.Empty<string>();
                
                Settings.StripCorpseLoot = true;

                Settings.Sensory.VisionCone = 160f;
                Settings.Sensory.IgnoreNonVisionSneakers = false;
                Settings.Sensory.IgnoreSafeZonePlayers = false;

                Settings.Sensory.SenseRange = float.MaxValue;
                Settings.Sensory.ListenRange = float.MaxValue;
                Settings.Sensory.TargetLostRange = float.MaxValue;
                Settings.Sensory.TargetLostRangeTime = float.MaxValue;
                Settings.Sensory.TargetLostLOSTime = float.MaxValue;
            }

            protected override void StartEvent()
            {
                base.StartEvent();
                CloseEvent();
            }

            protected override bool CanEnterBetweenRounds() => false;

            protected override void StartNextRound()
            {
                winningTeam = Arena.Team.None;

                base.StartNextRound();

                InvokeHandler.CancelInvoke(this, SpawnBots);
                InvokeHandler.Invoke(this, SpawnBots, 5f);
            }

            protected override Arena.Team GetPlayerTeam() => Arena.Team.A;
            
            protected override Arena.BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.AddComponent<NPCSurvivalPlayer>();

            protected override void CreateEventPlayer(BasePlayer player, Arena.Team team = Arena.Team.None)
            {
                team = GetPlayerTeam();
                
                base.CreateEventPlayer(player, team);

                Arena.LockClothingSlots(player);
            }

            public override bool CanDropActiveItem() => true;
            
            protected override float GetDamageModifier(Arena.BaseEventPlayer eventPlayer, Arena.BaseEventPlayer attackerPlayer)
            {
                if (attackerPlayer != null && eventPlayer.Team == attackerPlayer.Team && Configuration.FriendlyFireModifier != 1f)
                    return Configuration.FriendlyFireModifier;

                return 1f;
            }

            protected override bool CanKillEntity(BaseCombatEntity baseCombatEntity)
            {
                if (!baseCombatEntity || baseCombatEntity is NPCSurvivalBot)
                    return false;

                return base.CanKillEntity(baseCombatEntity);
            }

            protected override void OnKitGiven(Arena.BaseEventPlayer eventPlayer)
            {
                base.OnKitGiven(eventPlayer);

                BroadcastToPlayer(eventPlayer, string.Format(GetMessage("Lives.Remain", eventPlayer.Player.userID), playerLives - eventPlayer.Deaths));
            }

            public override void OnEventPlayerDeath(Arena.BaseEventPlayer victim, Arena.BaseEventPlayer attacker = null, HitInfo info = null)
            {
                if (!victim)
                    return;

                victim.OnPlayerDeath(attacker, Configuration.RespawnTime, info);

                if (attacker && victim != attacker)                                  
                    attacker.OnKilledPlayer(info);

                if (GetRemainingPlayerCount() == 0)
                {
                    winningTeam = Arena.Team.B;
                    InvokeHandler.Invoke(this, EndEvent, 0.1f);
                    return;
                }

                UpdateScoreboard();

                if (!attacker)
                {
                    victim.Player.StripInventory();

                    if (Arena.BroadcastKills) 
                        DisplayKillToChat(victim, info?.InitiatorPlayer ? "NPC" : string.Empty);
                }
                else base.OnEventPlayerDeath(victim, attacker, info);
            }
                       
            protected override bool CanRespawnPlayer(Arena.BaseEventPlayer eventPlayer)
            {
                return eventPlayer.Deaths < playerLives;
            }

            internal int GetRemainingPlayerCount()
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    Arena.BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer.Deaths < playerLives)
                        count++;
                }
                return count;
            }

            protected override void RebuildSpectateTargets()
            {     
                spectateTargets.Clear();

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    Arena.BaseEventPlayer eventPlayer = eventPlayers[i];

                    if (!eventPlayer || eventPlayer.IsDead || eventPlayer.Player.IsSpectating() || eventPlayer.Deaths >= playerLives)
                        continue;

                    spectateTargets.Add(eventPlayer);
                }
            }

            internal void OnNPCDeath(NPCSurvivalBot npcSurvivalBot, HitInfo hitInfo)
            {
                if (!npcSurvivalBot)
                    return;

                eventNPCs.Remove(npcSurvivalBot);

                if (hitInfo.InitiatorPlayer)
                {
                    Arena.BaseEventPlayer eventPlayer = hitInfo.InitiatorPlayer.GetComponent<Arena.BaseEventPlayer>();
                    if (eventPlayer)
                        eventPlayer.OnKilledPlayer(hitInfo);
                }

                if (eventNPCs.Count == 0)
                {
                    winningTeam = Arena.Team.A;
                    InvokeHandler.Invoke(this, EndRound, 0.1f);
                }

                UpdateScoreboard();
            }

            private void SpawnBots() => SpawnBots(npcsPerPlayer + ((additionalPerRound * (RoundNumber - 1)) * GetActualPlayerCount()));

            private void SpawnBots(int amount)
            {
                for (int i = 0; i < amount; i++)
                {
                    NPCSurvivalBot npcSurvivalBot = ChaosNPC.SpawnNPC<NPCSurvivalBot>(Plugin as Oxide.Core.Plugins.Plugin, npcSpawns.GetSpawnPoint(), Settings);
                    if (npcSurvivalBot) 
                    {
                        npcSurvivalBot.npcSurvivalEvent = this;
                        eventNPCs.Add(npcSurvivalBot);
                    }
                }

                UpdateScoreboard();
            }

            protected override void EndRound()
            {
                InvokeHandler.CancelInvoke(this, SpawnBots);
                KillRemainingNPCs();
                base.EndRound();
            }

            public override void EndEvent()
            {
                InvokeHandler.CancelInvoke(this, SpawnBots);
                KillRemainingNPCs();
                base.EndEvent();
            }

            private void KillRemainingNPCs()
            {
                for (int i = eventNPCs.Count - 1; i >= 0; i--)
                {
                    NPCSurvivalBot npcSurvivalBot = eventNPCs[i];
                    if (npcSurvivalBot && !npcSurvivalBot.IsDestroyed)
                        npcSurvivalBot.Kill(BaseNetworkable.DestroyMode.None);
                }

                eventNPCs.Clear();
            }

            protected override void GetWinningPlayers(ref List<Arena.BaseEventPlayer> winners)
            {
                if (winningTeam != Arena.Team.None)
                {
                    for (int i = 0; i < eventPlayers.Count; i++)
                    {
                        Arena.BaseEventPlayer eventPlayer = eventPlayers[i];
                        if (eventPlayer.Team == winningTeam && eventPlayer.Deaths < playerLives)
                            winners.Add(eventPlayer);
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

                Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.Players", 0UL), GetRemainingPlayerCount()), index += 1);

                Arena.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.NPCs", 0UL), eventNPCs.Count), index += 1);

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
                    return a.value2.CompareTo(b.value2) * -1;
                });
            }
            #endregion

            //protected override Arena.Team GetAIPlayerTeam() => Arena.Team.B;

            //internal override ArenaAI.Settings Settings => new ArenaAI.Settings() { NPCType = npcType };

            //internal override int GetTeamScore(Arena.Team team) => team == Arena.Team.B ? teamBScore : teamAScore;

            //private void SpawnBots(int amount)
            //{
            //    for (int i = 0; i < amount; i++)
            //    {
            //        ArenaAI.NPCSurvivalBot GetEntity() = CreateAIPlayer(TeamB.Spawns.GetSpawnPoint(), Settings);

            //        GetEntity().ResetPlayer();

            //        GetEntity().Event = this;
            //        GetEntity().Team = Arena.Team.B;

            //        eventPlayers.Add(GetEntity());
            //    }
            //}

        }
        
        private class NPCSurvivalPlayer : Arena.BaseEventPlayer
        {
            private NPCSurvivalEvent NPCSurvivalEvent
            {
                get
                {
                    return Event as NPCSurvivalEvent;
                }
            }

            public override void OnPlayerDeath(Arena.BaseEventPlayer attacker = null, float respawnTime = 5, HitInfo hitInfo = null)
            {
                string attackerName = attacker != null ? attacker.Player.displayName :
                                      hitInfo != null && hitInfo.InitiatorPlayer != null && hitInfo.InitiatorPlayer is ScientistNPC ? "NPC" : string.Empty;

                if (Deaths < NPCSurvivalEvent.playerLives)
                {
                    AddPlayerDeath(attacker);

                    _respawnDurationRemaining = respawnTime;

                    InvokeHandler.InvokeRepeating(this, RespawnTick, 1f, 1f);

                    DestroyUI();

                    string message = attackerName != null ? string.Format(Arena.Message("UI.Death.Killed", Player.userID), attackerName) :
                                     IsOutOfBounds ? Arena.Message("UI.Death.OOB", Player.userID) :
                                     Arena.Message("UI.Death.Suicide", Player.userID);

                    Arena.DisplayDeathScreen(this, message, true);

                }
                else
                {
                    AddPlayerDeath(attacker);

                    DestroyUI();

                    int position = NPCSurvivalEvent.GetRemainingPlayerCount();

                    string message = attackerName != null ? string.Format(GetMessage("Death.Killed", Player.userID), attackerName, ToOrdinal(position + 1), position) :
                                     IsOutOfBounds ? string.Format(GetMessage("Death.OOB", Player.userID), ToOrdinal(position + 1), position) :
                                     string.Format(GetMessage("Death.Suicide", Player.userID), ToOrdinal(position + 1), position);

                    Arena.DisplayDeathScreen(this, message, false);
                }
            }
        }

        #region NPC        
        public bool InitializeStates(BaseAIBrain customNPCBrain)
        {
            customNPCBrain.AddState(new BaseAIBrain.BaseIdleState());
            customNPCBrain.AddState(new NPCSurvivalBot.RoamState());
            customNPCBrain.AddState(new NPCSurvivalBot.ChaseState());

            return true;
        }

        public bool WantsToPopulateLoot(CustomScientistNPC npcSurvivalBot, NPCPlayerCorpse npcplayerCorpse) => true;

        public byte[] GetCustomDesign() => DESIGN;

        private static readonly byte[] DESIGN = new byte[] { 8, 1, 8, 2, 8, 3, 18, 61, 8, 0, 16, 1, 26, 25, 8, 0, 16, 1, 24, 0, 32, 0, 40, 0, 48, 0, 162, 6, 10, 13, 0, 0, 0, 0, 21, 0, 0, 128, 63, 26, 12, 8, 3, 16, 2, 24, 0, 32, 4, 40, 0, 48, 0, 26, 12, 8, 14, 16, 2, 24, 0, 32, 0, 40, 0, 48, 0, 32, 0, 18, 62, 8, 1, 16, 2, 26, 12, 8, 4, 16, 0, 24, 0, 32, 0, 40, 0, 48, 0, 26, 12, 8, 2, 16, 0, 24, 0, 32, 0, 40, 0, 48, 0, 26, 12, 8, 3, 16, 2, 24, 0, 32, 4, 40, 0, 48, 0, 26, 12, 8, 14, 16, 2, 24, 0, 32, 0, 40, 0, 48, 0, 32, 0, 18, 103, 8, 2, 16, 3, 26, 12, 8, 4, 16, 0, 24, 0, 32, 0, 40, 0, 48, 0, 26, 12, 8, 2, 16, 0, 24, 0, 32, 0, 40, 0, 48, 0, 26, 21, 8, 15, 16, 255, 255, 255, 255, 255, 255, 255, 255, 255, 1, 24, 1, 32, 0, 40, 0, 48, 0, 26, 21, 8, 5, 16, 255, 255, 255, 255, 255, 255, 255, 255, 255, 1, 24, 0, 32, 0, 40, 0, 48, 0, 26, 21, 8, 16, 16, 255, 255, 255, 255, 255, 255, 255, 255, 255, 1, 24, 0, 32, 0, 40, 0, 48, 0, 32, 0, 24, 0, 34, 14, 68, 101, 102, 97, 117, 108, 116, 32, 68, 101, 115, 105, 103, 110, 40, 0, 48, 0 };

        
        internal class NPCSurvivalBot : CustomScientistNPC, IAIAttack
        {
            internal NPCSurvivalEvent npcSurvivalEvent;

            public override void ServerInit()
            {
                base.ServerInit();

                InvokeRandomized(UpdateTargetsInMemory, 1f, 1f, 0.25f);
            }

            public new BaseEntity GetBestTarget()
            {
                BaseEntity target = null;
                float delta = -1f;
                foreach (Arena.BaseEventPlayer eventPlayer in npcSurvivalEvent.eventPlayers)
                {
                    if (!CanTargetEventPlayer(eventPlayer))
                        continue;

                    float distanceToTarget = Vector3.Distance(eventPlayer.transform.position, Transform.position);                    
                    float rangeDelta = 1f - Mathf.InverseLerp(1f, Brain.SenseRange, distanceToTarget);
                    float dot = Vector3.Dot((eventPlayer.transform.position - eyes.position).normalized, eyes.BodyForward());

                    rangeDelta += Mathf.InverseLerp(Brain.VisionCone, 1f, dot) / 2f;
                    rangeDelta += (Brain.Senses.Memory.IsLOS(eventPlayer.Player) ? 2f : 0f);

                    if (lastAttacker != eventPlayer && rangeDelta <= delta)
                        continue;

                    target = eventPlayer.Player;
                    delta = rangeDelta;
                }

                CurrentTarget = target;

                return CurrentTarget;
            }

            public override BaseCorpse CreateCorpse(PlayerFlags flagsOnDeath, Vector3 posOnDeath, Quaternion rotOnDeath, List<TriggerBase> triggersOnDeath, bool forceServerside = false)
            {
                NPCPlayerCorpse npcPlayerCorpse = DropCorpse("assets/prefabs/npc/scientist/scientist_corpse.prefab") as NPCPlayerCorpse;
                if (npcPlayerCorpse)
                {
                    npcPlayerCorpse.enableSaving = false;
                    npcPlayerCorpse.transform.position += (Vector3.down * NavAgent.baseOffset);
                    npcPlayerCorpse.SetFlag(Flags.Reserved2, true, false, true);
                    npcPlayerCorpse.TakeFrom(this, inventory.containerMain, inventory.containerWear, inventory.containerBelt );
                    npcPlayerCorpse.playerName = OverrideCorpseName;
                    npcPlayerCorpse.playerSteamID = userID;
                    npcPlayerCorpse.Spawn();
                    npcPlayerCorpse.TakeChildren(this);

                    for (int i = 0; i < npcPlayerCorpse.containers.Length; i++)
                        npcPlayerCorpse.containers[i].Clear();

                    npcPlayerCorpse.Invoke(npcPlayerCorpse.KillMessage, 3f);
                }

                return npcPlayerCorpse;
            }

            public override bool CanTargetBasePlayer(BasePlayer player)
            {
                if (player.IsSpectating() || player.limitNetworking)
                    return false;

                return base.CanTargetBasePlayer(player);
            }

            private void UpdateTargetsInMemory()
            {
                foreach (Arena.BaseEventPlayer eventPlayer in npcSurvivalEvent.eventPlayers)
                {
                    if (!CanTargetEventPlayer(eventPlayer))
                        continue;

                    Brain.Senses.Memory.SetKnown(eventPlayer.Player, this, null);
                }
            }

            public bool CanTargetEventPlayer(Arena.BaseEventPlayer eventPlayer) => eventPlayer == null || eventPlayer.IsDead || eventPlayer.Player.IsSpectating() ? false : true;

            public Vector3 PositionOfClosestPlayer()
            {
                Vector3 closestPosition = Transform.position;
                float closestDistance = float.MaxValue;

                foreach (Arena.BaseEventPlayer eventPlayer in npcSurvivalEvent.eventPlayers)
                {
                    if (eventPlayer == null || eventPlayer.IsDead || eventPlayer.Player.IsSpectating())
                        continue;

                    float distanceToTarget = Vector3.Distance(eventPlayer.transform.position, Transform.position);
                    if (distanceToTarget < closestDistance)
                    {
                        closestPosition = eventPlayer.transform.position;
                        closestDistance = distanceToTarget;
                    }
                }

                return closestPosition;
            }

            #region States
            public class RoamState : BaseAIBrain.BasicAIState
            {
                private StateStatus status = StateStatus.Error;

                public RoamState() : base(AIState.Roam) { }

                public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
                {
                    base.StateEnter(brain, entity);
                    status = StateStatus.Error;

                    if (brain.PathFinder == null)
                        return;

                    NPCSurvivalBot npcSurvivalBot = entity as NPCSurvivalBot;
                    
                    if (brain.Navigator.SetDestination(npcSurvivalBot.PositionOfClosestPlayer(), BaseNavigator.NavigationSpeed.Fast, 0f, 0f))
                    {
                        status = StateStatus.Running;
                        return;
                    }

                    status = StateStatus.Error;
                }

                public override void StateLeave(BaseAIBrain brain, BaseEntity entity)
                {
                    base.StateLeave(brain, entity);
                    Stop();
                }

                public override StateStatus StateThink(float delta, BaseAIBrain brain, BaseEntity entity)
                {
                    base.StateThink(delta, brain, entity);

                    if (status == StateStatus.Error)
                        return status;

                    if (brain.Navigator.Moving)
                        return StateStatus.Running;

                    return StateStatus.Finished;
                }

                private void Stop() => brain.Navigator.Stop();
            }

            public class ChaseState : BaseAIBrain.BasicAIState
            {
                private StateStatus status = StateStatus.Error;

                private float nextPositionUpdateTime;

                private float originalStopDistance;

                private bool unreachableLastUpdate;

                private NavMeshHit navmeshHit;

                public ChaseState() : base(AIState.Chase)
                {
                    AgrresiveState = true;
                }

                public override void StateLeave(BaseAIBrain brain, BaseEntity entity)
                {
                    base.StateLeave(brain, entity);

                    Stop();

                    brain.Navigator.StoppingDistance = originalStopDistance;
                }

                public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
                {
                    base.StateEnter(brain, entity);

                    status = StateStatus.Error;

                    if (brain.PathFinder == null)
                        return;

                    status = StateStatus.Running;
                    nextPositionUpdateTime = 0f;
                    originalStopDistance = brain.Navigator.StoppingDistance;

                    AttackEntity attackEntity = (entity as NPCSurvivalBot).CurrentWeapon;
                    if (attackEntity is BaseMelee)
                        brain.Navigator.StoppingDistance = 0.1f;

                    brain.Navigator.SetCurrentSpeed(BaseNavigator.NavigationSpeed.Fast);
                }

                private void Stop()
                {
                    brain.Navigator.Stop();
                    brain.Navigator.ClearFacingDirectionOverride();
                }

                public override StateStatus StateThink(float delta, BaseAIBrain brain, BaseEntity entity)
                {
                    base.StateThink(delta, brain, entity);
                    if (status == StateStatus.Error)
                        return status;

                    NPCSurvivalBot npcSurvivalBot = entity as NPCSurvivalBot;

                    BaseEntity baseEntity = brain.Events.Memory.Entity.Get(brain.Events.CurrentInputMemorySlot);
                    if (baseEntity == null || (baseEntity is BasePlayer && !npcSurvivalBot.CanTargetBasePlayer(baseEntity as BasePlayer)))
                    {
                        brain.Events.Memory.Entity.Remove(brain.Events.CurrentInputMemorySlot);
                        Stop();
                        return StateStatus.Error;
                    }

                    FaceTarget(npcSurvivalBot, baseEntity);
                                       
                    if (Time.time > nextPositionUpdateTime)
                    {
                        if (!(npcSurvivalBot.CurrentWeapon is BaseProjectile))
                        {
                            if (unreachableLastUpdate)
                            {
                                Vector3 position = GetRandomPositionAround(baseEntity.transform.position, 3f, 10f);
                                brain.Navigator.SetDestination(position, BaseNavigator.NavigationSpeed.Fast, 0.1f, 0f);
                                nextPositionUpdateTime = Time.time + 3f;
                                unreachableLastUpdate = false;

                                return StateStatus.Running;
                            }

                            brain.Navigator.SetDestination(baseEntity.transform.position, BaseNavigator.NavigationSpeed.Fast, 0.1f, 0f);

                            if (brain.Navigator.Agent.path.status > NavMeshPathStatus.PathComplete)
                                unreachableLastUpdate = true;

                            nextPositionUpdateTime = Time.time + 0.1f;

                            if (!brain.Navigator.Moving)
                                return StateStatus.Finished;
                        }
                        else
                        {
                            Vector3 position = GetRandomPositionAround(baseEntity.transform.position, 10f, npcSurvivalBot.EngagementRange() * 0.75f);

                            if (brain.Navigator.SetDestination(position, BaseNavigator.NavigationSpeed.Fast, 0f, 0f))
                                nextPositionUpdateTime = Random.Range(3f, 6f);
                        }
                    }

                    return StateStatus.Running;
                }

                private void FaceTarget(NPCSurvivalBot npcSurvivalBot, BaseEntity baseEntity)
                {
                    float distanceToTarget = Vector3.Distance(baseEntity.transform.position, npcSurvivalBot.Transform.position);

                    if (!(npcSurvivalBot.CurrentWeapon is BaseProjectile) && (brain.Senses.Memory.IsLOS(baseEntity) || distanceToTarget <= 10f))
                        brain.Navigator.SetFacingDirectionEntity(baseEntity);

                    else if (npcSurvivalBot.CurrentWeapon is BaseProjectile && brain.Senses.Memory.IsLOS(baseEntity))
                        brain.Navigator.SetFacingDirectionEntity(baseEntity);

                    else brain.Navigator.ClearFacingDirectionOverride();
                }
                               
                private Vector3 GetRandomPositionAround(Vector3 position, float minDistFrom = 0f, float maxDistFrom = 2f)
                {
                    if (maxDistFrom < 0f)
                        maxDistFrom = 0f;

                    Vector2 vector = Random.insideUnitCircle * maxDistFrom;
                    float x = Mathf.Clamp(Mathf.Max(Mathf.Abs(vector.x), minDistFrom), minDistFrom, maxDistFrom) * Mathf.Sign(vector.x);
                    float z = Mathf.Clamp(Mathf.Max(Mathf.Abs(vector.y), minDistFrom), minDistFrom, maxDistFrom) * Mathf.Sign(vector.y);
                    Vector3 random = position + new Vector3(x, 0f, z);

                    if (NavMesh.SamplePosition(position, out navmeshHit, 50f, brain.Navigator.Agent.areaMask))
                        random.y = navmeshHit.position.y;
                    else random.y = TerrainMeta.HeightMap.GetHeight(position);

                    return random;
                }
            }
            #endregion           
        }
        #endregion
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
                EventIcon = "https://www.rustedit.io/images/arena/arena_npcsurvival.png",
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
            ["Score.Name"] = "Kills",
            ["Round.Limit"] = "Round : {0} / {1}",
            ["Score.Players"] = "Players Alive : {0}",
            ["Score.NPCs"] = "NPCs Alive : {0}",
            ["Lives.Remain"] = "You have {0} lives remaining!",
            ["Death.Killed"] = "You were killed by {0}. You placed {1}",
            ["Death.OOB"] = "You wandered away... You placed {1}",
            ["Death.Suicide"] = "You killed yourself... You placed {1}"
        };
        #endregion
    }
}
