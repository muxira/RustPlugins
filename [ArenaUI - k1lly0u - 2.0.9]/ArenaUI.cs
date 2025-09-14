//Requires: ArenaStatistics

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using UnityEngine;
using System.Linq;
using System.Globalization;

namespace Oxide.Plugins
{
    [Info("ArenaUI", "k1lly0u", "2.0.9")]
    [Description("Manages and provides user interface for event games")]
    public class ArenaUI : RustPlugin
    {
        #region Fields    
        [PluginReference] private Plugin ImageLibrary;

        public static ArenaUI Instance { get; private set; }

        public static ConfigData Configuration { get; set; }
        #endregion

        #region Oxide Hooks        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(Messages, this);
        }

        private void OnServerInitialized()
        {
            Instance = this;

            cmd.AddChatCommand(Configuration.Commands.EventCommand, this, CmdEventMenu);
            cmd.AddChatCommand(Configuration.Commands.StatisticsCommand, this, CmdEventStatistics);
            cmd.AddChatCommand(Configuration.Commands.AdminCommand, this, CmdEventAdmin);
            cmd.AddChatCommand(Configuration.Commands.LeaveCommand, this, CmdEventLeave);

            timer.In(5f, RegisterImages);
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                DestroyAllUI(player);

            Instance = null;
            Configuration = null;
        }
        #endregion

        #region UI Menus        
        public enum MenuTab { Event, Statistics, Admin }

        public enum AdminTab { None, EditEvent, CreateEvent, DeleteEvent, Selector }

        public enum StatisticTab { Personal, Global, Leaders }

        public enum SelectionType { Field, Event, Player }


        private const float ELEMENT_HEIGHT = 0.035f;

        #region UI Images
        private const string DEATH_BACKGROUND = "arenaui.death_background";
        private const string DEFAULT_EVENT_ICON = "arenaui.default_event_icon";
        private const string SERVER_BANNER = "arenaui.server_banner";
        private const string SMALL_LOGO = "arenaui.server_logo_small";
        private const string EXIT_ICON = "arenaui.exit_icon";
        private const string RETURN_ICON = "arenaui.return_icon";
        private const string RESPAWN_ICON = "arenaui.respawn_icon";
        private const string STATISTICS_ICON = "arenaui.statistics_icon";
        private const string EVENTS_ICON = "arenaui.events_icon";
        private const string ADMIN_ICON = "arenaui.admin_icon";

        internal const string UI_MENU = "arenaui.menu";
        internal const string UI_TIMER = "arenaui.timer";
        internal const string UI_SCORES = "arenaui.scores";
        internal const string UI_POPUP = "arenaui.popup";
        internal const string UI_DEATH = "arenaui.death";
        internal const string UI_RESPAWN = "arenaui.respawn";
        internal const string UI_SPECTATE = "arenaui.spectate";
        internal const string UI_CLASS_SELECT = "arenaui.classselect";
        internal const string UI_TEAM_SELECT = "arenaui.teamselect";

        private Hash<string, string> _imageIDs = new Hash<string, string>();

        private void RegisterImages()
        {
            if (!ImageLibrary)
                return;

            Dictionary<string, string> loadOrder = new Dictionary<string, string>
            {
                [DEATH_BACKGROUND] = Configuration.Images.DeathBackground,
                [DEFAULT_EVENT_ICON] = Configuration.Images.DefaultEventIcon,
                [SERVER_BANNER] = Configuration.Images.ServerBanner,
                [SMALL_LOGO] = Configuration.Images.ServerLogo,
                [EXIT_ICON] = Configuration.Images.ExitIcon,
                [RETURN_ICON] = Configuration.Images.ReturnIcon,
                [RESPAWN_ICON] = Configuration.Images.RespawnIcon,
                [STATISTICS_ICON] = Configuration.Images.StatisticsIcon,
                [EVENTS_ICON] = Configuration.Images.EventsIcon,
                [ADMIN_ICON] = Configuration.Images.AdminIcon,

            };

            foreach (ArenaEx.IEventPlugin eventPlugin in Arena.Instance.EventModes.Values)
                loadOrder.Add($"Event.{eventPlugin.EventName}", eventPlugin.EventIcon);

            foreach (Arena.EventConfig eventConfig in Arena.Instance.Events.events.Values)
            {
                if (!string.IsNullOrEmpty(eventConfig.EventIcon))
                    loadOrder.Add(eventConfig.EventName, eventConfig.EventIcon);
            }

            ImageLibrary.CallHook("ImportImageList", "ArenaUI", loadOrder, 0UL, true, null);
        }

        internal void AddImage(string imageName, string url) => ImageLibrary.Call("AddImage", url, imageName);

        internal string GetImage(string name)
        {
            string result;
            if (!_imageIDs.TryGetValue(name, out result))
                result = _imageIDs[name] = (string)ImageLibrary.Call("GetImage", name);

            return result;
        }
        #endregion

        #region Match Menu
        private void OpenMatchMenu(Arena.BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Menu.Background.Get, new UI4(0.01f, 0.8f, 0.12f, 0.99f), true);
            UI.Image(container, UI_MENU, GetImage(SMALL_LOGO), new UI4(0, 0.34f, 1, 1));
            UI.Button(container, UI_MENU, Configuration.Menu.Button.Get, Message("UI.LeaveEvent", eventPlayer.Player.userID), 15, new UI4(0, 0.17f, 1, 0.32f), "arenaui.leaveevent");
            UI.Button(container, UI_MENU, Configuration.Menu.Button.Get, Message("UI.Close", eventPlayer.Player.userID), 15, new UI4(0, 0, 1, 0.15f), "arenaui.close");

