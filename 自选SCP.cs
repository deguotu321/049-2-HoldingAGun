using System;
using System.Collections.Generic;
using System.ComponentModel;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using Exiled.API.Interfaces;
using Exiled.Events.EventArgs.Player;
using Exiled.Events.EventArgs.Scp049;
using PlayerRoles;
using UnityEngine;
using MEC;
using System.Linq;

namespace Scp0492Abilities
{
    public class ZombieAbilities : Plugin<ZombieAbilities.PluginConfig>
    {
        public override string Name { get; } = "SCP-049-2 Abilities";
        public override string Author { get; } = "YourName";
        public override Version Version { get { return new Version(2, 2, 0); } }
        public override Version RequiredExiledVersion { get { return new Version(8, 9, 11); } }

        private readonly Dictionary<Player, DateTime> _disguiseCooldown = new Dictionary<Player, DateTime>();
        private readonly Dictionary<Player, string> _originalCustomInfo = new Dictionary<Player, string>();
        private readonly Dictionary<Player, CoroutineHandle> _weaponCheckCoroutines = new Dictionary<Player, CoroutineHandle>();

        public class PluginConfig : IConfig
        {
            [Description("Whether the plugin is enabled")]
            public bool IsEnabled { get; set; } = true;

            [Description("Whether debug mode is enabled")]
            public bool Debug { get; set; } = false;

            [Description("Hint display duration (seconds)")]
            public float DisplayDuration { get; set; } = 8f;

            [Description("Hint position (number of top lines)")]
            public int TopLines { get; set; } = 3;

            [Description("SCP-049-2 shield value")]
            public int ZombieShield { get; set; } = 800;

            [Description("SCP-049-2 health value")]
            public int ZombieHealth { get; set; } = 49;

            [Description("Max health increase when healing SCP-049")]
            public int HealHpIncrease { get; set; } = 25;

            [Description("Health restored when healing SCP-049")]
            public int HealAmount { get; set; } = 30;

            [Description("Weapon equipped by SCP-049-2")]
            public ItemType ZombieWeapon { get; set; } = ItemType.GunFSP9;

            [Description("Disguise item type")]
            public ItemType DisguiseItem { get; set; } = ItemType.Coin;

            [Description("Disguise duration (seconds)")]
            public float DisguiseDuration { get; set; } = 30f;

            [Description("Disguise cooldown time (seconds)")]
            public float DisguiseCooldown { get; set; } = 60f;

            [Description("Weapon check interval (seconds)")]
            public float WeaponCheckInterval { get; set; } = 0.5f;
        }

        public override void OnEnabled()
        {
            Exiled.Events.Handlers.Player.ChangingRole += OnChangingRole;
            Exiled.Events.Handlers.Scp049.Attacking += OnAttacking;
            Exiled.Events.Handlers.Player.Dying += OnDying;
            Exiled.Events.Handlers.Player.Hurting += OnHurting;
            Exiled.Events.Handlers.Player.UsingItem += OnUsingItem;
            Exiled.Events.Handlers.Player.DroppingItem += OnDroppingItem;
            Exiled.Events.Handlers.Player.Died += OnDied;
            Exiled.Events.Handlers.Player.ChangedItem += OnChangedItem;
            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            Exiled.Events.Handlers.Player.ChangingRole -= OnChangingRole;
            Exiled.Events.Handlers.Scp049.Attacking -= OnAttacking;
            Exiled.Events.Handlers.Player.Dying -= OnDying;
            Exiled.Events.Handlers.Player.Hurting -= OnHurting;
            Exiled.Events.Handlers.Player.UsingItem -= OnUsingItem;
            Exiled.Events.Handlers.Player.DroppingItem -= OnDroppingItem;
            Exiled.Events.Handlers.Player.Died -= OnDied;
            Exiled.Events.Handlers.Player.ChangedItem -= OnChangedItem;

            // Stop all coroutines
            foreach (var coroutine in _weaponCheckCoroutines.Values)
            {
                Timing.KillCoroutines(coroutine);
            }
            _weaponCheckCoroutines.Clear();
            _disguiseCooldown.Clear();
            base.OnDisabled();
        }

        private void OnChangedItem(ChangedItemEventArgs ev)
        {
            // Ensure SCP-049-2 always holds the first item
            if (ev.Player.Role == RoleTypeId.Scp0492 &&
                ev.Player.Items.Count > 0 &&
                ev.Player.CurrentItem != ev.Player.Items.ElementAt(0))
            {
                Timing.CallDelayed(0.1f, () =>
                {
                    if (ev.Player.IsAlive && ev.Player.Items.Count > 0)
                        ev.Player.CurrentItem = ev.Player.Items.ElementAt(0);
                });
            }
        }

