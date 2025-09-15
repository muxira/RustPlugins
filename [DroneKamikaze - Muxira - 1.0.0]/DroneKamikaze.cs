using UnityEngine;
using System.Collections.Generic;
using Rust;
using Oxide.Core;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("DroneKamikaze", "Muxira", "1.0.0")]
    public class DroneKamikaze : RustPlugin
    {
        private void Init()
        {
            permission.RegisterPermission("kamikaze.use", this);
        }

        private void OnServerInitialized()
        {
            // Удаляем неправильную подписку
            // Subscribe(nameof(OnButtonPress));
        }

        private object OnPlayerInput(BasePlayer player, InputState input)
        {
            if (input.WasJustPressed(BUTTON.RELOAD))
            {
                Debug.Log("OnPlayerInput: RELOAD pressed");
                
                // Сначала проверяем, не сидит ли игрок за станцией
                var computerStation = player.GetMounted() as ComputerStation;
                Debug.Log($"OnPlayerInput: computerStation = {computerStation != null}");
                
                if (computerStation != null)
                {
                    var drone = computerStation.currentlyControllingEnt.Get(true) as Drone;
                    Debug.Log($"OnPlayerInput: drone from station = {drone != null}");
                    
                    if (drone != null)
                    {
                        var kamikazeDrone = drone.gameObject.GetComponent<KamikazeDrone>();
                        Debug.Log($"OnPlayerInput: kamikazeDrone = {kamikazeDrone != null}");
                        
                        if (kamikazeDrone != null)
                        {
                            Debug.Log("OnPlayerInput: Calling CheckInput");
                            kamikazeDrone.CheckInput(input, player);
                        }
                    }
                }
                else // Если игрок не за станцией, проверяем минирование/разминирование
                {
                    RaycastHit hit;
                    Debug.Log("OnPlayerInput: Checking for drone to mine/unmine");
                    if (Physics.Raycast(player.eyes.HeadRay(), out hit, 10f))
                    {
                        var drone = hit.GetEntity() as Drone;
                        Debug.Log($"OnPlayerInput: found drone = {drone != null}");
                        
                        if (drone != null)
                        {
                            var heldItem = player.GetActiveItem();
                            Debug.Log($"OnPlayerInput: held item = {heldItem?.info.shortname}");

                            if (heldItem != null)
                            {
                                // Проверяем минирование гранатами
                                if (heldItem.info.shortname == "grenade.f1")
                                {
                                    Debug.Log("OnPlayerInput: Attempting to mine with grenades");
                                    if (heldItem.amount >= 3)
                                    {
                                        if (UnityEngine.Random.Range(0f, 100f) <= 10f)
                                        {
                                            Debug.Log("OnPlayerInput: Random explosion during mining!");
                                            SendReply(player, "Граната сдетонировала при установке!");
                                            heldItem.UseItem(3);
                                            
                                            var tempKamikaze = drone.gameObject.AddComponent<KamikazeDrone>();
                                            tempKamikaze.ForceExplode();
                                            return null;
                                        }

                                        Debug.Log("OnPlayerInput: Successfully mining drone");
                                        heldItem.UseItem(3);
                                        var kamikazeDrone = drone.gameObject.AddComponent<KamikazeDrone>();
                                        
                                        // Создаем три гранаты на дроне
                                        for (int i = 0; i < 3; i++)
                                        {
                                            var grenadeEntity = GameManager.server.CreateEntity("assets/prefabs/weapons/f1 grenade/grenade.f1.entity.prefab", 
                                                drone.transform.position + new Vector3((i - 1) * 0.1f, 0.2f, 0f));
                                            
                                            if (grenadeEntity != null)
                                            {
                                                grenadeEntity.SetParent(drone, true);
                                                grenadeEntity.Spawn();
                                                grenadeEntity.transform.localScale = new Vector3(2f, 2f, 2f);
                                                grenadeEntity.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                                                
                                                var rigidbody = grenadeEntity.GetComponent<Rigidbody>();
                                                if (rigidbody != null)
                                                {
                                                    UnityEngine.Object.Destroy(rigidbody);
                                                }
                                            }
                                        }
                                        
                                        SendReply(player, "Дрон успешно заминирован!");
                                    }
                                    else
                                    {
                                        SendReply(player, "Нужно 3 гранаты для минирования!");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return null;
        }
    }

    public class KamikazeDrone : MonoBehaviour
    {
        public bool HasTargetingComputer { get; set; }
        private Drone drone;
        private bool hasExploded = false;
        private Vector3 lastPosition;
        private float minSpeed = 3f;
        private float minHeight = 1f;
        private const float CHECK_INTERVAL = 0.1f;
        private float collisionForceThreshold = 2f;

        private void Awake()
        {
            drone = GetComponent<Drone>();
            lastPosition = drone.transform.position;
            InvokeRepeating(nameof(CheckSpeed), 0f, CHECK_INTERVAL);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (hasExploded || drone == null) return;

            float collisionForce = collision.relativeVelocity.magnitude;
            if (collisionForce >= collisionForceThreshold && IsValidForExplosion())
            {
                Explode();
            }
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(CheckSpeed));
        }

        private float GetCurrentSpeed()
        {
            var currentPosition = drone.transform.position;
            var distance = Vector3.Distance(lastPosition, currentPosition);
            var speed = distance / CHECK_INTERVAL;
            Debug.Log($"GetCurrentSpeed: distance = {distance}, interval = {CHECK_INTERVAL}, speed = {speed}");
            lastPosition = currentPosition;
            return speed;
        }

        private bool IsValidForExplosion()
        {
            if (drone == null)
            {
                Debug.Log("IsValidForExplosion: drone is null");
                return false;
            }

            RaycastHit hit;
            float height = 100f;
            if (Physics.Raycast(drone.transform.position, Vector3.down, out hit, 100f))
            {
                height = hit.distance;
            }
            
            float speed = GetCurrentSpeed();
            Debug.Log($"IsValidForExplosion: height = {height}, speed = {speed}, minHeight = {minHeight}, minSpeed = {minSpeed}");
            
            return speed >= minSpeed && height >= minHeight;
        }

        private void CheckSpeed()
        {
            if (drone == null || hasExploded) return;
            lastPosition = drone.transform.position;
        }

        public void CheckInput(InputState input, BasePlayer player)
        {
            if (drone == null)
            {
                Debug.Log("KamikazeDrone: drone is null");
                return;
            }
            if (!HasTargetingComputer)
            {
                Debug.Log("KamikazeDrone: no targeting computer");
                return;
            }
            if (hasExploded)
            {
                Debug.Log("KamikazeDrone: already exploded");
                return;
            }

            var computerStation = player.GetMounted() as ComputerStation;
            Debug.Log($"KamikazeDrone: computerStation = {computerStation != null}");
            
            if (computerStation != null)
            {
                var controlledDrone = computerStation.currentlyControllingEnt.Get(true);
                Debug.Log($"KamikazeDrone: controlledDrone = {controlledDrone != null}, matches this drone = {controlledDrone == drone}");
                
                if (controlledDrone == drone)
                {
                    Debug.Log($"KamikazeDrone: BUTTON.RELOAD pressed = {input.WasJustPressed(BUTTON.RELOAD)}");
                    if (input.WasJustPressed(BUTTON.RELOAD))
                    {
                        Debug.Log($"KamikazeDrone: IsValidForExplosion = {IsValidForExplosion()}");
                        if (IsValidForExplosion())
                        {
                            Debug.Log("KamikazeDrone: Calling ForceExplode");
                            ForceExplode();
                        }
                        else
                        {
                            Debug.Log("KamikazeDrone: Not valid for explosion, sending message");
                            player.ChatMessage("Дрон должен быть в полете и набрать достаточную скорость!");
                        }
                    }
                }
            }
        }

        public void Explode()
        {
            if (hasExploded) return;
            hasExploded = true;

            // Создаем эффекты взрыва
            Effect.server.Run("assets/prefabs/weapons/satchelcharge/effects/satchel-charge-explosion.prefab", drone.transform.position);
            Effect.server.Run("assets/prefabs/weapons/rocketlauncher/effects/rocket_explosion.prefab", drone.transform.position);

            // Наносим урон ближайшим объектам
            var position = drone.transform.position;
            var entities = Physics.OverlapSphere(position, 15f);
            
            foreach (var collider in entities)
            {
                var entity = collider.GetComponent<BaseCombatEntity>();
                if (entity != null)
                {
                    var building = entity as BuildingBlock;
                    if (building != null)
                    {
                        building.Hurt(1f, DamageType.Explosion, drone, true);
                    }
                    else
                    {
                        float distance = Vector3.Distance(position, entity.transform.position);
                        float damageMultiplier = 1f - (distance / 15f);
                        float damage = 500f * damageMultiplier;
                        entity.Hurt(damage, DamageType.Explosion, drone, true);
                    }
                }
            }

            // Уничтожаем дрон через кадр
            Invoke("DestroyDrone", 0.1f);
        }

        private void DestroyDrone()
        {
            if (drone != null)
                drone.Kill();
        }

        public void ForceExplode()
        {
            // Этот метод будет вызываться для принудительного взрыва, игнорируя проверки скорости и высоты
            if (hasExploded || drone == null) return;
            Explode();
        }
    }
}
