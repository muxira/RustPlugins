using CompanionServer;
using Facepunch;
using Facepunch.Math;
using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Ext.Chaos;
using Oxide.Ext.Chaos.Data;
using Oxide.Game.Rust.Cui;
using Oxide.Plugins.ArenaEx;
using ProtoBuf;
using Rust;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Random = UnityEngine.Random;
using Vector3Ex = UnityEngine.Vector3Ex;

using Chaos = Oxide.Ext.Chaos;

namespace Oxide.Plugins
{
    [Info("Arena", "k1lly0u", "2.1.9"), Description("The core mechanics for arena combat games")]
    public class Arena : ChaosPlugin
    {
        #region Fields  
        [PluginReference]
        private Plugin RotatingPickups;


        private int scrapItemId;

        private static Regex hexFilter;

        private static DropOnDeath dropOnDeath = DropOnDeath.Nothing;

        private readonly Hash<string, IEventPlugin> EventModes = new();

        private readonly Hash<string, BaseEventGame> ActiveEvents = new();

        private static Datafile<EventData> Events { get; set; }

        private static Datafile<Restoration> Restore { get; set; }

        private static Datafile<StatisticsData> Statistics { get; set; }

        
        private static LobbyHandler Lobby { get; set; }

        public static Arena Instance { get; private set; }

        public static bool IsUnloading { get; private set; }

        //private static Hash<uint, BaseEventGame.CustomNetworkGroup> m_CustomNetworkGroups = new Hash<uint, BaseEventGame.CustomNetworkGroup>();

        [Chaos.Permission]
        private const string ADMIN_PERMISSION = "arena.admin";

        [Chaos.Permission]
        private const string BLACKLISTED_PERMISSION = "arena.blacklisted";

        private const string RESPAWN_BAG_CMD = "respawn_sleepingbag";
        private const string REMOVE_BAG_CMD = "respawn_sleepingbag_remove";
        private const string RESPAWN_CMD = "respawn";

        private static readonly NetworkableId LOBBY_BAG_ID = new(113);
        #endregion

        #region Oxide Hooks
        private void Loaded()
        {
            Events = new Datafile<EventData>("Arena/event_data");
            
            Restore = new Datafile<Restoration>("Arena/restoration_data");

            Statistics = new Datafile<StatisticsData>("Arena/statistics_data");
            Statistics.Data.UpdateRankingScores();

            hexFilter = new Regex("^([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$");
                        
            Instance = this;

            IsUnloading = false;
        }

        private void OnServerInitialized()
        {
            if (!CheckDependencies())
                return;

            if (Configuration.Server.DisableServerEvents)
                ConVar.Server.events = false;

            if (!Configuration.Server.UseChat)
                Unsubscribe(nameof(OnPlayerChat));

            if (!Configuration.Event.AddToTeams)
            {
                Unsubscribe(nameof(OnTeamLeave));
                Unsubscribe(nameof(OnTeamDisband));
                Unsubscribe(nameof(OnTeamInvite));
                Unsubscribe(nameof(OnTeamCreate));
            }
            
            cmd.AddChatCommand(Configuration.Interface.Commands.EventCommand, this, CmdEventMenu);
            cmd.AddChatCommand(Configuration.Interface.Commands.StatisticsCommand, this, CmdEventStatistics);
            cmd.AddChatCommand(Configuration.Interface.Commands.AdminCommand, this, CmdEventAdmin);
            cmd.AddChatCommand(Configuration.Interface.Commands.LeaveCommand, this, CmdEventLeave);
            
            /*if (!Configuration.Event.CustomNetworkGroups)
                Unsubscribe(nameof(OnNetworkSubscriptionsGather));*/

            Lobby = new LobbyHandler(Configuration.Lobby.LobbySpawnfile);
            if (Lobby.IsEnabled)
                Lobby.SetupLobbyTeleporters();
            
            Restoration.Flags flags = Restoration.Flags.RestoreHealth | Restoration.Flags.RestoreInventory | Restoration.Flags.RestoreMetabolism;
            
            if (!Lobby.IsEnabled)
                flags |= Restoration.Flags.RestorePosition;
            
            if (Configuration.Event.AddToTeams)
                flags |= Restoration.Flags.RestoreTeam;
            
            Restore.Data.SetFlags(flags);

            scrapItemId = ItemManager.FindItemDefinition("scrap").itemid;
            dropOnDeath = ParseType<DropOnDeath>(Configuration.Event.DropOnDeath);

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                OnPlayerConnected(player);

            Debug.Log("[Arena] - Loading event games in 5 seconds...");
            timer.In(5, ()=>
            {
                RegisterImages();
                InitializeEvents();
            });
        }

        private void Unload()
        {
            IsUnloading = true;

            Restore.Save();
            
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                DestroyAllUI(player);

            foreach (BaseEventGame baseEventGame in ActiveEvents.Values)
                UnityEngine.Object.Destroy(baseEventGame);

            BaseEventPlayer[] eventPlayers = UnityEngine.Object.FindObjectsOfType<BaseEventPlayer>();
            for (int i = 0; i < eventPlayers?.Length; i++)
                UnityEngine.Object.DestroyImmediate(eventPlayers[i]);

            DestroyTeleporters();

            hexFilter = null;          
            Configuration = null;
            Instance = null;
            Lobby = null;

            Events = null;
            Restore = null;
            Statistics = null;
        }

        private void OnServerSave()
        {
            Restore.Save();
            Statistics.Save();
        }

        #endregion
        
        /*#region Custom Network Groups
        private object OnNetworkSubscriptionsGather(NetworkVisibilityGrid networkVisibilityGrid, Group group, List<Group> groups, int radius)
        {
            BaseEventGame.CustomNetworkGroup customNetworkGroup;
            
            if (m_CustomNetworkGroups.TryGetValue(group.ID, out customNetworkGroup))
            {
                groups.AddRange(customNetworkGroup.nearbyGroups);
                return true;
            }
            return null;
        }
        #endregion*/

        #region Player Connect/Disconnect
        private void OnPlayerConnected(BasePlayer player)
        {
            if (Configuration.Server.RestorePlayers)
                TryRestorePlayer(player);

            if (Lobby.ForceLobbyRespawn && player.IsDead())
            {
                NextTick(() => Lobby.SendRespawnOptions(player));
                return;
            }

            if (Lobby.ForceLobbyRespawn && Configuration.Lobby.KeepPlayersInLobby && !player.IsAdmin && !player.HasPermission(ADMIN_PERMISSION))
            {
                if (ZoneManager.IsLoaded && !ZoneManager.IsPlayerInZone(Configuration.Lobby.LobbyZoneID, player))
                    Lobby.TeleportToLobby(player);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)            
                eventPlayer.Event.LeaveEvent(player);            
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (Configuration.Server.RestorePlayers)
                TryRestorePlayer(player);
        }

        private void TryRestorePlayer(BasePlayer player)
        {
            if (player.IsSleeping() || player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(1f, () => OnPlayerConnected(player));
                return;
            }

            UnlockClothingSlots(player);

            if (Restore.Data.HasData(player.userID))
                Restore.Data.Restore(player);
        }

        private object OnDefaultItemsReceive(PlayerInventory playerInventory) => Lobby.ForceLobbyRespawn && !Configuration.Server.RestorePlayers ? true : null;
        #endregion

        #region Damage
        private void OnEntityTakeDamage(BaseEntity entity, HitInfo hitInfo)
        {
            if (!entity || hitInfo == null)
                return;

            BasePlayer player = entity.ToPlayer();

            if (player)
            {
                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer)
                {
                    eventPlayer.Event.OnPlayerTakeDamage(eventPlayer, hitInfo);                   
                }
            }
            else
            {
                BaseEventPlayer attacker = GetUser(hitInfo.InitiatorPlayer);
                if (attacker)
                {
                    if (attacker.Event.CanDealEntityDamage(attacker, entity, hitInfo))
                        return;

                    ClearDamage(hitInfo);
                }
            }
        }

        // TruePVE bypass
        private object CanEntityTakeDamage(BaseCombatEntity baseCombatEntity, HitInfo hitInfo)
        {
            if (!baseCombatEntity || !(baseCombatEntity is BasePlayer player))
                return null;

            return GetUser(player) ? true : null;
        }