        private void OnChangingRole(ChangingRoleEventArgs ev)
        {
            if (ev.NewRole != RoleTypeId.Scp0492)
                return;

            // Set SCP-049-2 health
            ev.Player.MaxHealth = Config.ZombieHealth;
            ev.Player.Health = Config.ZombieHealth;

            // Clear existing shield and add new shield
            ev.Player.ArtificialHealth = 0;
            ev.Player.ArtificialHealth = Config.ZombieShield;

            // Reset custom info cache
            if (_originalCustomInfo.ContainsKey(ev.Player))
                _originalCustomInfo.Remove(ev.Player);

            // Add weapons and disguise items
            Timing.RunCoroutine(EquipZombieItems(ev.Player));

            // Start weapon check coroutine
            StartWeaponCheck(ev.Player);
        }

        private void StartWeaponCheck(Player player)
        {
            // If coroutine exists, stop it first
            if (_weaponCheckCoroutines.ContainsKey(player))
            {
                Timing.KillCoroutines(_weaponCheckCoroutines[player]);
                _weaponCheckCoroutines.Remove(player);
            }

            // Start new weapon check coroutine
            var coroutine = Timing.RunCoroutine(WeaponCheckCoroutine(player));
            _weaponCheckCoroutines[player] = coroutine;
        }

        private IEnumerator<float> WeaponCheckCoroutine(Player player)
        {
            while (player.IsAlive && player.Role == RoleTypeId.Scp0492)
            {
                // Check if player has weapon
                bool hasWeapon = player.Items.Any(item => item.Type == Config.ZombieWeapon);

                // If no weapon, give one and equip it
                if (!hasWeapon)
                {
                    Item weapon = player.AddItem(Config.ZombieWeapon);

                    // Ensure weapon is equipped
                    if (weapon != null)
                    {
                        player.CurrentItem = weapon;

                        if (Config.Debug)
                            player.ShowHint($"<color=yellow>Weapon automatically replenished and equipped: {Config.ZombieWeapon}</color>", 3f);
                    }
                }

                // Ensure player is holding the first item
                if (player.Items.Count > 0 && player.CurrentItem == null)
                {
                    player.CurrentItem = player.Items.ElementAt(0);

                    if (Config.Debug)
                        player.ShowHint($"<color=yellow>Automatically equipped item: {player.CurrentItem.Type}</color>", 3f);
                }

                yield return Timing.WaitForSeconds(Config.WeaponCheckInterval);
            }
        }

        private IEnumerator<float> EquipZombieItems(Player player)
        {
            yield return Timing.WaitForOneFrame;

            try
            {
                // Clear existing items
                player.ClearInventory();

                // Add weapon as default equipment
                Item weapon = player.AddItem(Config.ZombieWeapon);

                // Add disguise item
                if (Config.DisguiseItem != ItemType.None)
                {
                    player.AddItem(Config.DisguiseItem);
                }

                // Set default held item to weapon
                if (weapon != null)
                {
                    player.CurrentItem = weapon;

                    if (Config.Debug)
                        player.ShowHint($"<color=green>Weapon and disguise item equipped</color>\nDefault held: {weapon.Type}", 10f);
                }
                else
                {
                    if (Config.Debug)
                        Log.Error($"Failed to add weapon: {Config.ZombieWeapon}");
                }
            }
            catch (Exception e)
            {
                if (Config.Debug)
                    Log.Error($"Error equipping items: {e}");
            }
        }

        private void OnDroppingItem(DroppingItemEventArgs ev)
        {
            // If SCP-049-2 drops weapon, immediately replenish and equip
            if (ev.Player.Role == RoleTypeId.Scp0492 && ev.Item.Type == Config.ZombieWeapon)
            {
                Timing.CallDelayed(0.1f, () =>
                {
                    if (ev.Player.IsAlive && !ev.Player.Items.Any(item => item.Type == Config.ZombieWeapon))
                    {
                        Item weapon = ev.Player.AddItem(Config.ZombieWeapon);

                        // Ensure new weapon is equipped
                        if (weapon != null)
                        {
                            ev.Player.CurrentItem = weapon;

                            if (Config.Debug)
                                ev.Player.ShowHint($"<color=yellow>Dropped weapon automatically replenished and equipped</color>", 3f);
                        }
                    }
                });
            }
        }

        private void OnDied(DiedEventArgs ev)
        {
            // Stop coroutine when player dies
            if (_weaponCheckCoroutines.ContainsKey(ev.Player))
            {
                Timing.KillCoroutines(_weaponCheckCoroutines[ev.Player]);
                _weaponCheckCoroutines.Remove(ev.Player);
            }
        }