            eventPlayer.AddUI(UI_MENU, container);
        }

        public static void ShowHelpText(Arena.BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_DEATH, Configuration.Menu.Background.Get, new UI4(0.343f, 0, 0.641f, 0.015f));
            UI.Label(container, UI_DEATH, string.Format(Message("UI.Help", eventPlayer.Player.userID), $"/{Configuration.Commands.EventCommand}", $"/{Configuration.Commands.LeaveCommand}"), 10, UI4.Full);

            eventPlayer.DestroyUI(UI_DEATH);
            eventPlayer.AddUI(UI_DEATH, container);
        }
        #endregion

        #region Event Menu
        private readonly GridAlignment CategoryGrid = new GridAlignment(5, 0.025f, 0.18f, 0.0125f, 0.82f, 0.32f, 0.075f);

        private void OpenEventMenu(BasePlayer player, string eventType, string eventName, int page)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Menu.Background.Get, new UI4(0.1f, 0.1f, 0.9f, 0.9f), true);

            UI.Image(container, UI_MENU, GetImage(SERVER_BANNER), new UI4(0f, 0.86f, 1f, 1f));

            AddImageIcon(container, GetImage(EXIT_ICON), Message("UI.Exit", player.userID), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arenaui.close");
            AddImageIcon(container, GetImage(STATISTICS_ICON), Message("UI.Leaders", player.userID), new UI4(0.01f, 0.905f, 0.055f, 0.985f), "arenaui.statistics 0 0 0");

            if (permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
                AddImageIcon(container, GetImage(ADMIN_ICON), Message("UI.Admin", player.userID), new UI4(0.06f, 0.905f, 0.115f, 0.985f), "arenaui.admin");

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
            List<ArenaEx.IEventPlugin> list = Facepunch.Pool.GetList<ArenaEx.IEventPlugin>();

            Arena.GetRegisteredEvents(list);

            if (Configuration.HideUnused)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    ArenaEx.IEventPlugin eventMode = list[i];

                    if (!Arena.HasActiveEventsOfType(eventMode.EventName))
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
                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "◀\n\n◀\n\n◀", 16, new UI4(0.005f, 0.35f, 0.02f, 0.58f), $"arenaui.categoryview {page - 1}");
            if (max < list.Count)
                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "▶\n\n▶\n\n▶", 16, new UI4(0.98f, 0.35f, 0.995f, 0.58f), $"arenaui.categoryview {page + 1}");

            Facepunch.Pool.FreeList(ref list);
        }

        private void CreatePluginEntry(BasePlayer player, CuiElementContainer container, ArenaEx.IEventPlugin eventPlugin, int index)
        {
            UI4 position = CategoryGrid.Evaluate(index);

            UI.Button(container, UI_MENU, GetImage($"Event.{eventPlugin.EventName}"), position, $"arenaui.gridview page {CommandSafe(eventPlugin.EventName)} 0");
            UI.Label(container, UI_MENU, eventPlugin.EventName.ToUpper(), 16, new UI4(position.xMin, position.yMin - 0.04f, position.xMax, position.yMin));
        }

        #region Event Selector Commands
        [ConsoleCommand("arenaui.categoryview")]
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
        private readonly GridAlignment EventGrid = new GridAlignment(5, 0.025f, 0.18f, 0.0125f, 0.8f, 0.32f, 0.1f);

        private void CreateEventGrid(BasePlayer player, CuiElementContainer container, string eventType, int page)
        {
            List<Arena.BaseEventGame> list = Facepunch.Pool.GetList<Arena.BaseEventGame>();

            Arena.GetEventsOfType(eventType, list);
                        
            AddImageIcon(container, GetImage(RETURN_ICON), Message("UI.Return", player.userID), new UI4(0.89f, 0.905f, 0.94f, 0.985f), "arenaui.categoryview 0");

            if (list.Count == 0)
            {
                UI.Label(container, UI_MENU, Message("UI.NoEventsAvailable", player.userID), 14, new UI4(0.015f, 0.8f, 0.99f, 0.86f), TextAnchor.MiddleLeft);
                Facepunch.Pool.FreeList(ref list);
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
                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "◀\n\n◀\n\n◀", 16, new UI4(0.005f, 0.35f, 0.02f, 0.58f), $"arenaui.gridview page {CommandSafe(eventType)} {page - 1}");
            if (max < list.Count)
                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "▶\n\n▶\n\n▶", 16, new UI4(0.98f, 0.35f, 0.995f, 0.58f), $"arenaui.gridview page {CommandSafe(eventType)} {page + 1}");

            Facepunch.Pool.FreeList(ref list);
        }

        private void CreateEventEntry(BasePlayer player, CuiElementContainer container, Arena.BaseEventGame eventGame, int index)
        {
            UI4 position = EventGrid.Evaluate(index);

            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(position.xMin, position.yMax, position.xMax, position.yMax + 0.04f));
            UI.Label(container, UI_MENU, eventGame.Config.EventName, 14, new UI4(position.xMin, position.yMax, position.xMax, position.yMax + 0.035f));

            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, position);

            string imageId = string.IsNullOrEmpty(eventGame.Config.EventIcon) ? GetImage(DEFAULT_EVENT_ICON) : GetImage(eventGame.Config.EventName);
            UI.Image(container, UI_MENU, imageId, new UI4(position.xMin + 0.005f, position.yMin + 0.035f, position.xMax - 0.005f, position.yMax - 0.0075f));

            UI.Label(container, UI_MENU, string.Format(Arena.Message("Info.Event.Status", player.userID), eventGame.Status), 14, new UI4(position.xMin + 0.005f, position.yMin, position.xMax, position.yMin + 0.035f), TextAnchor.MiddleLeft);

            UI.Button(container, UI_MENU, Configuration.Menu.Button.Get, "View Event", 14, new UI4(position.xMin, position.yMin - 0.035f, position.xMax, position.yMin), $"arenaui.gridview inspect {CommandSafe(eventGame.Config.EventType)} {CommandSafe(eventGame.Config.EventName)}");
        }

        #region Grid Commands
        [ConsoleCommand("arenaui.gridview")]
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
                default:
                    break;
            }
        }

        [ConsoleCommand("arenaui.eventview")]
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
            Arena.BaseEventGame eventGame;
            if (Arena.Instance.ActiveEvents.TryGetValue(eventName, out eventGame))
            {
                AddImageIcon(container, GetImage(RETURN_ICON), Message("UI.Return", player.userID), new UI4(0.89f, 0.905f, 0.94f, 0.985f), $"arenaui.gridview page {CommandSafe(eventGame.Config.EventType)} 0");
     
                UI.Panel(container, UI_MENU, Configuration.Menu.Button.Get, new UI4(0.005f, 0.82f, 0.499f, 0.86f));
                UI.Panel(container, UI_MENU, Configuration.Menu.Button.Get, new UI4(0.501f, 0.82f, 0.995f, 0.86f));

                UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.005f, 0.816f, 0.499f, 0.819f));
                UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.501f, 0.816f, 0.995f, 0.819f));

                UI.Label(container, UI_MENU, Message("UI.Event.Current", player.userID), 13, new UI4(0.01f, 0.82f, 0.499f, 0.86f), TextAnchor.MiddleLeft);

                #region Current Event Info
                int i = 0;
                CreateListEntryLeft(container, Message("UI.Event.Name", player.userID), eventGame.Config.EventName, GetVerticalPos(i += 1, 0.816f));

                CreateListEntryLeft(container, Message("UI.Event.Type", player.userID), eventGame.Config.EventType, GetVerticalPos(i += 1, 0.816f));

                if (!string.IsNullOrEmpty(eventGame.Config.Description))
                {
                    int lines = 0;
                    CreateDescriptionEntryLeft(container, eventGame.Config.Description, i, out lines);
                    i += lines;
                }

                CreateListEntryLeft(container, Message("UI.Event.Status", player.userID), eventGame.Status.ToString(), GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.RoundsToPlay > 1)
                    CreateListEntryLeft(container, Message("UI.Event.Rounds", player.userID), $"{eventGame.RoundNumber} / {eventGame.Config.RoundsToPlay}", GetVerticalPos(i += 1, 0.816f));

                CreateListEntryLeft(container, Message("UI.Event.Players", player.userID),
                    string.Format(Message("UI.Players.Format", player.userID), eventGame.eventPlayers.Count, eventGame.Config.MaximumPlayers, eventGame.joiningSpectators.Count),
                    GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.UseEventBots && eventGame.Config.MaximumBots > 0)
                    CreateListEntryLeft(container, Message("UI.Event.Bots", player.userID), $"{eventGame.GetNPCPlayerCount()} / {eventGame.Config.MaximumBots}", GetVerticalPos(i += 1, 0.816f));


                if (eventGame.Config.TimeLimit > 0)
                    CreateListEntryLeft(container, Message("UI.Event.TimeLimit", player.userID), $"{eventGame.Config.TimeLimit} seconds", GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.ScoreLimit > 0)
                    CreateListEntryLeft(container, Message("UI.Event.ScoreLimit", player.userID), eventGame.Config.ScoreLimit.ToString(), GetVerticalPos(i += 1, 0.816f));

                if (eventGame.Config.AllowClassSelection && (eventGame.GetAvailableKits(Arena.Team.A).Count > 1 || eventGame.GetAvailableKits(Arena.Team.B).Count > 1))
                    CreateListEntryLeft(container, Message("UI.Event.ClassSelection", player.userID), Message("UI.Enabled", player.userID), GetVerticalPos(i += 1, 0.816f));

                List<KeyValuePair<string, object>> additionalEventDetails = Facepunch.Pool.GetList<KeyValuePair<string, object>>();

                eventGame.GetAdditionalEventDetails(ref additionalEventDetails, player.userID);

                for (int y = 0; y < additionalEventDetails.Count; y++)
                {
                    KeyValuePair<string, object> kvp = additionalEventDetails[y];

                    CreateListEntryLeft(container, kvp.Key, kvp.Value.ToString(), GetVerticalPos(i += 1, 0.816f));
                }

                Facepunch.Pool.FreeList(ref additionalEventDetails);

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

                if (!string.IsNullOrEmpty(eventGame.Config.Permission) && !permission.UserHasPermission(player.UserIDString, eventGame.Config.Permission))
                {
                    UI.Button(container, UI_MENU, Configuration.Menu.Button.Get, Message("UI.Event.VIP", player.userID), 13, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT), "");
                }
                else
                {
                    if (eventGame.Plugin.IsTeamEvent && eventGame.Plugin.CanSelectTeam)
                    {
                        UI.Button(container, UI_MENU, string.IsNullOrEmpty(eventGame.Config.TeamConfigA.Color) ? Configuration.Menu.Highlight.Get : UI.Color(eventGame.TeamA.Color, 1f),
                            string.Format(Message("UI.Event.Join.Team", player.userID), eventGame.Config.TeamName(Arena.Team.A), eventGame.GetTeamCount(Arena.Team.A)),
                            12, new UI4(0.2585f, yMin, 0.378f, yMin + ELEMENT_HEIGHT), $"arenaui.joinevent {CommandSafe(eventGame.Config.EventName)} 0");

                        UI.Button(container, UI_MENU, string.IsNullOrEmpty(eventGame.Config.TeamConfigB.Color) ? Configuration.Menu.Highlight.Get : UI.Color(eventGame.TeamB.Color, 1f),
                            string.Format(Message("UI.Event.Join.Team", player.userID), eventGame.Config.TeamName(Arena.Team.B), eventGame.GetTeamCount(Arena.Team.B)),
                            12, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT), $"arenaui.joinevent {CommandSafe(eventGame.Config.EventName)} 1");
                    }
                    else UI.Button(container, UI_MENU, Configuration.Menu.Button.Get, Message("UI.Event.Enter", player.userID), 13, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT), $"arenaui.joinevent {CommandSafe(eventGame.Config.EventName)}");
                }
                #endregion

                #region Current Event Scores
                UI.Label(container, UI_MENU, Message("UI.Event.CurrentScores", player.userID), 13, new UI4(0.506f, 0.82f, 0.995f, 0.86f), TextAnchor.MiddleLeft);

                if (eventGame.scoreData.Count > 0)
                {
                    int j = 0;
                    const int ELEMENTS_PER_PAGE = 20;

                    if (eventGame.scoreData.Count > (ELEMENTS_PER_PAGE * page) + ELEMENTS_PER_PAGE)
                        UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "> > >", 10, new UI4(0.911f, 0.0075f, 0.995f, 0.0375f), $"arenaui.eventview {CommandSafe(eventGame.Config.EventType)} {CommandSafe(eventGame.Config.EventName)} {page + 1}");
                    if (page > 0)
                        UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "< < <", 10, new UI4(0.005f, 0.0075f, 0.089f, 0.0375f), $"arenaui.eventview {CommandSafe(eventGame.Config.EventType)} {CommandSafe(eventGame.Config.EventName)} {page - 1}");

                    if (eventGame.Plugin.IsTeamEvent)
                    {
                        CreateScoreEntryRight(container, Message("UI.Event.TeamScore", player.userID),
                            string.Format(Message("UI.Score.Team", player.userID), eventGame.Config.TeamName(Arena.Team.A), eventGame.GetTeamScore(Arena.Team.A)),
                            string.Format(Message("UI.Score.Team", player.userID), eventGame.Config.TeamName(Arena.Team.B), eventGame.GetTeamScore(Arena.Team.B)), GetVerticalPos(j += 1, 0.816f));
                    }

                    for (int k = page * ELEMENTS_PER_PAGE; k < (page * ELEMENTS_PER_PAGE) + ELEMENTS_PER_PAGE; k++)
                    {
                        if (k >= eventGame.scoreData.Count)
                            break;

                        Arena.ScoreEntry scoreEntry = eventGame.scoreData[k];

                        string score1, score2;
                        eventGame.Plugin.FormatScoreEntry(scoreEntry, player.userID, out score1, out score2);

                        string name = eventGame.Plugin.IsTeamEvent ? $"({(scoreEntry.team == Arena.Team.A ? eventGame.Plugin.TeamAName : eventGame.Plugin.TeamBName)}) {scoreEntry.displayName}" : scoreEntry.displayName;
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
            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.005f, yMin, 0.38f, yMin + ELEMENT_HEIGHT));
            UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.3805f, yMin, 0.499f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, key, 12, new UI4(0.01f, yMin, 0.38f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, value, 12, new UI4(0.3805f, yMin, 0.494f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
        }

        private void CreateDescriptionEntryLeft(CuiElementContainer container, string description, int currentLine, out int lines)
        {
            lines = Mathf.Max(Mathf.CeilToInt((float)description.Length / 120f), 1);

            float height = (ELEMENT_HEIGHT * lines) + (0.005f * (lines - 1));
            float yMin = GetVerticalPos(currentLine + lines, 0.816f);

            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.005f, yMin, 0.499f, yMin + height));
            UI.Label(container, UI_MENU, description, 12, new UI4(0.01f, yMin, 0.494f, yMin + height), TextAnchor.MiddleLeft);
        }

        private void CreateListEntryRight(CuiElementContainer container, string key, string value, float yMin)
        {
            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.501f, yMin, 0.88f, yMin + ELEMENT_HEIGHT));
            UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.8805f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, key, 12, new UI4(0.506f, yMin, 0.88f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, value, 12, new UI4(0.8805f, yMin, 0.99f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
        }

        private void CreateScoreEntryRight(CuiElementContainer container, string displayName, string score1, string score2, float yMin)
        {
            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.501f, yMin, 0.748f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, displayName, 12, new UI4(0.506f, yMin, 0.748f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            if (!string.IsNullOrEmpty(score1))
            {
                UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.7485f, yMin, 0.8725f, yMin + ELEMENT_HEIGHT));
                UI.Label(container, UI_MENU, score1, 12, new UI4(0.7535f, yMin, 0.8675f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
            }
            else UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.7485f, yMin, 0.8725f, yMin + ELEMENT_HEIGHT));

            if (!string.IsNullOrEmpty(score2))
            {
                UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.875f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
                UI.Label(container, UI_MENU, score2, 12, new UI4(0.88f, yMin, 0.99f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
            }
            else UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.875f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
        }

        private void CreateSplitEntryRight(CuiElementContainer container, string key, string value, float yMin)
        {
            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.501f, yMin, 0.748f, yMin + ELEMENT_HEIGHT));
            UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.7485f, yMin, 0.995f, yMin + ELEMENT_HEIGHT));
            UI.Label(container, UI_MENU, key, 12, new UI4(0.506f, yMin, 0.748f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, value, 12, new UI4(0.7485f, yMin, 0.99f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleRight);
        }
        #endregion
        #endregion

        #region Event Creation
        private Hash<ulong, Arena.EventConfig> _eventCreators = new Hash<ulong, Arena.EventConfig>();

        private void OpenAdminMenu(BasePlayer player, AdminTab adminTab, SelectorArgs selectorArgs = default(SelectorArgs), int page = 0)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Menu.Background.Get, new UI4(0.1f, 0.1f, 0.9f, 0.9f), true);

            UI.Image(container, UI_MENU, GetImage(SERVER_BANNER), new UI4(0f, 0.86f, 1f, 1f));

            AddImageIcon(container, GetImage(EXIT_ICON), Message("UI.Exit", player.userID), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arenaui.close");

            AddImageIcon(container, GetImage(EVENTS_ICON), Message("UI.Events", player.userID), new UI4(0.01f, 0.905f, 0.06f, 0.985f), "arenaui.categoryview 0");

            CreateAdminOptions(player, container, adminTab, selectorArgs, page);

            CuiHelper.DestroyUi(player, UI_MENU);
            CuiHelper.AddUi(player, container);
        }

        private void CreateAdminOptions(BasePlayer player, CuiElementContainer container, AdminTab adminTab, SelectorArgs selectorArgs, int page)
        {
            UI.Panel(container, UI_MENU, Configuration.Menu.Button.Get, new UI4(0.005f, 0.82f, 0.175f, 0.86f));
            UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.005f, 0.816f, 0.175f, 0.819f));

            UI.Panel(container, UI_MENU, Configuration.Menu.Button.Get, new UI4(0.177f, 0.82f, 0.995f, 0.86f));
            UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.177f, 0.816f, 0.995f, 0.819f));

            UI.Label(container, UI_MENU, Message("UI.Admin.Title", player.userID), 13, new UI4(0.01f, 0.82f, 0.175f, 0.86f), TextAnchor.MiddleLeft);

            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.005f, 0.0075f, 0.175f, 0.811f));
            UI.Panel(container, UI_MENU, Configuration.Menu.Panel.Get, new UI4(0.177f, 0.0075f, 0.995f, 0.811f));

            int i = 1;
            float yMin = GetVerticalPos(i, 0.811f);

            UI.Button(container, UI_MENU, adminTab == AdminTab.CreateEvent ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Admin.Create", player.userID), 12, new UI4(0.01f, yMin, 0.17f, yMin + ELEMENT_HEIGHT), "arenaui.create");
            yMin = GetVerticalPos(i += 1, 0.811f);

            UI.Button(container, UI_MENU, adminTab == AdminTab.EditEvent ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Admin.Edit", player.userID), 12, new UI4(0.01f, yMin, 0.17f, yMin + ELEMENT_HEIGHT), $"arenaui.eventselector {(int)AdminTab.EditEvent}");
            yMin = GetVerticalPos(i += 1, 0.811f);

            UI.Button(container, UI_MENU, adminTab == AdminTab.DeleteEvent ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Admin.Delete", player.userID), 12, new UI4(0.01f, yMin, 0.17f, yMin + ELEMENT_HEIGHT), $"arenaui.eventselector {(int)AdminTab.DeleteEvent}");
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
                default:
                    break;
            }
        }

        private void EventCreatorMenu(BasePlayer player, CuiElementContainer container, string panel)
        {
            Arena.EventConfig eventConfig;
            _eventCreators.TryGetValue(player.userID, out eventConfig);

            int i = 0;

            if (eventConfig == null || string.IsNullOrEmpty(eventConfig.EventType))
            {
                UI.Label(container, UI_MENU, "Select an event type", 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

                foreach (ArenaEx.IEventPlugin eventPlugin in Arena.Instance.EventModes.Values)
                {
                    float yMin = GetVerticalPos(i += 1, 0.811f);
                    UI.Button(container, panel, Configuration.Menu.Button.Get, eventPlugin.EventName, 12, new UI4(0.182f, yMin, 0.3f, yMin + ELEMENT_HEIGHT), $"arenaui.create {CommandSafe(eventPlugin.EventName)}");
                }
            }
            else
            {
                UI.Label(container, UI_MENU, $"Creating Event ({eventConfig.EventType})", 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "Save", 12, new UI4(0.925f, 0.82f, 0.995f, 0.86f), "arenaui.saveevent");
                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "Dispose", 12, new UI4(0.85f, 0.82f, 0.92f, 0.86f), "arenaui.disposeevent");

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

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamAName} Clothing", "teamAClothing", eventConfig.TeamConfigA.Clothing, "GetAllKits", false);

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Spawnfile", "teamBSpawnfile", eventConfig.TeamConfigB.Spawnfile, "GetSpawnfileNames");

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Kit(s)", "teamBKits", GetSelectorLabel(eventConfig.TeamConfigB.Kits), "GetAllKits", eventConfig.AllowClassSelection);

                    AddInputField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Color (Hex)", "teamBColor", eventConfig.TeamConfigB.Color);

                    AddSelectorField(container, panel, i += 1, $"{eventConfig.Plugin.TeamBName} Clothing", "teamBClothing", eventConfig.TeamConfigB.Clothing, "GetAllKits", false);
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

                AddSelectorField(container, panel, i += 1, "Reward Type", "rewardType", eventConfig.Rewards.Type, "GetRewardTypes", false);
                AddInputField(container, panel, i += 1, "Kill Amount", "killAmount", eventConfig.Rewards.KillAmount);
                AddInputField(container, panel, i += 1, "Headshot Amount", "headshotAmount", eventConfig.Rewards.HeadshotAmount);
                AddInputField(container, panel, i += 1, "Win Amount", "winAmount", eventConfig.Rewards.WinAmount);

                //AddToggleField(container, panel, i += 1, "Use Bots", "useEventBots", eventConfig.UseEventBots);

                //AddInputField(container, panel, i += 1, "Maximum Bots", "maximumBots", eventConfig.MaximumBots);

                List<Arena.EventParameter> eventParameters = eventConfig.Plugin.AdditionalParameters;

                for (int y = 0; y < eventParameters?.Count; y++)
                {
                    Arena.EventParameter eventParameter = eventParameters[y];

                    switch (eventParameter.Input)
                    {
                        case Arena.EventParameter.InputType.InputField:
                            {
                                string parameter = eventConfig.GetParameter<string>(eventParameter.Field);
                                AddInputField(container, panel, i += 1, eventParameter.Name, eventParameter.Field, string.IsNullOrEmpty(parameter) ? null : parameter);
                                break;
                            }
                        case Arena.EventParameter.InputType.Toggle:
                            {
                                bool parameter = eventConfig.GetParameter<bool>(eventParameter.Field);
                                AddToggleField(container, panel, i += 1, eventParameter.Name, eventParameter.Field, parameter);
                                break;
                            }
                        case Arena.EventParameter.InputType.Selector:
                            {
                                string parameter = eventConfig.GetParameter<string>(eventParameter.Field);
                                AddSelectorField(container, panel, i += 1, eventParameter.Name, eventParameter.Field, parameter, eventParameter.SelectorHook);
                            }
                            break;
                    }
                }
            }
        }

        private void AddInputField(CuiElementContainer container, string panel, int index, string title, string fieldName, object currentValue)
        {
            float yMin = GetVerticalPos(index >= 21 ? index - 20 : index, 0.811f);
            float hMin = index >= 21 ? 0.59f : 0.182f;

            UI.Label(container, panel, title, 12, new UI4(hMin, yMin, hMin + 0.118f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            UI.Panel(container, panel, Configuration.Menu.Button.Get, new UI4(hMin + 0.118f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT));

            string label = GetInputLabel(currentValue);
            if (!string.IsNullOrEmpty(label))
            {
                UI.Label(container, panel, label, 12, new UI4(hMin + 0.123f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
                UI.Button(container, panel, Configuration.Menu.Highlight.Get, "X", 12, new UI4(hMin + 0.38f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), $"arenaui.clear {fieldName}");
            }
            else UI.Input(container, panel, string.Empty, 12, $"arenaui.creator {fieldName}", new UI4(hMin + 0.123f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT));
        }

        private void AddToggleField(CuiElementContainer container, string panel, int index, string title, string fieldName, bool currentValue)
        {
            float yMin = GetVerticalPos(index >= 21 ? index - 20 : index, 0.811f);
            float hMin = index >= 21 ? 0.59f : 0.182f;

            UI.Label(container, panel, title, 12, new UI4(hMin, yMin, hMin + 0.118f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);
            UI.Toggle(container, panel, Configuration.Menu.Button.Get, 12, new UI4(hMin + 0.118f, yMin, hMin + 0.138f, yMin + ELEMENT_HEIGHT), $"arenaui.creator {fieldName} {!currentValue}", currentValue);
        }

        private void AddSelectorField(CuiElementContainer container, string panel, int index, string title, string fieldName, string currentValue, string hook, bool allowMultiple = false)
        {
            float yMin = GetVerticalPos(index >= 21 ? index - 20 : index, 0.811f);
            float hMin = index >= 21 ? 0.59f : 0.182f;

            UI.Label(container, panel, title, 12, new UI4(hMin, yMin, hMin + 0.118f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            UI.Panel(container, panel, Configuration.Menu.Button.Get, new UI4(hMin + 0.118f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT));

            if (!string.IsNullOrEmpty(currentValue))
                UI.Label(container, panel, currentValue.ToString(), 12, new UI4(hMin + 0.123f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), TextAnchor.MiddleLeft);

            UI.Button(container, panel, Configuration.Menu.Highlight.Get, "Select", 12, new UI4(hMin + 0.35f, yMin, hMin + 0.4f, yMin + ELEMENT_HEIGHT), $"arenaui.fieldselector {CommandSafe(title)} {fieldName} {hook} {allowMultiple}");
        }

        private string GetSelectorLabel(IEnumerable<object> list) => list.Count() == 0 ? "Nothing Selected" : list.Count() > 1 ? "Multiple Selected" : list.ElementAt(0).ToString();

        private string GetInputLabel(object obj)
        {
            if (obj is string)
                return string.IsNullOrEmpty(obj as string) ? null : obj.ToString();
            else if (obj is int)
                return (int)obj <= 0 ? null : obj.ToString();
            else if (obj is float)
                return (float)obj <= 0 ? null : obj.ToString();
            return null;
        }

        #region Selector
        private void OpenEventSelector(BasePlayer player, CuiElementContainer container, string panel, SelectorArgs args, int page)
        {
            UI.Label(container, UI_MENU, args.Title, 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

            int i = 0;
            foreach (KeyValuePair<string, Arena.EventConfig> kvp in Arena.Instance.Events.events)
            {
                UI.Button(container, panel, Configuration.Menu.Button.Get, $"{kvp.Key} <size=8>({kvp.Value.EventType})</size>", 11, GetGridLayout(i, 0.182f, 0.772f, 0.1578f, 0.035f, 5, 20), $"{args.Callback} {CommandSafe(kvp.Key)}");
                i++;
            }
        }

        private void OpenSelector(BasePlayer player, CuiElementContainer container, string panel, SelectorArgs args, int page)
        {
            UI.Label(container, UI_MENU, args.Title, 13, new UI4(0.182f, 0.82f, 0.99f, 0.86f), TextAnchor.MiddleLeft);

            UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "Back", 12, new UI4(0.925f, 0.82f, 0.995f, 0.86f), "arenaui.closeselector");

            string[] array = Interface.CallHook(args.Hook) as string[];
            if (array != null)
            {
                Arena.EventConfig eventConfig;
                _eventCreators.TryGetValue(player.userID, out eventConfig);

                string stringValue = eventConfig.GetString(args.FieldName);
                List<string> listValue = eventConfig?.GetList(args.FieldName);

                int count = 0;
                for (int i = page * 200; i < Mathf.Min((page + 1) * 200, array.Length); i++)
                {
                    string option = array[i];

                    string color = ((stringValue?.Equals(option, StringComparison.OrdinalIgnoreCase) ?? false) || (listValue?.Contains(option) ?? false)) ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get;

                    UI.Button(container, panel, color, array[i], 11, GetGridLayout(count), $"arenaui.select {CommandSafe(args.Title)} {args.FieldName} {args.Hook} {args.AllowMultiple} {CommandSafe(option)}");
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
        [ConsoleCommand("arenaui.admin")]
        private void ccmdAdminMenu(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
                OpenAdminMenu(player, AdminTab.None);
        }

        [ConsoleCommand("arenaui.create")]
        private void ccmdCreateEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
            {
                if (arg.HasArgs(1))
                {
                    Arena.EventConfig eventConfig;
                    if (!_eventCreators.TryGetValue(player.userID, out eventConfig))
                    {
                        string eventName = CommandSafe(arg.GetString(0), true);

                        ArenaEx.IEventPlugin eventPlugin = Arena.Instance.GetPlugin(eventName);

                        if (eventPlugin == null)
                            return;

                        _eventCreators[player.userID] = eventConfig = new Arena.EventConfig(eventName, eventPlugin);
                    }
                }

                OpenAdminMenu(player, AdminTab.CreateEvent);
            }
        }

        [ConsoleCommand("arenaui.saveevent")]
        private void ccmdSaveEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
            {
                Arena.EventConfig eventConfig;
                if (!_eventCreators.TryGetValue(player.userID, out eventConfig))
                    return;

                object success = Arena.Instance.ValidateEventConfig(eventConfig);
                if (success == null)
                {
                    Arena.SaveEventConfig(eventConfig);

                    if (!string.IsNullOrEmpty(eventConfig.EventIcon))
                        AddImage(eventConfig.EventName, eventConfig.EventIcon);

                    _eventCreators.Remove(player.userID);

                    OpenAdminMenu(player, AdminTab.None);

                    CreateMenuPopup(player, $"Successfully saved event {eventConfig.EventName}");
                }
                else CreateMenuPopup(player, (string)success);
            }
        }

        [ConsoleCommand("arenaui.disposeevent")]
        private void ccmdDisposeEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            _eventCreators.Remove(player.userID);

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
            {
                OpenAdminMenu(player, AdminTab.None);
                CreateMenuPopup(player, "Cancelled event creation");
            }
        }

        [ConsoleCommand("arenaui.clear")]
        private void ccmdClearField(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            Arena.EventConfig eventConfig;
            if (!_eventCreators.TryGetValue(player.userID, out eventConfig))
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

        [ConsoleCommand("arenaui.creator")]
        private void ccmdSetField(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            Arena.EventConfig eventConfig;
            if (!_eventCreators.TryGetValue(player.userID, out eventConfig))
                return;

            if (arg.HasArgs(2))
            {
                SetParameter(player, eventConfig, arg.GetString(0), string.Join(" ", arg.Args.Skip(1)));

                OpenAdminMenu(player, AdminTab.CreateEvent);
            }
        }

        [ConsoleCommand("arenaui.eventselector")]
        private void ccmdOpenEventSelector(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
            {
                AdminTab adminTab = (AdminTab)arg.GetInt(0);

                switch (adminTab)
                {                   
                    case AdminTab.EditEvent:
                        OpenAdminMenu(player, adminTab, new SelectorArgs("Select an event to edit", SelectionType.Event, "arenaui.editevent"), 0);
                        break;
                    case AdminTab.DeleteEvent:
                        OpenAdminMenu(player, adminTab, new SelectorArgs("Select an event to delete", SelectionType.Event, "arenaui.deleteevent"), 0);
                        break;
                    default:
                        break;
                }
            }
        }

        [ConsoleCommand("arenaui.editevent")]
        private void ccmdEditEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
            {
                string eventName = CommandSafe(arg.GetString(0), true);

                Arena.EventConfig eventConfig = Arena.Instance.Events.events[eventName];
                eventConfig.Plugin = Arena.Instance.GetPlugin(eventConfig.EventType);

                if (eventConfig.Plugin != null)
                {
                    CreateMenuPopup(player, $"Editing event {eventName} ({eventConfig.EventType})");
                    _eventCreators[player.userID] = eventConfig;
                    OpenAdminMenu(player, AdminTab.CreateEvent);
                }
                else CreateMenuPopup(player, $"The event plugin {eventConfig.EventType} is not loaded");
            }
        }

        [ConsoleCommand("arenaui.deleteevent")]
        private void ccmdDeleteEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
            {
                string eventName = CommandSafe(arg.GetString(0), true);

                CreateMenuPopup(player, $"Deleted event {eventName}");

                Arena.ShutdownEvent(eventName);

                Arena.Instance.Events.events.Remove(eventName);

                Arena.Instance.SaveEventData();

                OpenAdminMenu(player, AdminTab.None);
            }
        }

        [ConsoleCommand("arenaui.closeselector")]
        private void ccmdCloseSelector(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))            
                OpenAdminMenu(player, AdminTab.CreateEvent);            
        }

        [ConsoleCommand("arenaui.fieldselector")]
        private void ccmdOpenSelector(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))            
                OpenAdminMenu(player, AdminTab.Selector, new SelectorArgs(CommandSafe(arg.GetString(0), true), arg.GetString(1), arg.GetString(2), arg.GetBool(3)));            
        }

        [ConsoleCommand("arenaui.select")]
        private void ccmdSelect(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
            {
                Arena.EventConfig eventConfig;
                if (!_eventCreators.TryGetValue(player.userID, out eventConfig))
                    return;

                SetParameter(player, eventConfig, arg.GetString(1), CommandSafe(arg.GetString(4), true));

                if (arg.GetBool(3))
                    OpenAdminMenu(player, AdminTab.Selector, new SelectorArgs(CommandSafe(arg.GetString(0), true), arg.GetString(1), arg.GetString(2), true), 0);

                else OpenAdminMenu(player, AdminTab.CreateEvent);
            }
        }

        #region Creator Helpers
        private void SetParameter(BasePlayer player, Arena.EventConfig eventConfig, string fieldName, object value)
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
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.TimeLimit = intValue;
                    }
                    break;
                case "scoreLimit":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.ScoreLimit = intValue;
                    }
                    break;
                case "minimumPlayers":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.MinimumPlayers = intValue;
                    }
                    break;
                case "maximumPlayers":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
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
                        bool boolValue;
                        if (!TryConvertValue<bool>(value, out boolValue))
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
                        if (string.IsNullOrEmpty(color) || color.Length < 6 || color.Length > 6 || !Arena.IsValidHex(color))
                            CreateMenuPopup(player, "The color must be a 6 digit hex color, without the # prefix");
                        else eventConfig.TeamConfigA.Color = color;
                        break;
                    }
                case "teamBColor":
                    {
                        string color = (string)value;
                        if (string.IsNullOrEmpty(color) || color.Length < 6 || color.Length > 6 || !Arena.IsValidHex(color))
                            CreateMenuPopup(player, "The color must be a 6 digit hex color, without the # prefix");
                        else eventConfig.TeamConfigB.Color = color;
                        break;
                    }
                case "useEventBots":
                    {
                        bool boolValue;
                        if (!TryConvertValue<bool>(value, out boolValue))
                            CreateMenuPopup(player, "You must enter 'True' or 'False'");
                        else eventConfig.UseEventBots = boolValue;
                    }
                    break;
                case "maximumBots":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.MaximumBots = intValue;
                    }
                    break;
                case "killAmount":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.Rewards.KillAmount = intValue;
                    }
                    break;
                case "headshotAmount":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.Rewards.HeadshotAmount = intValue;
                    }
                    break;
                case "winAmount":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.Rewards.WinAmount = intValue;
                    }
                    break;
                case "rewardType":
                    eventConfig.Rewards.Type = (string)value;
                    break;
                case "roundsToPlay":
                    {
                        int intValue;
                        if (!TryConvertValue<int>(value, out intValue))
                            CreateMenuPopup(player, "You must enter a number");
                        else eventConfig.RoundsToPlay = intValue;
                    }
                    break;
                default:
                    List<Arena.EventParameter> additionalParameters = eventConfig.Plugin?.AdditionalParameters;
                    if (additionalParameters != null)
                    {
                        for (int i = 0; i < additionalParameters.Count; i++)
                        {
                            Arena.EventParameter eventParameter = additionalParameters[i];

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
                                        int intValue;
                                        if (!TryConvertValue<int>(value, out intValue))
                                            CreateMenuPopup(player, "You must enter a number");
                                        else eventConfig.AdditionalParams[eventParameter.Field] = intValue;
                                        break;
                                    case "float":
                                        float floatValue;
                                        if (!TryConvertValue<float>(value, out floatValue))
                                            CreateMenuPopup(player, "You must enter a number");
                                        else eventConfig.AdditionalParams[eventParameter.Field] = floatValue;
                                        break;
                                    case "bool":
                                        bool boolValue;
                                        if (!TryConvertValue<bool>(value, out boolValue))
                                            CreateMenuPopup(player, "You must enter 'True' or 'False'");
                                        else eventConfig.AdditionalParams[eventParameter.Field] = boolValue;
                                        break;
                                    case "List<string>":
                                        AddToRemoveFromList(eventConfig.AdditionalParams[eventParameter.Field] as List<string>, (string)value);
                                        break;
                                    default:
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
        private void OpenStatisticsMenu(BasePlayer player, StatisticTab statisticTab, ArenaStatistics.Statistic sortBy, int page)
        {
            CuiElementContainer container = UI.Container(UI_MENU, Configuration.Menu.Background.Get, new UI4(0.1f, 0.1f, 0.9f, 0.9f), true);

            UI.Image(container, UI_MENU, GetImage(SERVER_BANNER), new UI4(0f, 0.86f, 1f, 1f));

            AddImageIcon(container, GetImage(EVENTS_ICON), Message("UI.Events", player.userID), new UI4(0.01f, 0.905f, 0.06f, 0.985f), "arenaui.categoryview 0");

            if (permission.UserHasPermission(player.UserIDString, Arena.ADMIN_PERMISSION))
                AddImageIcon(container, GetImage(ADMIN_ICON), Message("UI.Admin", player.userID), new UI4(0.06f, 0.905f, 0.115f, 0.985f), "arenaui.admin");

            AddImageIcon(container, GetImage(EXIT_ICON), Message("UI.Exit", player.userID), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arenaui.close");

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

            UI.Button(container, UI_MENU, openTab == StatisticTab.Personal ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Statistics.Personal", playerId), 13, new UI4(xMin, 0.825f, xMin + 0.14f, 0.86f), openTab == StatisticTab.Personal ? "" : $"arenaui.statistics {(int)StatisticTab.Personal} {ArenaStatistics.Statistic.Rank}");
            xMin = GetHorizontalPos(i += 1) + (0.002f * i);

            UI.Button(container, UI_MENU, openTab == StatisticTab.Global ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Statistics.Global", playerId), 13, new UI4(xMin, 0.825f, xMin + 0.14f, 0.86f), openTab == StatisticTab.Global ? "" : $"arenaui.statistics {(int)StatisticTab.Global} {ArenaStatistics.Statistic.Rank}");
            xMin = GetHorizontalPos(i += 1) + (0.002f * i);

            UI.Button(container, UI_MENU, openTab == StatisticTab.Leaders ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Statistics.Leaders", playerId), 13, new UI4(xMin, 0.825f, xMin + 0.14f, 0.86f), openTab == StatisticTab.Leaders ? "" : $"arenaui.statistics {(int)StatisticTab.Leaders} {ArenaStatistics.Statistic.Rank}");
            xMin = GetHorizontalPos(i += 1) + (0.002f * i);
        }

        private void AddStatistics(CuiElementContainer container, bool isGlobal, ulong playerId, int page = 0)
        {
            UI.Panel(container, UI_MENU, Configuration.Menu.Button.Get, new UI4(0.005f, 0.78f, 0.499f, 0.82f));
            UI.Panel(container, UI_MENU, Configuration.Menu.Button.Get, new UI4(0.501f, 0.78f, 0.995f, 0.82f));
            UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.005f, 0.776f, 0.995f, 0.779f));

            UI.Label(container, UI_MENU, isGlobal ? Message("UI.Statistics.Global", playerId) : Message("UI.Statistics.Personal", playerId), 13, new UI4(0.01f, 0.78f, 0.499f, 0.82f), TextAnchor.MiddleLeft);
            UI.Label(container, UI_MENU, Message("UI.GamesPlayed", playerId), 13, new UI4(0.506f, 0.78f, 0.995f, 0.82f), TextAnchor.MiddleLeft);

            ArenaStatistics.Statistics.Data data = isGlobal ? ArenaStatistics.Data.global : ArenaStatistics.Data.Find(playerId);
            if (data != null)
            {
                const int ELEMENTS_PER_PAGE = 19;

                if (data.events.Count > (ELEMENTS_PER_PAGE * page) + ELEMENTS_PER_PAGE)
                    UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, ">\n>\n>", 20, new UI4(1f, 0f, 1.015f, 0.82f),
                        $"arenaui.statistics {(isGlobal ? (int)StatisticTab.Global : (int)StatisticTab.Personal)} 0 {page + 1}");
                if (page > 0)
                    UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "<\n<\n<", 20, new UI4(-0.015f, 0f, 0f, 0.82f),
                        $"arenaui.statistics {(isGlobal ? (int)StatisticTab.Global : (int)StatisticTab.Personal)} 0 {page - 1}");

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

        private void AddLeaderBoard(CuiElementContainer container, ulong playerId, int page = 0, ArenaStatistics.Statistic sortBy = ArenaStatistics.Statistic.Rank)
        {
            const int ELEMENTS_PER_PAGE = 19;

            List<ArenaStatistics.Statistics.Data> list = ArenaStatistics.Data.SortStatisticsBy(sortBy);

            if (list.Count > (ELEMENTS_PER_PAGE * page) + ELEMENTS_PER_PAGE)
                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, ">\n>\n>", 20, new UI4(1f, 0f, 1.015f, 0.82f),
                    $"arenaui.statistics {(int)StatisticTab.Leaders} {(int)sortBy} {page + 1}");
            if (page > 0)
                UI.Button(container, UI_MENU, Configuration.Menu.Highlight.Get, "<\n<\n<", 20, new UI4(-0.015f, 0f, 0f, 0.82f),
                    $"arenaui.statistics {(int)StatisticTab.Leaders} {(int)sortBy} {page - 1}");

            float yMin = 0.785f;

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Rank ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, string.Empty, page, ArenaStatistics.Statistic.Rank, 0.005f, 0.033f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Name ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Player", playerId), page, ArenaStatistics.Statistic.Name, 0.035f, 0.225f, yMin, TextAnchor.MiddleLeft);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Rank ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Score", playerId), page, ArenaStatistics.Statistic.Rank, 0.227f, 0.309f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Kills ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Kills", playerId), page, ArenaStatistics.Statistic.Kills, 0.311f, 0.393f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Deaths ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Deaths", playerId), page, ArenaStatistics.Statistic.Deaths, 0.395f, 0.479f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Assists ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Assists", playerId), page, ArenaStatistics.Statistic.Assists, 0.481f, 0.565f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Headshots ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Headshots", playerId), page, ArenaStatistics.Statistic.Headshots, 0.567f, 0.651f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Melee ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Melee", playerId), page, ArenaStatistics.Statistic.Melee, 0.653f, 0.737f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Wins ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Won", playerId), page, ArenaStatistics.Statistic.Wins, 0.739f, 0.823f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Losses ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Lost", playerId), page, ArenaStatistics.Statistic.Losses, 0.825f, 0.909f, yMin);

            AddLeaderSortButton(container, UI_MENU, sortBy == ArenaStatistics.Statistic.Played ? Configuration.Menu.Highlight.Get : Configuration.Menu.Button.Get, Message("UI.Played", playerId), page, ArenaStatistics.Statistic.Played, 0.911f, 0.995f, yMin);

            UI.Panel(container, UI_MENU, Configuration.Menu.Highlight.Get, new UI4(0.005f, 0.782f, 0.995f, 0.785f));

            int j = 1;
            for (int i = page * ELEMENTS_PER_PAGE; i < (page * ELEMENTS_PER_PAGE) + ELEMENTS_PER_PAGE; i++)
            {
                if (i >= list.Count)
                    break;

                ArenaStatistics.Statistics.Data userData = list[i];

                yMin = GetVerticalPos(j, 0.782f);

                if (userData != null)
                {
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.Rank.ToString(), 0.005f, 0.033f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.DisplayName ?? "Unknown", 0.035f, 0.225f, yMin, TextAnchor.MiddleLeft);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.Score.ToString(), 0.227f, 0.309f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Kills").ToString(), 0.311f, 0.393f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Deaths").ToString(), 0.395f, 0.479f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Assists").ToString(), 0.481f, 0.565f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Headshots").ToString(), 0.567f, 0.651f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Melee").ToString(), 0.653f, 0.737f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Wins").ToString(), 0.739f, 0.823f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Losses").ToString(), 0.825f, 0.909f, yMin);
                    AddStatistic(container, UI_MENU, Configuration.Menu.Panel.Get, userData.GetStatistic("Played").ToString(), 0.9111f, 0.995f, yMin);
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

        private void AddLeaderSortButton(CuiElementContainer container, string panel, string color, string message, int page, ArenaStatistics.Statistic statistic, float xMin, float xMax, float verticalPos, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            UI4 ui4 = new UI4(xMin, verticalPos, xMax, verticalPos + ELEMENT_HEIGHT);

            UI.Panel(container, panel, color, ui4);
            UI.Label(container, panel, message, 12, new UI4(xMin + (anchor != TextAnchor.MiddleCenter ? 0.005f : 0f), verticalPos, xMax - (anchor != TextAnchor.MiddleCenter ? 0.005f : 0f), verticalPos + ELEMENT_HEIGHT), anchor);
            UI.Button(container, panel, "0 0 0 0", string.Empty, 0, ui4, $"arenaui.statistics {(int)StatisticTab.Leaders} {(int)statistic} {page}");
        }

        private float GetHorizontalPos(int i, float start = 0.005f, float size = 0.1405f) => start + (size * i);

        private float GetVerticalPos(int i, float start = 0.9f) => start - (i * (ELEMENT_HEIGHT + 0.005f));
        #endregion

        #region Statistics Commands
        [ConsoleCommand("arenaui.statistics")]
        private void ccmdStatistics(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            OpenStatisticsMenu(player, (StatisticTab)arg.GetInt(0), (ArenaStatistics.Statistic)arg.GetInt(1), arg.GetInt(2));
        }
        #endregion
        #endregion

        #region Menu Popup Messages
        private void CreateMenuPopup(BasePlayer player, string text, float duration = 5f)
        {
            CuiElementContainer container = UI.Container(UI_POPUP, Configuration.Menu.Highlight.Get, new UI4(0.1f, 0.072f, 0.9f, 0.1f));
            UI.Label(container, UI_POPUP, text, 12, UI4.Full);

            CuiHelper.DestroyUi(player, UI_POPUP);
            CuiHelper.AddUi(player, container);

            player.Invoke(() => CuiHelper.DestroyUi(player, UI_POPUP), duration);
        }
        #endregion

        #region Scoreboards        
        public static CuiElementContainer CreateScoreboardBase(Arena.BaseEventGame baseEventGame)
        {
            CuiElementContainer container = UI.Container(UI_SCORES, Configuration.Scoreboard.Background.Get, Configuration.Scoreboard.Position.UI4, false);

            UI.Panel(container, UI_SCORES, Configuration.Scoreboard.Highlight.Get, UI4.Full);
            UI.Label(container, UI_SCORES, $"{baseEventGame.Config.EventName} ({baseEventGame.Config.EventType})", 11, UI4.Full);

            return container;
        }

        public static void CreateScoreEntry(CuiElementContainer container, string text, string value1, string value2, int index)
        {
            float yMax = -(1f * index);
            float yMin = -(1f * (index + 1));

            UI.Panel(container, UI_SCORES, Configuration.Scoreboard.Panel.Get, new UI4(0f, yMin + 0.02f, 1f, yMax - 0.02f));

            UI.Label(container, UI_SCORES, text, 11, new UI4(0.05f, yMin, 1f, yMax), TextAnchor.MiddleLeft);

            if (!string.IsNullOrEmpty(value1))
            {
                UI.Panel(container, UI_SCORES, Configuration.Scoreboard.Highlight.Get, new UI4(0.75f, yMin + 0.02f, 0.875f, yMax - 0.02f));
                UI.Label(container, UI_SCORES, value1, 11, new UI4(0.75f, yMin, 0.875f, yMax), TextAnchor.MiddleCenter);
            }

            if (!string.IsNullOrEmpty(value2))
            {
                UI.Panel(container, UI_SCORES, Configuration.Scoreboard.Highlight.Get, new UI4(0.875f, yMin + 0.02f, 1f, yMax - 0.02f));
                UI.Label(container, UI_SCORES, value2, 11, new UI4(0.875f, yMin, 1f, yMax), TextAnchor.MiddleCenter);
            }
        }

        public static void CreatePanelEntry(CuiElementContainer container, string text, int index)
        {
            float yMax = -(1f * index);
            float yMin = -(1f * (index + 1));

            UI.Panel(container, UI_SCORES, Configuration.Scoreboard.Foreground.Get, new UI4(0f, yMin + 0.02f, 1f, yMax - 0.02f));

            UI.Label(container, UI_SCORES, text, 11, new UI4(0.05f, yMin, 1f, yMax), TextAnchor.MiddleCenter);
        }
        #endregion

        #region DeathScreen  
        public static void DisplayDeathScreen(Arena.BaseEventPlayer victim, string message, bool canRespawn)
        {
            CuiElementContainer container = UI.Container(UI_DEATH, Instance.GetImage(DEATH_BACKGROUND));

            UI.Button(container, UI_DEATH, Instance.GetImage(EXIT_ICON), new UI4(0.945f, 0.905f, 0.99f, 0.985f), "arenaui.leaveevent");
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

        public static void DisplaySpectateScreen(Arena.BaseEventPlayer eventPlayer, string displayName)
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

        public static void UpdateRespawnButton(Arena.BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_RESPAWN, new UI4(0.8f, 0f, 1f, 0.15f), true);

            UI.Label(container, UI_RESPAWN, string.IsNullOrEmpty(eventPlayer.Kit) ? "SELECT A CLASS" : eventPlayer.CanRespawn ? "RESPAWN" : $"RESPAWN IN {eventPlayer.RespawnRemaining}", 13, new UI4(0.55f, 0f, 0.95f, 0.25f), TextAnchor.MiddleCenter);
            
            if (eventPlayer.CanRespawn && !string.IsNullOrEmpty(eventPlayer.Kit))
                UI.Button(container, UI_RESPAWN, Instance.GetImage(RESPAWN_ICON), new UI4(0.57f, 0.25f, 0.92f, 1f), "arenaui.respawn");

            if (!string.IsNullOrEmpty(eventPlayer.Kit))            
                UI.Button(container, UI_RESPAWN, Configuration.Menu.Panel.Get, Message(eventPlayer.AutoRespawn ? "UI.Death.AutoRespawn.Enabled" : "UI.Death.AutoRespawn.Disabled", eventPlayer.Player.userID), 13, new UI4(-0.1f, 0f, 0.55f, 0.25f), "arenaui.toggleautospawn");
            
            eventPlayer.DestroyUI(UI_RESPAWN);
            
            if (eventPlayer.IsDead)
                eventPlayer.AddUI(UI_RESPAWN, container);
        }

        private static void CreateClassSelector(Arena.BaseEventPlayer eventPlayer)
        {
            List<string> kits = eventPlayer.Event.GetAvailableKits(eventPlayer.Team);

            if (eventPlayer.Event.Config.AllowClassSelection && kits.Count > 1)
            {
                float halfHeight = ((float)kits.Count * 0.5f) * 0.095f;
                
                UI4 containerDimensions = new UI4(0.005f, 0.5f - halfHeight, 0.23f, 0.5f + halfHeight);

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
                    if (kitItems != null && kitItems.Count > 0)
                    {
                        float offsetY = (1f - (i * elementHeight) - (verticalSpace * i));

                        UI4 dimensions = new UI4(horizontalSpace, offsetY - elementHeight, 1f - horizontalSpace, offsetY);

                        bool isSelected = eventPlayer.Kit.Equals(kit);

                        if (isSelected)
                            UI.Panel(container, UI_CLASS_SELECT, Configuration.Menu.Highlight.Get, dimensions);

                        UI.Label(container, UI_CLASS_SELECT, kit.ToUpper(), 14, new UI4(dimensions.xMin, dimensions.yMax - labelHeight, dimensions.xMax, dimensions.yMax), TextAnchor.MiddleLeft);

                        for (int y = 0; y < kitItems.Count; y++)
                        {
                            UI4 itemDimensions = new UI4(dimensions.xMin + (imageWidth * y) + (horizontalSpace * y), dimensions.yMin, dimensions.xMin + (imageWidth * y) + imageWidth + (horizontalSpace * y), dimensions.yMin + imageHeight);
                            UI.Panel(container, UI_CLASS_SELECT, "0 0 0 0.6", itemDimensions);
                            UI.Icon(container, UI_CLASS_SELECT, kitItems[y].Key, kitItems[y].Value, itemDimensions);
                        }

                        if (!isSelected)
                            UI.Button(container, UI_CLASS_SELECT, dimensions, $"arenaui.selectkit {CommandSafe(kit)}");
                    }                    
                }

                eventPlayer.DestroyUI(UI_CLASS_SELECT);
                eventPlayer.AddUI(UI_CLASS_SELECT, container);
            }
        }

        private static void CreateTeamSelectionButton(Arena.BaseEventPlayer eventPlayer)
        {
            Arena.BaseEventGame eventGame = eventPlayer.Event;

            if (eventGame.Plugin.IsTeamEvent && eventGame.Plugin.CanSelectTeam)
            {
                CuiElementContainer container = UI.Container(UI_TEAM_SELECT, "0 0 0 0.3", new UI4(0.4f, 0.01f, 0.6f, 0.045f), true);

                UI.Label(container, UI_TEAM_SELECT, Message("UI.Death.ChangeTeam", eventPlayer.Player.userID), 13, new UI4(0f, 1f, 1f, 2f));

                UI.Panel(container, UI_TEAM_SELECT, Configuration.Menu.Highlight.Get, eventPlayer.Team == Arena.Team.A ? new UI4(0f, 0f, 0.45f, 1f) : new UI4(0.55f, 0f, 1f, 1f));

                UI.Button(container, UI_TEAM_SELECT, string.IsNullOrEmpty(eventGame.Config.TeamConfigA.Color) ? Configuration.Menu.Button.Get : UI.Color(eventGame.TeamA.Color, 1f), 
                    eventGame.Config.TeamName(Arena.Team.A), 13, new UI4(0.01f, 0.1f, 0.44f, 0.9f), "arenaui.changeteam 0");

                UI.Button(container, UI_TEAM_SELECT, string.IsNullOrEmpty(eventGame.Config.TeamConfigA.Color) ? Configuration.Menu.Button.Get : UI.Color(eventGame.TeamB.Color, 1f), 
                    eventGame.Config.TeamName(Arena.Team.B), 13, new UI4(0.56f, 0.1f, 0.99f, 0.9f), "arenaui.changeteam 1");

                eventPlayer.DestroyUI(UI_TEAM_SELECT);
                eventPlayer.AddUI(UI_TEAM_SELECT, container);
            }
        }

        private static void CreateSpectateLeaveButton(Arena.BaseEventPlayer eventPlayer)
        {
            CuiElementContainer container = UI.Container(UI_RESPAWN, Configuration.Menu.Panel.Get, new UI4(0f, 0f, 1f, 0.04f), true);

            UI.Panel(container, UI_RESPAWN, Configuration.Menu.Highlight.Get, new UI4(0f, 1f, 1f, 1.005f));

            UI.Button(container, UI_RESPAWN, Configuration.Menu.Highlight.Get, Message("UI.Death.Leave", eventPlayer.Player.userID), 13, new UI4(0.005f, 0.125f, 0.1f, 0.875f), "arenaui.leaveevent");

            UI.Button(container, UI_RESPAWN, Configuration.Menu.Highlight.Get, Message("UI.Death.Spectate", eventPlayer.Player.userID), 13, new UI4(0.105f, 0.125f, 0.2f, 0.875f), "arenaui.spectateevent");

            eventPlayer.DestroyUI(UI_RESPAWN);
            eventPlayer.AddUI(UI_RESPAWN, container);
        }

        #region Death Screen Commands
        [ConsoleCommand("arenaui.toggleautospawn")]
        private void ccmdToggleAutoSpawn(ConsoleSystem.Arg arg)
        {
            Arena.BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<Arena.BaseEventPlayer>();
            if (eventPlayer == null || !eventPlayer.IsDead)
                return;

            eventPlayer.AutoRespawn = !eventPlayer.AutoRespawn;
            UpdateRespawnButton(eventPlayer);
        }

        [ConsoleCommand("arenaui.changeteam")]
        private void ccmdChangeTeam(ConsoleSystem.Arg arg)
        {
            Arena.BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<Arena.BaseEventPlayer>();
            if (eventPlayer == null || !eventPlayer.IsDead)
                return;

            Arena.Team team = (Arena.Team)arg.GetInt(0);

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
                Arena.BaseEventGame.EventTeam currentTeam = eventPlayer.Team == Arena.Team.A ? eventPlayer.Event.TeamB : eventPlayer.Event.TeamA;
                Arena.BaseEventGame.EventTeam newTeam = eventPlayer.Team == Arena.Team.A ? eventPlayer.Event.TeamA : eventPlayer.Event.TeamB;

                currentTeam.RemoveFromTeam(eventPlayer.Player);
                newTeam.AddToTeam(eventPlayer.Player);
            }

            CreateClassSelector(eventPlayer);
            CreateTeamSelectionButton(eventPlayer);
        }

        [ConsoleCommand("arenaui.respawn")]
        private void ccmdRespawn(ConsoleSystem.Arg arg)
        {
            Arena.BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<Arena.BaseEventPlayer>();
            if (eventPlayer == null || !eventPlayer.IsDead || !eventPlayer.CanRespawn)
                return;

            if (string.IsNullOrEmpty(eventPlayer.Kit))
                return;

            Arena.RespawnPlayer(eventPlayer);
        }

        [ConsoleCommand("arenaui.selectkit")]
        private void ccmdSelectKit(ConsoleSystem.Arg arg)
        {
            Arena.BaseEventPlayer eventPlayer = arg.Player()?.GetComponent<Arena.BaseEventPlayer>();
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
            internal int Columns { get; set; }
            internal float XOffset { get; set; }
            internal float Width { get; set; }
            internal float XSpacing { get; set; }
            internal float YOffset { get; set; }
            internal float Height { get; set; }
            internal float YSpacing { get; set; }

            internal GridAlignment(int columns, float xOffset, float width, float xSpacing, float yOffset, float height, float ySpacing)
            {
                Columns = columns;
                XOffset = xOffset;
                Width = width;
                XSpacing = xSpacing;
                YOffset = yOffset;
                Height = height;
                YSpacing = ySpacing;
            }

            internal UI4 Evaluate(int index)
            {
                int rowNumber = index == 0 ? 0 : Mathf.FloorToInt(index / Columns);
                int columnNumber = index - (rowNumber * Columns);

                float offsetX = XOffset + (Width * columnNumber) + (XSpacing * columnNumber);

                float offsetY = (YOffset - (rowNumber * Height) - (YSpacing * rowNumber));

                return new UI4(offsetX, offsetY - Height, offsetX + Width, offsetY);
            }

            internal UI4 CenterVerticalEvaluate(int index, int total)
            {
                float halfHeight = ((float)total * 0.5f) * (YSpacing + Height);

                float top = 0.5f + halfHeight;

                int rowNumber = index == 0 ? 0 : Mathf.FloorToInt(index / Columns);
                int columnNumber = index - (rowNumber * Columns);

                float offsetX = XOffset + (Width * columnNumber) + (XSpacing * columnNumber);

                float offsetY = (top - (rowNumber * Height) - (YSpacing * rowNumber));

                return new UI4(offsetX, offsetY - Height, offsetX + Width, offsetY);
            }

            internal UI4 GetVerticalBounds(int total)
            {
                float halfHeight = ((float)total * 0.5f) * (YSpacing + Height);

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
        [ConsoleCommand("arenaui.close")]
        private void ccmdCloseUI(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            CuiHelper.DestroyUi(player, UI_MENU);
            CuiHelper.DestroyUi(player, UI_POPUP);
        }

        [ConsoleCommand("arenaui.joinevent")]
        private void ccmdJoinEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            Arena.BaseEventGame baseEventGame = Arena.Instance.FindEvent(CommandSafe(arg.GetString(0), true));
            if (baseEventGame == null)
                return;

            Arena.Team team = Arena.Team.None;
            if (arg.Args.Length > 1)
                team = (Arena.Team)arg.GetInt(1);

            if (baseEventGame.CanJoinEvent(player))
            {
                DestroyAllUI(player);
                baseEventGame.JoinEvent(player, team);
            }
        }
               
        [ConsoleCommand("arenaui.leaveevent")]
        private void ccmdLeaveEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            DestroyAllUI(player);

            Arena.BaseEventGame baseEventGame = player.GetComponent<Arena.BaseEventPlayer>()?.Event;
            if (baseEventGame == null)
                return;

            DestroyAllUI(player);
            baseEventGame.LeaveEvent(player);
        }

        [ConsoleCommand("arenaui.spectateevent")]
        private void ccmdSpectateEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            Arena.BaseEventPlayer eventPlayer = player.GetComponent<Arena.BaseEventPlayer>();
            if (eventPlayer == null)
                return;

            DestroyAllUI(player);
            eventPlayer.BeginSpectating();
        }
        #endregion

        #region Menu Selection  
        [ConsoleCommand("arenaui.event")]
        private void ccmdEvent(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            _eventCreators.Remove(player.userID);

            //OpenMenu(player, new MenuArgs(arg.GetInt(0), (MenuTab)arg.GetInt(1)));
        }
        #endregion
               
        #region Command Helpers
        private static string CommandSafe(string text, bool unpack = false) => unpack ? text.Replace("▊▊", " ") : text.Replace(" ", "▊▊");
        #endregion

        #region UI
        internal static void DestroyAllUI(BasePlayer player)
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

        public static class UI
        {
            public static CuiElementContainer Container(string panel, string color, UI4 dimensions, bool useCursor = false, string parent = "Overlay")
            {
                CuiElementContainer container = new CuiElementContainer()
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
                CuiElementContainer container = new CuiElementContainer()
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
                CuiElementContainer container = new CuiElementContainer()
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
                CuiElementContainer container = UI.Container(panel, "0 0 0 0", dimensions, false);

                UI.Label(container, panel, text, size, UI4.Full, align);

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
                CuiElement outline = new CuiElement
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
                ItemDefinition itemDefintion;
                if (ItemManager.itemDictionaryByName.TryGetValue(shortname, out itemDefintion)) 
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
                UI.Panel(container, panel, boxColor, dimensions);

                if (isOn)
                    UI.Label(container, panel, "✔", fontSize, dimensions);

                UI.Button(container, panel, "0 0 0 0", string.Empty, 0, dimensions, command);
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

            public static UI4 Full
            {
                get
                {
                    if (_full == null)
                        _full = new UI4(0, 0, 1, 1);
                    return _full;
                }
            }
        }
        #endregion

        #region Chat Commands
        private void CmdEventMenu(BasePlayer player, string command, string[] args)
        {
            Arena.BaseEventPlayer eventPlayer = Arena.GetUser(player);
            if (eventPlayer != null)
                OpenMatchMenu(eventPlayer);
            else
            {
                if (permission.UserHasPermission(player.UserIDString, Arena.BLACKLISTED_PERMISSION))
                {
                    player.ChatMessage(Message("Error.Blacklisted", player.userID));
                    return;
                }
                OpenEventMenu(player, string.Empty, string.Empty, 0);
            }
        }

        private void CmdEventStatistics(BasePlayer player, string command, string[] args) => OpenStatisticsMenu(player, StatisticTab.Personal, ArenaStatistics.Statistic.Rank, 0);

        private void CmdEventAdmin(BasePlayer player, string command, string[] args) => OpenAdminMenu(player, AdminTab.None, default(SelectorArgs), 0);

        private void CmdEventLeave(BasePlayer player, string command, string[] args)
        {
            DestroyAllUI(player);

            Arena.BaseEventGame baseEventGame = player.GetComponent<Arena.BaseEventPlayer>()?.Event;
            if (baseEventGame == null)
                return;

            DestroyAllUI(player);
            baseEventGame.LeaveEvent(player);
        }

        #endregion

        #region Config        
        public class ConfigData
        {
            public CommandOptions Commands { get; set; }

            public ImageOptions Images { get; set; }


            [JsonProperty(PropertyName = "Hide unused event types")]
            public bool HideUnused { get; set; }


            [JsonProperty(PropertyName = "Menu Colors")]
            public MenuColors Menu { get; set; }

            [JsonProperty(PropertyName = "Scoreboard Colors")]
            public ScoreboardColors Scoreboard { get; set; }


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

                [JsonProperty(PropertyName = "Statistics icon")]
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
                    get
                    {
                        if (_ui4 == null)
                            _ui4 = new UI4(CenterX - (Width * 0.5f), CenterY - (Height * 0.5f), CenterX + (Width * 0.5f), CenterY + (Height * 0.5f));
                        return _ui4;
                    }
                }
            }

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
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();
            if (Configuration.Version < new VersionNumber(2, 0, 3))
            {
                Configuration.Commands.LeaveCommand = baseConfig.Commands.LeaveCommand;
            }

            Configuration.Version = Version;
            PrintWarning("Config update completed!");
        }
        #endregion

        #region Localization
        private static string Message(string key, ulong playerId = 0UL) => Instance.lang.GetMessage(key, Instance, playerId != 0UL ? playerId.ToString() : null);

        private readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
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
            ["UI.Menu.Statistics"] = "Statistics",
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
    }
}