        private object CanBeWounded(BasePlayer player, HitInfo hitInfo)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
                return false;
            return null;
        }

        private object OnPlayerDeath(BasePlayer player, HitInfo hitInfo)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
            {
                if (!eventPlayer.IsDead)
                    eventPlayer.Event.PrePlayerDeath(eventPlayer, hitInfo);
                return false;
            }
            return null;
        }

        private void OnEntityDeath(BasePlayer player, HitInfo hitInfo)
        {
            if (Lobby.ForceLobbyRespawn)
                NextTick(() => Lobby.SendRespawnOptions(player));
        }
        #endregion

        #region Spectate
        private object CanSpectateTarget(BasePlayer player, string name)
        {
            BaseEventPlayer eventPlayer = player.GetComponent<BaseEventPlayer>();
            if (eventPlayer && eventPlayer.Player.IsSpectating())
            {                
                eventPlayer.UpdateSpectateTarget();
                return false;
            }
            return null;
        }
        #endregion

        #region Spawned Entities
        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            BasePlayer player = planner?.GetOwnerPlayer();
            if (!player)
                return;

            BaseCombatEntity baseCombatEntity = gameObject?.ToBaseEntity() as BaseCombatEntity;
            if (baseCombatEntity == null)
                return;

            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
                eventPlayer.Event.OnEntityDeployed(baseCombatEntity);
        }

        private void OnItemDeployed(Deployer deployer, BaseCombatEntity baseCombatEntity)
        {
            BasePlayer player = deployer.GetOwnerPlayer();
            if (!player)
                return;

            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
                eventPlayer.Event.OnEntityDeployed(baseCombatEntity);
        }

        private object OnCreateWorldProjectile(HitInfo hitInfo, Item item)
        {
            if (hitInfo == null)
                return null;

            if (hitInfo.InitiatorPlayer != null)
            {
                BaseEventPlayer eventPlayer = GetUser(hitInfo.InitiatorPlayer);
                if (eventPlayer)
                    return false;
            }

            if (hitInfo.HitEntity?.ToPlayer() != null)
            {
                BaseEventPlayer eventPlayer = GetUser(hitInfo.HitEntity.ToPlayer());
                if (eventPlayer)
                    return false;
            }

            return null;
        }

        private void OnItemDropped(Item item, WorldItem worldItem)
        {
            BasePlayer player = item.GetOwnerPlayer();
            if (player != null)
            {
                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer)
                {
                    eventPlayer.Event.OnWorldItemDropped(worldItem);
                }
            }
        }
        #endregion

        #region Items
        private object CanDropActiveItem(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer && (!eventPlayer.Event.CanDropActiveItem() || player.health <= 0f))
                return false;
            return null;
        }

        private string CanOpenBackpack(BasePlayer player, ulong backpackOwnerID)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
                return "You can not open your backpack during an event";
            return null;
        }                
        #endregion

        #region Command Blacklist
        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            BaseEventPlayer eventPlayer = GetUser(player);

            if (!player || player.IsAdmin || !eventPlayer)
                return null;

            if (Configuration.Event.CommandBlacklist.Any(x => x.StartsWith("/") ? x.Substring(1).Equals(command, StringComparison.OrdinalIgnoreCase) : x.Equals(command, StringComparison.OrdinalIgnoreCase)))
            {
                SendReply(player, Message("Error.CommandBlacklisted", player.userID));
                return false;
            }
            return null;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!player || !player.IsConnected)
                return null;

            if (Lobby.ForceLobbyRespawn && (player.IsDead() || player.IsSpectating()))
            {
                if (arg.cmd.Name == RESPAWN_CMD)
                {
                    Lobby.RespawnAtLobby(player);
                    return false;
                }

                if (arg.cmd.Name == REMOVE_BAG_CMD)
                {
                    NetworkableId num = arg.GetEntityID(0);
                    if (num == LOBBY_BAG_ID)
                        return false;
                }

                if (arg.cmd.Name == RESPAWN_BAG_CMD)
                {
                    NetworkableId num = arg.GetEntityID(0);
                    if (num == LOBBY_BAG_ID)
                    {
                        Lobby.RespawnAtLobby(player);
                        return false;
                    }
                }
            }

            BaseEventPlayer eventPlayer = GetUser(player);

            if (player.IsAdmin || !eventPlayer || arg.Args == null)
                return null;

            if (Configuration.Event.CommandBlacklist.Any(x => arg.cmd.FullName.Equals(x, StringComparison.OrdinalIgnoreCase)))
            {
                SendReply(player, Message("Error.CommandBlacklisted", player.userID));
                return false;
            }
            return null;
        }
        #endregion

        #region Chat Handler
        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (!player || player.IsAdmin || player.HasPermission("arena.admin"))
                return null;

            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
            {
                eventPlayer.Event.BroadcastToPlayers(player, message);
            }
            else
            {
                foreach (BasePlayer otherPlayer in BasePlayer.activePlayerList)
                {
                    if (!GetUser(otherPlayer))
                        otherPlayer.SendConsoleCommand("chat.add", 0, player.UserIDString, $"<color={(player.IsAdmin ? "#aaff55" : player.IsDeveloper ? "#fa5" : "#55AAFF")}>{player.displayName}</color>: {message}");
                }
            }
               
            return false;
        }
        #endregion

        #region Teams
        private object OnTeamLeave(RelationshipManager.PlayerTeam playerTeam, BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (!eventPlayer)
                return null;
            
            if (eventPlayer.Event.Plugin.IsTeamEvent)            
                return true;
            
            return null;
        }

        private object OnTeamDisband(RelationshipManager.PlayerTeam playerTeam)
        {
            foreach (BaseEventGame baseEventGame in ActiveEvents.Values)
            {
                if (baseEventGame.IsEventTeam(playerTeam.teamID))
                    return true;                
            }
            return null;
        }

        private object OnTeamInvite(BasePlayer player, BasePlayer other)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
                return true;
            return null;
        }

        private object OnTeamCreate(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
                return true;
            return null;
        }
        #endregion

        #region Event Construction
        public static void RegisterEvent(string eventName, IEventPlugin plugin) => Instance.EventModes[eventName] = plugin;
        
        public static void UnregisterEvent(string eventName)
        {
            Instance.EventModes.Remove(eventName);

            for (int i = Instance.ActiveEvents.Count - 1; i >= 0; i--)
            {
                KeyValuePair<string, BaseEventGame> kvp = Instance.ActiveEvents.ElementAt(i);

                if (kvp.Value.Config.EventType.Equals(eventName))
                {
                    kvp.Value.EndEvent();
                    UnityEngine.Object.Destroy(kvp.Value);
                    Instance.ActiveEvents.Remove(kvp.Key);
                }
            }
        }

        private void InitializeEvents()
        {
            foreach (string eventName in Events.Data.events.Keys)
            {
                object success = OpenEvent(eventName);
                if (success is string s)
                    Debug.LogWarning($"[Arena] - {s}");
            }            
        }

        private object OpenEvent(string eventName)
        {
            if (Events.Data.events.TryGetValue(eventName, out EventConfig eventConfig))
            {
                if (!string.IsNullOrEmpty(eventConfig.Permission) && !permission.PermissionExists(eventConfig.Permission))
                    permission.RegisterPermission(eventConfig.Permission, this);

                if (eventConfig.IsDisabled)
                    return $"The event {eventName} is disabled";

                if (!EventModes.TryGetValue(eventConfig.EventType, out IEventPlugin iEventPlugin) || iEventPlugin == null)
                    return $"Unable to find event plugin for game mode: {eventConfig.EventType}";

                object success = ValidateEventConfig(eventConfig);
                if (success is string s)
                    return $"Failed to open event {eventName} : {s}";

                if (!iEventPlugin.InitializeEvent(eventConfig))
                    return $"The event {eventName} is already active";
                return null;
            }

            return "Failed to find a event with the specified name";
        }        

        public static bool InitializeEvent<T>(IEventPlugin plugin, EventConfig config) where T : BaseEventGame
        {
            if (Instance.ActiveEvents.ContainsKey(config.EventName))
                return false;

            BaseEventGame eventGame = new GameObject(config.EventName).AddComponent<T>();
            eventGame.InitializeEvent(plugin, config);

            Instance.ActiveEvents[config.EventName] = eventGame;
            return true;
        }

        private static void ShutdownEvent(string eventName)
        {
            if (Instance.ActiveEvents.TryGetValue(eventName, out BaseEventGame baseEventGame))
            {
                Instance.ActiveEvents.Remove(eventName);
                UnityEngine.Object.Destroy(baseEventGame);
            }
        }
        #endregion

        #region Functions
        private BaseEventGame FindEvent(string name)
        {
            ActiveEvents.TryGetValue(name, out BaseEventGame baseEventGame);
            return baseEventGame;
        }

        private IEventPlugin GetPlugin(string name)
        {
            if (EventModes.TryGetValue(name, out IEventPlugin eventPlugin))
                return eventPlugin;

            return null;
        }

        private bool CheckDependencies()
        {
            if (!Spawns.IsLoaded)
            {
                PrintError("Unable to load Arena - Spawns database not found. Please download Spawns database to continue");
                NextFrame(() => Interface.Oxide.UnloadPlugin("Arena"));
                //Interface.Oxide.UnloadPlugin("Arena");
                return false;
            }

            if (!ZoneManager.IsLoaded)
                PrintError("ZoneManager is not installed! Unable to restrict event players to zones");

            if (!Kits.IsLoaded)
                PrintError("Kits is not installed! Unable to issue any weapon kits");

            return true;
        }

        private void TeleportPlayer(BasePlayer player, string spawnFile, bool sleep = false)
        {
            player.ResetMetabolism();

            object spawnPoint = Spawns.IsLoaded ? Spawns.GetRandomSpawn(spawnFile) : null;
            if (spawnPoint is Vector3 point)            
                player.Teleport(point, Quaternion.identity,  sleep);            
        }

        private static void Broadcast(string key, params object[] args)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                player?.SendConsoleCommand("chat.add", 0, Configuration.Message.ChatIcon, string.Format(Message(key, player.userID), args)); 
        }

        private static void GetEventsOfType(string eventType, List<BaseEventGame> list)
        {
            foreach (BaseEventGame eventGame in Instance.ActiveEvents.Values)
            {
                if (eventGame.Config.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                    list.Add(eventGame);
            }
        }

        private static bool HasActiveEventsOfType(string eventType)
        {
            foreach (BaseEventGame eventGame in Instance.ActiveEvents.Values)
            {
                if (eventGame.Config.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void GetRegisteredEvents(List<IEventPlugin> list)
        {
            list.AddRange(Instance.EventModes.Values);
            list.Sort((a, b) => a.EventName.CompareTo(b.EventName));
        }
        private static bool IsValidHex(string s) => hexFilter.IsMatch(s);
        #endregion

        #region Classes and Components  
        public abstract class BaseEventGame : MonoBehaviour
        {
            public IEventPlugin Plugin { get; private set; }

            public EventConfig Config { get; private set; }

            public EventStatus Status { get; private set; }

            protected GameTimer Timer { get; private set; }

            public RewardType RewardType { get; private set; }


            protected CuiElementContainer scoreContainer = null;


            public List<BaseEventPlayer> eventPlayers = Pool.Get<List<BaseEventPlayer>>();

            public List<BaseEventPlayer> joiningSpectators = Pool.Get<List<BaseEventPlayer>>();


            public List<ScoreEntry> scoreData = Pool.Get<List<ScoreEntry>>();

            private List<BaseCombatEntity> _deployedObjects = Pool.Get<List<BaseCombatEntity>>();

            private List<DroppedItemContainer> _droppedInventories = Pool.Get<List<DroppedItemContainer>>();

            private List<WorldItem> _droppedItems = Pool.Get<List<WorldItem>>();

            private List<PlayerCorpse> _droppedCorpses = Pool.Get<List<PlayerCorpse>>();


            private readonly Hash<string, List<KeyValuePair<string, ulong>>> _kitBeltItems = new();

            protected readonly HashSet<BaseEventPlayer> spectateTargets = new();
            
            public static readonly Hash<string, BaseEventGame> OccupiedZones = new();


            public HashSet<BaseEventPlayer> SpectateTargets => spectateTargets;


            //public CustomNetworkGroup customNetworkGroup;
            
            private EventTeleporter _eventTeleporter;

            private bool _isClosed;

            protected int _roundNumber;

            public int RoundNumber => _roundNumber;

            public EventTeam TeamA { get; private set; }

            public EventTeam TeamB { get; private set; }

            
            protected bool GodmodeEnabled { get; set; } = true;

            private EventResults LastEventResult { get; set; } = new();

            public string EventInformation
            {
                get
                {
                    string str = string.Format(Message("Info.Event.Current"), Config.EventName, Config.EventType);
                    str += string.Format(Message("Info.Event.Player"), eventPlayers.Count, Config.MaximumPlayers);
                    return str;
                }
            }

            public string EventStatus => string.Format(Message("Info.Event.Status"), Status);

            #region Initialization and Destruction             
            protected virtual void OnDestroy()
            {
                CleanupEntities();

                StopAllSpectating();

                for (int i = eventPlayers.Count - 1; i >= 0; i--)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    LeaveEvent(eventPlayer);
                }

                for (int i = joiningSpectators.Count - 1; i >= 0; i--)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    LeaveEvent(eventPlayer);
                }

                Pool.FreeUnmanaged(ref scoreData);
                Pool.FreeUnmanaged(ref _deployedObjects);
                Pool.FreeUnmanaged(ref _droppedItems);
                Pool.FreeUnmanaged(ref _droppedInventories);
                Pool.FreeUnmanaged(ref _droppedCorpses);
                Pool.FreeUnmanaged(ref eventPlayers);
                Pool.FreeUnmanaged(ref joiningSpectators);

                spectateTargets.Clear();

                TeamA?.Destroy();
                TeamB?.Destroy();

                Timer?.StopTimer();

                DestroyTeleporter();

                Destroy(gameObject);
            }

            public virtual void InitializeEvent(IEventPlugin plugin, EventConfig config)
            {
                Config = config;
                Plugin = Config.Plugin = plugin;

                TeamA = new EventTeam(Team.A, config.TeamConfigA.Color, config.TeamConfigA.Clothing, new SpawnSelector(config.TeamConfigA.Spawnfile));

                if (plugin.IsTeamEvent)
                    TeamB = new EventTeam(Team.B, config.TeamConfigB.Color, config.TeamConfigB.Clothing, new SpawnSelector(config.TeamConfigB.Spawnfile));
                    
                Timer = new GameTimer(this);

                GodmodeEnabled = true;

                RewardType = ParseType<RewardType>(Config.Rewards.Type);

                Status = Arena.EventStatus.Open;

                /*if (Configuration.Event.CustomNetworkGroups)
                    SetupCustomNetworkGroup();*/

                CreateTeleporter();
            }

            /*public class CustomNetworkGroup
            {
                public Group group;
                public List<Group> nearbyGroups;
            }
            
            private void SetupCustomNetworkGroup()
            {
                if (!Instance.ZoneManager)
                {
                    Debug.LogError($"[Arena] Tried creating custom network group for event {Config.EventName} but ZoneManager is not available");
                    return;
                }
                
                if (string.IsNullOrEmpty(Config.ZoneID))
                {
                    Debug.LogError($"[Arena] Tried creating custom network group for event {Config.EventName} but it does not have a zone set");
                    return;
                }

                Vector3 position = Instance.ZoneManager.Call<Vector3>("GetZoneLocation", Config.ZoneID);
                float radius = Instance.ZoneManager.Call<float>("GetZoneRadius", Config.ZoneID);

                customNetworkGroup = new CustomNetworkGroup()
                {
                    group = Net.sv.visibility.GetGroup(position),
                    nearbyGroups = new List<Group>()
                };

                Group closestGroup = Net.sv.visibility.GetGroup(position);
                
                NetworkVisibilityGrid networkVisibilityGrid = Net.sv.visibility.provider as NetworkVisibilityGrid;
                
                typeof(NetworkVisibilityGrid).GetMethod("GetVisibleFrom", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(networkVisibilityGrid, new object[]{closestGroup, customNetworkGroup.nearbyGroups, radius});

                m_CustomNetworkGroups[customNetworkGroup.group.ID] = customNetworkGroup;
            }*/

            public List<KeyValuePair<string, ulong>> GetItemsForKit(string kit)
            {
                if (!_kitBeltItems.TryGetValue(kit, out List<KeyValuePair<string, ulong>> list))
                {
                    JObject obj = Kits.IsLoaded ? Kits.GetKitObject(kit) : null;
                    if (obj == null)
                    {
                        Debug.LogError($"[Arena] - Kits failed to return data for kit : {kit}. Is this a valid kit name?");
                        return null;
                    }

                    list = _kitBeltItems[kit] = new List<KeyValuePair<string, ulong>>();

                    JArray jArray = obj["BeltItems"] as JArray;

                    foreach (JObject itemObj in jArray)
                        list.Add(new KeyValuePair<string, ulong>((string)itemObj["Shortname"], (ulong)itemObj["SkinID"]));

                }
                return list;
            }
            #endregion

            #region Event Management

            public virtual void CloseEvent()
            {
                _isClosed = true;
                BroadcastToPlayers("Notification.EventClosed");
            }
                        
            protected virtual void StartEvent()
            {
                if (joiningSpectators.Count > 0)
                {
                    joiningSpectators.ForEach(eventPlayer => eventPlayers.Add(eventPlayer));
                    joiningSpectators.Clear();
                }

                if (!HasMinimumRequiredPlayers())
                {
                    BroadcastToPlayers("Notification.NotEnoughToStart");
                    EndEvent();
                    return;
                }

                //if (Config.UseEventBots && Config.MaximumBots > 0 && eventPlayers.Count < Config.MaximumPlayers)                
                //    SpawnEventBots();

                _roundNumber = 0;

                LastEventResult.UpdateFromEvent(this);

                Timer.StopTimer();

                UpdateScoreboard();

                Status = Arena.EventStatus.Started;

                StartNextRound();

                SetZoneOccupied(this, true);
            }
                       
            protected virtual void StartNextRound()
            {
                if (!HasMinimumRequiredPlayers())
                {
                    BroadcastToPlayers("Notification.NotEnoughToContinue");
                    EndEvent();
                    return;
                }

                CleanupEntities();

                _roundNumber += 1;

                if (Config.TimeLimit > 0)
                    Timer.StartTimer(Config.TimeLimit, string.Empty, EndRound);

                GodmodeEnabled = false;

                if (CanEnterBetweenRounds())
                {
                    joiningSpectators.ForEach(eventPlayer => eventPlayers.Add(eventPlayer));
                    joiningSpectators.Clear();
                }

                //if (Config.UseEventBots && Config.MaximumBots > 0)
                //{
                //    if (eventPlayers.Count > Config.MaximumPlayers)
                //        RemoveExcessBots();
                //    else if (eventPlayers.Count < Config.MaximumPlayers)
                //        SpawnEventBots();
                //}

                eventPlayers.ForEach(eventPlayer =>
                {
                    if (!eventPlayer || !eventPlayer.Player)
                        return;

                    if (CanRespawnPlayer(eventPlayer))
                    {
                        if (eventPlayer.IsDead)
                            RespawnPlayer(eventPlayer);
                        else
                        {
                            ResetPlayer(eventPlayer.Player);
                            OnPlayerRespawn(eventPlayer);
                        }
                    }
                });

                RebuildSpectateTargets();
                UpdateSpectatorTargets();

                UpdateScoreboard();
            }

            protected virtual bool CanRespawnPlayer(BaseEventPlayer baseEventPlayer) => true;

            protected virtual void EndRound()
            {
                UpdateScoreboard();

                if (_roundNumber >= Config.RoundsToPlay)
                {
                    BroadcastToPlayers("Notification.EventFinished");
                    InvokeHandler.Invoke(this, EndEvent, 1f);
                }
                else
                {
                    GodmodeEnabled = true;

                    Timer.StopTimer();

                    LastEventResult.UpdateFromEvent(this);

                    if (Plugin.ProcessWinnersBetweenRounds)
                        ProcessWinners();
                                       
                    eventPlayers.ForEach(eventPlayer =>
                    {
                        if (!eventPlayer || !eventPlayer.Player)
                            return;

                        eventPlayer.ResetStatistics();

                        if (!CanRespawnPlayer(eventPlayer))
                            return;

                        if (eventPlayer.IsDead)
                        {
                            eventPlayer.OnRoundFinished();
                            RespawnPlayer(eventPlayer);
                        }

                        Statistics.Data.OnGamePlayed(eventPlayer.Player, Config.EventType);
                    });

                    Statistics.Data.OnGamePlayed(Config.EventType);

                    if (CanEnterBetweenRounds())
                    {
                        joiningSpectators.ForEach(eventPlayer =>
                        {
                            eventPlayers.Add(eventPlayer);
                            ResetPlayer(eventPlayer.Player);
                            OnPlayerRespawn(eventPlayer);
                        });

                        joiningSpectators.Clear();
                    }

                    RebuildSpectateTargets();
                    UpdateSpectatorTargets();

                    BroadcastToPlayers("Notification.NextRoundStartsIn", _roundNumber, Configuration.Timer.RoundInterval);
                    Timer.StartTimer(Configuration.Timer.RoundInterval, Message("Timer.NextRoundStartsIn"), StartNextRound);
                }
            }

            public virtual void EndEvent()
            {
                Timer.StopTimer();

                bool wasPlaying = Status == Arena.EventStatus.Started;
                
                Status = Arena.EventStatus.Finished;

                if (Configuration.Event.LockZonesToEvent && !string.IsNullOrEmpty(Config.ZoneID))
                    OccupiedZones.Remove(Config.ZoneID);

                GodmodeEnabled = true;

                LastEventResult.UpdateFromEvent(this);

                CleanupEntities();

                SetZoneOccupied(this, true);

                if (!IsUnloading && wasPlaying)
                    ProcessWinners();

                StopAllSpectating();

                eventPlayers.ForEach(eventPlayer =>
                {
                    if (!eventPlayer || !eventPlayer.Player)                    
                        return;
                    
                    eventPlayer.ResetStatistics();

                    if (eventPlayer is NPCEventPlayer)
                    {
                        eventPlayer.Player.Kill();
                        return;
                    }

                    if (eventPlayer.IsDead)
                        RespawnPlayer(eventPlayer);

                    if (!IsUnloading)
                        Statistics.Data.OnGamePlayed(eventPlayer.Player, Config.EventType);
                });

                if (!IsUnloading)
                {
                    Statistics.Data.OnGamePlayed(Config.EventType);

                    if (Configuration.Event.StartOnFinish)
                    {
                        BroadcastToPlayers("Notification.NextEventStartsIn", Configuration.Timer.RoundInterval);
                        Timer.StartTimer(Configuration.Timer.RoundInterval, Message("UI.NextGameStartsIn"), StartEvent);
                    }
                    else
                    {
                        EjectAllPlayers();

                        if (joiningSpectators.Count > 0)                        
                            InsertJoiningSpectators();
                        
                        if (eventPlayers.Count == 0 && joiningSpectators.Count == 0)
                            UnlockEventZone();
                    }

                    RebuildSpectateTargets();
                }
            }

            public bool IsEventLockedOut()
            {
                if (!Configuration.Event.LockZonesToEvent || string.IsNullOrEmpty(Config.ZoneID))
                    return false;

                if (!OccupiedZones.TryGetValue(Config.ZoneID, out BaseEventGame eventGame))
                    return false;
                
                return eventGame != this;
            }

            public void LockEventZone()
            {
                if (!Configuration.Event.LockZonesToEvent || string.IsNullOrEmpty(Config.ZoneID))
                    return;
                
                if (OccupiedZones.TryGetValue(Config.ZoneID, out BaseEventGame eventGame) && eventGame != this)
                    return;
                
                OccupiedZones[Config.ZoneID] = this;
            }
            
            public void UnlockEventZone()
            {
                if (!Configuration.Event.LockZonesToEvent || string.IsNullOrEmpty(Config.ZoneID))
                    return;
                
                if (!OccupiedZones.TryGetValue(Config.ZoneID, out BaseEventGame eventGame) || eventGame != this)
                    return;
                
                OccupiedZones.Remove(Config.ZoneID);
            }
            #endregion

            #region Player Management

            public bool CanJoinEvent(BasePlayer player)
            {
                if (IsEventLockedOut())
                {
                    player.LocalizedMessage(Instance, "Notification.LockedOut");
                    return false;
                }
                
                if ((GetActualPlayerCount() + joiningSpectators.Count) >= Config.MaximumPlayers)
                {
                    player.LocalizedMessage(Instance, "Notification.MaximumPlayers");
                    return false;
                }

                if (!string.IsNullOrEmpty(Config.Permission) && !player.HasPermission(Config.Permission))
                {
                    player.LocalizedMessage(Instance, "Error.NoPermission");
                    return false;
                }

                object isEventPlayer = Interface.CallHook("isEventPlayer", player);
                if (isEventPlayer != null)
                {
                    player.LocalizedMessage(Instance, "Error.IsAnotherEvent");
                    return false;
                }
                
                string str = CanJoinEvent();
                if (!string.IsNullOrEmpty(str))
                {
                    player.ChatMessage(str);
                    return false;
                }

                return true;
            }

            protected virtual string CanJoinEvent()
            {
                return string.Empty;
            }

            protected virtual bool CanEnterBetweenRounds() => true;

            protected virtual bool CanEnterDuringRound() => !_isClosed;

            public virtual void JoinEvent(BasePlayer player, Team team = Team.None)
            {
                if (Status == Arena.EventStatus.Started && !CanEnterDuringRound())                
                    CreateSpectatorPlayer(player, team);
                else
                {
                    CreateEventPlayer(player, team);

                    if (eventPlayers.Count == 1)
                        LockEventZone();
                }
                
                if (Configuration.Message.BroadcastJoinersGlobal)
                    BroadcastToServer("Notification.PlayerJoined", player.displayName, Config.EventName);

                if (Configuration.Message.BroadcastJoiners)
                    BroadcastToPlayers("Notification.PlayerJoined", player.displayName, Config.EventName);
                
                if (Status is Arena.EventStatus.Open or Arena.EventStatus.Finished)
                {
                    if (HasMinimumRequiredPlayers())
                    {
                        Status = Arena.EventStatus.Prestarting;
                        Timer.StartTimer(Configuration.Timer.Prestart, Message("Notification.RoundStartsIn"), StartEvent);
                    }
                    else BroadcastToPlayers("Notification.WaitingForPlayers", Config.MinimumPlayers - eventPlayers.Count);
                }
            }

            public virtual void LeaveEvent(BasePlayer player)
            {                
                BaseEventPlayer eventPlayer = GetUser(player);
                if (!eventPlayer)
                    return;

                if (eventPlayer is NPCEventPlayer npcEventPlayer)
                {
                    LeaveEvent(npcEventPlayer);
                }
                else
                {
                    LeaveEvent(eventPlayer);

                    if (Configuration.Message.BroadcastLeaversGlobal)
                        BroadcastToServer("Notification.PlayerLeft", player.displayName, Config.EventName);
                    
                    if (Configuration.Message.BroadcastLeavers)
                        BroadcastToPlayers("Notification.PlayerLeft", player.displayName, Config.EventName);
                }
            }

            private void LeaveEvent(NPCEventPlayer eventPlayer)
            {
                eventPlayers.Remove(eventPlayer);

                eventPlayer.Player.StripInventory();

                DestroyImmediate(eventPlayer);
            }

            public void LeaveEvent(BaseEventPlayer eventPlayer)
            {
                BasePlayer player = eventPlayer.Player;

                if (eventPlayer.IsDead || player.IsSpectating())
                {
                    ResetPlayer(eventPlayer.Player);
                    player.Teleport(eventPlayer.Team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), Quaternion.identity,  false);
                }

                player.StripInventory();

                player.ResetMetabolism();

                if (!string.IsNullOrEmpty(Config.ZoneID) && ZoneManager.IsLoaded)
                    ZoneManager.RemovePlayerFromZoneWhitelist(Config.ZoneID, player);

                if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams && Configuration.Event.AddToTeams)
                {
                    EventTeam eventTeam = eventPlayer.Team == Team.A ? TeamA : TeamB;
                    eventTeam.RemoveFromTeam(player);
                }

                eventPlayers.Remove(eventPlayer);
                joiningSpectators.Remove(eventPlayer);

                RebuildSpectateTargets();

                UpdateSpectatorTargets(eventPlayer);

                DestroyImmediate(eventPlayer);                

                if (!player.IsConnected || player.IsSleeping() || IsUnloading)
                    player.Die();
                else
                {
                    if (Configuration.Server.RestorePlayers)
                        Restore.Data.Restore(player);
                    
                    if (Lobby.IsEnabled)
                        Lobby.ReturnToLobby(player);
                }

                if (Status != Arena.EventStatus.Finished && (!HasMinimumRequiredPlayers() || GetActualPlayerCount() == 0))
                {
                    BroadcastToPlayers("Notification.NotEnoughToContinue");
                    EndEvent();
                    return;
                }
                
                if (eventPlayers.Count == 0 && joiningSpectators.Count == 0)
                    UnlockEventZone();
            }

            private void InsertJoiningSpectators()
            {
                joiningSpectators.ForEach(eventPlayer =>
                {
                    eventPlayers.Add(eventPlayer);

                    if (Configuration.Message.BroadcastJoinersGlobal)
                        BroadcastToServer("Notification.PlayerJoined", eventPlayer.Player.displayName, Config.EventName);
                    
                    if (Configuration.Message.BroadcastJoiners)
                        BroadcastToPlayers("Notification.PlayerJoined", eventPlayer.Player.displayName, Config.EventName);

                    if (eventPlayer.Player.IsSpectating())
                    {
                        ResetPlayer(eventPlayer.Player);
                        OnPlayerRespawn(eventPlayer);
                    }
                });

                joiningSpectators.Clear();

                if (HasMinimumRequiredPlayers())
                {
                    Status = Arena.EventStatus.Prestarting;
                    Timer.StartTimer(Configuration.Timer.Prestart, Message("Notification.RoundStartsIn"), StartEvent);
                }
            }

            protected virtual void CreateEventPlayer(BasePlayer player, Team team = Team.None)
            {
                if (!player)
                    return;

                Restore.Data.Store(player);
                
                BaseEventPlayer eventPlayer = AddPlayerComponent(player);

                eventPlayer.ResetPlayer();

                eventPlayer.Event = this;

                eventPlayer.Team = team;

                if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams && Configuration.Event.AddToTeams)
                {
                    EventTeam eventTeam = team == Team.A ? TeamA : TeamB;
                    player.RemoveFromCurrentTeam();
                    eventTeam.AddToTeam(player);                    
                }

                eventPlayers.Add(eventPlayer);

                if (!Config.AllowClassSelection || GetAvailableKits(eventPlayer.Team).Count == 1)
                    eventPlayer.Kit = GetAvailableKits(eventPlayer.Team).First();

                SpawnPlayer(eventPlayer, Configuration.Event.GiveKitsWhileWaiting || Status == Arena.EventStatus.Started, true);

                if (!string.IsNullOrEmpty(Config.ZoneID))
                    ZoneManager.AddPlayerToZoneWhitelist(Config.ZoneID, player);
            }

            protected virtual Team GetSpectatingTeam(Team currentTeam) => currentTeam == Team.A ? Team.A : Team.B;

            protected virtual void CreateSpectatorPlayer(BasePlayer player, Team team = Team.None)
            {
                if (!player)
                    return;

                Restore.Data.Store(player);
                
                BaseEventPlayer eventPlayer = AddPlayerComponent(player);

                eventPlayer.ResetPlayer();

                eventPlayer.Event = this;

                eventPlayer.Team = team;

                if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams && Configuration.Event.AddToTeams)
                {
                    EventTeam eventTeam = GetSpectatingTeam(team) == Team.A ? TeamA : TeamB;
                    player.RemoveFromCurrentTeam();
                    eventTeam.AddToTeam(player);
                }

                joiningSpectators.Add(eventPlayer);

                if (!Config.AllowClassSelection || GetAvailableKits(eventPlayer.Team).Count == 1)
                    eventPlayer.Kit = GetAvailableKits(team).First();

                eventPlayer.Player.GetMounted()?.AttemptDismount(eventPlayer.Player);

                if (eventPlayer.Player.HasParent())
                    eventPlayer.Player.SetParent(null, true, true);

                player.StripInventory();

                player.ResetMetabolism();

                player.Teleport(eventPlayer.Team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), Quaternion.identity,  false);

                if (!string.IsNullOrEmpty(Config.ZoneID))
                    ZoneManager.AddPlayerToZoneWhitelist(Config.ZoneID, player);

                eventPlayer.BeginSpectating();

                UpdateScoreboard(eventPlayer);
                 
                BroadcastToPlayer(eventPlayer, Message("Notification.JoinerSpectate", player.userID));

                ShowHelpText(eventPlayer);
            }

            protected virtual Team GetPlayerTeam() => Team.None;

            protected virtual BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.GetComponent<BaseEventPlayer>() ?? player.gameObject.AddComponent<BaseEventPlayer>();

            public virtual void OnPlayerRespawn(BaseEventPlayer baseEventPlayer)
            {
                SpawnPlayer(baseEventPlayer, Configuration.Event.GiveKitsWhileWaiting || Status == Arena.EventStatus.Started);
            }

            public void SpawnPlayer(BaseEventPlayer eventPlayer, bool giveKit = true, bool sleep = false)
            {
                if (!eventPlayer|| !eventPlayer.Player)
                    return;

                if (eventPlayer.Player.IsSpectating())
                    eventPlayer.FinishSpectating();

                eventPlayer.Player.GetMounted()?.AttemptDismount(eventPlayer.Player);
                                
                if (eventPlayer.Player.HasParent())
                    eventPlayer.Player.SetParent(null, true);

                eventPlayer.Player.StripInventory();

                eventPlayer.Player.ResetMetabolism();

                eventPlayer.Player.Teleport(eventPlayer.Team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), Quaternion.identity, sleep);

                if (string.IsNullOrEmpty(eventPlayer.Kit) && !(eventPlayer is NPCEventPlayer))
                {
                    eventPlayer.ForceSelectClass();
                    DisplayDeathScreen(eventPlayer, Message("UI.SelectClass", eventPlayer.Player.userID), true);
                    return;
                }

                UpdateScoreboard(eventPlayer);

                if (giveKit)
                {
                    Instance.NextTick(() =>
                    {
                        if (eventPlayer && CanGiveKit(eventPlayer))
                        {
                            GiveKit(eventPlayer.Player, eventPlayer.Kit);
                            OnKitGiven(eventPlayer);
                        }
                    });
                }

                eventPlayer.ApplyInvincibility();

                OnPlayerSpawned(eventPlayer);

                RebuildSpectateTargets();

                UpdateSpectatorTargets();

                ShowHelpText(eventPlayer);
            }

            protected virtual void OnPlayerSpawned(BaseEventPlayer eventPlayer) { }

            private void EjectAllPlayers()
            {
                for (int i = eventPlayers.Count - 1; i >= 0; i--)
                    LeaveEvent(eventPlayers[i].Player);
                eventPlayers.Clear();

                for (int i = joiningSpectators.Count - 1; i >= 0; i--)
                    LeaveEvent(joiningSpectators[i].Player);
                joiningSpectators.Clear();
            }
                        
            private bool HasMinimumRequiredPlayers()
            {
                if (GetActualPlayerCount() == 0)
                    return false;

                if (eventPlayers.Count >= Config.MinimumPlayers)
                    return true;

                if (Config.UseEventBots && eventPlayers.Count + Config.MaximumBots >= Config.MinimumPlayers)
                    return true;

                return false;
            }
                     
            #endregion

            #region Damage and Death

            public virtual bool CanDealEntityDamage(BaseEventPlayer attacker, BaseEntity entity, HitInfo hitInfo)
            {
                if (entity is BaseCombatEntity combatEntity && _deployedObjects.Contains(combatEntity))
                    return true;

                return false;
            }

            protected virtual float GetDamageModifier(BaseEventPlayer eventPlayer, BaseEventPlayer attackerPlayer) => 1f;

            public virtual void OnPlayerTakeDamage(BaseEventPlayer eventPlayer, HitInfo hitInfo)
            {
                BaseEventPlayer attacker = GetUser(hitInfo.InitiatorPlayer);

                if (!attacker && hitInfo.Initiator is BaseCombatEntity combatEntity && CanKillEntity(combatEntity))
                {
                    combatEntity.Die(new HitInfo(eventPlayer.Player, combatEntity, DamageType.Suicide, 1000f));
                    ClearDamage(hitInfo);
                    return;
                }

                if (eventPlayer.IsDead || eventPlayer.IsInvincible || eventPlayer.Player.IsSpectating())
                {
                    ClearDamage(hitInfo);
                    return;
                }

                if (GodmodeEnabled)
                {
                    if (Configuration.Event.GiveKitsWhileWaiting && Status != Arena.EventStatus.Started)
                    {
                        
                    }
                    else
                    {
                        ClearDamage(hitInfo);
                        return;
                    }
                }

                float damageModifier = GetDamageModifier(eventPlayer, attacker);
                if (damageModifier != 1f)
                    hitInfo.damageTypes.ScaleAll(damageModifier);

                eventPlayer.OnTakeDamage(attacker && attacker.Player ? attacker.Player.userID : 0UL);
            }

            protected virtual bool CanKillEntity(BaseCombatEntity baseCombatEntity)
            {
                if (!baseCombatEntity )
                    return false;

                return baseCombatEntity is BaseNpc or NPCPlayer;
            }

            public virtual void PrePlayerDeath(BaseEventPlayer eventPlayer, HitInfo hitInfo)
            {
                if (dropOnDeath != DropOnDeath.Nothing)
                {
                    if (dropOnDeath == DropOnDeath.Corpse && CanDropCorpse())
                        eventPlayer.DropCorpse();
                    else if (dropOnDeath == DropOnDeath.Backpack && CanDropBackpack())
                        eventPlayer.DropInventory();
                    else if (dropOnDeath == DropOnDeath.Weapon && CanDropWeapon())
                        eventPlayer.DropWeapon();
                    else if (dropOnDeath == DropOnDeath.Ammo && CanDropAmmo())
                        eventPlayer.DropAmmo();
                }

                if (eventPlayer.Player.isMounted)
                {
                    BaseMountable baseMountable = eventPlayer.Player.GetMounted();
                    if (baseMountable)
                    {
                        baseMountable.DismountPlayer(eventPlayer.Player);
                        eventPlayer.Player.EnsureDismounted();
                    }
                }

                if (Status != Arena.EventStatus.Started)
                {
                    if (Configuration.Message.BroadcastKills)
                    {
                        BaseEventPlayer attacker = GetUser(hitInfo?.InitiatorPlayer);
                        DisplayKillToChat(eventPlayer, attacker && attacker.Player ? attacker.Player.displayName : string.Empty);
                    }
                    
                    SpawnPlayer(eventPlayer, Configuration.Event.GiveKitsWhileWaiting);
                }
                else
                {
                    eventPlayer.IsDead = true;

                    RebuildSpectateTargets();

                    UpdateSpectatorTargets(eventPlayer);

                    eventPlayer.Player.limitNetworking = true;

                    eventPlayer.Player.DisablePlayerCollider();

                    eventPlayer.Player.RemoveFromTriggers();

                    eventPlayer.RemoveFromNetwork();
                    
                    OnEventPlayerDeath(eventPlayer, GetUser(hitInfo?.InitiatorPlayer), hitInfo);
                }

                ClearDamage(hitInfo);
            }

            public virtual void OnEventPlayerDeath(BaseEventPlayer victim, BaseEventPlayer attacker = null, HitInfo hitInfo = null)
            {
                if (!victim || !victim.Player)
                    return;

                victim.Player.StripInventory();

                if (Configuration.Message.BroadcastKills)
                    DisplayKillToChat(victim, attacker?.Player ? attacker.Player.displayName : string.Empty);
            }

            protected virtual void DisplayKillToChat(BaseEventPlayer victim, string attackerName)
            {
                if (string.IsNullOrEmpty(attackerName))
                {
                    BroadcastToPlayers(victim.IsOutOfBounds ? "Notification.Death.OOB" : "Notification.Death.Suicide", victim.Player.displayName);
                }
                else BroadcastToPlayers("Notification.Death.Killed", victim.Player.displayName, attackerName);
            }
            #endregion

            #region Winners            
            private void ProcessWinners()
            {
                List<BaseEventPlayer> winners = Pool.Get<List<BaseEventPlayer>>();
                GetWinningPlayers(ref winners);

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (!eventPlayer)
                        continue;
                    
                    if (winners.Contains(eventPlayer))
                    {
                        Statistics.Data.AddStatistic(eventPlayer.Player, "Wins");
                        Instance.GiveReward(eventPlayer, RewardType, Config.Rewards.WinAmount);
                    }
                    else
                    {
                        Statistics.Data.AddStatistic(eventPlayer.Player, "Losses");
                    }

                    Statistics.Data.AddStatistic(eventPlayer.Player, "Played");
                }

                if (winners.Count > 0)
                {
                    if (Configuration.Message.BroadcastWinners)
                    {
                        if (Plugin.IsTeamEvent)
                        {
                            Team team = winners[0].Team;
                            Broadcast("Notification.EventWin.Multiple.Team", team == Team.B ? TeamB.Color : TeamA.Color, team == Team.B ? Plugin.TeamBName : Plugin.TeamAName);
                        }
                        else
                        {
                            if (winners.Count > 1)
                                Broadcast("Notification.EventWin.Multiple", winners.Select(x => x.Player.displayName).ToSentence());
                            else Broadcast("Notification.EventWin", winners[0].Player.displayName);
                        }
                    }

                    if (Plugin.IsTeamEvent)
                    {
                        Team team = winners[0].Team;
                        BroadcastRoundWinMessage("UI.EventWin.Multiple.Team", team == Team.B ? TeamB.Color : TeamA.Color, team == Team.B ? Plugin.TeamBName : Plugin.TeamAName);
                    }
                    else
                    {
                        if (winners.Count > 1)
                            BroadcastRoundWinMessage("UI.EventWin.Multiple");
                        else BroadcastRoundWinMessage("UI.EventWin", winners[0].Player.displayName);
                    }
                }

                Pool.FreeUnmanaged(ref winners);
            }

            protected virtual bool CanIssueRewards(BaseEventPlayer eventPlayer) => true;

            protected abstract void GetWinningPlayers(ref List<BaseEventPlayer> list);
            #endregion

            #region Kits and Items           
            protected virtual bool CanDropBackpack() => true;

            protected virtual bool CanDropCorpse() => true;

            protected virtual bool CanDropWeapon() => true;

            protected virtual bool CanDropAmmo() => true;

            public virtual bool CanDropActiveItem() => false;

            protected virtual bool CanGiveKit(BaseEventPlayer eventPlayer) => true;

            protected virtual void OnKitGiven(BaseEventPlayer eventPlayer)
            {
                if (Plugin.IsTeamEvent)
                {
                    string kit = eventPlayer.Team == Team.B ? Config.TeamConfigB.Clothing : Config.TeamConfigA.Clothing;
                    if (!string.IsNullOrEmpty(kit))
                    {
                        List<Item> items = eventPlayer.Player.inventory.containerWear.itemList;
                        for (int i = 0; i < items.Count; i++)
                        {
                            Item item = items[i];
                            item.RemoveFromContainer();
                            item.Remove();
                        }

                        GiveKit(eventPlayer.Player, kit);
                    }
                }

                //if (eventPlayer is ArenaAI.HumanAI)
                //    (eventPlayer as ArenaAI.HumanAI).OnKitGiven();
            }

            public List<string> GetAvailableKits(Team team) => team == Team.B ? Config.TeamConfigB.Kits : Config.TeamConfigA.Kits;
            #endregion

            #region Overrides

            public virtual void GetAdditionalEventDetails(ref List<KeyValuePair<string, object>> list, ulong playerId) { }
            #endregion

            #region Spectating

            protected virtual void RebuildSpectateTargets()
            {
                spectateTargets.Clear();

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];

                    if (!eventPlayer || eventPlayer.IsDead || eventPlayer.Player.IsSleeping() || eventPlayer.Player.IsSpectating())
                        continue;

                    spectateTargets.Add(eventPlayer);
                }
            }

            private void UpdateSpectatorTargets(BaseEventPlayer target = null)
            {
                eventPlayers.ForEach(eventPlayer =>
                {
                    if (eventPlayer && eventPlayer.Player.IsSpectating() && (!eventPlayer.SpectateTarget || eventPlayer.SpectateTarget == target))
                    {
                        if (spectateTargets.Count > 0)
                            eventPlayer.UpdateSpectateTarget();
                        else
                        {
                            ResetPlayer(eventPlayer.Player);
                            OnPlayerRespawn(eventPlayer);
                        }
                    }
                });

                joiningSpectators.ForEach(eventPlayer =>
                {
                    if (eventPlayer && (!eventPlayer.SpectateTarget || eventPlayer.SpectateTarget == target))
                    {
                        if (spectateTargets.Count > 0)
                            eventPlayer.UpdateSpectateTarget();
                        else eventPlayer.SetSpectateTarget(null);                        
                    }
                });
            }    
            
            private void StopAllSpectating()
            {
                joiningSpectators.ForEach(eventPlayer =>
                {
                    if (eventPlayer && eventPlayer.Player && eventPlayer.Player.IsSpectating())
                        eventPlayer.FinishSpectating();
                });

                eventPlayers.ForEach(eventPlayer =>
                {
                    if (eventPlayer && eventPlayer.Player && eventPlayer.Player.IsSpectating())
                        eventPlayer.FinishSpectating();
                });
            }
            #endregion

            //#region Event Bots
            //internal virtual ArenaAI.Settings Settings { get; } = new ArenaAI.Settings()
            //{                
            //    GiveDefaultItems = false
            //};

            //internal virtual ArenaAI.HumanAI CreateAIPlayer(Vector3 position, ArenaAI.Settings settings) => ArenaAI.SpawnNPC<ArenaAI.HumanAI>(position, settings);

            //protected virtual Team GetAIPlayerTeam() => Team.None;

            //private void SpawnEventBots()
            //{
            //    Debug.Log($"spawn event bots");
            //    int count = Mathf.Min(Config.MaximumBots, Config.MaximumPlayers - eventPlayers.Count);

            //    for (int i = 0; i < count; i++)
            //        SpawnBot();
            //}

            //protected void SpawnBot()
            //{
            //    Debug.Log($"spawn bot");
            //    Team team = GetAIPlayerTeam();

            //    ArenaAI.HumanAI humanAI = CreateAIPlayer(team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), Settings);

            //    humanAI.ResetPlayer();

            //    humanAI.Event = this;
            //    humanAI.Team = team;

            //    if (Plugin.IsTeamEvent && Configuration.Event.AddToTeams)
            //    {
            //        EventTeam eventTeam = team == Team.A ? TeamA : TeamB;

            //        eventTeam.AddToTeam(humanAI);
            //    }

            //    eventPlayers.Add(humanAI);

            //    humanAI.Kit = GetAvailableKits(team).GetRandom();

            //    StripInventory(humanAI.Entity);

            //    GiveKit(humanAI.Entity, humanAI.Kit);

            //    OnKitGiven(humanAI);
            //}

            //private void RemoveExcessBots()
            //{
            //    int count = Mathf.Min(GetNPCPlayerCount(), eventPlayers.Count - Config.MaximumPlayers);

            //    for (int i = 0; i < count; i++)
            //        RemoveRandomBot();
            //}

            //protected virtual void RemoveRandomBot()
            //{
            //    if (Plugin.IsTeamEvent)
            //    {
            //        Team removeTeam = GetTeamCount(Team.A) > GetTeamCount(Team.B) ? Team.A : Team.B;

            //        for (int i = 0; i < eventPlayers.Count; i++)
            //        {
            //            BaseEventPlayer eventPlayer = eventPlayers[i];
            //            if (eventPlayer is NPCEventPlayer && eventPlayer.Team == removeTeam)
            //            {
            //                eventPlayers.Remove(eventPlayer);
            //                eventPlayer.Player.Kill(BaseNetworkable.DestroyMode.None);
            //                return;
            //            }
            //        }
            //    }
            //    else
            //    {
            //        for (int i = 0; i < eventPlayers.Count; i++)
            //        {
            //            BaseEventPlayer eventPlayer = eventPlayers[i];
            //            if (eventPlayer is NPCEventPlayer)
            //            {
            //                eventPlayers.Remove(eventPlayer);
            //                eventPlayer.Player.Kill(BaseNetworkable.DestroyMode.None);
            //                return;
            //            }
            //        }
            //    }
            //}

            //internal virtual void UpdateEnemyTargets(ArenaAI.HumanAI humanAI)
            //{
            //    for (int i = 0; i < eventPlayers.Count; i++)
            //    {
            //        BaseEventPlayer eventPlayer = eventPlayers[i];
            //        if (!eventPlayer || eventPlayer == humanAI || (Plugin.IsTeamEvent && eventPlayer.Team == humanAI.Team))
            //            continue;

            //        humanAI.Memory.Update(eventPlayer);
            //    }
            //}

            //internal Vector3 GetRandomAIDestination(ArenaAI.HumanAI eventPlayer)
            //{
            //    BaseEventPlayer randomPlayer = eventPlayers.GetRandom();
            //    if (randomPlayer == eventPlayer)
            //        return GetRandomAIDestination(eventPlayer);

            //    return randomPlayer.Transform.position;
            //}
            //#endregion

            #region Player Counts

            protected int GetActualPlayerCount()
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if (!(eventPlayers[i] is NPCEventPlayer))
                        count++;
                }
                return count;
            }

            public int GetNpcPlayerCount()
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if ((eventPlayers[i] is NPCEventPlayer))
                        count++;
                }
                return count;
            }

            public int GetAlivePlayerCount()
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if (!eventPlayers[i]?.IsDead ?? false)
                        count++;
                }
                return count;
            }

            public int GetTeamCount(Team team)
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if (eventPlayers[i]?.Team == team)
                        count++;
                }
                return count;
            }

            protected int GetTeamAliveCount(Team team)
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer && eventPlayer.Team == team && !eventPlayer.IsDead)
                        count++;
                }
                return count;
            }
            #endregion

            #region Teams

            public virtual int GetTeamScore(Team team) => 0;

            protected void BalanceTeams()
            {
                int aCount = GetTeamCount(Team.A);
                int bCount = GetTeamCount(Team.B);

                int difference = aCount > bCount + 1 ? aCount - bCount : bCount > aCount + 1 ? bCount - aCount : 0;
                Team moveFrom = aCount > bCount + 1 ? Team.A : bCount > aCount + 1 ? Team.B : Team.None;

                if (difference > 1 && moveFrom != Team.None)
                {
                    BroadcastToPlayers("Notification.Teams.Unbalanced");

                    List<BaseEventPlayer> teamPlayers = Pool.Get<List<BaseEventPlayer>>();

                    eventPlayers.ForEach(x =>
                    {
                        if (x.Team == moveFrom)
                            teamPlayers.Add(x);
                    });

                    for (int i = 0; i < (int)Math.Floor((float)difference / 2); i++)
                    {
                        BaseEventPlayer eventPlayer = teamPlayers.GetRandom();
                        teamPlayers.Remove(eventPlayer);

                        eventPlayer.Team = moveFrom == Team.A ? Team.B : Team.A;

                        if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams)
                        {
                            EventTeam currentTeam = eventPlayer.Team == Team.A ? eventPlayer.Event.TeamB : eventPlayer.Event.TeamA;
                            EventTeam newTeam = eventPlayer.Team == Team.A ? eventPlayer.Event.TeamA : eventPlayer.Event.TeamB;

                            currentTeam.RemoveFromTeam(eventPlayer.Player);
                            newTeam.AddToTeam(eventPlayer.Player);
                        }

                        BroadcastToPlayer(eventPlayer, string.Format(Message("Notification.Teams.TeamChanged", eventPlayer.Player.userID), eventPlayer.Team));
                    }

                    Pool.FreeUnmanaged(ref teamPlayers);
                }
            }
            #endregion

            #region Entity Management

            public void OnEntityDeployed(BaseCombatEntity entity) => _deployedObjects.Add(entity);

            public void OnWorldItemDropped(WorldItem worldItem) => _droppedItems.Add(worldItem);

            public void OnInventorySpawned(DroppedItemContainer entity) => _droppedInventories.Add(entity);

            public void OnCorpseSpawned(PlayerCorpse entity) => _droppedCorpses.Add(entity);

            private void CleanupEntities()
            {
                for (int i = _deployedObjects.Count - 1; i >= 0; i--)
                {
                    BaseCombatEntity entity = _deployedObjects[i];
                    if (entity != null && !entity.IsDestroyed)
                        entity.DieInstantly();
                }
                _deployedObjects.Clear();

                for (int i = _droppedInventories.Count - 1; i >= 0; i--)
                {
                    DroppedItemContainer droppedItemContainer = _droppedInventories[i];
                    if (droppedItemContainer != null && !droppedItemContainer.IsDestroyed)
                    {
                        droppedItemContainer.inventory?.Clear();
                        droppedItemContainer.DieInstantly();
                    }
                }
                _droppedInventories.Clear();

                for (int i = _droppedItems.Count - 1; i >= 0; i--)
                {
                    WorldItem worldItem = _droppedItems[i];
                    if (worldItem != null && !worldItem.IsDestroyed)
                    {
                        worldItem.DestroyItem();
                        worldItem.Kill();
                    }
                }
                _droppedItems.Clear();

                for (int i = _droppedCorpses.Count - 1; i >= 0; i--)
                {
                    PlayerCorpse playerCorpse = _droppedCorpses[i];
                    if (playerCorpse != null && !playerCorpse.IsDestroyed)
                    {
                        for (int y = 0; y < playerCorpse.containers?.Length; y++)                        
                            playerCorpse.containers[y]?.Clear();
                        
                        playerCorpse.DieInstantly();
                    }
                }
                _droppedCorpses.Clear();
            }
            #endregion

            #region Scoreboard

            public void UpdateScoreboard()
            {
                UpdateScores();
                BuildScoreboard();

                if (scoreContainer != null)
                {
                    eventPlayers.ForEach(eventPlayer =>
                    {
                        if (!eventPlayer.IsDead)
                            eventPlayer.AddUI(UI_SCORES, scoreContainer);
                    });

                    joiningSpectators.ForEach(eventPlayer => eventPlayer.AddUI(UI_SCORES, scoreContainer));
                }
            }

            private void UpdateScoreboard(BaseEventPlayer eventPlayer)
            {
                if (Status == Arena.EventStatus.Started && scoreContainer != null && !eventPlayer.IsDead)
                    eventPlayer.AddUI(UI_SCORES, scoreContainer);
            }

            private void UpdateScores()
            {
                scoreData.Clear();

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];

                    scoreData.Add(new ScoreEntry(eventPlayer, GetFirstScoreValue(eventPlayer), GetSecondScoreValue(eventPlayer)));
                }

                SortScores(ref scoreData);
            }

            protected abstract void BuildScoreboard();

            protected abstract float GetFirstScoreValue(BaseEventPlayer eventPlayer);

            protected abstract float GetSecondScoreValue(BaseEventPlayer eventPlayer);

            protected abstract void SortScores(ref List<ScoreEntry> list);
            #endregion

            #region Event Messaging 
            private void BroadcastToServer(string key, params object[] args)
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (!player.GetComponent<BaseEventPlayer>())
                        player.SendConsoleCommand("chat.add", 2, Configuration.Message.ChatIcon, args != null ? string.Format(Message(key, player.userID), args) : Message(key, player.userID));
                }                
            }
            
            private void BroadcastToPlayers(string key, params object[] args)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID));
                }

                for (int i = 0; i < joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID));
                }
            }

            public void BroadcastToPlayers(Func<string, ulong, string> GetMessage, string key, params object[] args)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(GetMessage(key, eventPlayer.Player.userID), args) : GetMessage(key, eventPlayer.Player.userID));
                }

                for (int i = 0; i < joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(GetMessage(key, eventPlayer.Player.userID), args) : GetMessage(key, eventPlayer.Player.userID));
                }
            }

            public void BroadcastToPlayers(BasePlayer player, string message)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null)
                        eventPlayer.Player.SendConsoleCommand("chat.add", 0, player.UserIDString, $"<color={(player.IsAdmin ? "#aaff55" : player.IsDeveloper ? "#fa5" : "#55AAFF")}>{player.displayName}</color>: {message}");
                }

                for (int i = 0; i < joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    if (eventPlayer?.Player != null)
                        eventPlayer.Player.SendConsoleCommand("chat.add", 0, player.UserIDString, $"<color={(player.IsAdmin ? "#aaff55" : player.IsDeveloper ? "#fa5" : "#55AAFF")}>{player.displayName}</color>: {message}");
                }
            }

            public void BroadcastToTeam(Team team, string key, string[] args = null)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null && eventPlayer.Team == team)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID));
                }
            }

            public void BroadcastToPlayer(BaseEventPlayer eventPlayer, string message) => eventPlayer?.Player?.SendConsoleCommand("chat.add", 2, Configuration.Message.ChatIcon, message);

            private void BroadcastRoundWinMessage(string key, params object[] args)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];

                    CuiElementContainer container = UI.Container(UI_POPUP, new UI4(0.2f, 0.4f, 0.8f, 0.6f));
                    UI.OutlineLabel(container, UI_POPUP, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID), 20, UI4.Full);

                    eventPlayer.AddUI(UI_POPUP, container);
                    InvokeHandler.Invoke(eventPlayer, () => eventPlayer.DestroyUI(UI_POPUP), 5f);
                }
            }
            #endregion

            #region Teams

            public bool IsEventTeam(ulong teamID)
            {
                if ((TeamA?.IsEventTeam(teamID) ?? false) || (TeamB?.IsEventTeam(teamID) ?? false))
                    return true;
                return false;
            }

            public class EventTeam
            {
                private Team Team { get; set; }

                public string Color { get; private set; }

                public string Clothing { get; private set; }

                public SpawnSelector Spawns { get; private set; }


                private RelationshipManager.PlayerTeam _playerTeam;

                private RelationshipManager.PlayerTeam PlayerTeam
                {
                    get
                    {
                        if (_playerTeam == null)
                        {
                            _playerTeam = RelationshipManager.ServerInstance.CreateTeam();

                            _playerTeam.invites.Clear();
                            _playerTeam.members.Clear();
                            _playerTeam.onlineMemberConnections.Clear();

                            _playerTeam.teamName = $"Team {Team}";
                        }
                        return _playerTeam;
                    }
                }

                public EventTeam(Team team, string color, string clothing, SpawnSelector spawns)
                {
                    Team = team;
                    Clothing = clothing;
                    Spawns = spawns;

                    if (string.IsNullOrEmpty(color) || color.Length < 6 || color.Length > 6 || !hexFilter.IsMatch(color))
                        Color = team == Team.A ? "#9b2021" : "#0000d8";
                    else Color = "#" + color;
                }

                public void AddToTeam(BasePlayer player)
                {
                    if (player.currentTeam != 0UL && player.currentTeam != PlayerTeam.teamID)
                    {
                        RelationshipManager.PlayerTeam oldTeam = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                        if (oldTeam != null)
                        {
                            oldTeam.members.Remove(player.userID.Get());
                            player.ClearTeam();
                        }
                    }

                    player.currentTeam = PlayerTeam.teamID;

                    if (!PlayerTeam.members.Contains(player.userID.Get()))
                        PlayerTeam.members.Add(player.userID);

                    RelationshipManager.ServerInstance.playerToTeam.Remove(player.userID);
                    RelationshipManager.ServerInstance.playerToTeam.Add(player.userID, PlayerTeam);

                    player.SendNetworkUpdate();
                    player.TeamUpdate();

                    PlayerTeam.MarkDirty();
                }

                //internal void AddToTeam(ArenaAI.HumanAI humanAI)
                //{

                //}

                public void RemoveFromTeam(BasePlayer player)
                {
                    if (_playerTeam == null || !player) 
                        return;
                    
                    _playerTeam.members.Remove(player.userID.Get());
                    RelationshipManager.ServerInstance.playerToTeam.Remove(player.userID);
                    player.ClearTeam();
                    player.BroadcastAppTeamRemoval();
                }

                //internal void RemoveFromTeam(ArenaAI.HumanAI humanAI)
                //{

                //}

                public void Destroy()
                {
                    Spawns.Destroy();

                    if (_playerTeam != null)
                    {
                        for (int i = _playerTeam.members.Count - 1; i >= 0; i--)
                        {
                            ulong playerID = _playerTeam.members[i];

                            _playerTeam.members.Remove(playerID);
                            RelationshipManager.ServerInstance.playerToTeam.Remove(playerID);

                            BasePlayer basePlayer = RelationshipManager.FindByID(playerID);
                            if (basePlayer != null)
                            {
                                basePlayer.ClearTeam();
                                basePlayer.BroadcastAppTeamRemoval();
                            }
                        }

                        RelationshipManager.ServerInstance.teams.Remove(_playerTeam.teamID);

                        _playerTeam.invites.Clear();
                        _playerTeam.members.Clear();
                        _playerTeam.onlineMemberConnections.Clear();
                        _playerTeam.teamID = 0UL;
                        _playerTeam.teamLeader = 0UL;
                        _playerTeam.teamName = string.Empty;

                        Pool.Free(ref _playerTeam);
                    }
                }

                public bool IsEventTeam(ulong teamID)
                {
                    if (_playerTeam == null)
                        return false;

                    return _playerTeam.teamID.Equals(teamID);
                }
            }
            #endregion

            #region Event Teleporter            
            private bool CanEnterTeleporter(BasePlayer player)
            {
                if (!string.IsNullOrEmpty(Config.Permission) && !player.HasPermission(Config.Permission))
                {
                    player.LocalizedMessage(Instance, "Error.NoPermission");
                    return false;
                }

                if (player.HasPermission(BLACKLISTED_PERMISSION))
                {
                    player.LocalizedMessage(Instance, "Error.Blacklisted");
                    return false;
                }

                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer)
                    return false;

                if (!CanJoinEvent(player))
                    return false;

                return true;
            }

            private void EnterTeleporter(BasePlayer player) => JoinEvent(player, GetPlayerTeam());

            private string GetTeleporterInformation()
            {
                return string.Format(Message("Info.Teleporter"), Config.EventName,
                        Config.EventType,
                        eventPlayers.Count,
                        Config.MaximumPlayers,
                        _roundNumber,
                        Config.RoundsToPlay,
                        IsEventLockedOut() ? Message("UI.Event.Locked") : Status,
                        Plugin.UseScoreLimit ? string.Format(Message("Info.Teleporter.Score"), Config.ScoreLimit) :
                        Plugin.UseTimeLimit ? string.Format(Message("Info.Teleporter.Time"), Config.TimeLimit == 0 ? "Unlimited" : Config.TimeLimit.ToString()) : string.Empty,
                        Config.AllowClassSelection ? "Class Selector" : "Event Specific");
            }

            private void CreateTeleporter()
            {
                if (Config.Teleporter == null)
                    return;

                CreateTeleporter(Config.Teleporter);
            }

            public void CreateTeleporter(EventConfig.SerializedTeleporter serializedTeleporter)
            {
                if (!Lobby.IsEnabled)
                {
                    Debug.LogWarning($"Failed setting up event teleporter for {Config.EventName}\nTeleporters require the lobby to be enabled and a valid lobby spawn file");
                    return;
                }

                _eventTeleporter = EventTeleporter.Create(serializedTeleporter.Position, serializedTeleporter.Radius);
                _eventTeleporter.Initialize(CanEnterTeleporter, EnterTeleporter, GetTeleporterInformation);
            }

            public void DestroyTeleporter()
            {
                if (!_eventTeleporter) 
                    return;
                
                Destroy(_eventTeleporter);
                _eventTeleporter = null;
            }
            #endregion
        }

        public class NPCEventPlayer : BaseEventPlayer
        {
            //protected override void Awake()
            //{
            //    Player = GetComponent<BasePlayer>();

            //    Transform = Player.transform;

            //    if (Configuration.Event.Health.Enabled)
            //    {
            //        _nextHealthRestoreTime = Time.time + Configuration.Event.Health.RestoreAfter;
            //        InvokeHandler.InvokeRepeating(this, HealthRestoreTick, 1f, 1f);
            //    }
            //}

            public override void AddUI(string panel, CuiElementContainer container) { }

            public override void DestroyUI() { }

            public override void DestroyUI(string panel) { }

            public override void AddPlayerDeath(BaseEventPlayer attacker = null)
            {
                Deaths++;
            }

            public override void OnPlayerDeath(BaseEventPlayer attacker = null, float respawnTime = 5, HitInfo hitInfo = null)
            {
                AddPlayerDeath(attacker);

                InvokeHandler.Invoke(this, ()=> RespawnPlayer(this), respawnTime);
            }
        }

        public class BaseEventPlayer : MonoBehaviour
        {
            protected float _respawnDurationRemaining;

            private float _invincibilityEndsAt;

            private double _resetDamageTime;

            private List<ulong> _damageContributors = Pool.Get<List<ulong>>();

            private bool _isOOB;

            private int _oobTime;

            private int _spectateIndex;

            private double _nextHealthRestoreTime;


            public BasePlayer Player { get; set; }

            public BaseEventGame Event { get; set; }

            public Transform Transform { get; set; }

            public Team Team { get; set; } = Team.None;

            public int Kills { get; set; }

            public int Deaths { get; set; }



            public bool IsDead { get; set; }

            public bool AutoRespawn { get; set; }

            public bool CanRespawn => _respawnDurationRemaining <= 0;

            public int RespawnRemaining => Mathf.CeilToInt(_respawnDurationRemaining);

            public bool IsInvincible => Time.time < _invincibilityEndsAt;


            public BaseEventPlayer SpectateTarget { get; private set; }


            public string Kit { get; set; }

            private ProtoBuf.PlayerModifiers m_Modifiers;


            public bool IsOutOfBounds
            {
                get => _isOOB;
                set
                {
                    if (value)
                    {
                        _oobTime = 10;
                        InvokeHandler.Invoke(this, TickOutOfBounds, 1f);
                    }
                    else InvokeHandler.CancelInvoke(this, TickOutOfBounds);

                    _isOOB = value;
                }
            }

            protected virtual void Awake()
            {
                Player = GetComponent<BasePlayer>();

                Transform = Player.transform;

                Player.modifiers.Save(false);

                Player.metabolism.bleeding.max = 0;
                Player.metabolism.bleeding.value = 0;
                Player.metabolism.radiation_level.max = 0;
                Player.metabolism.radiation_level.value = 0;
                Player.metabolism.radiation_poison.max = 0;
                Player.metabolism.radiation_poison.value = 0;

                m_Modifiers = Player.modifiers.Save(false);
                Player.modifiers.RemoveAll();
                
                Player.metabolism.SendChangesToClient();

                Interface.Call("DisableBypass", Player.userID);

                if (Configuration.Event.Health.Enabled)
                {
                    _nextHealthRestoreTime = Time.time + Configuration.Event.Health.RestoreAfter;
                    InvokeHandler.InvokeRepeating(this, HealthRestoreTick, 1f, 1f);
                }
            }

            protected virtual void OnDestroy()
            {
                if (Player.IsSpectating())
                    FinishSpectating();

                Player.limitNetworking = false;

                Player.EnablePlayerCollider();

                Player.health = Player.MaxHealth();

                Player.SendNetworkUpdate();

                Player.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);

                Player.metabolism.bleeding.max = 1;
                Player.metabolism.bleeding.value = 0;
                Player.metabolism.radiation_level.max = 100;
                Player.metabolism.radiation_level.value = 0;
                Player.metabolism.radiation_poison.max = 500;
                Player.metabolism.radiation_poison.value = 0;
                
                if (m_Modifiers != null)
                    Player.modifiers.Load(m_Modifiers, false);

                Player.metabolism.SendChangesToClient();

                Interface.Call("EnableBypass", Player.userID);

                if (Player.isMounted)
                    Player.GetMounted()?.AttemptDismount(Player);

                DestroyUI();

                if (IsUnloading)
                    Player.StripInventory();

                UnlockClothingSlots(Player);

                InvokeHandler.CancelInvoke(this, TickOutOfBounds);
                InvokeHandler.CancelInvoke(this, HealthRestoreTick);

                Pool.FreeUnmanaged(ref _damageContributors);
                Pool.FreeUnmanaged(ref _openPanels);
            }

            public void ResetPlayer()
            {
                Team = Team.None;
                Kills = 0;
                Deaths = 0;
                IsDead = false;
                AutoRespawn = false;
                Kit = string.Empty;

                _spectateIndex = 0;
                _respawnDurationRemaining = 0;
                _invincibilityEndsAt = 0;
                _resetDamageTime = 0;
                _oobTime = 0;
                _isOOB = false;

                _damageContributors.Clear();
            }

            public virtual void ResetStatistics()
            {
                Kills = 0;
                Deaths = 0;

                _spectateIndex = 0;
                _respawnDurationRemaining = 0;
                _invincibilityEndsAt = 0;
                _resetDamageTime = 0;
                _oobTime = 0;
                _isOOB = false;

                _damageContributors.Clear();
            }

            public void ForceSelectClass()
            {
                IsDead = true;
            }

            protected void RespawnTick()
            {
                if (!IsDead)
                    return;

                _respawnDurationRemaining = Mathf.Clamp(_respawnDurationRemaining - 1f, 0f, float.MaxValue);

                UpdateRespawnButton(this);

                if (_respawnDurationRemaining <= 0f)
                {
                    InvokeHandler.CancelInvoke(this, RespawnTick);

                    if (AutoRespawn)
                        RespawnPlayer(this);
                }
            }

            public void OnRoundFinished()
            {
                if (IsDead)
                {
                    InvokeHandler.CancelInvoke(this, RespawnTick);
                    _respawnDurationRemaining = 0;                    
                }
            }

            #region Death

            public void OnKilledPlayer(HitInfo hitInfo)
            {
                Kills++;

                int rewardAmount = Event.Config.Rewards.KillAmount;

                Statistics.Data.AddStatistic(Player, "Kills");

                if (hitInfo != null)
                {
                    if (hitInfo.damageTypes.IsMeleeType())
                        Statistics.Data.AddStatistic(Player, "Melee");

                    if (hitInfo.isHeadshot)
                    {
                        Statistics.Data.AddStatistic(Player, "Headshots");
                        rewardAmount = Event.Config.Rewards.HeadshotAmount;
                    }
                }

                if (rewardAmount > 0)
                    Instance.GiveReward(this, Event.RewardType, rewardAmount);
            }

            public virtual void OnPlayerDeath(BaseEventPlayer attacker = null, float respawnTime = 5f, HitInfo hitInfo = null)
            {
                AddPlayerDeath(attacker);

                _respawnDurationRemaining = respawnTime;

                InvokeHandler.InvokeRepeating(this, RespawnTick, 1f, 1f);

                DestroyUI();

                string message = attacker != null ? string.Format(Message("UI.Death.Killed", Player.userID), attacker.Player.displayName) :
                                 IsOutOfBounds ? Message("UI.Death.OOB", Player.userID) :
                                 Message("UI.Death.Suicide", Player.userID);

                DisplayDeathScreen(this, message, true);
            }

            public virtual void AddPlayerDeath(BaseEventPlayer attacker = null)
            {
                Deaths++;
                Statistics.Data.AddStatistic(Player, "Deaths");
                ApplyAssistPoints(attacker);
            }

            private void ApplyAssistPoints(BaseEventPlayer attacker = null)
            {
                if (_damageContributors.Count > 1)
                {
                    for (int i = 0; i < _damageContributors.Count - 1; i++)
                    {
                        ulong contributorId = _damageContributors[i];
                        if (attacker != null && attacker.Player.userID == contributorId)
                            continue;

                        Statistics.Data.AddStatistic(contributorId, "Assists");
                    }
                }

                _resetDamageTime = 0;
                _damageContributors.Clear();
            }

            public void ApplyInvincibility() => _invincibilityEndsAt = Time.time + Configuration.Event.InvincibilityTime;
            #endregion

            private void TickOutOfBounds()
            {
                if (!Player)
                {
                    Event.LeaveEvent(this);
                    return;
                }

                if (IsDead || Player.IsSpectating())
                    return;

                if (IsOutOfBounds)
                {
                    if (_oobTime == 10)
                        Event.BroadcastToPlayer(this, Message("Notification.OutOfBounds", Player.userID));
                    else if (_oobTime == 0)
                    {
                        Effect.server.Run("assets/prefabs/tools/c4/effects/c4_explosion.prefab", Player.transform.position);

                        if (Event.Status == EventStatus.Started)
                            Event.PrePlayerDeath(this, null);
                        else Event.SpawnPlayer(this, Configuration.Event.GiveKitsWhileWaiting);
                    }
                    else Event.BroadcastToPlayer(this, string.Format(Message("Notification.OutOfBounds.Time", Player.userID), _oobTime));

                    _oobTime--;

                    InvokeHandler.Invoke(this, TickOutOfBounds, 1f);
                }
            }

            #region Drop On Death

            public void DropInventory()
            {
                const string BACKPACK_PREFAB = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";

                DroppedItemContainer itemContainer = ItemContainer.Drop(BACKPACK_PREFAB, Player.transform.position, Quaternion.identity, Player.inventory.containerBelt, Player.inventory.containerMain);
                if (itemContainer != null)
                {
                    itemContainer.playerName = Player.displayName;
                    itemContainer.playerSteamID = Player.userID;

                    itemContainer.CancelInvoke(itemContainer.RemoveMe);
                    itemContainer.Invoke(itemContainer.RemoveMe, Configuration.Timer.Corpse);

                    Event.OnInventorySpawned(itemContainer);
                }
            }

            public void DropCorpse()
            {
                const string CORPSE_PREFAB = "assets/prefabs/player/player_corpse.prefab";

                PlayerCorpse playerCorpse = Player.DropCorpse(CORPSE_PREFAB) as PlayerCorpse;
                if (playerCorpse != null)    
                {
                    playerCorpse.TakeFrom(Player, Player.inventory.containerMain, Player.inventory.containerBelt);
                    playerCorpse.playerName = Player.displayName;
                    playerCorpse.playerSteamID = Player.userID;
                    playerCorpse.underwearSkin = Player.GetUnderwearSkin();
                    playerCorpse.Spawn();
                    playerCorpse.TakeChildren(Player);

                    playerCorpse.ResetRemovalTime(Configuration.Timer.Corpse);

                    Event.OnCorpseSpawned(playerCorpse);
                }
            }

            public void DropWeapon()
            {
                Item item = Player.GetActiveItem();
                if (item != null)
                {
                    DroppedItem droppedItem = item.Drop(Player.transform.position, Vector3.up) as DroppedItem;
                    droppedItem.CancelInvoke(droppedItem.IdleDestroy);
                    droppedItem.Invoke(droppedItem.IdleDestroy, 30f);

                    RotateDroppedItem(droppedItem);

                    Event.OnWorldItemDropped(droppedItem);
                }
            }

            public void DropAmmo()
            {
                Item item = Player.GetActiveItem();
                if (item != null)
                {
                    BaseProjectile baseProjectile = item.GetHeldEntity() as BaseProjectile;
                    if (baseProjectile != null && baseProjectile.primaryMagazine.contents > 0)
                    {
                        Item ammo = ItemManager.Create(baseProjectile.primaryMagazine.ammoType, baseProjectile.primaryMagazine.contents);
                        
                        DroppedItem droppedItem = ammo.Drop(Player.transform.position, Vector3.up) as DroppedItem;
                        droppedItem.CancelInvoke(droppedItem.IdleDestroy);
                        droppedItem.Invoke(droppedItem.IdleDestroy, 30f);

                        baseProjectile.primaryMagazine.contents = 0;

                        RotateDroppedItem(droppedItem);

                        Event.OnWorldItemDropped(droppedItem);
                    }
                }
            }
            #endregion

            #region Networking

            public void RemoveFromNetwork()
            {
                NetWrite netWrite = Net.sv.StartWrite();
                netWrite.PacketID(Network.Message.Type.EntityDestroy);
                netWrite.EntityID(Player.net.ID);
                netWrite.UInt8((byte)BaseNetworkable.DestroyMode.None);
                netWrite.Send(new SendInfo(Player.net.group.subscribers.Where(x => x.userid != Player.userID).ToList()));
            }

            public void AddToNetwork() => Player.SendFullSnapshot();
            #endregion

            #region Damage Contributors

            public void OnTakeDamage(ulong attackerId)
            {
                _nextHealthRestoreTime = Time.time + Configuration.Event.Health.RestoreAfter;

                float time = Time.realtimeSinceStartup;
                if (time > _resetDamageTime)
                {
                    _resetDamageTime = time + 3f;
                    _damageContributors.Clear();
                }

                if (attackerId != 0U && attackerId != Player.userID)
                {
                    if (_damageContributors.Contains(attackerId))
                        _damageContributors.Remove(attackerId);
                    _damageContributors.Add(attackerId);
                }
            }

            public List<ulong> DamageContributors => _damageContributors;
            #endregion

            #region Health Restoration
            private void HealthRestoreTick()
            {
                if (!Player || IsDead)
                    return;

                if (Time.time > _nextHealthRestoreTime && Player.health < Player.MaxHealth())
                {
                    Player.health = Mathf.Clamp(Player._health + Configuration.Event.Health.Amount, 0f, Player._maxHealth);
                    Player.SendNetworkUpdate();
                }
            }
            #endregion

            #region Spectating  
            public void BeginSpectating()
            {
                if (Player.IsSpectating())
                    return;

                DestroyUI();

                Player.limitNetworking = true;
                RemoveFromNetwork();

                Player.StartSpectating();
                Player.LocalizedMessage(Instance, "Notification.SpectateCycle");

                UpdateSpectateTarget();
            }

            public void FinishSpectating()
            {
                if (!Player.IsSpectating())
                    return;
                
                Player.limitNetworking = false;
                AddToNetwork();

                Player.SetParent(null, false, true);
                Player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
                Player.gameObject.SetLayerRecursive(17);

                SpectateTarget = null;
                DestroyUI(UI_SPECTATE);
            }

            public void SetSpectateTarget(BaseEventPlayer eventPlayer)
            {
                SpectateTarget = eventPlayer;
                
                if (eventPlayer && eventPlayer.Player)
                {
                    Event.BroadcastToPlayer(this, $"Spectating: {eventPlayer.Player.displayName}");
                    DisplaySpectateScreen(this, eventPlayer.Player.displayName);

                    Player.SendEntitySnapshot(eventPlayer.Player);
                    Player.gameObject.Identity();
                    Player.SetParent(eventPlayer.Player, false, true);
                }
                else
                {
                    Event.BroadcastToPlayer(this, "Player spectating paused. Waiting for spectating targets...");
                    DisplaySpectateScreen(this, "No one");

                    Player.gameObject.Identity();
                    Player.SetParent(null, true, true);
                }
            }

            public void UpdateSpectateTarget()
            {                
                if (Event.SpectateTargets.Count == 0)
                {
                    Arena.ResetPlayer(Player);
                    Event.OnPlayerRespawn(this);                    
                }
                else
                {
                    _spectateIndex += 1;

                    if (_spectateIndex >= Event.SpectateTargets.Count)
                        _spectateIndex = 0;
                    
                    SetSpectateTarget(Event.SpectateTargets.ElementAt(_spectateIndex));                    
                }
            }
            #endregion

            #region UI Management
            private List<string> _openPanels = Pool.Get<List<string>>();

            public virtual void AddUI(string panel, CuiElementContainer container)
            {
                DestroyUI(panel);

                _openPanels.Add(panel);

                if (Player != null && Player.IsConnected)
                    CuiHelper.AddUi(Player, container);
            }

            public virtual void DestroyUI()
            {
                _openPanels.ForEach(s =>
                {
                    if (Player != null && Player.IsConnected)
                        CuiHelper.DestroyUi(Player, s);
                });

                _openPanels.Clear();
            }

            public virtual void DestroyUI(string panel)
            {
                if (_openPanels.Contains(panel))
                    _openPanels.Remove(panel);
                
                if (Player != null && Player.IsConnected)
                    CuiHelper.DestroyUi(Player, panel);
            }            
            #endregion
        }

        #region Event Teleporter
        public class EventTeleporter : MonoBehaviour
        {
            private SphereEntity Entity { get; set; }


            private Func<BasePlayer, bool> canEnterTrigger;

            private Action<BasePlayer> onEnterTrigger;

            private Func<string> getInformationString;


            private Vector3 position;

            private const string SPHERE_ENTITY = "assets/prefabs/visualization/sphere.prefab";

            private const float REFRESH_RATE = 2f;

            private bool isTriggerReady;

            private void Awake()
            {
                Entity = GetComponent<SphereEntity>();
                position = Entity.transform.position;
            }

            private void OnDestroy()
            {
                InvokeHandler.CancelInvoke(this, InformationTick);
                Entity.Kill();
                Destroy(gameObject);
            }

            private void OnTriggerEnter(Collider col)
            {
                if (!isTriggerReady)
                    return;

                BasePlayer player = col.gameObject?.ToBaseEntity()?.ToPlayer();
                if (!player)
                    return;

                if (player.isMounted)
                    return;

                if (!canEnterTrigger(player))
                    return;

                onEnterTrigger(player);                          
            }

            public void Initialize(Func<BasePlayer, bool> canEnterTrigger, Action<BasePlayer> onEnterTrigger, Func<string> getInformationString)
            {
                this.canEnterTrigger = canEnterTrigger;
                this.onEnterTrigger = onEnterTrigger;
                this.getInformationString = getInformationString;

                InvokeHandler.InvokeRepeating(this, InformationTick, Random.Range(0.1f, 2f), REFRESH_RATE);

                InvokeHandler.Invoke(this, ()=> isTriggerReady = true, 1f);
            }

            private void InformationTick()
            {                
                List<BasePlayer> list = Pool.Get<List<BasePlayer>>();
                Vis.Entities(position, 10f, list);

                if (list.Count > 0)
                {
                    string informationStr = getInformationString.Invoke(); 

                    for (int i = 0; i < list.Count; i++)
                    {
                        BasePlayer player = list[i];
                        if (!player || player.IsDead() || player.isMounted)
                            continue;

                        if (player.IsAdmin)
                            player.SendConsoleCommand("ddraw.text", REFRESH_RATE, Color.white, position, informationStr);
                        else
                        {
                            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                            player.SendNetworkUpdateImmediate();
                            player.SendConsoleCommand("ddraw.text", REFRESH_RATE, Color.white, position, informationStr);
                            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                            player.SendNetworkUpdateImmediate();
                        }
                    }
                }

                Pool.FreeUnmanaged(ref list);
            }

            public static EventTeleporter Create(Vector3 position, float radius)
            {
                SphereEntity sphereEntity = GameManager.server.CreateEntity(SPHERE_ENTITY, position, Quaternion.identity) as SphereEntity;
                sphereEntity.currentRadius = sphereEntity.lerpRadius = radius * 2f;

                sphereEntity.enableSaving = false;
                sphereEntity.Spawn();

                sphereEntity.gameObject.layer = (int)Layer.Reserved2;

                SphereCollider sphereCollider = sphereEntity.gameObject.AddComponent<SphereCollider>();
                sphereCollider.radius = radius;
                sphereCollider.isTrigger = true;

                return sphereEntity.gameObject.AddComponent<EventTeleporter>();
            }
        }

        #region API
        private readonly Hash<Plugin, Hash<string, EventTeleporter>> _pluginTeleporters = new();

        private bool CreateEventTeleporter(Plugin plugin, string teleporterID, Vector3 position, float radius, Func<BasePlayer, bool> canEnterTrigger, Action<BasePlayer> onEnterTrigger, Func<string> getInformationString)
        {
            if (!Lobby.IsEnabled)
            {
                Debug.LogWarning($"Failed setting up event teleporter for {plugin.Name}\nTeleporters require the lobby to be enabled and a valid lobby spawn file");
                return false;
            }

            if (!_pluginTeleporters.TryGetValue(plugin, out Hash<string, EventTeleporter> teleporters))
                teleporters = _pluginTeleporters[plugin] = new Hash<string, EventTeleporter>();

            if (teleporters.TryGetValue(teleporterID, out EventTeleporter eventTeleporter))
                UnityEngine.Object.Destroy(eventTeleporter.gameObject);

            eventTeleporter = EventTeleporter.Create(position, radius);
            eventTeleporter.Initialize(canEnterTrigger, onEnterTrigger, getInformationString);

            teleporters[teleporterID] = eventTeleporter;
            return true;
        }

        private void DestroyEventTeleporter(Plugin plugin, string teleporterID)
        {
            if (!_pluginTeleporters.TryGetValue(plugin, out Hash<string, EventTeleporter> teleporters))
                return;

            if (!teleporters.TryGetValue(teleporterID, out EventTeleporter eventTeleporter))
                return;

            teleporters.Remove(teleporterID);
            UnityEngine.Object.Destroy(eventTeleporter.gameObject);
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (!_pluginTeleporters.TryGetValue(plugin, out Hash<string, EventTeleporter> teleporters))
                return;

            foreach (EventTeleporter eventTeleporter in teleporters.Values)
                UnityEngine.Object.Destroy(eventTeleporter.gameObject);

            _pluginTeleporters.Remove(plugin);
        }

        private void DestroyTeleporters()
        {
            foreach(Hash<string, EventTeleporter> teleporters in _pluginTeleporters.Values)
            {
                foreach(EventTeleporter eventTeleporter in teleporters.Values)
                {
                    UnityEngine.Object.Destroy(eventTeleporter.gameObject);
                }
            }

            _pluginTeleporters.Clear();
        }
        #endregion
        #endregion

        #region Event Timer
        public class GameTimer
        {
            private readonly BaseEventGame _owner;

            private string _message;
            private int _timeRemaining;
            private Action _callback;

            public GameTimer(BaseEventGame owner)
            {
                _owner = owner;
            }

            public void StartTimer(int time, string message = "", Action callback = null)
            {
                _timeRemaining = time;
                _message = message;
                _callback = callback;

                InvokeHandler.InvokeRepeating(_owner, TimerTick, 1f, 1f);
            }

            public void StopTimer()
            {
                InvokeHandler.CancelInvoke(_owner, TimerTick);

                for (int i = 0; i < _owner?.eventPlayers?.Count; i++)
                    _owner.eventPlayers[i]?.DestroyUI(UI_TIMER);
            }

            private void TimerTick()
            {
                _timeRemaining--;
                if (_timeRemaining == 0)
                {
                    StopTimer();
                    _callback?.Invoke();
                }
                else UpdateTimer();
            }

            private void UpdateTimer()
            {
                string clockTime = string.Empty;

                TimeSpan dateDifference = TimeSpan.FromSeconds(_timeRemaining);
                int hours = dateDifference.Hours;
                int mins = dateDifference.Minutes;
                int secs = dateDifference.Seconds;

                clockTime = hours > 0 ? $"{hours:00}:{mins:00}:{secs:00}" : $"{mins:00}:{secs:00}";

                CuiElementContainer container = UI.Container(UI_TIMER, "0.1 0.1 0.1 0.7", new UI4(0.46f, 0.92f, 0.54f, 0.95f), false, "Hud");

                UI.Label(container, UI_TIMER, clockTime, 14, UI4.Full);

                if (!string.IsNullOrEmpty(_message))
                    UI.Label(container, UI_TIMER, _message, 14, new UI4(-5f, 0f, -0.1f, 1), TextAnchor.MiddleRight);

                for (int i = 0; i < _owner.eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = _owner.eventPlayers[i];
                    if (!eventPlayer)
                        continue;

                    eventPlayer.DestroyUI(UI_TIMER);
                    eventPlayer.AddUI(UI_TIMER, container);
                }

                for (int i = 0; i < _owner.joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = _owner.joiningSpectators[i];
                    if (!eventPlayer)
                        continue;

                    eventPlayer.DestroyUI(UI_TIMER);
                    eventPlayer.AddUI(UI_TIMER, container);
                }
            }
        }
        #endregion

        #region Spawn Management

        public class SpawnSelector
        {
            protected List<Vector3> _defaultSpawns;
            protected List<Vector3> _availableSpawns;

            public virtual int Count { get; private set; }

            public SpawnSelector() { }

            public SpawnSelector(string spawnFile)
            {
                _availableSpawns = Pool.Get<List<Vector3>>();

                try
                {
                    if (!Spawns.IsLoaded)
                    {
                        Debug.Log($"[Arena] Spawns Database is not loaded. Unable to load spawn file {spawnFile}");
                        return;
                    }

                    List<Vector3> list = Spawns.LoadSpawnFile(spawnFile) as List<Vector3>;
                    if (list == null)
                    {
                        Debug.Log($"[Arena] Unable to load spawn file {spawnFile}. It either does not exist, or has no spawn points");
                        return;
                    }

                    _defaultSpawns = list;
                    _availableSpawns.AddRange(_defaultSpawns);
                }
                finally
                {
                    Count = _availableSpawns.Count;
                }
            }

            public Vector3 GetSpawnPoint()
            {
                Vector3 point = _availableSpawns.GetRandom();
                _availableSpawns.Remove(point);

                if (_availableSpawns.Count == 0)
                    _availableSpawns.AddRange(_defaultSpawns);

                return point;
            }

            public Vector3 ReserveSpawnPoint(int index)
            {
                Vector3 reserved = _defaultSpawns[index];
                _defaultSpawns.RemoveAt(index);

                _availableSpawns.Clear();
                _availableSpawns.AddRange(_defaultSpawns);

                return reserved;
            }

            public void Destroy()
            {
                Pool.FreeUnmanaged(ref _availableSpawns);
            }
        }
        #endregion

        #region Event Config
        public class EventConfig
        {
            public string EventName { get; set; } = string.Empty;
            public string EventType { get; set; } = string.Empty;

            public string ZoneID { get; set; } = string.Empty;
            public string Permission { get; set; } = string.Empty;

            public string EventIcon { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;

            public int TimeLimit { get; set; }
            public int ScoreLimit { get; set; }
            public int MinimumPlayers { get; set; }
            public int MaximumPlayers { get; set; }

            public bool UseEventBots { get; set; }
            public int MaximumBots { get; set; }

            public bool AllowClassSelection { get; set; }

            public bool IsDisabled { get; set; }

            public int RoundsToPlay { get; set; }
            
            //public List<string> KeepItems { get; set; } = new List<string>();

            public TeamConfig TeamConfigA { get; set; } = new();

            public TeamConfig TeamConfigB { get; set; } = new();

            public RewardOptions Rewards { get; set; } = new();

            public SerializedTeleporter Teleporter { get; set; }

            public Hash<string, object> AdditionalParams { get; set; } = new();

            public EventConfig() { }

            public EventConfig(string type, IEventPlugin eventPlugin)
            {
                EventType = type;
                Plugin = eventPlugin;

                if (eventPlugin.AdditionalParameters != null)
                {
                    for (int i = 0; i < eventPlugin.AdditionalParameters.Count; i++)
                    {
                        EventParameter eventParameter = eventPlugin.AdditionalParameters[i];

                        if (eventParameter.DefaultValue == null && eventParameter.IsList)
                            AdditionalParams[eventParameter.Field] = new List<string>();
                        else AdditionalParams[eventParameter.Field] = eventParameter.DefaultValue;
                    }
                }
            }
            
            public bool HasParameter(string key) => AdditionalParams.ContainsKey(key);

            public T GetParameter<T>(string key)
            {
                try
                {
                    if (AdditionalParams.TryGetValue(key, out object obj))
                        return (T)Convert.ChangeType(obj, typeof(T));
                }
                catch { }

                return default(T);
            }

            public string GetString(string fieldName)
            {
                switch (fieldName)
                {
                    case "teamASpawnfile":
                        return TeamConfigA.Spawnfile;
                    case "teamBSpawnfile":
                        return TeamConfigB.Spawnfile;
                    case "zoneID":
                        return ZoneID;
                    default:
                        if (AdditionalParams.TryGetValue(fieldName, out object obj) && obj is string value)
                            return value;
                        return null;
                }
            }

            public List<string> GetList(string fieldName)
            {
                switch (fieldName)
                {
                    case "teamAKits":
                        return TeamConfigA.Kits;
                    case "teamBKits":
                        return TeamConfigB.Kits;
                    default:
                        if (AdditionalParams.TryGetValue(fieldName, out object obj) && obj is List<string> list)
                            return list;
                        return null;
                }
            }

            public string TeamName(Team team)
            {
                TeamConfig teamConfig = team == Team.B ? TeamConfigB : TeamConfigA;
               
                return string.IsNullOrEmpty(teamConfig.Name) ? (team == Team.B ? Plugin.TeamBName : Plugin.TeamAName) : teamConfig.Name;
            }

            public class RewardOptions
            {
                public int KillAmount { get; set; }

                public int WinAmount { get; set; }

                public int HeadshotAmount { get; set; }

                public string Type { get; set; } = "Scrap";
            }

            public class TeamConfig
            {
                public string Name { get; set; } = string.Empty;

                public string Color { get; set; } = string.Empty;

                public string Spawnfile { get; set; } = string.Empty;

                public string Clothing { get; set; } = string.Empty;

                public List<string> Kits { get; set; } = new();
            }

            public class SerializedTeleporter
            {
                public float X, Y, Z;

                public float Radius = 0.75f;

                [JsonIgnore]
                public Vector3 Position => new(X, Y, Z);

                public SerializedTeleporter() { }

                public SerializedTeleporter(Vector3 v)
                {
                    X = v.x;
                    Y = v.y;
                    Z = v.z;
                }
            }

            [JsonIgnore]
            public IEventPlugin Plugin { get; set; }
        }
        #endregion
        #endregion

        #region Rewards
        private void GiveReward(BaseEventPlayer baseEventPlayer, RewardType rewardType, int amount)
        {
            if (amount <= 0)
                return;

            switch (rewardType)
            {
                case RewardType.ServerRewards:
                    ServerRewards?.Call("AddPoints", baseEventPlayer.Player.UserIDString, amount);
                    break;
                case RewardType.Economics:
                    Economics?.Call("Deposit", baseEventPlayer.Player.UserIDString, (double)amount);
                    break;
                case RewardType.Scrap:
                    Restore.Data.AddItem(baseEventPlayer.Player.userID, scrapItemId, amount);
                    break;
            }
        }

        private string[] GetRewardTypes() => new[] { "Scrap", "ServerRewards", "Economics" };
        #endregion

        #region Enums
        public enum RewardType { ServerRewards, Economics, Scrap }

        public enum EventStatus { Finished, Open, Prestarting, Started }

        public enum Team { A, B, None }

        private enum DropOnDeath { Nothing, Ammo, Backpack, Corpse, Weapon }
        #endregion

        #region Helpers  
        private static BaseEventPlayer GetUser(BasePlayer player) => !player ? null : player.GetComponent<BaseEventPlayer>();
        
        public static void LockClothingSlots(BasePlayer player)
        {
            if (!player)
                return;

            if (!player.inventory.containerWear.HasFlag(ItemContainer.Flag.IsLocked))
            {
                player.inventory.containerWear.SetFlag(ItemContainer.Flag.IsLocked, true);
                player.inventory.SendSnapshot();
            }
        }

        private static void UnlockClothingSlots(BasePlayer player)
        {
            if (!player)
                return;

            if (player.inventory.containerWear.HasFlag(ItemContainer.Flag.IsLocked))
            {
                player.inventory.containerWear.SetFlag(ItemContainer.Flag.IsLocked, false);
                player.inventory.SendSnapshot();
            }
        }
        
        public static void GiveKit(BasePlayer player, string kitname) => Kits.GiveKit(player, kitname);

        private static void ClearDamage(HitInfo hitInfo)
        {
            if (hitInfo == null)
                return;

            hitInfo.damageTypes.Clear();
            hitInfo.HitEntity = null;
            hitInfo.HitMaterial = 0;
            hitInfo.PointStart = Vector3.zero;
        }

        public static void ResetPlayer(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);

            if (!eventPlayer)
                return;

            if (player is ScientistNPC)
            {
                player.limitNetworking = false;

                player.health = player.MaxHealth();

                player.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);

                eventPlayer.IsDead = false;
            }
            else
            {
                if (eventPlayer.Player.IsSpectating())
                    eventPlayer.FinishSpectating();

                player.limitNetworking = false;

                player.EnablePlayerCollider();

                player.health = player.MaxHealth();

                player.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);

                eventPlayer.IsDead = false;

                eventPlayer.AddToNetwork();
            }
        }

        private static void RespawnPlayer(BaseEventPlayer eventPlayer)
        {
            if (!eventPlayer.IsDead)
                return;

            eventPlayer.DestroyUI(UI_DEATH);
            eventPlayer.DestroyUI(UI_RESPAWN);
            eventPlayer.DestroyUI(UI_CLASS_SELECT);
            eventPlayer.DestroyUI(UI_TEAM_SELECT);
            eventPlayer.DestroyUI(UI_SPECTATE);

            ResetPlayer(eventPlayer.Player);

            eventPlayer.Event.OnPlayerRespawn(eventPlayer);
        }

        private static string StripTags(string str)
        {
            if (str.StartsWith("[") && str.Contains("]") && str.Length > str.IndexOf("]"))
                str = str.Substring(str.IndexOf("]") + 1).Trim();

            if (str.StartsWith("[") && str.Contains("]") && str.Length > str.IndexOf("]"))
                StripTags(str);

            return str;
        }

        private static string TrimToSize(string str, int size = 18)
        {
            if (str.Length > size)
                str = str.Substring(0, size);
            return str;
        }

        private object IsEventPlayer(BasePlayer player) => player.GetComponent<BaseEventPlayer>() != null ? true : null;

        private object IsEventPlayerDead(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = player.GetComponent<BaseEventPlayer>();
            if (eventPlayer)
                return eventPlayer.IsDead;
            return false;
        }

        private object isEventPlayer(BasePlayer player) => IsEventPlayer(player);
        #endregion

        #region Zone Management
        private void OnExitZone(string zoneId, BasePlayer player)
        {
            if (!player)
                return;

            BaseEventPlayer eventPlayer = GetUser(player);

            if (!string.IsNullOrEmpty(Configuration.Lobby.LobbyZoneID) && zoneId == Configuration.Lobby.LobbyZoneID)
            {
                if (!eventPlayer && Lobby.IsEnabled && Configuration.Lobby.KeepPlayersInLobby)
                {
                    if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
                        return;

                    player.LocalizedMessage(Instance, "Lobby.LeftZone");
                    Lobby.ReturnToLobby(player);
                    return;
                }
            }

            if (!eventPlayer || eventPlayer.IsDead)
                return;

            if (zoneId == eventPlayer.Event.Config.ZoneID)
                eventPlayer.IsOutOfBounds = true;
        }

        private void OnEnterZone(string zoneId, BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);

            if (!eventPlayer || eventPlayer.IsDead)
                return;

            if (zoneId == eventPlayer.Event.Config.ZoneID)
                eventPlayer.IsOutOfBounds = false;
        }

        private static void SetZoneOccupied(BaseEventGame baseEventGame, bool isOccupied)
        {
            if (string.IsNullOrEmpty(baseEventGame.Config.ZoneID))
                return;

            if (isOccupied)
                ZoneManager.AddFlag(baseEventGame.Config.ZoneID, "Eject");
            else ZoneManager.RemoveFlag(baseEventGame.Config.ZoneID, "Eject");
        }
        #endregion

        #region Rotating Pickups
        private static void RotateDroppedItem(WorldItem worldItem)
        {
            if (Instance.RotatingPickups && Configuration.Event.UseRotator)
            {
                if (worldItem)
                    Instance.RotatingPickups.Call("AddItemRotator", worldItem, true);
            }
        }
        #endregion

        #region File Validation

        private object ValidateEventConfig(EventConfig eventConfig)
        {
            if (string.IsNullOrEmpty(eventConfig.EventType) || !EventModes.TryGetValue(eventConfig.EventType, out IEventPlugin plugin))
                return string.Concat("Event mode ", eventConfig.EventType, " is not currently loaded");

            if (!plugin.CanUseClassSelector && eventConfig.TeamConfigA.Kits.Count == 0)
                return "You must set atleast 1 kit";

            if (eventConfig.MinimumPlayers == 0)
                return "You must set the minimum players";

            if (eventConfig.MaximumPlayers == 0)
                return "You must set the maximum players";

            if (plugin.RequireTimeLimit && eventConfig.TimeLimit == 0)
                return "You must set a time limit";

            if (plugin.RequireScoreLimit && eventConfig.ScoreLimit == 0)
                return "You must set a score limit";

            object success;

            foreach (string kit in eventConfig.TeamConfigA.Kits)
            {
                success = ValidateKit(kit);
                if (success is string)
                    return $"Invalid kit: {kit}";
            }

            success = ValidateSpawnFile(eventConfig.TeamConfigA.Spawnfile);
            if (success is string)
                return $"Invalid spawn file: {eventConfig.TeamConfigA.Spawnfile}";

            if (plugin.IsTeamEvent)
            {
                success = ValidateSpawnFile(eventConfig.TeamConfigB.Spawnfile);
                if (success is string)
                    return $"Invalid second spawn file: {eventConfig.TeamConfigB.Spawnfile}";

                if (eventConfig.TeamConfigB.Kits.Count == 0)
                    return "You must set atleast 1 kit for Team B";

                foreach (string kit in eventConfig.TeamConfigB.Kits)
                {
                    success = ValidateKit(kit);
                    if (success is string)
                        return $"Invalid kit: {kit}";
                }
            }

            success = ValidateZoneID(eventConfig.ZoneID);
            if (success is string)
                return $"Invalid zone ID: {eventConfig.ZoneID}";

            for (int i = 0; i < plugin.AdditionalParameters?.Count; i++)
            {
                EventParameter eventParameter = plugin.AdditionalParameters[i];

                if (eventParameter.IsRequired)
                {
                    eventConfig.AdditionalParams.TryGetValue(eventParameter.Field, out object value);

                    if (value == null)
                        return $"Missing event parameter: ({eventParameter.DataType}){eventParameter.Field}";
                    success = plugin.ParameterIsValid(eventParameter.Field, value);
                    if (success is string s)
                        return s;
                }
            }

            return null;
        }

        private object ValidateSpawnFile(string name)
        {
            object success = Spawns.IsLoaded ? Spawns.GetSpawnsCount(name) : null;
            if (success is string s)
                return s;
            return null;
        }

        private object ValidateZoneID(string name)
        {
            object success = ZoneManager.IsLoaded ? ZoneManager.CheckZoneID(name) : null;
            if (name is string && !string.IsNullOrEmpty(name))
                return null;
            return $"Zone \"{name}\" does not exist!";
        }

        public object ValidateKit(string name)
        {
            object success = Kits.IsLoaded ? Kits.IsKit(name) : null;
            if ((success is bool b))
            {
                if (!b)
                    return $"Kit \"{name}\" does not exist!";
            }
            return null;
        }
        #endregion

        #region Scoring
        public struct ScoreEntry
        {
            public int position;
            public string displayName;
            public float value1;
            public float value2;
            public Team team;

            public ScoreEntry(BaseEventPlayer eventPlayer, int position, float value1, float value2)
            {
                this.position = position;
                displayName = StripTags(eventPlayer.Player.displayName);
                team = eventPlayer.Team;
                this.value1 = value1;
                this.value2 = value2;
            }

            public ScoreEntry(BaseEventPlayer eventPlayer, float value1, float value2)
            {
                position = 0;
                displayName = StripTags(eventPlayer.Player.displayName);
                team = eventPlayer.Team;
                this.value1 = value1;
                this.value2 = value2;
            }

            public ScoreEntry(float value1, float value2)
            {
                position = 0;
                displayName = string.Empty;
                team = Team.None;
                this.value1 = value1;
                this.value2 = value2;
            }
        }

        private class EventResults
        {
            public string EventName { get; private set; }

            public string EventType { get; private set; }

            public ScoreEntry TeamScore { get; private set; }

            private IEventPlugin Plugin { get; set; }

            private List<ScoreEntry> Scores { get; set; } = new();

            public bool IsValid => Plugin != null;

            public void UpdateFromEvent(BaseEventGame baseEventGame)
            {
                EventName = baseEventGame.Config.EventName;
                EventType = baseEventGame.Config.EventType;
                Plugin = baseEventGame.Plugin;

                TeamScore = Plugin.IsTeamEvent ? new ScoreEntry(baseEventGame.GetTeamScore(Team.A), baseEventGame.GetTeamScore(Team.B)) : default(ScoreEntry);

                Scores.Clear();

                if (baseEventGame.scoreData.Count > 0)
                    Scores.AddRange(baseEventGame.scoreData);
            }
        }
        #endregion

        #region Lobby TP
        [ChatCommand("lobby")]
        private void cmdLobbyTP(BasePlayer player, string command, string[] args)
        {
            if (!Lobby.IsEnabled)
                return;

            Lobby.TeleportToLobbyCommand(player);
        }

        [ChatCommand("lobbyc")]
        private void cmdLobbyCancel(BasePlayer player, string command, string[] args)
        {
            if (!Lobby.IsEnabled)
                return;

            Lobby.CancelLobbyTeleportCommand(player);
        }

        private class LobbyHandler
        {
            private SpawnSelector _spawns;

            private Hash<ulong, Action> _pendingLobbyTP = new();

            private Hash<ulong, double> _cooldownLobbyTP = new();

            public bool IsEnabled { get; private set; }

            public bool ForceLobbyRespawn => IsEnabled && Configuration.Lobby.ForceLobbyRespawn;

            public LobbyHandler(string spawnFile)
            {
                if (string.IsNullOrEmpty(spawnFile))                
                    return;                

                _spawns = new SpawnSelector(spawnFile);
                if (_spawns.Count == 0)
                    return;

                IsEnabled = true;
            }

            public void SetupLobbyTeleporters()
            {
                if (Configuration.Lobby.Teleporters.Count == 0)
                    return;

                for (int i = 0; i < Configuration.Lobby.Teleporters.Count; i++)
                    CreateLobbyTeleporter(Configuration.Lobby.Teleporters[i]);
            }

            public void CreateLobbyTeleporter(ConfigData.LobbyOptions.LobbyTeleporter teleporter)
            {
                if (!teleporter.Enabled)
                    return;
                    
                Instance.CreateEventTeleporter(Instance, teleporter.ID, teleporter.Position, teleporter.Radius, CanEnterTeleporter, EnterTeleporter, GetTeleporterInformation);
            }

            private bool CanEnterTeleporter(BasePlayer player)
            {
                if (player.HasPermission(BLACKLISTED_PERMISSION))
                {
                    player.LocalizedMessage(Instance, "Error.Blacklisted");
                    return false;
                }

                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer)
                    return false;

                return true;
            }

            private void EnterTeleporter(BasePlayer player)
            {
                if (GetUser(player) != null)
                {
                    player.LocalizedMessage(Instance, "Lobby.InEvent");
                    return;
                }

                if (_pendingLobbyTP.ContainsKey(player.userID))
                {
                    player.LocalizedMessage(Instance, "Lobby.IsPending");
                    return;
                }

                string str = MeetsLobbyTpRequirements(player);
                if (!string.IsNullOrEmpty(str))
                {
                    player.ChatMessage(str);
                    return;
                }
                
                TeleportToLobby(player);
            }
            
            private string GetTeleporterInformation()
            {
                return Message("Info.Teleporter.Lobby");
            }

            public void SendRespawnOptions(BasePlayer player)
            {
                if (!player || player.IsNpc || !player.IsConnected)
                    return;

                using (RespawnInformation respawnInformation = Pool.Get<RespawnInformation>())
                {
                    respawnInformation.spawnOptions = Pool.Get<List<RespawnInformation.SpawnOptions>>();

                    RespawnInformation.SpawnOptions d = Pool.Get<RespawnInformation.SpawnOptions>();
                    d.id = LOBBY_BAG_ID;
                    d.name = Message("Lobby.RespawnButton", player.userID);
                    d.worldPosition = _spawns.GetSpawnPoint();
                    d.type = RespawnInformation.SpawnOptions.RespawnType.Bed;
                    d.unlockSeconds = 0;

                    respawnInformation.spawnOptions.Add(d);

                    respawnInformation.previousLife = player.previousLifeStory;
                    respawnInformation.fadeIn = (player.previousLifeStory != null && player.previousLifeStory.timeDied > (Epoch.Current - 5));

                    player.ClientRPCPlayer(null, player, "OnRespawnInformation", respawnInformation);
                }
            }

            public void RespawnAtLobby(BasePlayer player)
            {
                player.RespawnAt(_spawns.GetSpawnPoint(), Quaternion.identity);

                if (!Configuration.Server.RestorePlayers && !string.IsNullOrEmpty(Configuration.Lobby.LobbyKit))
                    GiveKit(player, Configuration.Lobby.LobbyKit);
            }

            public void ReturnToLobby(BasePlayer player) => player.Teleport(_spawns.GetSpawnPoint(), Quaternion.identity, false);

            public void TeleportToLobbyCommand(BasePlayer player)
            {
                if (!Configuration.Lobby.TP.AllowLobbyTP)
                    return;

                if (GetUser(player) != null)
                {
                    player.LocalizedMessage(Instance, "Lobby.InEvent");
                    return;
                }

                if (_pendingLobbyTP.ContainsKey(player.userID))
                {
                    player.LocalizedMessage(Instance, "Lobby.IsPending");
                    return;
                }

                if (_cooldownLobbyTP.TryGetValue(player.userID, out double time))
                {
                    if (time > Time.realtimeSinceStartup)
                    {
                        player.ChatMessage(string.Format(Message("Lobby.OnCooldown", player.userID), Mathf.RoundToInt((float)time - Time.realtimeSinceStartup)));
                        return;
                    }
                }

                string str = MeetsLobbyTpRequirements(player);
                if (!string.IsNullOrEmpty(str))
                {
                    player.ChatMessage(str);
                    return;
                }

                if (Configuration.Lobby.TP.Timer == 0)
                {
                    TeleportToLobby(player);
                    return;
                }

                Action action = () => TryTeleportToLobby(player);
                player.Invoke(action, Configuration.Lobby.TP.Timer);
                _pendingLobbyTP[player.userID] = action;
                player.ChatMessage(string.Format(Message("Lobby.TPConfirmed", player.userID), Configuration.Lobby.TP.Timer));
            }

            public void CancelLobbyTeleportCommand(BasePlayer player)
            {
                if (!Configuration.Lobby.TP.AllowLobbyTP)
                    return;

                if (!HasPendingTeleports(player))
                {
                    player.LocalizedMessage(Instance, "Lobby.NoTPPending");
                    return;
                }

                CancelPendingTeleports(player);
                player.LocalizedMessage(Instance, "Lobby.TPCancelled");
            }

            private void TryTeleportToLobby(BasePlayer player)
            {
                _pendingLobbyTP.Remove(player.userID);

                string str = MeetsLobbyTpRequirements(player);
                if (!string.IsNullOrEmpty(str))
                {
                    player.ChatMessage(str);
                    return;
                }

                TeleportToLobby(player);

                if (Configuration.Lobby.TP.Cooldown > 0)
                    _cooldownLobbyTP[player.userID] = Time.realtimeSinceStartup + Configuration.Lobby.TP.Cooldown;
            }

            public void TeleportToLobby(BasePlayer player)
            {
                if (!player)
                    return;

                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer)
                    UnityEngine.Object.DestroyImmediate(eventPlayer);

                CancelPendingTeleports(player);

                player.ResetMetabolism();

                player.Teleport(_spawns.GetSpawnPoint(), Quaternion.identity,  true);

                if (!Configuration.Server.RestorePlayers && !string.IsNullOrEmpty(Configuration.Lobby.LobbyKit))  
                    GiveKit(player, Configuration.Lobby.LobbyKit); 
            }

            private bool HasPendingTeleports(BasePlayer player) => _pendingLobbyTP.ContainsKey(player.userID);

            private void CancelPendingTeleports(BasePlayer player)
            {
                if (!player)
                    return;

                if (HasPendingTeleports(player))
                {
                    player.CancelInvoke(_pendingLobbyTP[player.userID]);
                    _pendingLobbyTP.Remove(player.userID);
                }
            }

            private string MeetsLobbyTpRequirements(BasePlayer player)
            {
                if (!string.IsNullOrEmpty(Configuration.Lobby.LobbyZoneID) && ZoneManager.IsLoaded && ZoneManager.IsPlayerInZone(Configuration.Lobby.LobbyZoneID, player))
                    return Message("Lobby.AlreadyThere", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromBuildBlock && !player.CanBuild())
                    return Message("Lobby.BuildBlocked", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromCargoShip && player.GetParentEntity() is CargoShip)
                    return Message("Lobby.Prevent.CargoShip", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromHotAirBalloon && player.GetParentEntity() is HotAirBalloon)
                    return Message("Lobby.Prevent.HAB", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromMounted && player.isMounted)
                    return Message("Lobby.Prevent.Mounted", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromOilRig && IsNearOilRig(player))
                    return Message("Lobby.Prevent.OilRig", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportWhilstBleeding && player.metabolism.bleeding.value > 0)
                    return Message("Lobby.Prevent.Bleeding", player.userID);

                if (IsRaidBlocked(player))
                    return Message("Lobby.Prevent.RaidBlocked", player.userID);

                if (IsCombatBlocked(player))
                    return Message("Lobby.Prevent.CombatBlocked", player.userID);

                if (Interface.Oxide.CallHook("CanTeleport", player) is string str)
                    return str;

                return string.Empty;
            }

            private bool IsNearOilRig(BasePlayer player)
            {
                for (int i = 0; i < TerrainMeta.Path.Monuments.Count; i++)
                {
                    MonumentInfo monumentInfo = TerrainMeta.Path.Monuments[i];

                    if (monumentInfo.gameObject.name.Contains("oilrig", CompareOptions.OrdinalIgnoreCase))
                    {
                        if (Vector3Ex.Distance2D(player.transform.position, monumentInfo.transform.position) <= 100f)
                            return true;
                    }
                }

                return false;
            }

            private bool IsRaidBlocked(BasePlayer player)
            {
                if (NoEscape.IsLoaded)
                {
                    if (Configuration.Lobby.TP.AllowTeleportWhilstRaidBlocked)
                    {
                        bool success = NoEscape.IsRaidBlocked(player);
                        if (success)
                            return true;
                    }
                }
                return false;
            }

            private bool IsCombatBlocked(BasePlayer player)
            {
                if (NoEscape.IsLoaded)
                {
                    if (Configuration.Lobby.TP.AllowTeleportWhilstCombatBlocked)
                    {
                        bool success = NoEscape.IsCombatBlocked(player);
                        if (success)
                            return true;
                    }
                }
                return false;
            }            
        }
        #endregion

        #region Create Teleporters
        [ChatCommand("teleporter")]
        private void cmdArenaTeleporter(BasePlayer player, string command, string[] args)
        {
            if (!player.HasPermission(ADMIN_PERMISSION))
                return;

            if (args.Length == 0)
            {
                SendReply(player, "<color=#ce422b>/teleporter add \"event name\"</color> - Create a arena teleporter for the specified event on your position");
                SendReply(player, "<color=#ce422b>/teleporter remove \"event name\"</color> - Remove the arena teleporter for the specified event");
                SendReply(player, "<color=#ce422b>/teleporter lobbyadd \"unique ID\"</color> - Create a lobby teleporter that teleports players to the lobby");
                SendReply(player, "<color=#ce422b>/teleporter lobbyremove \"unique ID\"</color> - Remove the lobby teleporter with the specified name");
                return;
            }

            switch (args[0].ToLower())
            {
                case "add":
                    {
                        if (args.Length < 2)
                        {
                            player.ChatMessage("Invalid syntax! <color=#ce422b>/arenateleporter add \"event name\"</color>");
                            return;
                        }

                        string eventName = args[1];
                        if (!Events.Data.events.TryGetValue(eventName, out EventConfig @event))
                        {
                            player.ChatMessage($"Unable to find an event with the name {eventName}");
                            return;
                        }

                        if (@event.Teleporter != null)
                        {
                            player.ChatMessage("This event already has a teleporter. You need to remove it before continuing");
                            return;
                        }

                        EventConfig.SerializedTeleporter serializedTeleporter = new(player.transform.position + (Vector3.up * 1.5f));
                        Events.Data.events[eventName].Teleporter = serializedTeleporter;
                       
                        Events.Save();

                        if (!Lobby.IsEnabled)
                        {
                            player.ChatMessage($"You have successfully setup a teleporter for event {eventName}, however teleporters require the lobby to be setup and enabled. Event teleporters will not be active until a lobby has been setup");
                            return;
                        }

                        if (ActiveEvents.ContainsKey(eventName))
                        {
                            ActiveEvents[eventName].CreateTeleporter(serializedTeleporter);
                            player.ChatMessage($"You have successfully setup a teleporter for event {eventName}");
                            return;
                        }

                        player.ChatMessage($"You have successfully setup a teleporter for event {eventName}, however the event is not currently running so it hasn't been created");
                    }
                    return;
                case "remove":
                    {
                        if (args.Length < 2)
                        {
                            player.ChatMessage("Invalid syntax! <color=#ce422b>/arenateleporter remove \"event name\"</color>");
                            return;
                        }

                        string eventName = args[1];
                        if (!Events.Data.events.TryGetValue(eventName, out EventConfig @event))
                        {
                            player.ChatMessage($"Unable to find an event with the name {eventName}");
                            return;
                        }

                        if (@event.Teleporter == null)
                        {
                            player.ChatMessage("This event does not have a teleporter");
                            return;
                        }

                        Events.Data.events[eventName].Teleporter = null;

                        Events.Save();

                        if (ActiveEvents.ContainsKey(eventName))
                            ActiveEvents[eventName].DestroyTeleporter();

                        player.ChatMessage($"You have removed the teleporter for event {eventName}");
                    }
                    return;
                case "addlobby":
                    {
                        if (args.Length < 2)
                        {
                            player.ChatMessage("Invalid syntax! <color=#ce422b>/arenateleporter addlobby \"unique ID\"</color>");
                            return;
                        }

                        string uniqueId = args[1];
                        
                        if (Configuration.Lobby.Teleporters.Any(x => x.ID == uniqueId))
                        {
                            player.ChatMessage($"A lobby teleporter with the ID {uniqueId} already exists");
                            return;
                        }

                        Vector3 position = player.transform.position + (Vector3.up * 1.5f);
                        ConfigData.LobbyOptions.LobbyTeleporter teleporter = new()
                        {
                            Enabled = true,
                            ID = uniqueId,
                            X = position.x,
                            Y = position.y,
                            Z = position.z,
                            Radius = 0.75f
                        };
                        
                        Configuration.Lobby.Teleporters.Add(teleporter);
                        SaveConfiguration();
                        
                        if (!Lobby.IsEnabled)
                        {
                            player.ChatMessage($"You have successfully setup a lobby teleporter, however the lobby is not setup or enabled.");
                            return;
                        }

                        Lobby.CreateLobbyTeleporter(teleporter);
                        player.ChatMessage($"You have successfully setup a teleporter for event {uniqueId}");
                    }
                    return;
                case "removelobby":
                    {
                        if (args.Length < 2)
                        {
                            player.ChatMessage("Invalid syntax! <color=#ce422b>/arenateleporter lobbyremove \"unique ID\"</color>");
                            return;
                        }

                        string uniqueId = args[1];
                        ConfigData.LobbyOptions.LobbyTeleporter teleporter = null;
                        
                        foreach (ConfigData.LobbyOptions.LobbyTeleporter t in Configuration.Lobby.Teleporters)
                        {
                            if (t.ID == uniqueId)
                            {
                                teleporter = t;
                                break;
                            }
                        }
                        
                        if (teleporter == null)
                        {
                            player.ChatMessage($"There is not a lobby teleporter with the ID {uniqueId}");
                            return;
                        }
                        
                        DestroyEventTeleporter(this, uniqueId);

                        Configuration.Lobby.Teleporters.Remove(teleporter);
                        SaveConfiguration();
                        
                        player.ChatMessage($"You have removed the lobby teleporter {uniqueId}");
                    }
                    return;
                default:
                    player.ChatMessage("Invalid syntax");
                    break;
            }
        }
        #endregion

        #region Event Enable/Disable
        [ConsoleCommand("arena.enable")]
        private void ccmdEnableEvent(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
                return;

            if (arg.Args == null || arg.Args.Length == 0)
            {
                SendReply(arg, "arena.enable <eventname> - Enable a previously disabled event");
                return;
            }

            string eventName = arg.Args[0];

            if (!Events.Data.events.ContainsKey(eventName))
            {
                SendReply(arg, "Invalid event name entered");
                return;
            }

            if (ActiveEvents.ContainsKey(eventName))
            {
                SendReply(arg, "This event is already running");
                return;
            }

            if (!Events.Data.events[eventName].IsDisabled)
            {
                SendReply(arg, "This event is not disabled");
                return;
            }

            Events.Data.events[eventName].IsDisabled = false;
            Events.Save();

            OpenEvent(eventName);

            SendReply(arg, $"{eventName} has been enabled");
        }

        [ConsoleCommand("arena.disable")]
        private void ccmdDisableEvent(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
                return;

            if (arg.Args == null || arg.Args.Length == 0)
            {
                SendReply(arg, "arena.disable <eventname> - Disable a event");
                return;
            }

            string eventName = arg.Args[0];

            if (!Events.Data.events.TryGetValue(eventName, out EventConfig @event))
            {
                SendReply(arg, "Invalid event name entered");
                return;
            }

            if (@event.IsDisabled)
            {
                SendReply(arg, "This event is already disabled");
                return;
            }

            Events.Data.events[eventName].IsDisabled = true;
            Events.Save();

            ShutdownEvent(eventName);
            
            SendReply(arg, $"{eventName} has been disabled");
        }
        #endregion

        #region Config  
        private static ConfigData Configuration;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Configuration = ConfigurationData as ConfigData;
        }

        protected override void PrepareConfigFile(ref ConfigurationFile configurationFile) => configurationFile = new ConfigurationFile<ConfigData>(Config);
        
        protected class ConfigData : BaseConfigData
        {
            [JsonProperty(PropertyName = "Server Settings")]
            public ServerOptions Server { get; set; }

            [JsonProperty(PropertyName = "Event Options")]
            public EventOptions Event { get; set; }

            [JsonProperty(PropertyName = "Lobby Options")]
            public LobbyOptions Lobby { get; set; }

            [JsonProperty(PropertyName = "Timer Options")]
            public TimerOptions Timer { get; set; }

            [JsonProperty(PropertyName = "Message Options")]
            public MessageOptions Message { get; set; }
            
            [JsonProperty(PropertyName = "UI Options")]
            public UIOptions Interface { get; set; }

            public class ServerOptions
            {
                [JsonProperty(PropertyName = "Restore players when they leave an event")]
                public bool RestorePlayers { get; set; }

                [JsonProperty(PropertyName = "Disable server events (Patrol Helicopter, Cargo Ship, Airdrops etc)")]
                public bool DisableServerEvents { get; set; }

                [JsonProperty(PropertyName = "Use inbuilt chat manager")]
                public bool UseChat { get; set; }               
            }

            public class EventOptions
            {
                [JsonProperty(PropertyName = "Create and add players to Rusts team system for team based events")]
                public bool AddToTeams { get; set; }
                
                [JsonProperty(PropertyName = "Drop on death (Nothing, Ammo, Backpack, Corpse, Weapon)")]
                public string DropOnDeath { get; set; }

                [JsonProperty(PropertyName = "Add rotator to dropped items (Requires RotatingPickups)")]
                public bool UseRotator { get; set; }

                [JsonProperty(PropertyName = "Invincibility time after respawn (seconds)")]
                public float InvincibilityTime { get; set; }

                [JsonProperty(PropertyName = "Blacklisted commands for event players")]
                public string[] CommandBlacklist { get; set; }

                [JsonProperty(PropertyName = "Restart the event when it finishes")]
                public bool StartOnFinish { get; set; }
                
                [JsonProperty(PropertyName = "Give kits to players waiting for more people to join a event")]
                public bool GiveKitsWhileWaiting { get; set; }

                [JsonProperty(PropertyName = "Automatic Health Restore Options")]
                public HealthRestore Health { get; set; }
                
                [JsonProperty(PropertyName = "Lock zones when starting an event to prevent other events from using the same zone")]
                public bool LockZonesToEvent { get; set; }
                
                /*[JsonProperty(PropertyName = "Use custom network groups for each event (allows multiple events in the same arena)")]
                public bool CustomNetworkGroups { get; set; }*/

                public class HealthRestore
                {
                    [JsonProperty(PropertyName = "Enable automatic health restoration")]
                    public bool Enabled { get; set; }

                    [JsonProperty(PropertyName = "Start restoring if no damage taken for x seconds")]
                    public float RestoreAfter { get; set; }

                    [JsonProperty(PropertyName = "Amount of health to restore every second")]
                    public float Amount { get; set; }
                }
            }

            public class LobbyOptions
            {
                [JsonProperty(PropertyName = "Force all respawns to be in the lobby (Event only servers! Do not enable on a server with regular gameplay)")]
                public bool ForceLobbyRespawn { get; set; }

                [JsonProperty(PropertyName = "Lobby spawnfile")]
                public string LobbySpawnfile { get; set; }

                [JsonProperty(PropertyName = "Lobby zone ID")]
                public string LobbyZoneID { get; set; }

                [JsonProperty(PropertyName = "Keep players in the lobby zone (Event only servers! Do not enable on a server with regular gameplay)")]
                public bool KeepPlayersInLobby { get; set; }

                [JsonProperty(PropertyName = "Lobby kit (only applies if event only server)")]
                public string LobbyKit { get; set; }

                [JsonProperty(PropertyName = "Lobby teleportation options")]
                public Teleportation TP { get; set; }

                [JsonProperty(PropertyName = "Lobby Teleporters")]
                public List<LobbyTeleporter> Teleporters { get; set; }

                public class LobbyTeleporter
                {
                    [JsonProperty(PropertyName = "Is this teleporter enabled?")]
                    public bool Enabled { get; set; }
                    
                    [JsonProperty(PropertyName = "Teleporter ID (must be unique)")]
                    public string ID { get; set; }
                    
                    [JsonProperty(PropertyName = "Teleporter position X")]
                    public float X { get; set; }

                    [JsonProperty(PropertyName = "Teleporter position Y")]
                    public float Y { get; set; }
                    
                    [JsonProperty(PropertyName = "Teleporter position Z")] 
                    public float Z { get; set; }

                    [JsonProperty(PropertyName = "Teleporter radius")]
                    public float Radius { get; set; }

                    [JsonIgnore]
                    public Vector3 Position => new(X, Y, Z);
                }
                
                public class Teleportation
                {
                    [JsonProperty(PropertyName = "Allow teleportation to the lobby (Requires a lobby spawn file)")]
                    public bool AllowLobbyTP { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if Raid Blocked (NoEscape)")]
                    public bool AllowTeleportWhilstRaidBlocked { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if Combat Blocked (NoEscape)")]
                    public bool AllowTeleportWhilstCombatBlocked { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if bleeding")]
                    public bool AllowTeleportWhilstBleeding { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if building blocked")]
                    public bool AllowTeleportFromBuildBlock { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if on the CargoShip")]
                    public bool AllowTeleportFromCargoShip { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if in a HotAirBalloon")]
                    public bool AllowTeleportFromHotAirBalloon { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if on the OilRig")]
                    public bool AllowTeleportFromOilRig { get; set; }
                    
                    [JsonProperty(PropertyName = "Allow teleportation if in a safe zone")]
                    public bool AllowTeleportFromSafeZone { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if mounted")]
                    public bool AllowTeleportFromMounted { get; set; }

                    [JsonProperty(PropertyName = "Teleportation countdown timer")]
                    public int Timer { get; set; }

                    [JsonProperty(PropertyName = "Teleportation cooldown timer")]
                    public int Cooldown { get; set; }
                }
            }

            public class TimerOptions
            {               
                [JsonProperty(PropertyName = "Match pre-start timer (seconds)")]
                public int Prestart { get; set; }

                [JsonProperty(PropertyName = "Round interval timer (seconds)")]
                public int RoundInterval { get; set; }

                [JsonProperty(PropertyName = "Backpack/corpse despawn timer (seconds)")]
                public int Corpse { get; set; }
            }

            public class MessageOptions
            {                
                [JsonProperty(PropertyName = "Broadcast when a player joins an event to event players")]
                public bool BroadcastJoiners { get; set; }
                
                [JsonProperty(PropertyName = "Broadcast when a player joins an event to server players")]
                public bool BroadcastJoinersGlobal { get; set; }

                [JsonProperty(PropertyName = "Broadcast when a player leaves an event to event players")]
                public bool BroadcastLeavers { get; set; }
                
                [JsonProperty(PropertyName = "Broadcast when a player leaves an event to server players")]
                public bool BroadcastLeaversGlobal { get; set; }

                [JsonProperty(PropertyName = "Broadcast the name(s) of the winning player(s) to chat")]
                public bool BroadcastWinners { get; set; }

                [JsonProperty(PropertyName = "Broadcast kills to event players")]
                public bool BroadcastKills { get; set; }

                [JsonProperty(PropertyName = "Chat icon Steam ID")]
                public ulong ChatIcon { get; set; }
            }

            public class UIOptions
            {
                public CommandOptions Commands { get; set; }

                public ImageOptions Images { get; set; }


                [JsonProperty(PropertyName = "Hide unused event types")]
                public bool HideUnused { get; set; }


                [JsonProperty(PropertyName = "Menu Colors")]
                public MenuColors Menu { get; set; }

                [JsonProperty(PropertyName = "Scoreboard Colors")]
                public ScoreboardColors Scoreboard { get; set; }
            }
            
            public class CommandOptions
            {
                [JsonProperty(PropertyName = "Chat command to open the event menu")]
                public string EventCommand { get; set; }

                [JsonProperty(PropertyName = "Chat command to open the statistics menu")]
                public string StatisticsCommand { get; set; }

                [JsonProperty(PropertyName = "Chat command to open the admin menu")]
                public string AdminCommand { get; set; }

                [JsonProperty(PropertyName = "Chat command to leave a event")]
                public string LeaveCommand { get; set; }
            }

            public class ImageOptions
            {               
                [JsonProperty(PropertyName = "Death screen background image")]
                public string DeathBackground { get; set; }

                [JsonProperty(PropertyName = "Default event icon image")]
                public string DefaultEventIcon { get; set; }

                [JsonProperty(PropertyName = "Server banner image")]
                public string ServerBanner { get; set; }

                [JsonProperty(PropertyName = "Server logo image")]
                public string ServerLogo { get; set; }

                [JsonProperty(PropertyName = "Exit icon")]
                public string ExitIcon { get; set; }

                [JsonProperty(PropertyName = "Return icon")]
                public string ReturnIcon { get; set; }

                [JsonProperty(PropertyName = "Respawn icon")]
                public string RespawnIcon { get; set; }

                [JsonProperty(PropertyName = "arena.statistics icon")]
                public string StatisticsIcon { get; set; }

                [JsonProperty(PropertyName = "Events icon")]
                public string EventsIcon { get; set; }

                [JsonProperty(PropertyName = "Admin icon")]
                public string AdminIcon { get; set; }
            }

            public class MenuColors
            {
                [JsonProperty(PropertyName = "Background Color")]
                public UIColor Background { get; set; }

                [JsonProperty(PropertyName = "Panel Color")]
                public UIColor Panel { get; set; }

                [JsonProperty(PropertyName = "Button Color")]
                public UIColor Button { get; set; }

                [JsonProperty(PropertyName = "Highlight Color")]
                public UIColor Highlight { get; set; }
            }

            public class ScoreboardColors
            {
                [JsonProperty(PropertyName = "Background Color")]
                public UIColor Background { get; set; }

                [JsonProperty(PropertyName = "Foreground Color")]
                public UIColor Foreground { get; set; }

                [JsonProperty(PropertyName = "Panel Color")]
                public UIColor Panel { get; set; }

                [JsonProperty(PropertyName = "Highlight Color")]
                public UIColor Highlight { get; set; }

                [JsonProperty(PropertyName = "Screen Position")]
                public UIPosition Position { get; set; }
            }

            public class UIColor
            {
                public string Hex { get; set; }
                public float Alpha { get; set; }

                [JsonIgnore]
                private string _color;

                [JsonIgnore]
                public string Get
                {
                    get
                    {
                        if (string.IsNullOrEmpty(_color))
                            _color = UI.Color(Hex, Alpha);
                        return _color;
                    }
                }
            }

            public class UIPosition
            {
                [JsonProperty(PropertyName = "Center Position X (0.0 - 1.0)")]
                public float CenterX { get; set; }

                [JsonProperty(PropertyName = "Center Position Y (0.0 - 1.0)")]
                public float CenterY { get; set; }

                [JsonProperty(PropertyName = "Panel Width")]
                public float Width { get; set; }

                [JsonProperty(PropertyName = "Panel Height")]
                public float Height { get; set; }

                private UI4 _ui4;

                public UI4 UI4
                {
                    get { return _ui4 ??= new UI4(CenterX - (Width * 0.5f), CenterY - (Height * 0.5f), CenterX + (Width * 0.5f), CenterY + (Height * 0.5f)); }
                }
            }
        }

        protected override T GenerateDefaultConfiguration<T>()
        {
            return new ConfigData
            {        
                Server = new ConfigData.ServerOptions
                {
                    RestorePlayers = true,
                    DisableServerEvents = false,                    
                    UseChat = false
                },
                Event = new ConfigData.EventOptions
                {
                    AddToTeams = true,
                    DropOnDeath = "Backpack",
                    Health = new ConfigData.EventOptions.HealthRestore
                    {
                        Enabled = false,
                        RestoreAfter = 5f,
                        Amount = 2.5f
                    },
                    InvincibilityTime = 3f,
                    UseRotator = false,
                    StartOnFinish = false,
                    CommandBlacklist = new[] { "s", "tp" },
                },
                Lobby = new ConfigData.LobbyOptions
                {
                    ForceLobbyRespawn = false,
                    LobbyKit = string.Empty,
                    LobbySpawnfile = string.Empty,
                    LobbyZoneID = string.Empty,
                    TP = new ConfigData.LobbyOptions.Teleportation
                    {
                        AllowLobbyTP = false,
                        AllowTeleportFromBuildBlock = false,
                        AllowTeleportFromCargoShip = false,
                        AllowTeleportFromHotAirBalloon = false,
                        AllowTeleportFromMounted = false,
                        AllowTeleportFromOilRig = false,
                        AllowTeleportFromSafeZone = false,
                        AllowTeleportWhilstBleeding = false,
                        AllowTeleportWhilstCombatBlocked = false,
                        AllowTeleportWhilstRaidBlocked = false,
                        Cooldown = 60,
                        Timer = 10
                    },
                    Teleporters = new List<ConfigData.LobbyOptions.LobbyTeleporter>
                    {
                        new ()
                        {
                            Enabled = false,
                            ID = "example1",
                            X = 0,
                            Y = 0,
                            Z = 0,
                            Radius = 0.75f
                        },
                        new ()
                        {
                            Enabled = false,
                            ID = "example2",
                            X = 0,
                            Y = 0,
                            Z = 0,
                            Radius = 0.75f
                        }
                    }
                },
                Message = new ConfigData.MessageOptions
                {                    
                    BroadcastJoiners = true,
                    BroadcastJoinersGlobal = false,
                    BroadcastLeavers = true,
                    BroadcastLeaversGlobal = false,
                    BroadcastWinners = true,
                    BroadcastKills = true,
                    ChatIcon = 76561198403299915
                },
                Timer = new ConfigData.TimerOptions
                {
                    Prestart = 10,
                    RoundInterval = 10,
                    Corpse = 30
                },
                Interface = new ConfigData.UIOptions
                {
                    Commands = new ConfigData.CommandOptions
                    {
                        EventCommand = "menu",
                        StatisticsCommand = "stats",
                        AdminCommand = "menu.admin",
                        LeaveCommand = "leave",
                    },
                    Images = new ConfigData.ImageOptions
                    {
                        DeathBackground = "https://www.rustedit.io/images/arena/arena_death_screen.png",
                        ServerBanner = "https://www.rustedit.io/images/arena/arena_server_banner.png",
                        DefaultEventIcon = "https://www.rustedit.io/images/arena/arena_event_placeholder.png",
                        ServerLogo = "https://www.rustedit.io/images/arena/arena_logo_small.png",
                        ExitIcon = "https://www.rustedit.io/images/arena/arena_exit.png",
                        ReturnIcon = "https://www.rustedit.io/images/arena/arena_return.png",
                        RespawnIcon = "https://www.rustedit.io/images/arena/arena_respawn.png",
                        StatisticsIcon = "https://www.rustedit.io/images/arena/arena_statistics.png",
                        EventsIcon = "https://www.rustedit.io/images/arena/arena_events.png",
                        AdminIcon = "https://www.rustedit.io/images/arena/arena_admin.png",
                    },
                    Menu = new ConfigData.MenuColors
                    {
                        Background = new ConfigData.UIColor { Hex = "#000000", Alpha = 0.9f },
                        Panel = new ConfigData.UIColor { Hex = "#2c2c2c", Alpha = 1f },
                        Button = new ConfigData.UIColor { Hex = "#222222", Alpha = 1f },
                        Highlight = new ConfigData.UIColor { Hex = "#9b2021", Alpha = 1f },
                        
                    },
                    Scoreboard = new ConfigData.ScoreboardColors
                    {
                        Background = new ConfigData.UIColor { Hex = "#000000", Alpha = 0.9f },
                        Foreground = new ConfigData.UIColor { Hex = "#252526", Alpha = 0.8f },
                        Panel = new ConfigData.UIColor { Hex = "#2d2d30", Alpha = 0.8f },
                        Highlight = new ConfigData.UIColor { Hex = "#9c2021", Alpha = 0.8f },
                        Position = new ConfigData.UIPosition { CenterX = 0.9325f, CenterY = 0.98f, Width = 0.125f, Height = 0.02f }
                    },
                    HideUnused = false,
                },
                Version = Version
            } as T;
        }
        
        protected override void OnConfigurationUpdated(VersionNumber oldVersion)
        {
            ConfigData baseConfigData = GenerateDefaultConfiguration<ConfigData>();
            if (oldVersion < new VersionNumber(2, 1, 1))
            {
                (ConfigurationData as ConfigData).Interface = baseConfigData.Interface;
            }

            if (oldVersion < new VersionNumber(2, 1, 6))
            {
                (ConfigurationData as ConfigData).Lobby.Teleporters = baseConfigData.Lobby.Teleporters;
            }
        }
        
        public static bool AddToTeams => Configuration.Event.AddToTeams;
        public static bool BroadcastKills => Configuration.Message.BroadcastKills;
        #endregion

        #region Data

        private static void SaveEventConfig(EventConfig eventConfig)
        {
            ShutdownEvent(eventConfig.EventName);

            Events.Data.events[eventConfig.EventName] = eventConfig;
            Events.Save();

            object success = Instance.OpenEvent(eventConfig.EventName);
            if (success is string s)
                Debug.LogWarning($"[Arena] - {s}");
        }
        
        
        private class EventData
        {
            public Hash<string, EventConfig> events = new();
        }

        public class EventParameter
        {
            public string Name; // The name shown in the UI
            public InputType Input; // The type of input used to select the value in the UI

            public string Field; // The name of the custom field stored in the event config
            public string DataType; // The type of the field (string, int, float, bool, List<string>)

            public bool IsRequired; // Is this field required to complete event creation?

            public string SelectorHook; // The hook that is called to gather the options that can be selected. This should return a string[] (ex. GetZoneIDs from ZoneManager, GetAllKits from Kits)
            public bool SelectMultiple; // Allows the user to select multiple elements when using the selector

            public object DefaultValue; // Set the default value for this field

            [JsonIgnore]
            public bool IsList => Input == InputType.Selector && DataType.Equals("List<string>", StringComparison.OrdinalIgnoreCase);

            public enum InputType { InputField, Toggle, Selector }
        }
        #endregion

        #region Localization
        public static string Message(string key, ulong playerId = 0U) => Instance.lang.GetMessage(key, Instance, playerId != 0U ? playerId.ToString() : null);

        protected override Dictionary<string, string> Messages { get; } = new()
        {
            ["Notification.NotEnoughToContinue"] = "There are not enough players to continue the event...",
            ["Notification.NotEnoughToStart"] = "There is not enough players to start the event...",
            ["Notification.EventOpen"] = "The event <color=#9b2021>{0}</color> (<color=#9b2021>{1}</color>) is open for players\nIt will start in <color=#9b2021>{2} seconds</color>\nType <color=#9b2021>/event</color> to join",
            ["Notification.EventClosed"] = "The event has been closed to new players",
            ["Notification.EventFinished"] = "The event has finished",
            ["Notification.MaximumPlayers"] = "The event is already at maximum capacity",
            ["Notification.LockedOut"] = "This event is locked as another event is running in the same location",
            ["Notification.PlayerJoined"] = "<color=#9b2021>{0}</color> has joined the <color=#9b2021>{1}</color> event!",
            ["Notification.PlayerLeft"] = "<color=#9b2021>{0}</color> has left the <color=#9b2021>{1}</color> event!",
            ["Notification.RoundStartsIn"] = "Round starts in",
            ["Notification.EventWin"] = "<color=#9b2021>{0}</color> won the event!",
            ["Notification.EventWin.Multiple"] = "The following players won the event; <color=#9b2021>{0}</color>",
            ["Notification.EventWin.Multiple.Team"] = "<color={0}>Team {1}</color> won the event",
            ["Notification.Teams.Unbalanced"] = "The teams are unbalanced. Shuffling players...",
            ["Notification.Teams.TeamChanged"] = "You were moved to team <color=#9b2021>{0}</color>",
            ["Notification.OutOfBounds"] = "You are out of the playable area. <color=#9b2021>Return immediately</color> or you will be killed!",
            ["Notification.OutOfBounds.Time"] = "You have <color=#9b2021>{0} seconds</color> to return...",
            ["Notification.Death.Suicide"] = "<color=#9b2021>{0}</color> killed themselves...",
            ["Notification.Death.OOB"] = "<color=#9b2021>{0}</color> tried to run away...",
            ["Notification.Death.Killed"] = "<color=#9b2021>{0}</color> was killed by <color=#9b2021>{1}</color>",
            ["Notification.Suvival.Remain"] = "(<color=#9b2021>{0}</color> players remain)",
            ["Notification.SpectateCycle"] = "Press <color=#9b2021>JUMP</color> to cycle spectate targets",
            ["Notification.NextRoundStartsIn"] = "Round {0} has finished! The next round starts in {1} seconds",
            ["Notification.NextEventStartsIn"] = "The next event starts in {0} seconds", 
            ["Notification.WaitingForPlayers"] = "Waiting for atleast {0} more players to start...",
            ["Notification.JoinerSpectate"] = "You are spectating until the event allows more players",

            ["Lobby.InEvent"] = "You can not teleport to the lobby whilst in an event",
            ["Lobby.IsPending"] = "You already have a pending TP to the lobby",
            ["Lobby.OnCooldown"] = "You must wait another {0} seconds before you can use this command again",
            ["Lobby.TPConfirmed"] = "You will be teleported to the lobby in {0} seconds",
            ["Lobby.NoTPPending"] = "You do not have a pending TP request to the lobby",
            ["Lobby.TPCancelled"] = "You have cancelled your request to TP to the lobby",
            ["Lobby.AlreadyThere"] = "You are already in the lobby zone",
            ["Lobby.BuildBlocked"] = "You can not TP to the lobby when building blocked",
            ["Lobby.Prevent.CargoShip"] = "You can not TP to the lobby whilst on the cargo ship",
            ["Lobby.Prevent.HAB"] = "You can not TP to the lobby whilst in a hot air balloon",
            ["Lobby.Prevent.Mounted"] = "You can not TP to the lobby whilst mounted",
            ["Lobby.Prevent.OilRig"] = "You can not TP to the lobby whilst on a oil rig",
            ["Lobby.Prevent.Bleeding"] = "You can not TP to the lobby whilst bleeding",
            ["Lobby.Prevent.RaidBlocked"] = "You can not TP to the lobby whilst raid blocked",
            ["Lobby.Prevent.CombatBlocked"] = "You can not TP to the lobby whilst combat blocked",
            ["Lobby.RespawnButton"] = "Event Lobby",
            ["Lobby.LeftZone"] = "You are not allowed to leave the lobby area",

            ["Info.Event.Current"] = "Current Event: {0} ({1})",
            ["Info.Event.Players"] = "\n{0} / {1} Players",
            ["Info.Event.Status"] = "Status : {0}",
            ["UI.NextGameStartsIn"] = "The next game starts in",
            ["UI.SelectClass"] = "Select a class to continue...",
            ["UI.Death.Killed"] = "You were killed by {0}",
            ["UI.Death.Suicide"] = "You are dead...",
            ["UI.Death.OOB"] = "Don't wander off...",

            ["Timer.NextRoundStartsIn"] = "The next round starts in",

            ["UI.EventWin"] = "<color=#9b2021>{0}</color> won the event!",
            ["UI.EventWin.Multiple"] = "Multiple players won the event!",
            ["UI.EventWin.Multiple.Team"] = "<color={0}>Team {1}</color> won the event",

            ["Error.CommandBlacklisted"] = "You can not run that command whilst playing an event",
            ["Error.Blacklisted"] = "You are blacklisted from joining events",
            ["Error.NoPermission"] = "This event is for <color=#ce422b>donators</color> only!",
            ["Error.IsAnotherEvent"] = "You can not join a event whilst in another event",
            
            ["Info.Teleporter"] = "<size=25>{0}</size><size=16>\nGame: {1}\nPlayers: {2} / {3}\nRound: {4} / {5}\nStatus: {6}\n{7}\nKit: {8}</size>",
            ["Info.Teleporter.Score"] = "Score Limit: {0}",
            ["Info.Teleporter.Time"] = "Time Limit: {0}",
            ["Info.Teleporter.Lobby"] = "<size=25>TP to Arena Lobby</size>",

            ["UI.Event.Current"] = "Current Event",
            ["UI.Event.NoEvent"] = "No event in progress",
            ["UI.Event.CurrentScores"] = "Scoreboard",
            ["UI.Event.NoScoresRecorded"] = "No scores have been recorded yet",
            ["UI.Event.TeamScore"] = "Team Scores",
            ["UI.Event.Previous"] = "Previous Event Scores",
            ["UI.Event.NoPrevious"] = "No event has been played yet",

            ["UI.Event.Name"] = "Name",
            ["UI.Event.Type"] = "Type",
            ["UI.Event.Description"] = "Description",
            ["UI.Event.Status"] = "Status",
            ["UI.Event.Locked"] = "Locked",
            ["UI.Event.Rounds"] = "Round",
            ["UI.Event.Bots"] = "Bots",
            ["UI.Event.Players"] = "Players",
            ["UI.Players.Format"] = "{0} / {1} ({2} in queue)",
            ["UI.Event.TimeLimit"] = "Time Limit",
            ["UI.Event.ScoreLimit"] = "Score Limit",
            ["UI.Event.WinReward"] = "Win Reward",
            ["UI.Event.KillReward"] = "Kill Reward",
            ["UI.Event.HeadshotReward"] = "Headshot Reward",
            ["UI.Event.ClassSelection"] = "Class Selector",

            ["UI.Event.Leave"] = "Leave Event",
            ["UI.Event.Enter"] = "Enter Event",
            ["UI.Event.LockedOut"] = "Another event is using this area",
            ["UI.Event.VIP"] = "VIP Only Event",
            ["UI.Event.Join.Team"] = "Join {0} <size=8>({1} players)</size>",
            ["UI.Popup.EnterEvent"] = "You have entered the event",
            ["UI.Popup.LeaveEvent"] = "You have left the event",

            ["UI.Reward.Format"] = "{0} {1}",
            ["UI.Reward.Scrap"] = "Scrap",
            ["UI.Reward.Economics"] = "Coins",
            ["UI.Reward.ServerRewards"] = "RP",

            ["UI.Admin.Title"] = "Admin Options",
            ["UI.Admin.Start"] = "Start Event",
            ["UI.Admin.Close"] = "Close Event",
            ["UI.Admin.End"] = "End Event",
            ["UI.Admin.Kick"] = "Kick Player",
            ["UI.Admin.Open"] = "Open Event",
            ["UI.Admin.Edit"] = "Edit Event",
            ["UI.Admin.Create"] = "Create Event",
            ["UI.Admin.Delete"] = "Delete Event",

            ["UI.Menu.Admin"] = "Admin",
            ["UI.Menu.Statistics"] = "arena.statistics",
            ["UI.Menu.Event"] = "Event",

            ["UI.LeaveEvent"] = "Leave Event",
            ["UI.JoinEvent"] = "Join Event",

            ["UI.Statistics.Personal"] = "Personal Statistics",
            ["UI.Statistics.Global"] = "Global Statistics",
            ["UI.Statistics.Leaders"] = "Leader Boards",
            ["UI.NoStatisticsSaved"] = "No statistics have been recorded yet",

            ["UI.Rank"] = "Rank",
            ["UI.GamesPlayed"] = "Games Played",

            ["UI.Next"] = "Next",
            ["UI.Back"] = "Back",

            ["UI.Player"] = "Player",
            ["UI.Score"] = "Score",
            ["UI.Kills"] = "Kills",
            ["UI.Deaths"] = "Deaths",
            ["UI.Assists"] = "Kill Assists",
            ["UI.Headshots"] = "Headshots",
            ["UI.Melee"] = "Melee Kills",
            ["UI.Won"] = "Games Won",
            ["UI.Lost"] = "Games Lost",
            ["UI.Played"] = "Games Played",

            ["UI.Totals"] = "Total {0}",

            ["UI.Return"] = "RETURN",
            ["UI.Events"] = "EVENTS",
            ["UI.Leaders"] = "LEADERS",
            ["UI.Admin"] = "ADMIN",
            ["UI.Exit"] = "EXIT",
            ["UI.Close"] = "Cancel",

            ["UI.Death.Leave"] = "LEAVE",
            ["UI.Death.Respawn"] = "RESPAWN",
            ["UI.Death.Respawn.Time"] = "RESPAWN ({0})",
            ["UI.Death.AutoRespawn.Enabled"] = "AUTO RESPAWN ENABLED",
            ["UI.Death.AutoRespawn.Disabled"] = "<color=#9b2021>AUTO RESPAWN DISABLED</color>",
            ["UI.Death.Spectate"] = "SPECTATE",
            ["UI.Death.ChangeTeam"] = "CHANGE TEAM",

            ["UI.Help"] = "Type <color=#ce422b>{0}</color> to open the event menu   |   Type <color=#ce422b>{1}</color> to leave the event",

            ["UI.NoEventsAvailable"] = "No events of this type are currently active",

            ["UI.Score.Team"] = "{0} : {1}",
            ["UI.Spectating"] = "Spectating : {0}",

            ["UI.Enabled"] = "Enabled",

            ["Error.Blacklisted"] = "You are blacklisted from joining events"
        };
        #endregion
        
        #region UI
        
        #region UI Menus        
        public enum MenuTab { Event, Statistics, Admin }

        private enum AdminTab { None, EditEvent, CreateEvent, DeleteEvent, Selector }

        private enum StatisticTab { Personal, Global, Leaders }

        public enum SelectionType { Field, Event, Player }


        private const float ELEMENT_HEIGHT = 0.035f;

        #region UI Images
        private const string DEATH_BACKGROUND = "death_background";
        private const string DEFAULT_EVENT_ICON = "default_event_icon";
        private const string SERVER_BANNER = "server_banner";
        private const string SMALL_LOGO = "server_logo_small";
        private const string EXIT_ICON = "exit_icon";
        private const string RETURN_ICON = "return_icon";
        private const string RESPAWN_ICON = "respawn_icon";
        private const string STATISTICS_ICON = "arena.statistics_icon";
        private const string EVENTS_ICON = "events_icon";
        private const string ADMIN_ICON = "admin_icon";

        private const string UI_MENU = "menu";
        private const string UI_TIMER = "timer";
        private const string UI_SCORES = "scores";
        private const string UI_POPUP = "popup";
        private const string UI_DEATH = "death";
        private const string UI_RESPAWN = "respawn";
        private const string UI_SPECTATE = "spectate";
        private const string UI_CLASS_SELECT = "classselect";
        private const string UI_TEAM_SELECT = "teamselect";

        private Hash<string, string> _imageIDs = new();

        private void RegisterImages()
        {
            if (!ImageLibrary.IsLoaded)
                return;

            Dictionary<string, string> loadOrder = new()
            {
                [DEATH_BACKGROUND] = Configuration.Interface.Images.DeathBackground,
                [DEFAULT_EVENT_ICON] = Configuration.Interface.Images.DefaultEventIcon,
                [SERVER_BANNER] = Configuration.Interface.Images.ServerBanner,
                [SMALL_LOGO] = Configuration.Interface.Images.ServerLogo,
                [EXIT_ICON] = Configuration.Interface.Images.ExitIcon,
                [RETURN_ICON] = Configuration.Interface.Images.ReturnIcon,
                [RESPAWN_ICON] = Configuration.Interface.Images.RespawnIcon,
                [STATISTICS_ICON] = Configuration.Interface.Images.StatisticsIcon,
                [EVENTS_ICON] = Configuration.Interface.Images.EventsIcon,
                [ADMIN_ICON] = Configuration.Interface.Images.AdminIcon,

            };

            foreach (IEventPlugin eventPlugin in Instance.EventModes.Values)
                loadOrder.Add($"Event.{eventPlugin.EventName}", eventPlugin.EventIcon);

            foreach (EventConfig eventConfig in Events.Data.events.Values)
            {
                if (!string.IsNullOrEmpty(eventConfig.EventIcon))
                    loadOrder.Add(eventConfig.EventName, eventConfig.EventIcon);
            }

            ImageLibrary.ImportImageList("ArenaUI", loadOrder, 0UL, true, null);
        }

        private void AddImage(string imageName, string url) => ImageLibrary.Call("AddImage", url, imageName);

        private string GetImage(string name)
        {
            if (!_imageIDs.TryGetValue(name, out string result))
                result = _imageIDs[name] = ImageLibrary.GetImage(name);

            return result;
        }
        #endregion

        #region Match Menu
        private void OpenMatchMenu(BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Interface.Menu.Background.Get, new UI4(0.01f, 0.8f, 0.12f, 0.99f), true);
            UI.Image(container, UI_MENU, GetImage(SMALL_LOGO), new UI4(0, 0.34f, 1, 1));
            UI.Button(container, UI_MENU, Configuration.Interface.Menu.Button.Get, Message("UI.LeaveEvent", eventPlayer.Player.userID), 15, new UI4(0, 0.17f, 1, 0.32f), "arena.leaveevent");
            UI.Button(container, UI_MENU, Configuration.Interface.Menu.Button.Get, Message("UI.Close", eventPlayer.Player.userID), 15, new UI4(0, 0, 1, 0.15f), "arena.close");

            eventPlayer.AddUI(UI_MENU, container);
        }

        private static void ShowHelpText(BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_DEATH, Configuration.Interface.Menu.Background.Get, new UI4(0.343f, 0, 0.641f, 0.015f));
            UI.Label(container, UI_DEATH, string.Format(Message("UI.Help", eventPlayer.Player.userID), $"/{Configuration.Interface.Commands.EventCommand}", $"/{Configuration.Interface.Commands.LeaveCommand}"), 10, UI4.Full);

            eventPlayer.DestroyUI(UI_DEATH);
            eventPlayer.AddUI(UI_DEATH, container);
        }
        #endregion

        #region Event Menu
        private readonly GridAlignment CategoryGrid = new(5, 0.025f, 0.18f, 0.0125f, 0.82f, 0.32f, 0.075f);

        private void OpenEventMenu(BasePlayer player, string eventType, string eventName, int page)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Interface.Menu.Background.Get, new UI4(0.1f, 0.1f, 0.9f, 0.9f), true);

            UI.Image(container, UI_MENU, GetImage(SERVER_BANNER), new UI4(0f, 0.86f, 1f, 1f));

            AddImageIcon(container, GetImage(EXIT_ICON), Message("UI.Exit", player.userID), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arena.close");
            AddImageIcon(container, GetImage(STATISTICS_ICON), Message("UI.Leaders", player.userID), new UI4(0.01f, 0.905f, 0.055f, 0.985f), "arena.statistics 0 0 0");

            if (player.HasPermission(ADMIN_PERMISSION))
                AddImageIcon(container, GetImage(ADMIN_ICON), Message("UI.Admin", player.userID), new UI4(0.06f, 0.905f, 0.115f, 0.985f), "arena.admin");

            if (string.IsNullOrEmpty(eventType))
                SelectEventButtons(player, container, page);
            else if (string.IsNullOrEmpty(eventName))
                CreateEventGrid(player, container, eventType, page);
            else CreateEventView(player, container, eventName, page);

            CuiHelper.DestroyUi(player, UI_MENU);
            CuiHelper.AddUi(player, container);
        }

        private void AddImageIcon(CuiElementContainer container, string icon, string text, UI4 dimensions, string command)
        {
            UI.Button(container, UI_MENU, icon, dimensions, command);
            UI.Label(container, UI_MENU, text, 10, new UI4(dimensions.xMin, dimensions.yMin - 0.03f, dimensions.xMax, dimensions.yMin), TextAnchor.UpperCenter);
        }

        #region Event Type Selector
        private void SelectEventButtons(BasePlayer player, CuiElementContainer container, int page)
        {
            List<IEventPlugin> list = Pool.Get<List<IEventPlugin>>();

            GetRegisteredEvents(list);

            if (Configuration.Interface.HideUnused)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    IEventPlugin eventMode = list[i];

                    if (!HasActiveEventsOfType(eventMode.EventName))
                        list.Remove(eventMode);
                }                
            }

            int max = Mathf.Min(list.Count, (page + 1) * 10);
            int count = 0;
            for (int i = page * 10; i < max; i++)
            {
                CreatePluginEntry(player, container, list[i], count);
                count += 1;
            }

            if (page > 0)
                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "◀\n\n◀\n\n◀", 16, new UI4(0.005f, 0.35f, 0.02f, 0.58f), $"arena.categoryview {page - 1}");
            if (max < list.Count)
                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "▶\n\n▶\n\n▶", 16, new UI4(0.98f, 0.35f, 0.995f, 0.58f), $"arena.categoryview {page + 1}");

            Pool.FreeUnmanaged(ref list);
        }

        private void CreatePluginEntry(BasePlayer player, CuiElementContainer container, IEventPlugin eventPlugin, int index)
        {
            UI4 position = CategoryGrid.Evaluate(index);

            UI.Button(container, UI_MENU, GetImage($"Event.{eventPlugin.EventName}"), position, $"arena.gridview page {CommandSafe(eventPlugin.EventName)} 0");
            UI.Label(container, UI_MENU, eventPlugin.EventName.ToUpper(), 16, new UI4(position.xMin, position.yMin - 0.04f, position.xMax, position.yMin));
        }

        #region Event Selector Commands
        [ConsoleCommand("arena.categoryview")]
        private void ccmdArenaCategoryView(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null)
                return;

            OpenEventMenu(player, string.Empty, string.Empty, arg.GetInt(0));
        }
        #endregion
        #endregion

        #region Grid View
        private readonly GridAlignment EventGrid = new(5, 0.025f, 0.18f, 0.0125f, 0.8f, 0.32f, 0.1f);

        private void CreateEventGrid(BasePlayer player, CuiElementContainer container, string eventType, int page)
        {
            List<BaseEventGame> list = Pool.Get<List<BaseEventGame>>();

            GetEventsOfType(eventType, list);
                        
            AddImageIcon(container, GetImage(RETURN_ICON), Message("UI.Return", player.userID), new UI4(0.89f, 0.905f, 0.94f, 0.985f), "arena.categoryview 0");

            if (list.Count == 0)
            {
                UI.Label(container, UI_MENU, Message("UI.NoEventsAvailable", player.userID), 14, new UI4(0.015f, 0.8f, 0.99f, 0.86f), TextAnchor.MiddleLeft);
                Pool.FreeUnmanaged(ref list);
                return;
            }

            int max = Mathf.Min(list.Count, (page + 1) * 10);
            int count = 0;
            for (int i = page * 10; i < max; i++)
            {
                CreateEventEntry(player, container, list[i], count);
                count += 1;
            }

            if (page > 0)
                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "◀\n\n◀\n\n◀", 16, new UI4(0.005f, 0.35f, 0.02f, 0.58f), $"arena.gridview page {CommandSafe(eventType)} {page - 1}");
            if (max < list.Count)
                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "▶\n\n▶\n\n▶", 16, new UI4(0.98f, 0.35f, 0.995f, 0.58f), $"arena.gridview page {CommandSafe(eventType)} {page + 1}");

            Pool.FreeUnmanaged(ref list);
        }

        private void CreateEventEntry(BasePlayer player, CuiElementContainer container, BaseEventGame eventGame, int index)
        {
            UI4 position = EventGrid.Evaluate(index);

            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(position.xMin, position.yMax, position.xMax, position.yMax + 0.04f));
            UI.Label(container, UI_MENU, eventGame.Config.EventName, 14, new UI4(position.xMin, position.yMax, position.xMax, position.yMax + 0.035f));

            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, position);

            string imageId = string.IsNullOrEmpty(eventGame.Config.EventIcon) ? GetImage(DEFAULT_EVENT_ICON) : GetImage(eventGame.Config.EventName);
            UI.Image(container, UI_MENU, imageId, new UI4(position.xMin + 0.005f, position.yMin + 0.035f, position.xMax - 0.005f, position.yMax - 0.0075f));

            UI.Label(container, UI_MENU, string.Format(Message("Info.Event.Status", player.userID), eventGame.IsEventLockedOut() ? Message("UI.Event.Locked", player.userID) : eventGame.Status), 14, new UI4(position.xMin + 0.005f, position.yMin, position.xMax, position.yMin + 0.035f), TextAnchor.MiddleLeft);

            UI.Button(container, UI_MENU, Configuration.Interface.Menu.Button.Get, "View Event", 14, new UI4(position.xMin, position.yMin - 0.035f, position.xMax, position.yMin), $"arena.gridview inspect {CommandSafe(eventGame.Config.EventType)} {CommandSafe(eventGame.Config.EventName)}");
        }

        #region Grid Commands
        [ConsoleCommand("arena.gridview")]
        private void ccmdArenaGridView(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null)
                return;

            switch (arg.GetString(0).ToLower())
            {
                case "page":
                    OpenEventMenu(player, CommandSafe(arg.GetString(1), true), string.Empty, arg.GetInt(2));
                    return;
                case "inspect":
                    OpenEventMenu(player, CommandSafe(arg.GetString(1), true), CommandSafe(arg.GetString(2), true), 0);
                    return;
            }
        }

        [ConsoleCommand("arena.eventview")]
        private void ccmdArenaEventView(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null)
                return;

            OpenEventMenu(player, CommandSafe(arg.GetString(0), true), CommandSafe(arg.GetString(1), true), arg.GetInt(2));
        }
        #endregion
        #endregion

        private void CreateEventView(BasePlayer player, CuiElementContainer container, string eventName, int page)
        {
            if (Instance.ActiveEvents.TryGetValue(eventName, out BaseEventGame eventGame))
            {
                AddImageIcon(container, GetImage(RETURN_ICON), Message("UI.Return", player.userID), new UI4(0.89f, 0.905f, 0.94f, 0.985f), $"arena.gridview page {CommandSafe(eventGame.Config.EventType)} 0");
     
                UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Button.Get, new UI4(0.005f, 0.82f, 0.499f, 0.86f));
                UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Button.Get, new UI4(0.501f, 0.82f, 0.995f, 0.86f));

                UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.005f, 0.816f, 0.499f, 0.819f));
                UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.501f, 0.816f, 0.995f, 0.819f));

                UI.Label(container, UI_MENU, Message("UI.Event.Current", player.userID), 13, new UI4(0.01f, 0.82f, 0.499f, 0.86f), TextAnchor.MiddleLeft);

                #region Current Event Info
                int i = 0;
                CreateListEntryLeft(container, Message("UI.Event.Name", player.userID), eventGame.Config.EventName, GetVerticalPos(i += 1, 0.816f));

                CreateListEntryLeft(container, Message("UI.Event.Type", player.userID), eventGame.Config.EventType, GetVerticalPos(i += 1, 0.816f));

                if (!string.IsNullOrEmpty(eventGame.Config.Description))
                {
                    CreateDescriptionEntryLeft(container, eventGame.Config.Description, i, out int lines);
                    i += lines;
                }

                CreateListEntryLeft(container, Message("UI.Event.Status", player.userID), eventGame.IsEventLockedOut() ? Message("UI.Event.Locked", player.userID) : eventGame.Status.ToString(), GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.RoundsToPlay > 1)
                    CreateListEntryLeft(container, Message("UI.Event.Rounds", player.userID), $"{eventGame.RoundNumber} / {eventGame.Config.RoundsToPlay}", GetVerticalPos(i += 1, 0.816f));

                CreateListEntryLeft(container, Message("UI.Event.Players", player.userID),
                    string.Format(Message("UI.Players.Format", player.userID), eventGame.eventPlayers.Count, eventGame.Config.MaximumPlayers, eventGame.joiningSpectators.Count),
                    GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.UseEventBots && eventGame.Config.MaximumBots > 0)
                    CreateListEntryLeft(container, Message("UI.Event.Bots", player.userID), $"{eventGame.GetNpcPlayerCount()} / {eventGame.Config.MaximumBots}", GetVerticalPos(i += 1, 0.816f));


                if (eventGame.Config.TimeLimit > 0)
                    CreateListEntryLeft(container, Message("UI.Event.TimeLimit", player.userID), $"{eventGame.Config.TimeLimit} seconds", GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.ScoreLimit > 0)
                    CreateListEntryLeft(container, Message("UI.Event.ScoreLimit", player.userID), eventGame.Config.ScoreLimit.ToString(), GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.AllowClassSelection && (eventGame.GetAvailableKits(Team.A).Count > 1 || eventGame.GetAvailableKits(Team.B).Count > 1))
                    CreateListEntryLeft(container, Message("UI.Event.ClassSelection", player.userID), Message("UI.Enabled", player.userID), GetVerticalPos(i += 1, 0.816f));

                List<KeyValuePair<string, object>> additionalEventDetails = Pool.Get<List<KeyValuePair<string, object>>>();

                eventGame.GetAdditionalEventDetails(ref additionalEventDetails, player.userID);

                for (int y = 0; y < additionalEventDetails.Count; y++)
                {
                    KeyValuePair<string, object> kvp = additionalEventDetails[y];

                    CreateListEntryLeft(container, kvp.Key, kvp.Value.ToString(), GetVerticalPos(i += 1, 0.816f));
                }

                Pool.FreeUnmanaged(ref additionalEventDetails);

                if (eventGame.Config.Rewards.WinAmount > 0)
                {
                    CreateListEntryLeft(container, Message("UI.Event.WinReward", player.userID),
                        string.Format(Message("UI.Reward.Format", player.userID), eventGame.Config.Rewards.WinAmount, Message($"UI.Reward.{eventGame.Config.Rewards.Type}", player.userID)),
                        GetVerticalPos(i += 1, 0.816f));
                }

                if (eventGame.Config.Rewards.KillAmount > 0)
                {
                    CreateListEntryLeft(container, Message("UI.Event.KillReward", player.userID),
                        string.Format(Message("UI.Reward.Format", player.userID), eventGame.Config.Rewards.KillAmount, Message($"UI.Reward.{eventGame.Config.Rewards.Type}", player.userID)),
                        GetVerticalPos(i += 1, 0.816f));
                }

                if (eventGame.Config.Rewards.HeadshotAmount > 0)
                {
                    CreateListEntryLeft(container, Message("UI.Event.HeadshotReward", player.userID),
                        string.Format(Message("UI.Reward.Format", player.userID), eventGame.Config.Rewards.HeadshotAmount, Message($"UI.Reward.{eventGame.Config.Rewards.Type}", player.userID)),
                        GetVerticalPos(i += 1, 0.816f));
                }

                float yMin = GetVerticalPos(i += 1, 0.816f);

                if (!string.IsNullOrEmpty(eventGame.Config.Permission) && !player.HasPermission(eventGame.Config.Permission))
                {
                    UI.Button(container, UI_MENU, Configuration.Interface.Menu.Button.Get, Message("UI.Event.VIP", player.userID), 13, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT), "");
                }
                else
                {
                    if (eventGame.IsEventLockedOut())
                    {
                        UI.Button(container, UI_MENU, Configuration.Interface.Menu.Button.Get, Message("UI.Event.LockedOut", player.userID), 12, new UI4(0.2585f, yMin, 0.499f, yMin + ELEMENT_HEIGHT), $"");
                    }
                    else if (eventGame.Plugin.IsTeamEvent && eventGame.Plugin.CanSelectTeam)
                    {
                        UI.Button(container, UI_MENU, string.IsNullOrEmpty(eventGame.Config.TeamConfigA.Color) ? Configuration.Interface.Menu.Highlight.Get : UI.Color(eventGame.TeamA.Color, 1f),
                            string.Format(Message("UI.Event.Join.Team", player.userID), eventGame.Config.TeamName(Team.A), eventGame.GetTeamCount(Team.A)),
                            12, new UI4(0.2585f, yMin, 0.378f, yMin + ELEMENT_HEIGHT), $"arena.joinevent {CommandSafe(eventGame.Config.EventName)} 0");

                        UI.Button(container, UI_MENU, string.IsNullOrEmpty(eventGame.Config.TeamConfigB.Color) ? Configuration.Interface.Menu.Highlight.Get : UI.Color(eventGame.TeamB.Color, 1f),
                            string.Format(Message("UI.Event.Join.Team", player.userID), eventGame.Config.TeamName(Team.B), eventGame.GetTeamCount(Team.B)),
                            12, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT), $"arena.joinevent {CommandSafe(eventGame.Config.EventName)} 1");
                    }
                    else UI.Button(container, UI_MENU, Configuration.Interface.Menu.Button.Get, Message("UI.Event.Enter", player.userID), 13, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT), $"arena.joinevent {CommandSafe(eventGame.Config.EventName)}");
                }
                #endregion

                #region Current Event Scores
                UI.Label(container, UI_MENU, Message("UI.Event.CurrentScores", player.userID), 13, new UI4(0.506f, 0.82f, 0.995f, 0.86f), TextAnchor.MiddleLeft);

                if (eventGame.scoreData.Count > 0)
                {
                    int j = 0;
                    const int ELEMENTS_PER_PAGE = 20;

                    if (eventGame.scoreData.Count > (ELEMENTS_PER_PAGE * page) + ELEMENTS_PER_PAGE)
                        UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "> > >", 10, new UI4(0.911f, 0.0075f, 0.995f, 0.0375f), $"arena.eventview {CommandSafe(eventGame.Config.EventType)} {CommandSafe(eventGame.Config.EventName)} {page + 1}");
                    if (page > 0)
                        UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "< < <", 10, new UI4(0.005f, 0.0075f, 0.089f, 0.0375f), $"arena.eventview {CommandSafe(eventGame.Config.EventType)} {CommandSafe(eventGame.Config.EventName)} {page - 1}");

                    if (eventGame.Plugin.IsTeamEvent)
                    {
                        CreateScoreEntryRight(container, Message("UI.Event.TeamScore", player.userID),
                            string.Format(Message("UI.Score.Team", player.userID), eventGame.Config.TeamName(Team.A), eventGame.GetTeamScore(Team.A)),
                            string.Format(Message("UI.Score.Team", player.userID), eventGame.Config.TeamName(Team.B), eventGame.GetTeamScore(Team.B)), GetVerticalPos(j += 1, 0.816f));
                    }

                    for (int k = page * ELEMENTS_PER_PAGE; k < (page * ELEMENTS_PER_PAGE) + ELEMENTS_PER_PAGE; k++)
                    {
                        if (k >= eventGame.scoreData.Count)
                            break;

                        ScoreEntry scoreEntry = eventGame.scoreData[k];

                        eventGame.Plugin.FormatScoreEntry(scoreEntry, player.userID, out string score1, out string score2);

                        string name = eventGame.Plugin.IsTeamEvent ? $"({(scoreEntry.team == Team.A ? eventGame.Plugin.TeamAName : eventGame.Plugin.TeamBName)}) {scoreEntry.displayName}" : scoreEntry.displayName;
                        CreateScoreEntryRight(container, name, score1, score2, GetVerticalPos(j += 1, 0.816f));
                    }
                }
                else UI.Label(container, UI_MENU, Message("UI.Event.NoScoresRecorded", player.userID), 13, new UI4(0.506f, 0.806f, 0.88f, 0.816f), TextAnchor.MiddleLeft);
                #endregion
            }
        }

        #region Helpers
        private void CreateListEntryLeft(CuiElementContainer container, string key, string value, float yMin)
        {
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.005f, yMin, 0.38f, yMin + ELEMENT_HEIGHT));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, key, 12, new UI4(0.01f, yMin, 0.38f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, value, 12, new UI4(0.3805f, yMin, 0.494f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
        }

        private void CreateDescriptionEntryLeft(CuiElementContainer container, string description, int currentLine, out int lines)
        {
            lines = Mathf.Max(Mathf.CeilToInt(description.Length / 120f), 1);

            float height = (ELEMENT_HEIGHT * lines) + (0.005f * (lines - 1));
            float yMin = GetVerticalPos(currentLine + lines, 0.816f);

            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.005f, yMin, 0.499f, yMin + height));
            UI.Label(container, UI_MENU, description, 12, new UI4(0.01f, yMin, 0.494f, yMin + height), TextAnchor.MiddleLeft);
        }

        private void CreateListEntryRight(CuiElementContainer container, string key, string value, float yMin)
        {
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.501f, yMin, 0.88f, yMin + ELEMENT_HEIGHT));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.8805f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, key, 12, new UI4(0.506f, yMin, 0.88f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, value, 12, new UI4(0.8805f, yMin, 0.99f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
        }

        private void CreateScoreEntryRight(CuiElementContainer container, string displayName, string score1, string score2, float yMin)
        {
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.501f, yMin, 0.748f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, displayName, 12, new UI4(0.506f, yMin, 0.748f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            if (!string.IsNullOrEmpty(score1))
            {
                UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.7485f, yMin, 0.8725f, yMin + ELEMENT_HEIGHT));
                UI.Label(container, UI_MENU, score1, 12, new UI4(0.7535f, yMin, 0.8675f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
            }
            else UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.7485f, yMin, 0.8725f, yMin + ELEMENT_HEIGHT));

            if (!string.IsNullOrEmpty(score2))
            {
                UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.875f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
                UI.Label(container, UI_MENU, score2, 12, new UI4(0.88f, yMin, 0.99f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
            }
            else UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.875f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
        }

        private void CreateSplitEntryRight(CuiElementContainer container, string key, string value, float yMin)
        {
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.501f, yMin, 0.748f, yMin + ELEMENT_HEIGHT));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.7485f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, key, 12, new UI4(0.506f, yMin, 0.748f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, value, 12, new UI4(0.7485f, yMin, 0.99f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
        }
        #endregion
        #endregion

        #region Event Creation
        private Hash<ulong, EventConfig> _eventCreators = new();

        private void OpenAdminMenu(BasePlayer player, AdminTab adminTab, SelectorArgs selectorArgs = default(SelectorArgs), int page = 0)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Interface.Menu.Background.Get, new UI4(0.1f, 0.1f, 0.9f, 0.9f), true);

            UI.Image(container, UI_MENU, GetImage(SERVER_BANNER), new UI4(0f, 0.86f, 1f, 1f));

            AddImageIcon(container, GetImage(EXIT_ICON), Message("UI.Exit", player.userID), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arena.close");

            AddImageIcon(container, GetImage(EVENTS_ICON), Message("UI.Events", player.userID), new UI4(0.01f, 0.905f, 0.06f, 0.985f), "arena.categoryview 0");

            CreateAdminOptions(player, container, adminTab, selectorArgs, page);

            CuiHelper.DestroyUi(player, UI_MENU);
            CuiHelper.AddUi(player, container);
        }

        private void CreateAdminOptions(BasePlayer player, CuiElementContainer container, AdminTab adminTab, SelectorArgs selectorArgs, int page)
        {
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Button.Get, new UI4(0.005f, 0.82f, 0.175f, 0.86f));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.005f, 0.816f, 0.175f, 0.819f));

            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Button.Get, new UI4(0.177f, 0.82f, 0.995f, 0.86f));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.177f, 0.816f, 0.995f, 0.819f));

            UI.Label(container, UI_MENU, Message("UI.Admin.Title", player.userID), 13, new UI4(0.01f, 0.82f, 0.175f, 0.86f), TextAnchor.MiddleLeft);

            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.005f, 0.0075f, 0.175f, 0.811f));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, new UI4(0.177f, 0.0075f, 0.995f, 0.811f));

            int i = 1;
            float yMin = GetVerticalPos(i, 0.811f);

            UI.Button(container, UI_MENU, adminTab == AdminTab.CreateEvent ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Admin.Create", player.userID), 12, new UI4(0.01f, yMin, 0.17f, yMin + ELEMENT_HEIGHT), "arena.create");
            yMin = GetVerticalPos(i += 1, 0.811f);

            UI.Button(container, UI_MENU, adminTab == AdminTab.EditEvent ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Admin.Edit", player.userID), 12, new UI4(0.01f, yMin, 0.17f, yMin + ELEMENT_HEIGHT), $"arena.eventselector {(int)AdminTab.EditEvent}");
            yMin = GetVerticalPos(i += 1, 0.811f);

            UI.Button(container, UI_MENU, adminTab == AdminTab.DeleteEvent ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Admin.Delete", player.userID), 12, new UI4(0.01f, yMin, 0.17f, yMin + ELEMENT_HEIGHT), $"arena.eventselector {(int)AdminTab.DeleteEvent}");
            yMin = GetVerticalPos(i += 1, 0.811f);

            switch (adminTab)
            {
                case AdminTab.EditEvent:
                case AdminTab.DeleteEvent:
                    OpenEventSelector(player, container, UI_MENU, selectorArgs, page);
                    break;
                case AdminTab.CreateEvent:
                    EventCreatorMenu(player, container, UI_MENU);
                    break;
                case AdminTab.Selector:
                    OpenSelector(player, container, UI_MENU, selectorArgs, page);
                    break;
            }
        }

        private void EventCreatorMenu(BasePlayer player, CuiElementContainer container, string panel)
        {
            _eventCreators.TryGetValue(player.userID, out EventConfig eventConfig);

            int i = 0;

            if (eventConfig == null || string.IsNullOrEmpty(eventConfig.EventType))
            {
                UI.Label(container, UI_MENU, "Select an event type", 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

                foreach (IEventPlugin eventPlugin in Instance.EventModes.Values)
                {
                    float yMin = GetVerticalPos(i += 1, 0.811f);
                    UI.Button(container, panel, Configuration.Interface.Menu.Button.Get, eventPlugin.EventName, 12, new UI4(0.182f, yMin, 0.3f, yMin + ELEMENT_HEIGHT), $"arena.create {CommandSafe(eventPlugin.EventName)}");
                }
            }
            else
            {
                UI.Label(container, UI_MENU, $"Creating Event ({eventConfig.EventType})", 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "Save", 12, new UI4(0.925f, 0.82f, 0.995f, 0.86f), "arena.saveevent");
                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "Dispose", 12, new UI4(0.85f, 0.82f, 0.92f, 0.86f), "arena.disposeevent");

                AddInputField(container, panel, i += 1, "Event Name", "eventName", eventConfig.EventName);

                AddInputField(container, panel, i += 1, "Description", "description", eventConfig.Description);

                AddInputField(container, panel, i += 1, "Permission", "permission", eventConfig.Permission);

                AddInputField(container, panel, i += 1, "Event Icon", "eventIcon", eventConfig.EventIcon);

                AddSelectorField(container, panel, i += 1, "Zone ID", "zoneID", eventConfig.ZoneID, "GetZoneIDs");

                if (eventConfig.Plugin.IsTeamEvent)
                {
                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamAName} Spawnfile", "teamASpawnfile", eventConfig.TeamConfigA.Spawnfile, "GetSpawnfileNames");

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamAName} Kit(s)", "teamAKits", GetSelectorLabel(eventConfig.TeamConfigA.Kits), "GetAllKits", eventConfig.AllowClassSelection);

                    AddInputField(container, panel, i += 1, $"{eventConfig.Plugin.TeamAName} Color (Hex)", "teamAColor", eventConfig.TeamConfigA.Color);

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamAName} Clothing", "teamAClothing", eventConfig.TeamConfigA.Clothing, "GetAllKits");

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Spawnfile", "teamBSpawnfile", eventConfig.TeamConfigB.Spawnfile, "GetSpawnfileNames");

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Kit(s)", "teamBKits", GetSelectorLabel(eventConfig.TeamConfigB.Kits), "GetAllKits", eventConfig.AllowClassSelection);

                    AddInputField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Color (Hex)", "teamBColor", eventConfig.TeamConfigB.Color);

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Clothing", "teamBClothing", eventConfig.TeamConfigB.Clothing, "GetAllKits");
                }
                else
                {
                    AddSelectorField(container, panel, i += 1, "Spawnfile", "teamASpawnfile", eventConfig.TeamConfigA.Spawnfile, "GetSpawnfileNames");

                    AddSelectorField(container, panel, i += 1, "Kit(s)", "teamAKits", GetSelectorLabel(eventConfig.TeamConfigA.Kits), "GetAllKits", eventConfig.AllowClassSelection);
                }

                if (eventConfig.Plugin.CanUseClassSelector)
                    AddToggleField(container, panel, i += 1, "Use Class Selector", "useClassSelector", eventConfig.AllowClassSelection);

                if (eventConfig.Plugin.UseTimeLimit)
                    AddInputField(container, panel, i += 1, "Time Limit (seconds)", "timeLimit", eventConfig.TimeLimit);

                if (eventConfig.Plugin.UseScoreLimit)
                    AddInputField(container, panel, i += 1, "Score Limit", "scoreLimit", eventConfig.ScoreLimit);

                if (!eventConfig.Plugin.IsRoundBased)
                    AddInputField(container, panel, i += 1, "Rounds To Play", "roundsToPlay", eventConfig.RoundsToPlay);

                AddInputField(container, panel, i += 1, "Minimum Players", "minimumPlayers", eventConfig.MinimumPlayers);
                AddInputField(container, panel, i += 1, "Maximum Players", "maximumPlayers", eventConfig.MaximumPlayers);

                AddSelectorField(container, panel, i += 1, "Reward Type", "rewardType", eventConfig.Rewards.Type, "GetRewardTypes");
                AddInputField(container, panel, i += 1, "Kill Amount", "killAmount", eventConfig.Rewards.KillAmount);
                AddInputField(container, panel, i += 1, "Headshot Amount", "headshotAmount", eventConfig.Rewards.HeadshotAmount);
                AddInputField(container, panel, i += 1, "Win Amount", "winAmount", eventConfig.Rewards.WinAmount);

                //AddSelectorField(container, panel, i += 1, "Keep Items", "keepItems", eventConfig.KeepItems, "GetKeepItemOptions");
                //AddToggleField(container, panel, i += 1, "Use Bots", "useEventBots", eventConfig.UseEventBots);

                //AddInputField(container, panel, i += 1, "Maximum Bots", "maximumBots", eventConfig.MaximumBots);

                List<EventParameter> eventParameters = eventConfig.Plugin.AdditionalParameters;

                for (int y = 0; y < eventParameters?.Count; y++)
                {
                    EventParameter eventParameter = eventParameters[y];

                    switch (eventParameter.Input)
                    {
                        case EventParameter.InputType.InputField:
                            {
                                string parameter = eventConfig.GetParameter<string>(eventParameter.Field);
                                AddInputField(container, panel, i += 1, eventParameter.Name, eventParameter.Field, string.IsNullOrEmpty(parameter) ? null : parameter);
                                break;
                            }
                        case EventParameter.InputType.Toggle:
                            {
                                bool parameter = eventConfig.GetParameter<bool>(eventParameter.Field);
                                AddToggleField(container, panel, i += 1, eventParameter.Name, eventParameter.Field, parameter);
                                break;
                            }
                        case EventParameter.InputType.Selector:
                        {
                            List<string> listParam = eventParameter.SelectMultiple ? eventConfig.GetParameter<List<string>>(eventParameter.Field) : null;
                            string currentValue = listParam != null ? GetSelectorLabel(listParam) : eventConfig.GetParameter<string>(eventParameter.Field);

                            AddSelectorField(container, panel, i += 1, eventParameter.Name, eventParameter.Field, currentValue, eventParameter.SelectorHook, eventParameter.SelectMultiple);
                            break;
                        }
                    }
                }
            }
        }

        private void AddInputField(CuiElementContainer container, string panel, int index, string title, string fieldName, object currentValue)
        {
            float yMin = GetVerticalPos(index >= 21 ? index - 20 : index, 0.811f);
            float hMin = index >= 21 ? 0.59f : 0.182f;

            UI.Label(container, panel, title, 12, new UI4(hMin, yMin, hMin + 0.118f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            UI.Panel(container, panel, Configuration.Interface.Menu.Button.Get, new UI4(hMin + 0.118f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT));

            string label = GetInputLabel(currentValue);
            if (!string.IsNullOrEmpty(label))
            {
                UI.Label(container, panel, label, 12, new UI4(hMin + 0.123f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
                UI.Button(container, panel, Configuration.Interface.Menu.Highlight.Get, "X", 12, new UI4(hMin + 0.38f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), $"arena.clear {fieldName}");
            }
            else UI.Input(container, panel, string.Empty, 12, $"arena.creator {fieldName}", new UI4(hMin + 0.123f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT));
        }

        private void AddToggleField(CuiElementContainer container, string panel, int index, string title, string fieldName, bool currentValue)
        {
            float yMin = GetVerticalPos(index >= 21 ? index - 20 : index, 0.811f);
            float hMin = index >= 21 ? 0.59f : 0.182f;

            UI.Label(container, panel, title, 12, new UI4(hMin, yMin, hMin + 0.118f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Toggle(container, panel, Configuration.Interface.Menu.Button.Get, 12, new UI4(hMin + 0.118f, yMin, hMin + 0.138f, yMin + ELEMENT_HEIGHT), $"arena.creator {fieldName} {!currentValue}", currentValue);
        }

        private void AddSelectorField(CuiElementContainer container, string panel, int index, string title, string fieldName, string currentValue, string hook, bool allowMultiple = false)
        {
            float yMin = GetVerticalPos(index >= 21 ? index - 20 : index, 0.811f);
            float hMin = index >= 21 ? 0.59f : 0.182f;

            UI.Label(container, panel, title, 12, new UI4(hMin, yMin, hMin + 0.118f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            UI.Panel(container, panel, Configuration.Interface.Menu.Button.Get, new UI4(hMin + 0.118f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT));

            if (!string.IsNullOrEmpty(currentValue))
                UI.Label(container, panel, currentValue, 12, new UI4(hMin + 0.123f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            UI.Button(container, panel, Configuration.Interface.Menu.Highlight.Get, "Select", 12, new UI4(hMin + 0.35f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), $"arena.fieldselector {CommandSafe(title)} {fieldName} {hook} {allowMultiple}");
        }

        private string GetSelectorLabel(IEnumerable<object> list) => !list.Any() ? "Nothing Selected" : list.Count() > 1 ? "Multiple Selected" : list.ElementAt(0).ToString();

        private string GetInputLabel(object obj)
        {
            if (obj is string s)
                return string.IsNullOrEmpty(s) ? null : s.ToString();
            if (obj is int i)
                return i <= 0 ? null : i.ToString();
            if (obj is float f)
                return f <= 0 ? null : f.ToString();
            return null;
        }

        #region Selector
        private void OpenEventSelector(BasePlayer player, CuiElementContainer container, string panel, SelectorArgs args, int page)
        {
            UI.Label(container, UI_MENU, args.Title, 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

            int i = 0;
            foreach (KeyValuePair<string, EventConfig> kvp in Events.Data.events)
            {
                UI.Button(container, panel, Configuration.Interface.Menu.Button.Get, $"{kvp.Key} <size=8>({kvp.Value.EventType})</size>", 11, GetGridLayout(i, 0.182f, 0.772f, 0.1578f, 0.035f, 5), $"{args.Callback} {CommandSafe(kvp.Key)}");
                i++;
            }
        }

        private void OpenSelector(BasePlayer player, CuiElementContainer container, string panel, SelectorArgs args, int page)
        {
            UI.Label(container, UI_MENU, args.Title, 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

            UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "Back", 12, new UI4(0.925f, 0.82f, 0.995f, 0.86f), "arena.closeselector");

            string[] array = Interface.CallHook(args.Hook) as string[];
            if (array != null)
            {
                _eventCreators.TryGetValue(player.userID, out EventConfig eventConfig);

                string stringValue = eventConfig.GetString(args.FieldName);
                List<string> listValue = eventConfig?.GetList(args.FieldName);

                int count = 0;
                for (int i = page * 200; i < Mathf.Min((page + 1) * 200, array.Length); i++)
                {
                    string option = array[i];

                    string color = ((stringValue?.Equals(option, StringComparison.OrdinalIgnoreCase) ?? false) || (listValue?.Contains(option) ?? false)) ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get;

                    UI.Button(container, panel, color, array[i], 11, GetGridLayout(count), $"arena.select {CommandSafe(args.Title)} {args.FieldName} {args.Hook} {args.AllowMultiple} {CommandSafe(option)}");
                    count += 1;
                }
            }
            else
            {
                UI.Label(container, UI_MENU, "No options available for selection", 13, new UI4(0.182f, 0.770f, 0.99f, 0.811f), TextAnchor.MiddleLeft);
            }
        }

        private UI4 GetGridLayout(int index, float xMin = 0.182f, float yMin = 0.772f, float width = 0.0764f, float height = 0.035f, int columns = 10, int rows = 20)
        {
            int columnNumber = index == 0 ? 0 : Mathf.FloorToInt(index / (float)columns);
            int rowNumber = index - (columnNumber * columns);

            float x = xMin + ((width + 0.005f) * rowNumber);
            float y = yMin - ((height + 0.0075f) * columnNumber);

            return new UI4(x, y, x + width, y + height);
        }
        #endregion

        #region Creator Commands
        [ConsoleCommand("arena.admin")]
        private void ccmdAdminMenu(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!player)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
                OpenAdminMenu(player, AdminTab.None);
        }

        [ConsoleCommand("arena.create")]
        private void ccmdCreateEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!player)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
            {
                if (arg.HasArgs())
                {
                    EventConfig eventConfig;
                    if (!_eventCreators.TryGetValue(player.userID, out eventConfig))
                    {
                        string eventName = CommandSafe(arg.GetString(0), true);

                        IEventPlugin eventPlugin = Instance.GetPlugin(eventName);

                        if (eventPlugin == null)
                            return;

                        _eventCreators[player.userID] = eventConfig = new EventConfig(eventName, eventPlugin);
                    }
                }

                OpenAdminMenu(player, AdminTab.CreateEvent);
            }
        }

        [ConsoleCommand("arena.saveevent")]
        private void ccmdSaveEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
            {
                if (!_eventCreators.TryGetValue(player.userID, out EventConfig eventConfig))
                    return;

                object success = Instance.ValidateEventConfig(eventConfig);
                if (success == null)
                {
                    SaveEventConfig(eventConfig);

                    if (!string.IsNullOrEmpty(eventConfig.EventIcon))
                        AddImage(eventConfig.EventName, eventConfig.EventIcon);

                    _eventCreators.Remove(player.userID);

                    OpenAdminMenu(player, AdminTab.None);

                    CreateMenuPopup(player, $"Successfully saved event {eventConfig.EventName}");
                }
                else CreateMenuPopup(player, (string)success);
            }
        }

        [ConsoleCommand("arena.disposeevent")]
        private void ccmdDisposeEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            _eventCreators.Remove(player.userID);

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
            {
                OpenAdminMenu(player, AdminTab.None);
                CreateMenuPopup(player, "Cancelled event creation");
            }
        }

        [ConsoleCommand("arena.clear")]
        private void ccmdClearField(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (!_eventCreators.TryGetValue(player.userID, out EventConfig eventConfig))
                return;

            string fieldName = arg.GetString(0);

            switch (fieldName)
            {
                case "eventName":
                    eventConfig.EventName = string.Empty;
                    break;
                case "zoneID":
                    eventConfig.ZoneID = string.Empty;
                    break;
                case "timeLimit":
                    eventConfig.TimeLimit = 0;
                    break;
                case "eventIcon":
                    eventConfig.EventName = string.Empty;
                    break;
                case "permission":
                    eventConfig.Permission = string.Empty;
                    break;
                case "description":
                    eventConfig.Description = string.Empty;
                    break;
                case "scoreLimit":
                    eventConfig.ScoreLimit = 0;
                    break;
                case "minimumPlayers":
                    eventConfig.MinimumPlayers = 0;
                    break;
                case "maximumPlayers":
                    eventConfig.MaximumPlayers = 0;
                    break;
                case "teamASpawnfile":
                    eventConfig.TeamConfigA.Spawnfile = string.Empty;
                    break;
                case "teamBSpawnfile":
                    eventConfig.TeamConfigB.Spawnfile = string.Empty;
                    break;
                case "teamAColor":
                    eventConfig.TeamConfigA.Color = string.Empty;
                    break;
                case "teamBColor":
                    eventConfig.TeamConfigB.Color = string.Empty;
                    break;
                case "roundsToPlay":
                    eventConfig.RoundsToPlay = 0;
                    break;
                case "maximumBots":
                    eventConfig.MaximumBots = 0;
                    break;
                case "headshotAmount":
                    eventConfig.Rewards.HeadshotAmount = 0;
                    break;
                case "killAmount":
                    eventConfig.Rewards.KillAmount = 0;
                    break;
                case "winAmount":
                    eventConfig.Rewards.WinAmount = 0;
                    break;
                default:
                    foreach (KeyValuePair<string, object> kvp in eventConfig.AdditionalParams)
                    {
                        if (kvp.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                        {
                            eventConfig.AdditionalParams[fieldName] = null;
                            break;
                        }
                    }
                    break;
            }

            OpenAdminMenu(player, AdminTab.CreateEvent);
        }

        [ConsoleCommand("arena.creator")]
        private void ccmdSetField(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (!_eventCreators.TryGetValue(player.userID, out EventConfig eventConfig))
                return;

            if (arg.HasArgs(2))
            {
                SetParameter(player, eventConfig, arg.GetString(0), string.Join(" ", arg.Args.Skip(1)));

                OpenAdminMenu(player, AdminTab.CreateEvent);
            }
        }

        [ConsoleCommand("arena.eventselector")]
        private void ccmdOpenEventSelector(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
            {
                AdminTab adminTab = (AdminTab)arg.GetInt(0);

                switch (adminTab)
                {                   
                    case AdminTab.EditEvent:
                        OpenAdminMenu(player, adminTab, new SelectorArgs("Select an event to edit", SelectionType.Event, "arena.editevent"));
                        break;
                    case AdminTab.DeleteEvent:
                        OpenAdminMenu(player, adminTab, new SelectorArgs("Select an event to delete", SelectionType.Event, "arena.deleteevent"));
                        break;
                }
            }
        }

        [ConsoleCommand("arena.editevent")]
        private void ccmdEditEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
            {
                string eventName = CommandSafe(arg.GetString(0), true);

                EventConfig eventConfig = Events.Data.events[eventName];
                eventConfig.Plugin = Instance.GetPlugin(eventConfig.EventType);

                if (eventConfig.Plugin != null)
                {
                    CreateMenuPopup(player, $"Editing event {eventName} ({eventConfig.EventType})");
                    _eventCreators[player.userID] = eventConfig;
                    OpenAdminMenu(player, AdminTab.CreateEvent);
                }
                else CreateMenuPopup(player, $"The event plugin {eventConfig.EventType} is not loaded");
            }
        }

        [ConsoleCommand("arena.deleteevent")]
        private void ccmdDeleteEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
            {
                string eventName = CommandSafe(arg.GetString(0), true);

                CreateMenuPopup(player, $"Deleted event {eventName}");

                ShutdownEvent(eventName);

                Events.Data.events.Remove(eventName);
                Events.Save();

                OpenAdminMenu(player, AdminTab.None);
            }
        }

        [ConsoleCommand("arena.closeselector")]
        private void ccmdCloseSelector(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))            
                OpenAdminMenu(player, AdminTab.CreateEvent);            
        }

        [ConsoleCommand("arena.fieldselector")]
        private void ccmdOpenSelector(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))            
                OpenAdminMenu(player, AdminTab.Selector, new SelectorArgs(CommandSafe(arg.GetString(0), true), arg.GetString(1), arg.GetString(2), arg.GetBool(3)));            
        }

        [ConsoleCommand("arena.select")]
        private void ccmdSelect(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || player.HasPermission(ADMIN_PERMISSION))
            {
                if (!_eventCreators.TryGetValue(player.userID, out EventConfig eventConfig))
                    return;

                SetParameter(player, eventConfig, arg.GetString(1), CommandSafe(arg.GetString(4), true));

                if (arg.GetBool(3))
                    OpenAdminMenu(player, AdminTab.Selector, new SelectorArgs(CommandSafe(arg.GetString(0), true), arg.GetString(1), arg.GetString(2), true));

                else OpenAdminMenu(player, AdminTab.CreateEvent);
            }
        }

        #region Creator Helpers
        private void SetParameter(BasePlayer player, EventConfig eventConfig, string fieldName, object value)
        {
            if (value == null)
                return;

            switch (fieldName)
            {
                case "eventName":
                    eventConfig.EventName = (string)value;
                    break;
                case "zoneID":
                    eventConfig.ZoneID = (string)value;
                    break;
                case "eventIcon":
                    eventConfig.EventIcon = (string)value;
                    break;
                case "description":
                    eventConfig.Description = (string)value;
                    break;
                case "permission":
                    eventConfig.Permission = (string)value;
                    break;
                case "timeLimit":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.TimeLimit = intValue;
                    }
                    break;
                case "scoreLimit":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.ScoreLimit = intValue;
                    }
                    break;
                case "minimumPlayers":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.MinimumPlayers = intValue;
                    }
                    break;
                case "maximumPlayers":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.MaximumPlayers = intValue;
                    }
                    break;
                case "teamASpawnfile":
                    eventConfig.TeamConfigA.Spawnfile = (string)value;
                    break;
                case "teamBSpawnfile":
                    eventConfig.TeamConfigB.Spawnfile = (string)value;
                    break;
                case "useClassSelector":
                    {
                        if (!TryConvertValue(value, out bool boolValue))
                            CreateMenuPopup(player, "You must enter 'True' or 'False'");
                        else eventConfig.AllowClassSelection = boolValue;
                    }
                    break;
                case "teamAKits":
                    AddToRemoveFromList(eventConfig.TeamConfigA.Kits, (string)value);
                    break;
                case "teamBKits":
                    AddToRemoveFromList(eventConfig.TeamConfigB.Kits, (string)value);
                    break;
                case "teamAClothing":
                    eventConfig.TeamConfigA.Clothing = (string)value;
                    break;
                case "teamBClothing":
                    eventConfig.TeamConfigB.Clothing = (string)value;
                    break;
                case "teamAColor":
                    {
                        string color = (string)value;
                        if (string.IsNullOrEmpty(color) || color.Length < 6 || color.Length > 6 || !IsValidHex(color))
                            CreateMenuPopup(player, "The color must be a 6 digit hex color, without the # prefix");
                        else eventConfig.TeamConfigA.Color = color;
                        break;
                    }
                case "teamBColor":
                    {
                        string color = (string)value;
                        if (string.IsNullOrEmpty(color) || color.Length < 6 || color.Length > 6 || !IsValidHex(color))
                            CreateMenuPopup(player, "The color must be a 6 digit hex color, without the # prefix");
                        else eventConfig.TeamConfigB.Color = color;
                        break;
                    }
                case "useEventBots":
                    {
                        if (!TryConvertValue(value, out bool boolValue))
                            CreateMenuPopup(player, "You must enter 'True' or 'False'");
                        else eventConfig.UseEventBots = boolValue;
                    }
                    break;
                case "maximumBots":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.MaximumBots = intValue;
                    }
                    break;
                case "killAmount":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.Rewards.KillAmount = intValue;
                    }
                    break;
                case "headshotAmount":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.Rewards.HeadshotAmount = intValue;
                    }
                    break;
                case "winAmount":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.Rewards.WinAmount = intValue;
                    }
                    break;
                case "rewardType":
                    eventConfig.Rewards.Type = (string)value;
                    break;
                case "roundsToPlay":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.RoundsToPlay = intValue;
                    }
                    break;
                default:
                    List<EventParameter> additionalParameters = eventConfig.Plugin?.AdditionalParameters;
                    if (additionalParameters != null)
                    {
                        for (int i = 0; i < additionalParameters.Count; i++)
                        {
                            EventParameter eventParameter = additionalParameters[i];

                            if (!eventConfig.AdditionalParams.ContainsKey(eventParameter.Field))
                            {
                                if (eventParameter.IsList)
                                    eventConfig.AdditionalParams[eventParameter.Field] = new List<string>();
                                else eventConfig.AdditionalParams[eventParameter.Field] = eventParameter.DefaultValue == null ? null : eventParameter.DefaultValue;
                            }

                            if (fieldName.Equals(eventParameter.Field, StringComparison.OrdinalIgnoreCase))
                            {
                                object success = eventConfig.Plugin.ParameterIsValid(fieldName, value);
                                if (success != null)
                                {
                                    CreateMenuPopup(player, (string)success);
                                    return;
                                }

                                switch (eventParameter.DataType)
                                {
                                    case "string":
                                        eventConfig.AdditionalParams[eventParameter.Field] = (string)value;
                                        break;
                                    case "int":
                                        if (!TryConvertValue(value, out int intValue))
                                            CreateMenuPopup(player, "You must enter a number");
                                        else eventConfig.AdditionalParams[eventParameter.Field] = intValue;
                                        break;
                                    case "float":
                                        if (!TryConvertValue(value, out float floatValue))
                                            CreateMenuPopup(player, "You must enter a number");
                                        else eventConfig.AdditionalParams[eventParameter.Field] = floatValue;
                                        break;
                                    case "bool":
                                        if (!TryConvertValue(value, out bool boolValue))
                                            CreateMenuPopup(player, "You must enter 'True' or 'False'");
                                        else eventConfig.AdditionalParams[eventParameter.Field] = boolValue;
                                        break;
                                    case "List<string>":
                                        List<string> list = eventConfig.AdditionalParams[eventParameter.Field] as List<string>;
                                        if (list == null)
                                            eventConfig.AdditionalParams[eventParameter.Field] = list = new List<string>();
                                        AddToRemoveFromList(list, (string)value);
                                        break;
                                }
                            }
                        }
                    }
                    return;
            }
        }

        private bool TryConvertValue<T>(object value, out T result)
        {
            try
            {
                result = (T)Convert.ChangeType(value, typeof(T));
                return true;
            }
            catch
            {
                result = default(T);
                return false;
            }
        }

        private void AddToRemoveFromList(List<string> list, string value)
        {
            if (list.Contains(value))
                list.Remove(value);
            else list.Add(value);
        }
        #endregion
        #endregion

        #endregion        
        #endregion

        #region Statistics  
        private void OpenStatisticsMenu(BasePlayer player, StatisticTab statisticTab, Statistic sortBy, int page)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Interface.Menu.Background.Get, new UI4(0.1f, 0.1f, 0.9f, 0.9f), true);

            UI.Image(container, UI_MENU, GetImage(SERVER_BANNER), new UI4(0f, 0.86f, 1f, 1f));

            AddImageIcon(container, GetImage(EVENTS_ICON), Message("UI.Events", player.userID), new UI4(0.01f, 0.905f, 0.06f, 0.985f), "arena.categoryview 0");

            if (player.HasPermission(ADMIN_PERMISSION))
                AddImageIcon(container, GetImage(ADMIN_ICON), Message("UI.Admin", player.userID), new UI4(0.06f, 0.905f, 0.115f, 0.985f), "arena.admin");

            AddImageIcon(container, GetImage(EXIT_ICON), Message("UI.Exit", player.userID), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arena.close");

            AddStatisticHeader(container, player.userID, statisticTab);

            switch (statisticTab)
            {
                case StatisticTab.Personal:
                    AddStatistics(container, false, player.userID, page);
                    break;
                case StatisticTab.Global:
                    AddStatistics(container, true, player.userID, page);
                    break;
                case StatisticTab.Leaders:
                    AddLeaderBoard(container, player.userID, page, sortBy);
                    break;
            }

            CuiHelper.DestroyUi(player, UI_MENU);
            CuiHelper.AddUi(player, container);
        }

        private void AddStatisticHeader(CuiElementContainer container, ulong playerId, StatisticTab openTab)
        {
            int i = 0;
            float xMin = GetHorizontalPos(i);

            UI.Button(container, UI_MENU, openTab == StatisticTab.Personal ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Statistics.Personal", playerId), 13, new UI4(xMin, 0.825f, xMin + 0.14f, 0.86f), openTab == StatisticTab.Personal ? "" : $"arena.statistics {(int)StatisticTab.Personal} {Statistic.Rank}");
            xMin = GetHorizontalPos(i += 1) + (0.002f * i);

            UI.Button(container, UI_MENU, openTab == StatisticTab.Global ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Statistics.Global", playerId), 13, new UI4(xMin, 0.825f, xMin + 0.14f, 0.86f), openTab == StatisticTab.Global ? "" : $"arena.statistics {(int)StatisticTab.Global} {Statistic.Rank}");
            xMin = GetHorizontalPos(i += 1) + (0.002f * i);

            UI.Button(container, UI_MENU, openTab == StatisticTab.Leaders ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Statistics.Leaders", playerId), 13, new UI4(xMin, 0.825f, xMin + 0.14f, 0.86f), openTab == StatisticTab.Leaders ? "" : $"arena.statistics {(int)StatisticTab.Leaders} {Statistic.Rank}");
            xMin = GetHorizontalPos(i += 1) + (0.002f * i);
        }

        private void AddStatistics(CuiElementContainer container, bool isGlobal, ulong playerId, int page = 0)
        {
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Button.Get, new UI4(0.005f, 0.78f, 0.499f, 0.82f));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Button.Get, new UI4(0.501f, 0.78f, 0.995f, 0.82f));
            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.005f, 0.776f, 0.995f, 0.779f));

            UI.Label(container, UI_MENU, isGlobal ? Message("UI.Statistics.Global", playerId) : Message("UI.Statistics.Personal", playerId), 13, new UI4(0.01f, 0.78f, 0.499f, 0.82f), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, Message("UI.GamesPlayed", playerId), 13, new UI4(0.506f, 0.78f, 0.995f, 0.82f), TextAnchor.MiddleLeft);

            StatisticsData.Data data = isGlobal ? Statistics.Data.global : Statistics.Data.Find(playerId);
            if (data != null)
            {
                const int ELEMENTS_PER_PAGE = 19;

                if (data.events.Count > (ELEMENTS_PER_PAGE * page) + ELEMENTS_PER_PAGE)
                    UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, ">\n>\n>", 20, new UI4(1f, 0f, 1.015f, 0.82f),
                        $"arena.statistics {(isGlobal ? (int)StatisticTab.Global : (int)StatisticTab.Personal)} 0 {page + 1}");
                if (page > 0)
                    UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "<\n<\n<", 20, new UI4(-0.015f, 0f, 0f, 0.82f),
                        $"arena.statistics {(isGlobal ? (int)StatisticTab.Global : (int)StatisticTab.Personal)} 0 {page - 1}");

                int i = 0;
                if (!isGlobal)
                {
                    CreateListEntryLeft(container, Message("UI.Rank", playerId), data.Rank == -1 ? "-" : data.Rank.ToString(), GetVerticalPos(i += 1, 0.776f));
                    CreateListEntryLeft(container, Message("UI.Score", playerId), data.Score.ToString(), GetVerticalPos(i += 1, 0.776f));
                }

                foreach (KeyValuePair<string, int> score in data.statistics)
                    CreateListEntryLeft(container, isGlobal ? string.Format(Message("UI.Totals", playerId), score.Key) : Message(score.Key, playerId), score.Value.ToString(), GetVerticalPos(i += 1, 0.776f));

                int j = 1;
                for (int k = page * ELEMENTS_PER_PAGE; k < (page * ELEMENTS_PER_PAGE) + ELEMENTS_PER_PAGE; k++)
                {
                    if (k >= data.events.Count)
                        break;

                    KeyValuePair<string, int> eventGame = data.events.ElementAt(k);

                    CreateListEntryRight(container, eventGame.Key, eventGame.Value.ToString(), GetVerticalPos(j++, 0.776f));
                }
            }
            else
            {
                float yMin = GetVerticalPos(1, 0.776f);
                UI.Label(container, UI_MENU, Message("UI.NoStatisticsSaved", playerId), 13, new UI4(0.01f, yMin, 0.38f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            }
        }

        private void AddLeaderBoard(CuiElementContainer container, ulong playerId, int page = 0, Statistic sortBy = Statistic.Rank)
        {
            const int ELEMENTS_PER_PAGE = 19;

            List<StatisticsData.Data> list = Statistics.Data.SortStatisticsBy(sortBy);

            if (list.Count > (ELEMENTS_PER_PAGE * page) + ELEMENTS_PER_PAGE)
                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, ">\n>\n>", 20, new UI4(1f, 0f, 1.015f, 0.82f),
                    $"arena.statistics {(int)StatisticTab.Leaders} {(int)sortBy} {page + 1}");
            if (page > 0)
                UI.Button(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, "<\n<\n<", 20, new UI4(-0.015f, 0f, 0f, 0.82f),
                    $"arena.statistics {(int)StatisticTab.Leaders} {(int)sortBy} {page - 1}");

            float yMin = 0.785f;

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Rank ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, string.Empty, page, Statistic.Rank, 0.005f, 0.033f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Name ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Player", playerId), page, Statistic.Name, 0.035f, 0.225f, yMin, TextAnchor.MiddleLeft);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Rank ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Score", playerId), page, Statistic.Rank, 0.227f, 0.309f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Kills ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Kills", playerId), page, Statistic.Kills, 0.311f, 0.393f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Deaths ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Deaths", playerId), page, Statistic.Deaths, 0.395f, 0.479f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Assists ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Assists", playerId), page, Statistic.Assists, 0.481f, 0.565f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Headshots ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Headshots", playerId), page, Statistic.Headshots, 0.567f, 0.651f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Melee ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Melee", playerId), page, Statistic.Melee, 0.653f, 0.737f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Wins ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Won", playerId), page, Statistic.Wins, 0.739f, 0.823f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Losses ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Lost", playerId), page, Statistic.Losses, 0.825f, 0.909f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == Statistic.Played ? Configuration.Interface.Menu.Highlight.Get : Configuration.Interface.Menu.Button.Get, Message("UI.Played", playerId), page, Statistic.Played, 0.911f, 0.995f, yMin);

            UI.Panel(container, UI_MENU, Configuration.Interface.Menu.Highlight.Get, new UI4(0.005f, 0.782f, 0.995f, 0.785f));

            int j = 1;
            for (int i = page * ELEMENTS_PER_PAGE; i < (page * ELEMENTS_PER_PAGE) + ELEMENTS_PER_PAGE; i++)
            {
                if (i >= list.Count)
                    break;

                StatisticsData.Data userData = list[i];

                yMin = GetVerticalPos(j, 0.782f);

                if (userData != null)
                {
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.Rank.ToString(), 0.005f, 0.033f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.DisplayName ?? "Unknown", 0.035f, 0.225f, yMin, TextAnchor.MiddleLeft);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.Score.ToString(), 0.227f, 0.309f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Kills").ToString(), 0.311f, 0.393f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Deaths").ToString(), 0.395f, 0.479f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Assists").ToString(), 0.481f, 0.565f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Headshots").ToString(), 0.567f, 0.651f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Melee").ToString(), 0.653f, 0.737f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Wins").ToString(), 0.739f, 0.823f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Losses").ToString(), 0.825f, 0.909f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Interface.Menu.Panel.Get, userData.GetStatistic("Played").ToString(), 0.9111f, 0.995f, yMin);
                    j++;
                }
            }
        }

        #region Helpers
        private void AddStatistic(CuiElementContainer container, string panel, string color, string message, float xMin, float xMax, float verticalPos, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            UI.Panel(container, panel, color, new UI4(xMin, verticalPos, xMax, verticalPos + ELEMENT_HEIGHT));
            UI.Label(container, panel, message, 12, new UI4(xMin + (anchor != TextAnchor.MiddleCenter ? 0.005f : 0f), verticalPos, xMax - (anchor != TextAnchor.MiddleCenter ? 0.005f : 0f), verticalPos + ELEMENT_HEIGHT), anchor);
        }

        private void AddLeaderSortButton(CuiElementContainer container, string panel, string color, string message, int page, Statistic statistic, float xMin, float xMax, float verticalPos, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            UI4 ui4 = new(xMin, verticalPos, xMax, verticalPos + ELEMENT_HEIGHT);

            UI.Panel(container, panel, color, ui4);
            UI.Label(container, panel, message, 12, new UI4(xMin + (anchor != TextAnchor.MiddleCenter ? 0.005f : 0f), verticalPos, xMax - (anchor != TextAnchor.MiddleCenter ? 0.005f : 0f), verticalPos + ELEMENT_HEIGHT), anchor);
            UI.Button(container, panel, "0 0 0 0", string.Empty, 0, ui4, $"arena.statistics {(int)StatisticTab.Leaders} {(int)statistic} {page}");
        }

        private float GetHorizontalPos(int i, float start = 0.005f, float size = 0.1405f) => start + (size * i);

        private float GetVerticalPos(int i, float start = 0.9f) => start - (i * (ELEMENT_HEIGHT + 0.005f));
        #endregion

        #region Statistics Commands
        [ConsoleCommand("arena.statistics")]
        private void ccmdStatistics(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            OpenStatisticsMenu(player, (StatisticTab)arg.GetInt(0), (Statistic)arg.GetInt(1), arg.GetInt(2));
        }
        #endregion
        #endregion

        #region Menu Popup Messages
        private void CreateMenuPopup(BasePlayer player, string text, float duration = 5f)
        {
            CuiElementContainer container = UI.Container(UI_POPUP, Configuration.Interface.Menu.Highlight.Get, new UI4(0.1f, 0.072f, 0.9f, 0.1f));
            UI.Label(container, UI_POPUP, text, 12, UI4.Full);

            CuiHelper.DestroyUi(player, UI_POPUP);
            CuiHelper.AddUi(player, container);

            player.Invoke(() => CuiHelper.DestroyUi(player, UI_POPUP), duration);
        }
        #endregion

        #region Scoreboards        
        public static CuiElementContainer CreateScoreboardBase(BaseEventGame baseEventGame)
        {
            CuiElementContainer container = UI.Container(UI_SCORES, Configuration.Interface.Scoreboard.Background.Get, Configuration.Interface.Scoreboard.Position.UI4);

            UI.Panel(container, UI_SCORES, Configuration.Interface.Scoreboard.Highlight.Get, UI4.Full);
            UI.Label(container, UI_SCORES, $"{baseEventGame.Config.EventName} ({baseEventGame.Config.EventType})", 11, UI4.Full);

            return container;
        }

        public static void CreateScoreEntry(CuiElementContainer container, string text, string value1, string value2, int index)
        {
            float yMax = -(1f * index);
            float yMin = -(1f * (index + 1));

            UI.Panel(container, UI_SCORES, Configuration.Interface.Scoreboard.Panel.Get, new UI4(0f, yMin + 0.02f, 1f, yMax - 0.02f));

            UI.Label(container, UI_SCORES, text, 11, new UI4(0.05f, yMin, 1f, yMax), TextAnchor.MiddleLeft);

            if (!string.IsNullOrEmpty(value1))
            {
                UI.Panel(container, UI_SCORES, Configuration.Interface.Scoreboard.Highlight.Get, new UI4(0.75f, yMin + 0.02f, 0.875f, yMax - 0.02f));
                UI.Label(container, UI_SCORES, value1, 11, new UI4(0.75f, yMin, 0.875f, yMax));
            }

            if (!string.IsNullOrEmpty(value2))
            {
                UI.Panel(container, UI_SCORES, Configuration.Interface.Scoreboard.Highlight.Get, new UI4(0.875f, yMin + 0.02f, 1f, yMax - 0.02f));
                UI.Label(container, UI_SCORES, value2, 11, new UI4(0.875f, yMin, 1f, yMax));
            }
        }

        public static void CreatePanelEntry(CuiElementContainer container, string text, int index)
        {
            float yMax = -(1f * index);
            float yMin = -(1f * (index + 1));

            UI.Panel(container, UI_SCORES, Configuration.Interface.Scoreboard.Foreground.Get, new UI4(0f, yMin + 0.02f, 1f, yMax - 0.02f));

            UI.Label(container, UI_SCORES, text, 11, new UI4(0.05f, yMin, 1f, yMax));
        }
        #endregion

        #region DeathScreen  
        public static void DisplayDeathScreen(BaseEventPlayer victim, string message, bool canRespawn)
        {
            CuiElementContainer container = UI.Container(UI_DEATH, Instance.GetImage(DEATH_BACKGROUND));

            UI.Button(container, UI_DEATH, Instance.GetImage(EXIT_ICON), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arena.leaveevent");
            UI.Label(container, UI_DEATH, Message("UI.Exit", victim.Player.userID), 10, new UI4(0.945f, 0.875f, 0.99f, 0.905f), TextAnchor.UpperCenter);

            UI.Label(container, UI_DEATH, message, 22, new UI4(0.2f, 0.15f, 0.8f, 0.3f));

            victim.DestroyUI(UI_DEATH);
            victim.AddUI(UI_DEATH, container);

            if (canRespawn)
            {
                UpdateRespawnButton(victim);
                CreateClassSelector(victim);
                CreateTeamSelectionButton(victim);
            }
            else CreateSpectateLeaveButton(victim);            
        }

        public static void DisplaySpectateScreen(BaseEventPlayer eventPlayer, string displayName)
        {
            if (eventPlayer != null && eventPlayer.SpectateTarget != null)
            {
                CuiElementContainer container = UI.Container(UI_SPECTATE, new UI4(0.25f, 0.88f, 0.75f, 0.92f));
                UI.OutlineLabel(container, UI_SPECTATE, string.Format(Message("UI.Spectating", eventPlayer.Player.userID), displayName), 16, UI4.Full);

                eventPlayer.DestroyUI(UI_SPECTATE);
                eventPlayer.AddUI(UI_SPECTATE, container);
            }
            else eventPlayer.DestroyUI(UI_SPECTATE);
        }

        public static void UpdateRespawnButton(BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_RESPAWN, new UI4(0.8f, 0f, 1f, 0.15f), true);

            UI.Label(container, UI_RESPAWN, string.IsNullOrEmpty(eventPlayer.Kit) ? "SELECT A CLASS" : eventPlayer.CanRespawn ? "RESPAWN" : $"RESPAWN IN {eventPlayer.RespawnRemaining}", 13, new UI4(0.55f, 0f, 0.95f, 0.25f));
            
            if (eventPlayer.CanRespawn && !string.IsNullOrEmpty(eventPlayer.Kit))
                UI.Button(container, UI_RESPAWN, Instance.GetImage(RESPAWN_ICON), new UI4(0.57f, 0.25f, 0.92f, 1f), "arena.respawn");

            if (!string.IsNullOrEmpty(eventPlayer.Kit))            
                UI.Button(container, UI_RESPAWN, Configuration.Interface.Menu.Panel.Get, Message(eventPlayer.AutoRespawn ? "UI.Death.AutoRespawn.Enabled" : "UI.Death.AutoRespawn.Disabled", eventPlayer.Player.userID), 13, new UI4(-0.1f, 0f, 0.55f, 0.25f), "arena.toggleautospawn");
            
            eventPlayer.DestroyUI(UI_RESPAWN);
            
            if (eventPlayer.IsDead)
                eventPlayer.AddUI(UI_RESPAWN, container);
        }

        private static void CreateClassSelector(BaseEventPlayer eventPlayer)
        {
            List<string> kits = eventPlayer.Event.GetAvailableKits(eventPlayer.Team);

            if (eventPlayer.Event.Config.AllowClassSelection && kits.Count > 1)
            {
                float halfHeight = (kits.Count * 0.5f) * 0.095f;
                
                UI4 containerDimensions = new(0.005f, 0.5f - halfHeight, 0.23f, 0.5f + halfHeight);

                float elementHeight = 0.085f * (1f / (containerDimensions.yMax - containerDimensions.yMin));
                float labelHeight = 0.03f * (1f / (containerDimensions.yMax - containerDimensions.yMin));
                float imageHeight = 0.055f * (1f / (containerDimensions.yMax - containerDimensions.yMin));
                float verticalSpace = 0.01f * (1f / (containerDimensions.yMax - containerDimensions.yMin));
                float horizontalSpace = 0.022f;
                float imageWidth = 0.141f;

                CuiElementContainer container = UI.Container(UI_CLASS_SELECT, "0 0 0 0.3", containerDimensions, true);

                UI.Panel(container, UI_CLASS_SELECT, "0 0 0 0.5", new UI4(0f, 1f, 1f, 1f + labelHeight));
                UI.Label(container, UI_CLASS_SELECT, "CLASSES", 15, new UI4(0f, 1f, 1f, 1f + labelHeight));

                for (int i = 0; i < kits.Count; i++)
                {
                    string kit = kits[i];

                    List<KeyValuePair<string, ulong>> kitItems = eventPlayer.Event.GetItemsForKit(kit);
                    if (kitItems is { Count: > 0 })
                    {
                        float offsetY = (1f - (i * elementHeight) - (verticalSpace * i));

                        UI4 dimensions = new(horizontalSpace, offsetY - elementHeight, 1f - horizontalSpace, offsetY);

                        bool isSelected = eventPlayer.Kit.Equals(kit);

                        if (isSelected)
                            UI.Panel(container, UI_CLASS_SELECT, Configuration.Interface.Menu.Highlight.Get, dimensions);

                        UI.Label(container, UI_CLASS_SELECT, kit.ToUpper(), 14, new UI4(dimensions.xMin, dimensions.yMax - labelHeight, dimensions.xMax, dimensions.yMax), TextAnchor.MiddleLeft);

                        for (int y = 0; y < kitItems.Count; y++)
                        {
                            UI4 itemDimensions = new(dimensions.xMin + (imageWidth * y) + (horizontalSpace * y), dimensions.yMin, dimensions.xMin + (imageWidth * y) + imageWidth + (horizontalSpace * y), dimensions.yMin + imageHeight);
                            UI.Panel(container, UI_CLASS_SELECT, "0 0 0 0.6", itemDimensions);
                            UI.Icon(container, UI_CLASS_SELECT, kitItems[y].Key, kitItems[y].Value, itemDimensions);
                        }

                        if (!isSelected)
                            UI.Button(container, UI_CLASS_SELECT, dimensions, $"arena.selectkit {CommandSafe(kit)}");
                    }                    
                }

                eventPlayer.DestroyUI(UI_CLASS_SELECT);
                eventPlayer.AddUI(UI_CLASS_SELECT, container);
            }
        }

        private static void CreateTeamSelectionButton(BaseEventPlayer eventPlayer)
        {
            BaseEventGame eventGame = eventPlayer.Event;

            if (eventGame.Plugin.IsTeamEvent && eventGame.Plugin.CanSelectTeam)
            {
                CuiElementContainer container = UI.Container(UI_TEAM_SELECT, "0 0 0 0.3", new UI4(0.4f, 0.01f, 0.6f, 0.045f), true);

                UI.Label(container, UI_TEAM_SELECT, Message("UI.Death.ChangeTeam", eventPlayer.Player.userID), 13, new UI4(0f, 1f, 1f, 2f));

                UI.Panel(container, UI_TEAM_SELECT, Configuration.Interface.Menu.Highlight.Get, eventPlayer.Team == Team.A ? new UI4(0f, 0f, 0.45f, 1f) : new UI4(0.55f, 0f, 1f, 1f));

                UI.Button(container, UI_TEAM_SELECT, string.IsNullOrEmpty(eventGame.Config.TeamConfigA.Color) ? Configuration.Interface.Menu.Button.Get : UI.Color(eventGame.TeamA.Color, 1f), 
                    eventGame.Config.TeamName(Team.A), 13, new UI4(0.01f, 0.1f, 0.44f, 0.9f), "arena.changeteam 0");

                UI.Button(container, UI_TEAM_SELECT, string.IsNullOrEmpty(eventGame.Config.TeamConfigA.Color) ? Configuration.Interface.Menu.Button.Get : UI.Color(eventGame.TeamB.Color, 1f), 
                    eventGame.Config.TeamName(Team.B), 13, new UI4(0.56f, 0.1f, 0.99f, 0.9f), "arena.changeteam 1");

                eventPlayer.DestroyUI(UI_TEAM_SELECT);
                eventPlayer.AddUI(UI_TEAM_SELECT, container);
            }
        }

        private static void CreateSpectateLeaveButton(BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_RESPAWN, Configuration.Interface.Menu.Panel.Get, new UI4(0f, 0f, 1f, 0.04f), true);

            UI.Panel(container, UI_RESPAWN, Configuration.Interface.Menu.Highlight.Get, new UI4(0f, 1f, 1f, 1.005f));

            UI.Button(container, UI_RESPAWN, Configuration.Interface.Menu.Highlight.Get, Message("UI.Death.Leave", eventPlayer.Player.userID), 13, new UI4(0.005f, 0.125f, 0.1f, 0.875f), "arena.leaveevent");

            UI.Button(container, UI_RESPAWN, Configuration.Interface.Menu.Highlight.Get, Message("UI.Death.Spectate", eventPlayer.Player.userID), 13, new UI4(0.105f, 0.125f, 0.2f, 0.875f), "arena.spectateevent");

            eventPlayer.DestroyUI(UI_RESPAWN);
            eventPlayer.AddUI(UI_RESPAWN, container);
        }

        #region Death Screen Commands
        [ConsoleCommand("arena.toggleautospawn")]
        private void ccmdToggleAutoSpawn(ConsoleSystem.Arg arg)
        {
            BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<BaseEventPlayer>();
            if (eventPlayer == null || !eventPlayer.IsDead)
                return;

            eventPlayer.AutoRespawn = !eventPlayer.AutoRespawn;
            UpdateRespawnButton(eventPlayer);
        }

        [ConsoleCommand("arena.changeteam")]
        private void ccmdChangeTeam(ConsoleSystem.Arg arg)
        {
            BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<BaseEventPlayer>();
            if (eventPlayer == null || !eventPlayer.IsDead)
                return;

            Team team = (Team)arg.GetInt(0);

            if (eventPlayer.Team == team)
                return;

            eventPlayer.Team = team;

            if (eventPlayer.Event.Config.AllowClassSelection)
                eventPlayer.Kit = string.Empty;
            else
            {
                if (eventPlayer.Event.GetAvailableKits(team).Count >= 1)
                    eventPlayer.Kit = eventPlayer.Event.GetAvailableKits(team).First();
            }

            if (eventPlayer.Event.Plugin.IsTeamEvent && eventPlayer.Event.Plugin.CanUseRustTeams)
            {
                BaseEventGame.EventTeam currentTeam = eventPlayer.Team == Team.A ? eventPlayer.Event.TeamB : eventPlayer.Event.TeamA;
                BaseEventGame.EventTeam newTeam = eventPlayer.Team == Team.A ? eventPlayer.Event.TeamA : eventPlayer.Event.TeamB;

                currentTeam.RemoveFromTeam(eventPlayer.Player);
                newTeam.AddToTeam(eventPlayer.Player);
            }

            CreateClassSelector(eventPlayer);
            CreateTeamSelectionButton(eventPlayer);
        }

        [ConsoleCommand("arena.respawn")]
        private void ccmdRespawn(ConsoleSystem.Arg arg)
        {
            BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<BaseEventPlayer>();
            if (eventPlayer == null || !eventPlayer.IsDead || !eventPlayer.CanRespawn)
                return;

            if (string.IsNullOrEmpty(eventPlayer.Kit))
                return;

            RespawnPlayer(eventPlayer);
        }

        [ConsoleCommand("arena.selectkit")]
        private void ccmdSelectKit(ConsoleSystem.Arg arg)
        {
            BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<BaseEventPlayer>();
            if (eventPlayer == null || !eventPlayer.IsDead)
                return;

            bool updateRespawn = eventPlayer.Kit == string.Empty;
            eventPlayer.Kit = CommandSafe(arg.GetString(0), true);

            CreateClassSelector(eventPlayer);

            if (updateRespawn)
                UpdateRespawnButton(eventPlayer);
        }
        #endregion
        #endregion

        #region UI Grid Helper
        private class GridAlignment
        {
            public int Columns { get; set; }
            public float XOffset { get; set; }
            public float Width { get; set; }
            public float XSpacing { get; set; }
            public float YOffset { get; set; }
            public float Height { get; set; }
            public float YSpacing { get; set; }

            public GridAlignment(int columns, float xOffset, float width, float xSpacing, float yOffset, float height, float ySpacing)
            {
                Columns = columns;
                XOffset = xOffset;
                Width = width;
                XSpacing = xSpacing;
                YOffset = yOffset;
                Height = height;
                YSpacing = ySpacing;
            }

            public UI4 Evaluate(int index)
            {
                int rowNumber = index == 0 ? 0 : Mathf.FloorToInt(index / Columns);
                int columnNumber = index - (rowNumber * Columns);

                float offsetX = XOffset + (Width * columnNumber) + (XSpacing * columnNumber);

                float offsetY = (YOffset - (rowNumber * Height) - (YSpacing * rowNumber));

                return new UI4(offsetX, offsetY - Height, offsetX + Width, offsetY);
            }

            public UI4 CenterVerticalEvaluate(int index, int total)
            {
                float halfHeight = (total * 0.5f) * (YSpacing + Height);

                float top = 0.5f + halfHeight;

                int rowNumber = index == 0 ? 0 : Mathf.FloorToInt(index / Columns);
                int columnNumber = index - (rowNumber * Columns);

                float offsetX = XOffset + (Width * columnNumber) + (XSpacing * columnNumber);

                float offsetY = (top - (rowNumber * Height) - (YSpacing * rowNumber));

                return new UI4(offsetX, offsetY - Height, offsetX + Width, offsetY);
            }

            public UI4 GetVerticalBounds(int total)
            {
                float halfHeight = (total * 0.5f) * (YSpacing + Height);

                return new UI4(XOffset, 0.5f - halfHeight, XOffset + Width, 0.5f + halfHeight);
            }
        }
        #endregion

        #region UI Args       
        public struct SelectorArgs
        {
            public string Title;
            public string FieldName;
            public string Hook;
            public bool AllowMultiple;

            public SelectionType Type;
            public string Callback;

            public SelectorArgs(string title, string fieldName, string hook, bool allowMultiple, SelectionType type = SelectionType.Field)
            {
                Title = title;
                FieldName = fieldName;
                Hook = hook;
                AllowMultiple = allowMultiple;
                Type = SelectionType.Field;
                Callback = string.Empty;
            }

            public SelectorArgs(string title, SelectionType type, string callback)
            {
                Title = title;
                FieldName = string.Empty;
                Hook = string.Empty;
                AllowMultiple = false;
                Type = type;
                Callback = callback;
            }
        }

        #endregion
        
        #region General Commands
        [ConsoleCommand("arena.close")]
        private void ccmdCloseUI(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            CuiHelper.DestroyUi(player, UI_MENU);
            CuiHelper.DestroyUi(player, UI_POPUP);
        }

        [ConsoleCommand("arena.joinevent")]
        private void ccmdJoinEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            BaseEventGame baseEventGame = Instance.FindEvent(CommandSafe(arg.GetString(0), true));
            if (baseEventGame == null)
                return;

            Team team = Team.None;
            if (arg.Args.Length > 1)
                team = (Team)arg.GetInt(1);

            if (baseEventGame.CanJoinEvent(player))
            {
                DestroyAllUI(player);
                baseEventGame.JoinEvent(player, team);
            }
        }
               
        [ConsoleCommand("arena.leaveevent")]
        private void ccmdLeaveEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            DestroyAllUI(player);

            BaseEventGame baseEventGame = player.GetComponent<BaseEventPlayer>()?.Event;
            if (baseEventGame == null)
                return;

            DestroyAllUI(player);
            baseEventGame.LeaveEvent(player);
        }

        [ConsoleCommand("arena.spectateevent")]
        private void ccmdSpectateEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!player)
                return;

            BaseEventPlayer eventPlayer = player.GetComponent<BaseEventPlayer>();
            if (eventPlayer == null)
                return;

            DestroyAllUI(player);
            eventPlayer.BeginSpectating();
        }
        #endregion

        #region Menu Selection  
        [ConsoleCommand("event")]
        private void ccmdEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!player)
                return;

            _eventCreators.Remove(player.userID);

            //OpenMenu(player, new MenuArgs(arg.GetInt(0), (MenuTab)arg.GetInt(1)));
        }
        #endregion
               
        #region Command Helpers
        private static string CommandSafe(string text, bool unpack = false) => unpack ? text.Replace("▊▊", " ") : text.Replace(" ", "▊▊");
        #endregion

        #region UI

        private static void DestroyAllUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_MENU);
            CuiHelper.DestroyUi(player, UI_DEATH);
            CuiHelper.DestroyUi(player, UI_POPUP);
            CuiHelper.DestroyUi(player, UI_RESPAWN);
            CuiHelper.DestroyUi(player, UI_CLASS_SELECT);
            CuiHelper.DestroyUi(player, UI_TEAM_SELECT);
            CuiHelper.DestroyUi(player, UI_SPECTATE);
            CuiHelper.DestroyUi(player, UI_SCORES);
            CuiHelper.DestroyUi(player, UI_TIMER);
        }

        private static class UI
        {
            public static CuiElementContainer Container(string panel, string color, UI4 dimensions, bool useCursor = false, string parent = "Overlay")
            {
                CuiElementContainer container = new()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = color, Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                            RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };
                return container;
            }

            public static CuiElementContainer Container(string panel, UI4 dimensions, bool useCursor = false, string parent = "Overlay")
            {
                CuiElementContainer container = new()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = "0 0 0 0" },
                            RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };
                return container;
            }

            public static CuiElementContainer Container(string panel, string png, bool useCursor = true, string parent = "Overlay")
            {
                CuiElementContainer container = new()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = "0 0 0 0" },
                            RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };

                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent {Png = png },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });

                return container;
            }


            public static CuiElementContainer Popup(string panel, string text, int size, UI4 dimensions, TextAnchor align = TextAnchor.MiddleCenter, string parent = "Overlay")
            {
                CuiElementContainer container = Container(panel, "0 0 0 0", dimensions);

                Label(container, panel, text, size, UI4.Full, align);

                return container;
            }

            public static void Panel(CuiElementContainer container, string panel, string color, UI4 dimensions)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = color },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                },
                panel);
            }

            public static void BlurPanel(CuiElementContainer container, string panel, string color, UI4 dimensions)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = color, Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                },
                panel);
            }

            public static void Label(CuiElementContainer container, string panel, string text, int size, UI4 dimensions, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text = { FontSize = size, Align = align, Text = text },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                },
                panel);
            }

            public static void OutlineLabel(CuiElementContainer container, string panel, string text, int size, UI4 dimensions, TextAnchor align = TextAnchor.MiddleCenter)
            {
                CuiElement outline = new()
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = text,
                            FontSize = size,
                            Align = TextAnchor.MiddleCenter,
                            FadeIn = 0.2f
                        },
                        new CuiOutlineComponent
                        {
                            Distance = "0.5 0.5",
                            Color = "0 0 0 1"
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = dimensions.GetMin(),
                            AnchorMax = dimensions.GetMax()
                        }
                    }
                };

                container.Add(outline);
            }

            public static void Button(CuiElementContainer container, string panel, UI4 dimensions, string command)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = command, FadeIn = 0f, },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                    Text = { Text = string.Empty }

                },
                panel);
            }

            public static void Button(CuiElementContainer container, string panel, string color, string text, int size, UI4 dimensions, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = command, FadeIn = 0f,  },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                    Text = { Text = text, FontSize = size, Align = align }
                    
                },
                panel);
            }

            public static void Button(CuiElementContainer container, string panel, string png, UI4 dimensions, string command)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent {Png = png },
                        new CuiRectTransformComponent { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                    }
                });
                container.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = command, FadeIn = 0f, },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                    Text = { Text = string.Empty }
                },
                panel);
            }           

            public static void Input(CuiElementContainer container, string panel, string text, int size, string command, UI4 dimensions)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Align = TextAnchor.MiddleLeft,
                            CharsLimit = 300,
                            Command = command + text,
                            FontSize = size,
                            IsPassword = false,
                            Text = text
                        },
                        new CuiRectTransformComponent {AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                    }
                });
            }

            public static void Image(CuiElementContainer container, string panel, string png, UI4 dimensions)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent {Png = png },
                        new CuiRectTransformComponent { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                    }
                });
            }

            public static void Icon(CuiElementContainer container, string panel, string shortname, ulong skin, UI4 dimensions)
            {
                if (ItemManager.itemDictionaryByName.TryGetValue(shortname, out ItemDefinition itemDefintion)) 
                {
                    container.Add(new CuiElement
                    {
                        Name = CuiHelper.GetGuid(),
                        Parent = panel,
                        Components =
                    {
                        new CuiImageComponent { ItemId = itemDefintion.itemid, SkinId = skin },
                        new CuiRectTransformComponent { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                    }
                    });
                }
                else Debug.Log($"[ArenaUI] Tried getting item icon for '{shortname}' but that shortname is invalid");
            }

            public static void Toggle(CuiElementContainer container, string panel, string boxColor, int fontSize, UI4 dimensions, string command, bool isOn)
            {
                Panel(container, panel, boxColor, dimensions);

                if (isOn)
                    Label(container, panel, "✔", fontSize, dimensions);

                Button(container, panel, "0 0 0 0", string.Empty, 0, dimensions, command);
            }

            public static string Color(string hexColor, float alpha)
            {
                if (hexColor.StartsWith("#"))
                    hexColor = hexColor.TrimStart('#');

                int red = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int green = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int blue = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);

                return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
            }
        }

        public class UI4
        {
            public float xMin, yMin, xMax, yMax;

            public UI4(float xMin, float yMin, float xMax, float yMax)
            {
                this.xMin = xMin;
                this.yMin = yMin;
                this.xMax = xMax;
                this.yMax = yMax;
            }

            public string GetMin() => $"{xMin} {yMin}";

            public string GetMax() => $"{xMax} {yMax}";

            private static UI4 _full;

            public static UI4 Full => _full ??= new UI4(0, 0, 1, 1);
        }
        #endregion

        #region Chat Commands
        private void CmdEventMenu(BasePlayer player, string command, string[] args)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer)
                OpenMatchMenu(eventPlayer);
            else
            {
                if (player.HasPermission(BLACKLISTED_PERMISSION))
                {
                    player.LocalizedMessage(Instance, "Error.Blacklisted");
                    return;
                }
                OpenEventMenu(player, string.Empty, string.Empty, 0);
            }
        }

        private void CmdEventStatistics(BasePlayer player, string command, string[] args) => OpenStatisticsMenu(player, StatisticTab.Personal, Statistic.Rank, 0);

        private void CmdEventAdmin(BasePlayer player, string command, string[] args) => OpenAdminMenu(player, AdminTab.None);

        private void CmdEventLeave(BasePlayer player, string command, string[] args)
        {
            DestroyAllUI(player);

            BaseEventGame baseEventGame = player.GetComponent<BaseEventPlayer>()?.Event;
            if (baseEventGame == null)
                return;

            DestroyAllUI(player);
            baseEventGame.LeaveEvent(player);
        }

        #endregion
        
        #endregion
        
        #region Statistics
        
        #region API
        [HookMethod("AddStatistic")]
        public void AddStatistic(BasePlayer player, string statistic, int amount = 1) => Statistics.Data.AddStatistic(player, statistic, amount);

        [HookMethod("AddStatistic")]
        public void AddStatistic(ulong playerId, string statistic, int amount = 1) => Statistics.Data.AddStatistic(playerId, statistic, amount);

        [HookMethod("AddGlobalStatistic")]
        public void AddGlobalStatistic(string statistic, int amount = 1) => Statistics.Data.AddGlobalStatistic(statistic, amount);

        [HookMethod("OnGamePlayed")]
        public void OnGamePlayed(string eventName) => Statistics.Data.OnGamePlayed(eventName);

        [HookMethod("OnGamePlayed")]
        public void OnGamePlayed(BasePlayer player, string eventName) => Statistics.Data.OnGamePlayed(player, eventName);

        [HookMethod("GetStatistic")]
        public int GetStatistic(ulong playerId, string statistic) => Statistics.Data.GetStatistic(playerId, statistic);

        [HookMethod("GetRank")]
        public int GetRank(ulong playerId) => Statistics.Data.GetRank(playerId);

        [HookMethod("GetEventStatistic")]
        public int GetEventStatistic(ulong playerId, string eventName) => Statistics.Data.GetEventStatistic(playerId, eventName);

        [HookMethod("GetPlayerStatistics")]
        public void GetPlayerStatistics(ref List<KeyValuePair<string, int>> list, ulong playerId) => Statistics.Data.GetPlayerStatistics(ref list, playerId);

        [HookMethod("GetPlayerEvents")]
        public void GetPlayerEvents(ref List<KeyValuePair<string, int>> list, ulong playerId) => Statistics.Data.GetPlayerEvents(ref list, playerId);

        [HookMethod("GetGlobalStatistic")]
        public int GetGlobalStatistic(string statistic) => Statistics.Data.GetGlobalStatistic(statistic);

        [HookMethod("GetGlobalEventStatistic")]
        public int GetGlobalEventStatistic(string eventName) => Statistics.Data.GetGlobalEventStatistic(eventName);

        [HookMethod("GetGlobalStatistics")]
        public void GetGlobalStatistics(ref List<KeyValuePair<string, int>> list) => Statistics.Data.GetGlobalStatistics(ref list);

        [HookMethod("GetGlobalEvents")]
        public void GetGlobalEvents(ref List<KeyValuePair<string, int>> list) => Statistics.Data.GetGlobalEvents(ref list);

        [HookMethod("GetStatisticNames")]
        public void GetStatisticNames(ref List<string> list) => Statistics.Data.GetStatisticNames(ref list);
        #endregion

        #region Statistics
        public enum Statistic { Rank, Name, Kills, Deaths, Assists, Headshots, Melee, Wins, Losses, Played }

        public class StatisticsData
        {
            public Hash<ulong, Data> players = new();

            public Data global = new(0UL, "Global");

            [JsonIgnore]
            private Hash<Statistic, List<Data>> _cachedSortResults = new();

            public Data Find(ulong playerId)
            {
                if (players.TryGetValue(playerId, out Data data))
                    return data;
                return null;
            }

            public void OnGamePlayed(string eventName)
            {
                global.AddGamePlayed(eventName);
                ClearCachedSortResults();
                UpdateRankingScores();
            }

            public void OnGamePlayed(BasePlayer player, string eventName)
            {
                if (!players.TryGetValue(player.userID, out Data data))
                    players[player.userID] = data = new Data(player.userID, player.displayName);
                else data.UpdateName(player.displayName);

                data.AddGamePlayed(eventName);

                data.UpdateRankingScore();
            }

            public void OnGamePlayed(ulong playerId, string eventName)
            {
                if (!players.TryGetValue(playerId, out Data data))
                    players[playerId] = data = new Data(playerId, "Unknown");

                data.AddGamePlayed(eventName);

                data.UpdateRankingScore();
            }

            public void AddStatistic(BasePlayer player, string statistic, int amount = 1)
            {
                global.AddStatistic(statistic, amount);

                if (!players.TryGetValue(player.userID, out Data data))
                    players[player.userID] = data = new Data(player.userID, player.displayName);

                data.AddStatistic(statistic, amount);
            }

            public void AddStatistic(ulong playerId, string statistic, int amount = 1)
            {
                global.AddStatistic(statistic, amount);

                if (!players.TryGetValue(playerId, out Data data))
                    players[playerId] = data = new Data(playerId, "Unknown");

                data.AddStatistic(statistic, amount);
            }

            public void AddGlobalStatistic(string statistic, int amount = 1)
            {
                global.AddStatistic(statistic, amount);
            }

            public int GetStatistic(ulong playerId, string statistic)
            {
                if (players.TryGetValue(playerId, out Data data))
                {
                    if (data.statistics.TryGetValue(statistic, out int amount))
                        return amount;
                }
                return 0;
            }

            public void GetPlayerStatistics(ref List<KeyValuePair<string, int>> list, ulong playerId)
            {
                if (players.TryGetValue(playerId, out Data data))
                    list.AddRange(data.statistics);
            }

            public void GetPlayerEvents(ref List<KeyValuePair<string, int>> list, ulong playerId)
            {
                if (players.TryGetValue(playerId, out Data data))
                    list.AddRange(data.events);
            }

            public int GetRank(ulong playerId)
            {
                List<Data> list = SortStatisticsBy(Statistic.Rank);

                for (int i = 0; i < list.Count; i++)
                {
                    Data data = list[i];
                    if (data.UserID.Equals(playerId))
                        return i + 1;
                }

                return -1;
            }

            public int GetEventStatistic(ulong playerId, string eventName)
            {
                if (players.TryGetValue(playerId, out Data data))
                {
                    if (data.events.TryGetValue(eventName, out int amount))
                        return amount;
                }
                return 0;
            }

            public int GetGlobalStatistic(string statistic)
            {
                if (global.statistics.TryGetValue(statistic, out int amount))
                    return amount;
                return 0;
            }

            public int GetGlobalEventStatistic(string eventName)
            {
                if (global.events.TryGetValue(eventName, out int amount))
                    return amount;
                return 0;
            }

            public void GetGlobalStatistics(ref List<KeyValuePair<string, int>> list)
            {
                list.AddRange(global.statistics);
            }

            public void GetGlobalEvents(ref List<KeyValuePair<string, int>> list)
            {
                list.AddRange(global.events);
            }

            public void GetStatisticNames(ref List<string> list) => list.AddRange(global.statistics.Keys);

            private void ClearCachedSortResults()
            {
                foreach (KeyValuePair<Statistic, List<Data>> kvp in _cachedSortResults)
                {
                    List<Data> list = kvp.Value;
                    Pool.FreeUnmanaged(ref list);
                }

                _cachedSortResults.Clear();
            }

            public List<Data> SortStatisticsBy(Statistic statistic)
            {
                if (_cachedSortResults.TryGetValue(statistic, out List<Data> list))
                    return list;
                _cachedSortResults[statistic] = list = Pool.Get<List<Data>>();

                string statisticString = statistic.ToString();

                list.AddRange(players.Values);
                list.Sort(delegate (Data a, Data b)
                {
                    if (a == null || b == null)
                        return 0;

                    switch (statistic)
                    {
                        case Statistic.Rank:
                            return a.Score.CompareTo(b.Score);
                        case Statistic.Name:
                            return a.DisplayName.CompareTo(b.DisplayName);
                        case Statistic.Kills:
                        case Statistic.Deaths:
                        case Statistic.Assists:
                        case Statistic.Headshots:
                        case Statistic.Melee:
                        case Statistic.Wins:
                        case Statistic.Losses:
                        case Statistic.Played:
                            return a.GetStatistic(statisticString).CompareTo(b.GetStatistic(statisticString));
                    }

                    return 0;
                });

                if (statistic != Statistic.Name)
                    list.Reverse();

                return list;
            }

            public void UpdateRankingScores()
            {
                foreach (KeyValuePair<ulong, Data> player in players)
                    player.Value.UpdateRankingScore();

                List<Data> list = SortStatisticsBy(Statistic.Rank);

                for (int i = 0; i < list.Count; i++)
                {
                    list[i].Rank = i + 1;
                }
            }

            public class Data
            {
                public Hash<string, int> events = new();

                public Hash<string, int> statistics = new()
                {
                    ["Kills"] = 0,
                    ["Deaths"] = 0,
                    ["Assists"] = 0,
                    ["Headshots"] = 0,
                    ["Melee"] = 0,
                    ["Wins"] = 0,
                    ["Losses"] = 0,
                    ["Played"] = 0
                };

                public string DisplayName { get; set; }

                public ulong UserID { get; set; }

                [JsonIgnore]
                public float Score { get; private set; }

                [JsonIgnore]
                public int Rank { get; set; }

                public Data(ulong userID, string displayName)
                {
                    UserID = userID;
                    DisplayName = displayName.StripTags();
                }

                public void UpdateName(string displayName)
                {
                    DisplayName = displayName.StripTags();
                }

                public void AddStatistic(string statisticName, int value)
                {
                    statistics[statisticName] += value;
                }

                public void AddGamePlayed(string name)
                {
                    events[name] += 1;
                    UpdateRankingScore();
                }

                public int GetStatistic(string statistic)
                {
                    statistics.TryGetValue(statistic, out int value);
                    return value;
                }

                public void UpdateRankingScore()
                {
                    Score = 0;
                    Score += GetStatistic("Kills");
                    Score += Mathf.CeilToInt(GetStatistic("Assists") * 0.25f);
                    Score += Mathf.CeilToInt(GetStatistic("Melee") * 0.25f);
                    Score += Mathf.CeilToInt(GetStatistic("Headshots") * 0.5f);
                    Score += Mathf.CeilToInt(GetStatistic("Played") * 0.5f);
                    Score += GetStatistic("Wins") * 2;
                }
            }
        }
        #endregion
        
        #endregion
    }

    namespace ArenaEx
    {
        public interface IEventPlugin
        {
            string EventName { get; }

            bool InitializeEvent(Arena.EventConfig config);

            void FormatScoreEntry(Arena.ScoreEntry scoreEntry, ulong langUserId, out string score1, out string score2);

            List<Arena.EventParameter> AdditionalParameters { get; }

            string ParameterIsValid(string fieldName, object value);

            bool CanUseClassSelector { get; }

            bool RequireTimeLimit { get; }

            bool RequireScoreLimit { get; }

            bool UseScoreLimit { get; }

            bool UseTimeLimit { get; }

            bool IsTeamEvent { get; }

            bool CanSelectTeam { get; }

            bool CanUseRustTeams { get; }

            bool IsRoundBased { get; }
            
            bool ProcessWinnersBetweenRounds { get; }
            
            bool CanUseBots { get; }

            string EventIcon { get; }

            string TeamAName { get; }

            string TeamBName { get; }
        }
    }
}