        private void OnUsingItem(UsingItemEventArgs ev)
        {
            // Check if SCP-049-2 is using disguise item
            if (ev.Player.Role != RoleTypeId.Scp0492 ||
                ev.Item.Type != Config.DisguiseItem)
                return;

            // Check cooldown
            if (_disguiseCooldown.TryGetValue(ev.Player, out var lastUse) &&
                (DateTime.Now - lastUse).TotalSeconds < Config.DisguiseCooldown)
            {
                ev.Player.ShowHint($"<color=red>Disguise ability cooling down! Remaining time: {(int)(Config.DisguiseCooldown - (DateTime.Now - lastUse).TotalSeconds)} seconds</color>", 5f);
                ev.IsAllowed = false;
                return;
            }

            // Trigger disguise effect
            ev.IsAllowed = true;
            _disguiseCooldown[ev.Player] = DateTime.Now;

            // Start disguise coroutine
            Timing.RunCoroutine(ApplyDisguise(ev.Player));

            if (Config.Debug)
                Log.Debug($"{ev.Player.Nickname} activated disguise ability");
        }

        private IEnumerator<float> ApplyDisguise(Player player)
        {
            // Save original custom info
            if (!_originalCustomInfo.ContainsKey(player))
            {
                _originalCustomInfo[player] = player.CustomInfo;
            }

            // Apply disguise effect
            player.CustomInfo = "Class-D Personnel";
            player.ShowHint("<color=yellow>You are now disguised as Class-D!\nUse item to cancel disguise</color>", Config.DisplayDuration);

            // Broadcast notification
            BroadcastMessage($"<color=yellow>SCP-049-2 {player.Nickname} disguised as human!</color>");

            // Wait for disguise cancellation
            bool disguiseActive = true;
            DateTime startTime = DateTime.Now;

            while (disguiseActive && player.IsAlive)
            {
                // Check if time expired
                if ((DateTime.Now - startTime).TotalSeconds >= Config.DisguiseDuration)
                {
                    disguiseActive = false;
                }

                yield return Timing.WaitForOneFrame;
            }

            // Restore original state
            if (player.IsAlive)
            {
                player.CustomInfo = _originalCustomInfo[player];
                player.ShowHint("<color=red>Your disguise has ended!</color>", 5f);

                // Re-give disguise item
                if (!player.Items.Any(item => item.Type == Config.DisguiseItem))
                {
                    player.AddItem(Config.DisguiseItem);
                    if (Config.Debug)
                        player.ShowHint($"<color=green>Disguise item regained</color>", 3f);
                }
            }
        }

        private void OnAttacking(AttackingEventArgs ev)
        {
            // Allow SCP-049-2 to pick up items
            if (ev.Player.Role == RoleTypeId.Scp0492)
            {
                ev.IsAllowed = true;
            }
        }

        private void OnDying(DyingEventArgs ev)
        {
            if (ev.Attacker == null || ev.Player == ev.Attacker)
                return;

            // SCP-049-2 kills human
            if (ev.Attacker.Role == RoleTypeId.Scp0492 &&
                ev.Player.Role.Team != Team.SCPs)
            {
                ev.IsAllowed = false;

                // Broadcast message
                BroadcastMessage(
                    $"<color=red>{ev.Player.Nickname} was infected by zombie!</color>"
                );

                // Convert human to SCP-049-2
                ev.Player.Role.Set(RoleTypeId.Scp0492, SpawnReason.Revived);

                Timing.RunCoroutine(EquipZombieItems(ev.Player));

                // Start weapon check
                StartWeaponCheck(ev.Player);
            }
        }

        private void OnHurting(HurtingEventArgs ev)
        {
            if (ev.Attacker == null || ev.Player == ev.Attacker)
                return;

            // SCP-049-2 shooting SCP-049
            if (ev.Attacker.Role == RoleTypeId.Scp0492 &&
                ev.Player.Role == RoleTypeId.Scp049)
            {
                ev.IsAllowed = false;

                // Increase SCP-049 max health
                ev.Player.MaxHealth += Config.HealHpIncrease;
                ev.Player.Health = Math.Min(ev.Player.Health + Config.HealAmount, ev.Player.MaxHealth);

                // Broadcast message
                BroadcastMessage(
                    $"<color=green>SCP-049 was healed by zombie!" +
                    $"\nHealth: +{Config.HealAmount}, Max Health: +{Config.HealHpIncrease}</color>"
                );

                // Private hint for SCP-049-2
                ev.Attacker.ShowHint(
                    $"<color=yellow>You healed SCP-049!</color>",
                    Config.DisplayDuration
                );
            }
        }

        private void BroadcastMessage(string content)
        {
            string formattedMessage = $"{new string('\n', Config.TopLines)}<align=center><b>{content}</b></align>";

            foreach (Player player in Player.List)
            {
                player.ShowHint(formattedMessage, Config.DisplayDuration);
            }
        }
    }
}
