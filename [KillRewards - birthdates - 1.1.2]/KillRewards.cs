using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Kill Rewards", "birthdates", "1.1.2")]
    [Description("Get rewards for getting x amount of kills during 1 life or the entire wipe")]
    public class KillRewards : RustPlugin
    {
        #region Variables

        private const string permission_use = "killrewards.use";

        #endregion

        #region Hooks

        private void Init()
        {
            LoadConfig();
            permission.RegisterPermission(permission_use, this);
            _data = Interface.Oxide.DataFileSystem.ReadObject<Data>(Name);
        }

        private void Unload()
        {
            SaveData();
        }

        private void OnEntityDeath(BasePlayer target, HitInfo info)
        {
            if (info == null || target == null || !target.userID.IsSteamId()) return;
            if (_config.oneLife) ClearKills(target.UserIDString);
            if (info.InitiatorPlayer == null || info.InitiatorPlayer == target) return;
            var player = info.InitiatorPlayer;
            var ID = player.UserIDString;
            if (!permission.UserHasPermission(ID, permission_use)) return;
            var amount = AddKill(ID);
            KillReward Reward;
            if (!TryGetReward(amount, out Reward)) return;
            Reward.Commands.ForEach(Command => Server.Command(string.Format(Command, ID)));
            player.ChatMessage(string.Format(lang.GetMessage("KillRewarded", this, player.UserIDString), amount));
        }

        private bool TryGetReward(int Kills, out KillReward Reward)
        {
            Reward = _config.rewards.Find(reward => reward.Kills == Kills);
            return Reward != null;
        }

        private void ClearKills(string ID)
        {
            _data.kills.Remove(ID);
        }

        private int AddKill(string ID)
        {
            var kills = 1;
            if (_data.kills.ContainsKey(ID))
                kills = _data.kills[ID]++;
            else
                _data.kills.Add(ID, 1);
            return kills;
        }

        #endregion

        #region Configuration & Language

        private ConfigFile _config;
        private Data _data;

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"KillRewarded", "Woohoo! A {0} killstreak!"}
            }, this);
        }

        private class Data
        {
            public readonly Dictionary<string, int> kills = new Dictionary<string, int>();
        }


        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        public class KillReward
        {
            public List<string> Commands;

            [JsonProperty("Kill Goal")] public int Kills;
        }

        public class ConfigFile
        {
            [JsonProperty("One Life?")] public bool oneLife;

            [JsonProperty("Rewards")] public List<KillReward> rewards;

            public static ConfigFile DefaultConfig()
            {
                return new ConfigFile
                {
                    oneLife = true,
                    rewards = new List<KillReward>
                    {
                        new KillReward
                        {
                            Kills = 1,
                            Commands = new List<string>
                            {
                                "inventory.giveto {0} stones 100"
                            }
                        },
                        new KillReward
                        {
                            Kills = 5,
                            Commands = new List<string>
                            {
                                "inventory.giveto {0} stones 500"
                            }
                        }
                    }
                };
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<ConfigFile>();
            if (_config == null) LoadDefaultConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = ConfigFile.DefaultConfig();
            PrintWarning("Default configuration has been loaded.");
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        #endregion
    }
}
//Generated with birthdates' Plugin Maker