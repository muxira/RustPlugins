using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("MyMiniPRO", "Muxira", "1.0.0")]
    public class MyMiniPRO : RustPlugin
    {
        private void Init()
        {
            permission.RegisterPermission("mymini.use", this);
        }

        [ChatCommand("mymini")]
        private void SpawnMiniCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, "mymini.use") && 
                !player.IsAdmin && !player.IsDeveloper)
            {
                SendReply(player, "У вас нет разрешения на использование этой команды!");
                return;
            }

            Vector3 position = player.eyes.position;
            Vector3 forward = player.eyes.BodyForward();
            Vector3 spawnPos = position + (forward * 3f) + new Vector3(0f, 1f, 0f);
            
            if (Physics.OverlapSphere(spawnPos, 1f, LayerMask.GetMask("Construction", "World", "Tree")).Length > 0)
            {
                SendReply(player, "Невозможно создать миникоптер здесь. Пожалуйста, выберите место с большим пространством!");
                return;
            }
            
            BaseEntity mini = GameManager.server.CreateEntity("assets/content/vehicles/minicopter/minicopter.entity.prefab", spawnPos);
            if (mini == null)
            {
                SendReply(player, "Ошибка при создании миникоптера!");
                return;
            }

            mini.transform.rotation = Quaternion.Euler(0f, player.eyes.rotation.eulerAngles.y, 0f);
            mini.Spawn();

            // Добавляем топливо в миникоптер
            var vehicle = mini as BaseVehicle;
            if (vehicle != null)
            {
                var fuelContainers = vehicle.GetComponentsInChildren<StorageContainer>();
                foreach (var container in fuelContainers)
                {
                    if (container != null && container.ShortPrefabName.Contains("fuel"))
                    {
                        Item fuel = ItemManager.CreateByName("lowgradefuel", 500);
                        if (fuel != null)
                        {
                            container.inventory.AddItem(fuel.info, 500);
                        }
                    }
                }
            }

            // Добавляем турель
            AutoTurret turret = GameManager.server.CreateEntity("assets/prefabs/npc/autoturret/autoturret_deployed.prefab", mini.transform.position) as AutoTurret;
            if (turret != null)
            {
                turret.SetParent(mini);
                turret.transform.localPosition = new Vector3(0f, 0.5f, -2.5f);
                turret.transform.localRotation = Quaternion.identity;
                turret.Spawn();
                
                // Настраиваем турель
                turret.pickup.enabled = false;
                turret.SetIsOnline(true);
                
                // Добавляем АК в турель
                Item weapon = ItemManager.CreateByName("rifle.ak", 1);
                if (weapon != null && turret.inventory != null)
                {
                    weapon.condition = 100;
                    turret.inventory.AddItem(weapon.info, 1);
                }
                
                // Добавляем 2000 патронов в турель
                Item ammo = ItemManager.CreateByName("ammo.rifle", 2000);
                if (ammo != null && turret.inventory != null)
                {
                    turret.inventory.AddItem(ammo.info, 2000);
                }

                // Авторизуем всех игроков для турели
                turret.authorizedPlayers.Clear();
                foreach (var p in BasePlayer.activePlayerList)
                {
                    var playerNameID = new ProtoBuf.PlayerNameID
                    {
                        userid = p.userID,
                        username = p.displayName
                    };
                    turret.authorizedPlayers.Add(playerNameID);
                }
            }

            // Добавляем хранилище к миникоптеру
            StorageContainer storage = GameManager.server.CreateEntity("assets/prefabs/deployable/small stash/small_stash_deployed.prefab", mini.transform.position) as StorageContainer;
            if (storage != null)
            {
                storage.SetParent(mini);
                storage.transform.localPosition = new Vector3(0f, 0.5f, 1.5f);
                storage.transform.localRotation = Quaternion.identity;
                storage.Spawn();
                storage.inventory.capacity = 12;
                storage.pickup.enabled = false;

                // Список возможного оружия с правильными типами патронов
                var weapons = new Dictionary<string, string>()
                {
                    {"rifle.ak", "ammo.rifle"},
                    {"rifle.lr300", "ammo.rifle"},
                    {"rifle.bolt", "ammo.rifle"},
                    {"smg.thompson", "ammo.pistol"},
                    {"smg.mp5", "ammo.pistol"},
                    {"pistol.python", "ammo.pistol"},
                    {"pistol.revolver", "ammo.pistol"}
                };

                // Выбираем случайное оружие
                string randomWeapon = weapons.Keys.ToList()[UnityEngine.Random.Range(0, weapons.Count)];
                Item weapon = ItemManager.CreateByName(randomWeapon, 1);
                if (weapon != null)
                {
                    weapon.condition = 100;
                    storage.inventory.AddItem(weapon.info, 1);
                    
                    // Добавляем правильные патроны для оружия
                    string ammoType = weapons[randomWeapon];
                    Item ammo = ItemManager.CreateByName(ammoType, 30);
                    if (ammo != null)
                    {
                        storage.inventory.AddItem(ammo.info, 30);
                    }
                }
            }
            
            SendReply(player, "Миникоптер создан с полным баком, случайным оружием и автоматической турелью!");

            // Добавляем таймер для проверки состояния турели
            timer.Every(5f, () =>
            {
                if (mini == null || mini.IsDestroyed)
                {
                    return;
                }

                var fuelContainer = vehicle?.GetComponentsInChildren<StorageContainer>()
                    .FirstOrDefault(x => x != null && x.ShortPrefabName.Contains("fuel"));
                
                if (fuelContainer != null)
                {
                    int fuelAmount = 0;
                    foreach (var item in fuelContainer.inventory.itemList)
                    {
                        if (item.info.shortname == "lowgradefuel")
                        {
                            fuelAmount += item.amount;
                        }
                    }
                    
                    bool isGrounded = !vehicle.IsMoving();

                    if (turret != null && !turret.IsDestroyed)
                    {
                        turret.SetIsOnline(fuelAmount >= 100 && isGrounded);
                    }
                }
            });
        }
    }
}
