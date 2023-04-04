using LibBSP;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class EntityConverter
	{
		[Flags]
		private enum TargetInitFlags
		{
			KeepArmor = 1,
			KeepHealth = 2,
			KeepWeapons = 4,
			KeepPowerUps = 8,
			KeepHoldable = 16,
			RemoveMachineGun = 32
		}

		[Flags]
		private enum FuncButtonFlags
		{
			DontMove = 1,
			TouchActivates = 256,
			DamageActivates = 512,
		}

		[Flags]
		private enum Q3TriggerTeleportFlags
		{
			Spectator = 1,
			KeepSpeed = 2
		}

		private enum WeaponSlot
		{
			MachineGun = 2,
			Gauntlet = 3,
			GrenadeLauncher = 4,
			RocketLauncher = 5,
			// LightningGun = 6,
			// Railgun = 7,
			PlasmaGun = 8,
			BFG = 9
		}

		private Entities q3Entities;
		private Entities sourceEntities;
		private Dictionary<string, Shader> shaderDict;
		private int minDamageToConvertTrigger;

		private Dictionary<string, List<Entity>> entityDict = new Dictionary<string, List<Entity>>();
		private List<Entity> removeEntities = new List<Entity>(); // Entities to remove after conversion (ex: remove weapons after converting a trigger_multiple that references target_give). TODO: It might be better to convert entities by priority, such as trigger_multiples first so that target_give weapons can be ignored after
		private int currentCheckpointIndex = 2;

		private const string MOMENTUM_START_ENTITY = "_momentum_player_start_";

		public EntityConverter(Entities q3Entities, Entities sourceEntities, Dictionary<string, Shader> shaderDict, int minDamageToConvertTrigger)
		{
			this.q3Entities = q3Entities;
			this.sourceEntities = sourceEntities;
			this.shaderDict = shaderDict;
			this.minDamageToConvertTrigger = minDamageToConvertTrigger;

			foreach (var entity in q3Entities)
			{
				if (!entityDict.ContainsKey(entity.Name))
					entityDict.Add(entity.Name, new List<Entity>() { entity });
				else
					entityDict[entity.Name].Add(entity);
			}
		}

		public void Convert()
		{
			var giveTargets = GetGiveTargets();

			foreach (var entity in q3Entities)
			{
				var ignoreEntity = false;

				switch (entity.ClassName)
				{
					case "worldspawn":
						ConvertWorldspawn(entity);
						break;
					case "info_player_start":
						ConvertPlayerStart(entity);
						break;
					case "info_player_deathmatch":
						ConvertPlayerStart(entity);
						break;
					case "trigger_hurt":
						ConvertTriggerHurt(entity);
						break;
					case "trigger_multiple":
						ConvertTriggerMultiple(entity);
						break;
					case "trigger_push":
						ConvertTriggerPush(entity);
						break;
					case "trigger_teleport":
						ConvertTriggerTeleport(entity);
						break;
					case "misc_teleporter_dest":
						ConvertTeleportDestination(entity);
						break;
					case "target_position":
						entity.ClassName = "info_target";
						break;
					case "func_door":
						ConvertFuncDoor(entity);
						break;
					case "func_button":
						ConvertFuncButton(entity);
						break;
					// Ignore these entities since they have no use in Source engine
					case "target_startTimer":
					case "target_stopTimer":
					case "target_checkpoint":
					case "target_give":
					case "target_init":
						ignoreEntity = true;
						break;
					default:
						{
							if (!giveTargets.Contains(entity.Name)) // Don't convert equipment linked to target_give
								ConvertEquipment(entity);

							break;
						}
				}

				if (!ignoreEntity)
				{
					ConvertAngles(entity);
					sourceEntities.Add(entity);
				}
			}

			foreach (var entity in removeEntities)
				sourceEntities.Remove(entity);
		}

		private void ConvertTeleportDestination(Entity entity)
		{
			entity.ClassName = "info_teleport_destination";
			SetTeleportOrigin(entity);
		}

		private HashSet<string> GetGiveTargets()
		{
			var targets = new HashSet<string>();
			foreach (var entity in q3Entities)
			{
				if (entity.ClassName == "target_give" && entity.TryGetValue("target", out var target))
					targets.Add(target);
			}

			return targets;
		}

		private void ConvertFuncDoor(Entity entity)
		{
			SetMoveDir(entity);

			if (float.TryParse(entity["health"], out var health))
				entity.ClassName = "func_button"; // Health is obsolete on func_door, maybe fix in engine and update this
		}

		private void ConvertFuncButton(Entity entity)
		{
			SetMoveDir(entity);
			SetButtonFlags(entity);

			if (entity["wait"] == "-1") // A value of -1 in quake is instantly reset position, in source it is don't reset position.
				entity["wait"] = "0.001"; // exactly 0 also behaves as don't reset in source, so the delay is as short as possible without being 0.

			var targets = GetTargetEntities(entity);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "func_door":
						OpenDoorOnPressed(entity, target);
						break;
				}
			}
		}

		private static void OpenDoorOnPressed(Entity button, Entity door)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnPressed",
				target = door["targetname"],
				action = "Open",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			button.connections.Add(connection);
		}

		private static void SetButtonFlags(Entity button)
		{
			if (!float.TryParse(button["speed"], out var speed))
				return;

			var spawnflags = 0;

			if ((speed == -1 || speed >= 9999) && (button["wait"] == "-1")) // TODO: Add customization setting for the upper bounds potentially?
				spawnflags |= (int)FuncButtonFlags.DontMove;

			if (!float.TryParse(button["health"], out var health) || button["health"] == "0")
				spawnflags |= (int)FuncButtonFlags.TouchActivates;
			else
				spawnflags |= (int)FuncButtonFlags.DamageActivates;

			button["spawnflags"] = spawnflags.ToString();
		}

		private static void SetMoveDir(Entity entity)
		{
			if (!float.TryParse(entity["angle"], out var angle))
				return;

			if (angle == -1) // UP
				entity["movedir"] = "-90 0 0";
			else if (angle == -2) // DOWN
				entity["movedir"] = "90 0 0";
			else
				entity["movedir"] = $"0 {angle} 0";

			entity.Remove("angle");
		}

		private void ConvertWorldspawn(Entity worldspawn)
		{
			foreach (var shader in shaderDict.Values)
			{
				if (shader.skyParms != null)
				{
					var skyName = shader.skyParms.outerBox;
					if (!string.IsNullOrEmpty(skyName))
						worldspawn["skyname"] = skyName;
				}
			}
		}

		private void ConvertPlayerStart(Entity playerStart)
		{
			playerStart.ClassName = "info_player_start";
			playerStart.Name = MOMENTUM_START_ENTITY;

			var targets = GetTargetEntities(playerStart);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "target_give":
						ConvertPlayerStartTargetGive(playerStart, target);
						break;
					case "target_init":
						ConvertPlayerStartTargetInit(playerStart, target);
						break;
				}
			}
		}

		private void ConvertPlayerStartTargetGive(Entity playerStart, Entity targetGive)
		{
			var targets = GetTargetEntities(targetGive);
			foreach (var target in targets)
			{
				if (target.ClassName.StartsWith("weapon_"))
				{
					var weaponName = GetMomentumWeaponName(target.ClassName);
					var weapon = CreateTargetGiveWeapon(weaponName, playerStart.Origin, target["count"]);
					sourceEntities.Add(weapon);
				}
				else if (target.ClassName.StartsWith("ammo_"))
				{
					var ammoName = GetMomentumAmmoName(target.ClassName);
					var ammo = CreateTargetGiveAmmo(ammoName, playerStart.Origin, target["count"]);
					sourceEntities.Add(ammo);
				}
				else if (target.ClassName.StartsWith("item_"))
				{
					var itemName = GetMomentumItemName(target.ClassName);
					var item = CreateTargetGiveItem(itemName, playerStart.Origin, target["count"]);
					sourceEntities.Add(item);
				}

				removeEntities.Add(target);
			}
		}

		private void ConvertPlayerStartTargetInit(Entity playerStart, Entity targetInit)
		{
			var targets = GetTargetEntities(targetInit);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "target_give":
						ConvertPlayerStartTargetGive(playerStart, target);
						break;
				}
			}
		}

		private Entity CreateTargetGiveWeapon(string weaponName, Vector3 origin, string count)
		{
			var weapon = new Entity();

			weapon.ClassName = "momentum_weapon_spawner";
			weapon.Origin = origin;
			weapon["weaponname"] = weaponName;
			weapon["pickupammo"] = count;
			weapon["resettime"] = "-1"; // Only use once
			weapon["rendermode"] = "10";

			return weapon;
		}

		private Entity CreateTargetGiveAmmo(string ammoName, Vector3 origin, string count)
		{
			var ammo = new Entity();

			ammo.ClassName = "momentum_pickup_ammo";
			ammo.Origin = origin;
			ammo["ammoname"] = ammoName;
			ammo["pickupammo"] = count;
			ammo["resettime"] = "-1"; // Only use once
			ammo["rendermode"] = "10";

			return ammo;
		}

		private Entity CreateTargetGiveItem(string itemName, Vector3 origin, string count)
		{
			var item = new Entity();

			item.ClassName = itemName;
			item.Origin = origin;
			item["resettime"] = "-1"; // Only use once
			item["rendermode"] = "10";

			if (itemName == "momentum_powerup_haste")
				item["hastetime"] = count;
			else if (itemName == "momentum_powerup_damage_boost")
				item["damageboosttime"] = count;

			return item;
		}

		private void ConvertTriggerHurt(Entity trigger)
		{
			if (int.TryParse(trigger["dmg"], out var damage))
			{
				if (damage >= minDamageToConvertTrigger)
				{
					trigger.ClassName = "trigger_teleport";
					trigger["target"] = MOMENTUM_START_ENTITY;
					trigger["spawnflags"] = "1";
					trigger["mode"] = "1";
				}
			}
		}

		private void ConvertTriggerMultiple(Entity trigger)
		{
			var targets = GetTargetEntities(trigger);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "target_stopTimer":
						ConvertTimerTrigger(trigger, "trigger_momentum_timer_stop", 0);
						break;
					case "target_checkpoint":
						ConvertTimerTrigger(trigger, "trigger_momentum_timer_checkpoint", currentCheckpointIndex);
						currentCheckpointIndex++;
						break;
					case "target_give":
						ConvertGiveTrigger(trigger, target);
						break;
					case "target_teleporter":
						ConvertTeleportTrigger(trigger, target);
						break;
					case "target_kill":
						ConvertKillTrigger(trigger);
						break;
					case "target_init":
						ConvertInitTrigger(trigger, target);
						break;
					case "func_door":
						OpenDoorOnStartTouch(trigger, target);
						break;
				}
			}

			trigger["spawnflags"] = "1";
		}

		private void OpenDoorOnStartTouch(Entity trigger, Entity door)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = door["targetname"],
				action = "Open",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private void ConvertInitTrigger(Entity trigger, Entity targetInit)
		{
			var spawnflags = (TargetInitFlags)targetInit.Spawnflags;
			if (!spawnflags.HasFlag(TargetInitFlags.KeepPowerUps))
			{
				GiveHasteOnStartTouch(trigger, "0");
				GiveQuadOnStartTouch(trigger, "0");
			}
			if (!spawnflags.HasFlag(TargetInitFlags.KeepWeapons))
			{
				RemoveWeaponOnStartTouch(trigger, (int)WeaponSlot.Gauntlet);
				RemoveWeaponOnStartTouch(trigger, (int)WeaponSlot.GrenadeLauncher);
				RemoveWeaponOnStartTouch(trigger, (int)WeaponSlot.RocketLauncher);
				RemoveWeaponOnStartTouch(trigger, (int)WeaponSlot.PlasmaGun);
				RemoveWeaponOnStartTouch(trigger, (int)WeaponSlot.BFG);
			}
			if (spawnflags.HasFlag(TargetInitFlags.RemoveMachineGun))
			{
				RemoveWeaponOnStartTouch(trigger, (int)WeaponSlot.MachineGun);
			}

			var targets = GetTargetEntities(targetInit);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "target_give":
						ConvertGiveTrigger(trigger, target);
						break;
				}
			}
		}

		private static void RemoveWeaponOnStartTouch(Entity trigger, int weaponIndex)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = "RemoveDFWeapon",
				param = weaponIndex.ToString(),
				delay = 0f,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private void ConvertKillTrigger(Entity trigger)
		{
			trigger.ClassName = "trigger_teleport";
			trigger["target"] = MOMENTUM_START_ENTITY;
			trigger["mode"] = "1";
		}

		private static void ConvertTimerTrigger(Entity trigger, string className, int zoneNumber)
		{
			trigger.ClassName = className;
			//trigger["track_number"] = "0";
			trigger["zone_number"] = zoneNumber.ToString();

			trigger.Remove("target");
		}

		// TODO: Convert target_give for player spawn entities
		private void ConvertGiveTrigger(Entity trigger, Entity targetGive)
		{
			// TODO: Support more entities (health, armor, etc.)
			var targets = GetTargetEntities(targetGive);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "item_haste":
						GiveHasteOnStartTouch(trigger, target["count"]);
						break;
					case "item_enviro": // TODO: Not supported yet
						break;
					case "item_flight": // TODO: Not supported yet
						break;
					case "item_quad":
						GiveQuadOnStartTouch(trigger, target["count"]);
						break;
					default:
						if (target.ClassName.StartsWith("weapon_"))
							GiveWeaponOnStartTouch(trigger, target);
						else if (target.ClassName.StartsWith("ammo_"))
							GiveAmmoOnStartTouch(trigger, target);
						break;
				}

				removeEntities.Add(target);
			}

			trigger.Remove("target");
		}

		private void GiveHasteOnStartTouch(Entity trigger, string duration)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = "SetHaste",
				param = duration,
				delay = 0f,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private void GiveQuadOnStartTouch(Entity trigger, string duration)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = "SetDamageBoost",
				param = duration,
				delay = 0f,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private void GiveWeaponOnStartTouch(Entity trigger, Entity weaponEnt)
		{
			var weaponIndex = GetWeaponIndex(weaponEnt.ClassName);
			if (weaponIndex == -1)
				return;

			// TODO: Support weapon count
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = "GiveDFWeapon",
				param = weaponIndex.ToString(),
				delay = 0.01f, //hack to make sure that the weapon removal applies before weapon give
				fireOnce = -1
			};
			trigger.connections.Add(connection);

			GiveWeaponAmmoOnStartTouch(trigger, weaponEnt);

			var targets = GetTargetEntities(weaponEnt); //TODO: more robust solution for entities targeting other entities inside a trigger_multiple
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "target_give":
						ConvertGiveTrigger(trigger, target);
						break;
				}
			}
		}

		private void GiveWeaponAmmoOnStartTouch(Entity trigger, Entity weaponEnt)
		{
			if (!weaponEnt.TryGetValue("count", out var count) || count == "0") // Every quake weapon has a default ammo count when none is specified
				count = GetDefaultAmmoCount(weaponEnt.ClassName);

			if (count == "-1")
				return;

			var ammoType = GetWeaponAmmoType(weaponEnt.ClassName);
			if (string.IsNullOrEmpty(ammoType))
				return;

			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = ammoType,
				param = count,
				delay = 0f,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private string GetDefaultAmmoCount(string weaponName)
		{
			switch (weaponName)
			{
				case "weapon_machinegun":
					return "40";
				case "weapon_grenadelauncher":
					return "10";
				case "weapon_rocketlauncher":
					return "10";
				case "weapon_plasmagun":
					return "50";
				case "weapon_lightning":
					return "100";
				case "weapon_bfg":
					return "20";
				default:
					return "-1";
			}
		}

		private int GetWeaponIndex(string weaponName)
		{
			switch (weaponName)
			{
				case "weapon_machinegun":
					return (int)WeaponSlot.MachineGun;
				case "weapon_gauntlet":
					return (int)WeaponSlot.Gauntlet;
				case "weapon_grenadelauncher":
					return (int)WeaponSlot.GrenadeLauncher;
				case "weapon_rocketlauncher":
					return (int)WeaponSlot.RocketLauncher;
				case "weapon_plasmagun":
					return (int)WeaponSlot.PlasmaGun;
				case "weapon_lightning": // TEMP: Lightning gun doesn't exist yet
				case "weapon_bfg":
					return (int)WeaponSlot.BFG;
				default:
					return -1;
			}
		}

		private string GetWeaponAmmoType(string weaponName)
		{
			switch (weaponName)
			{
				case "weapon_machinegun":
					return "SetBullets";
				case "weapon_grenadelauncher":
					return "SetGrenades";
				case "weapon_rocketlauncher":
					return "SetRockets";
				case "weapon_plasmagun":
					return "SetCells";
				//case "weapon_lightning":
				//	return "SetLightning";
				case "weapon_bfg":
					return "SetBfgRockets";
				default:
					return string.Empty;
			}
		}

		private void GiveAmmoOnStartTouch(Entity trigger, Entity ammoEnt)
		{
			if (ammoEnt["notcpm"] == "1") // TODO: Figure out how to handle gamemode specific entities more robustly
				return;

			var ammoOutput = GetAmmoOutput(ammoEnt.ClassName);
			if (string.IsNullOrEmpty(ammoOutput))
				return;

			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = ammoOutput,
				param = ammoEnt["count"],
				delay = 0.01f, //hack to make giving ammo happen after setting ammo
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private string GetAmmoOutput(string ammoName)
		{
			switch (ammoName)
			{
				case "ammo_bfg":
					return "AddBfgRockets";
				case "ammo_bullets": // Machine gun
					return "AddBullets";
				case "ammo_cells": // Plasma gun
					return "AddCells";
				case "ammo_grenades":
					return "AddGrenades";
				case "ammo_lightning":
					return "AddLightning";
				case "ammo_rockets":
					return "AddRockets";
				case "ammo_shells": // Shotgun
					return "AddShells";
				case "ammo_slugs": // Railgun
					return "AddRails";
				default:
					return string.Empty;
			}
		}

		private void ConvertTeleportTrigger(Entity trigger, Entity targetTele)
		{
			var targets = GetTargetEntities(targetTele);
			if (targets.Any())
			{
				trigger.ClassName = "trigger_teleport";
				trigger["target"] = targets.First().Name;
			}

			if (targetTele["spawnflags"] == "1")
				trigger["mode"] = "3";
			else
			{
				trigger["mode"] = "5";
				trigger["setspeed"] = "400";
			}
		}

		private void ConvertTriggerPush(Entity trigger)
		{
			var targets = GetTargetEntities(trigger);
			if (targets.Any())
			{
				trigger.ClassName = "trigger_catapult";
				trigger["launchtarget"] = targets.First().Name;
				trigger["spawnflags"] = "1";
				trigger["playerspeed"] = "450";

				trigger.Remove("target");
			}
		}

		private void ConvertTriggerTeleport(Entity trigger)
		{
			var spawnflags = (Q3TriggerTeleportFlags)trigger.Spawnflags;

			if (spawnflags.HasFlag(Q3TriggerTeleportFlags.KeepSpeed))
				trigger["mode"] = "3";
			else
			{
				trigger["mode"] = "5";
				trigger["setspeed"] = "400";
			}
			trigger["spawnflags"] = "1";

			var targets = GetTargetEntities(trigger);
			foreach (var target in targets)
			{
				if (target.ClassName != "info_teleport_destination")
					ConvertTeleportDestination(target);
			}
		}

		private void ConvertEquipment(Entity entity)
		{
			if (entity.ClassName.StartsWith("weapon_"))
				ConvertWeapon(entity);
			else if (entity.ClassName.StartsWith("ammo_"))
				ConvertAmmo(entity);
			else if (entity.ClassName.StartsWith("item_"))
				ConvertItem(entity);
		}

		private void ConvertWeapon(Entity weaponEnt)
		{
			weaponEnt["resettime"] = GetWeaponRespawnTime(weaponEnt);
			weaponEnt["weaponname"] = GetMomentumWeaponName(weaponEnt.ClassName);
			weaponEnt["pickupammo"] = weaponEnt["count"];
			weaponEnt.ClassName = "momentum_weapon_spawner";
		}

		private string GetWeaponRespawnTime(Entity weaponEnt)
		{
			if (weaponEnt.TryGetValue("wait", out var wait))
				return wait;
			
			return "5";
		}

		private string GetMomentumWeaponName(string q3WeaponName)
		{
			switch (q3WeaponName)
			{
				case "weapon_machinegun":
					return "weapon_momentum_machinegun";
				case "weapon_gauntlet":
					return "weapon_knife";
				case "weapon_grenadelauncher":
					return "weapon_momentum_df_grenadelauncher";
				case "weapon_rocketlauncher":
					return "weapon_momentum_df_rocketlauncher";
				case "weapon_plasmagun":
					return "weapon_momentum_df_plasmagun";
				case "weapon_bfg":
					return "weapon_momentum_df_bfg";
				case "item_haste":
					return "momentum_powerup_haste";
				case "item_quad":
					return "momentum_powerup_damage_boost";
				default:
					return string.Empty;
			}
		}

		private void ConvertAmmo(Entity ammoEnt)
		{
			ammoEnt["resettime"] = GetAmmoRespawnTime(ammoEnt);
			ammoEnt["ammoname"] = GetMomentumAmmoName(ammoEnt.ClassName);
			ammoEnt["pickupammo"] = ammoEnt["count"];
			ammoEnt.ClassName = "momentum_pickup_ammo";
		}

		private string GetAmmoRespawnTime(Entity ammoEnt)
		{
			if (ammoEnt.TryGetValue("wait", out var wait))
				return wait;
			
			return "40";
		}

		private string GetMomentumAmmoName(string q3AmmoName)
		{
			switch (q3AmmoName)
			{
				case "ammo_bfg":
					return "bfg_rockets";
				case "ammo_bullets": // Machine gun
					return "bullets";
				case "ammo_cells": // Plasma gun
					return "cells";
				case "ammo_grenades":
					return "grenades";
				case "ammo_lightning":
					return "lightning";
				case "ammo_rockets":
					return "rockets";
				case "ammo_shells": // Shotgun
					return "shells";
				case "ammo_slugs": // Railgun
					return "rails";
				default:
					return string.Empty;
			}
		}

		private void ConvertItem(Entity itemEnt)
		{
			itemEnt["resettime"] = GetItemRespawnTime(itemEnt);
			if (itemEnt.ClassName == "item_haste")
				itemEnt["hastetime"] = itemEnt["count"];
			else if (itemEnt.ClassName == "item_quad")
				itemEnt["damageboosttime"] = itemEnt["count"];

			itemEnt.ClassName = GetMomentumItemName(itemEnt.ClassName);
		}

		private string GetItemRespawnTime(Entity itemEnt)
		{
			if (itemEnt.TryGetValue("wait", out var wait))
				return wait;
			
			return "120";
		}

		private string GetMomentumItemName(string q3ItemName)
		{
			switch (q3ItemName)
			{
				case "item_haste":
					return "momentum_powerup_haste";
				case "item_quad":
					return "momentum_powerup_damage_boost";
				default:
					return string.Empty;
			}
		}

		private void ConvertAngles(Entity entity)
		{
			if (float.TryParse(entity["angle"], out var angle))
			{
				entity.Angles = new Vector3(0f, angle, 0f);
				entity.Remove("angle");
			}
		}

		private void SetTeleportOrigin(Entity teleDest)
		{
			var origin = teleDest.Origin;
			origin.Z -= 24; // misc_teleporter_dest entities are 24 units higher than they should be
			teleDest.Origin = origin;
		}

		private List<Entity> GetTargetEntities(Entity sourceEntity)
		{
			if (sourceEntity.TryGetValue("target", out var target) && entityDict.ContainsKey(target))
				return entityDict[target];

			return new List<Entity>();
		}
	}
}
