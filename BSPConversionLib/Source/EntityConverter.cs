using LibBSP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class EntityConverter
	{
		private Entities q3Entities;
		private Entities sourceEntities;
		private Dictionary<string, Shader> shaderDict;
		
		private Dictionary<string, List<Entity>> entityDict = new Dictionary<string, List<Entity>>();
		private List<Entity> removeEntities = new List<Entity>(); // Entities to remove after conversion (ex: remove weapons after converting a trigger_multiple that references target_give). TODO: It might be better to convert entities by priority, such as trigger_multiples first so that target_give weapons can be ignored after
		private int currentCheckpointIndex = 2;

		private const string MOMENTUM_START_ENTITY = "_momentum_player_start_";

		public EntityConverter(Entities q3Entities, Entities sourceEntities, Dictionary<string, Shader> shaderDict)
		{
			this.q3Entities = q3Entities;
			this.sourceEntities = sourceEntities;
			this.shaderDict = shaderDict;
			
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
			foreach (var entity in q3Entities)
			{
				var ignoreEntity = false;

				switch (entity.ClassName)
				{
					case "worldspawn":
						ConvertWorldspawn(entity);
						break;
					case "info_player_start":
						entity.Name = MOMENTUM_START_ENTITY;
						break;
					case "info_player_deathmatch":
						entity.ClassName = "info_player_start";
						entity.Name = MOMENTUM_START_ENTITY;
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
						entity.ClassName = "info_teleport_destination";
						break;
					case "target_position":
						entity.ClassName = "info_target";
						break;
					case "weapon_machinegun":
						ConvertWeapon(entity, 2);
						break;
					case "weapon_gauntlet":
						ConvertWeapon(entity, 3);
						break;
					case "weapon_grenadelauncher":
						ConvertWeapon(entity, 4);
						break;
					case "weapon_rocketlauncher":
						ConvertWeapon(entity, 5);
						break;
					case "weapon_plasmagun":
						ConvertWeapon(entity, 8);
						break;
					case "weapon_bfg":
						ConvertWeapon(entity, 9);
						break;
					// Ignore these entities since they have no use in Source engine
					case "target_startTimer":
					case "target_stopTimer":
					case "target_checkpoint":
					case "target_give":
						ignoreEntity = true;
						break;
				}

				if (!ignoreEntity)
					sourceEntities.Add(entity);
			}

			foreach (var entity in removeEntities)
				sourceEntities.Remove(entity);
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

		private bool TryGetTargetEntities(Entity sourceEntity, out List<Entity> targetEntities)
		{
			if (sourceEntity.TryGetValue("target", out var target))
			{
				if (entityDict.ContainsKey(target))
				{
					targetEntities = entityDict[target];
					return true;
				}
			}

			targetEntities = new List<Entity>();
			return false;
		}

		private void ConvertTriggerMultiple(Entity trigger)
		{
			if (!TryGetTargetEntities(trigger, out var targetEnts))
				return;

			var targetEnt = targetEnts.First();
			switch (targetEnt.ClassName)
			{
				case "target_startTimer":
					ConvertTimerTrigger(trigger, "trigger_momentum_timer_start", 1);
					trigger["teleport_destination"] = MOMENTUM_START_ENTITY;
					//entity["start_on_jump"] = "0";
					//entity["speed_limit"] = "999999";
					break;
				case "target_stopTimer":
					ConvertTimerTrigger(trigger, "trigger_momentum_timer_stop", 0);
					break;
				case "target_checkpoint":
					ConvertTimerTrigger(trigger, "trigger_momentum_timer_checkpoint", currentCheckpointIndex);
					currentCheckpointIndex++;
					break;
				case "target_give":
					ConvertGiveTrigger(trigger, targetEnt);
					break;
			}
		}

		private static void ConvertTimerTrigger(Entity trigger, string className, int zoneNumber)
		{
			trigger.ClassName = className;
			//trigger["track_number"] = "0";
			trigger["zone_number"] = zoneNumber.ToString();
			trigger["spawnflags"] = "1";

			trigger.Remove("target");
		}

		private void ConvertTriggerPush(Entity trigger)
		{
			if (!TryGetTargetEntities(trigger, out var targetEnts))
				return;

			trigger.ClassName = "trigger_catapult";
			trigger["launchtarget"] = targetEnts.First().Name;
			trigger["spawnflags"] = "1";
			trigger["playerspeed"] = "450";

			trigger.Remove("target");
		}

		// TODO: Convert target_give for player spawn entities
		private void ConvertGiveTrigger(Entity trigger, Entity targetGive)
		{
			if (!TryGetTargetEntities(targetGive, out var targetEnts))
				return;
			
			// TODO: Support more entities (ammo, health, armor, etc.)
			foreach (var target in targetEnts)
			{
				switch (target.ClassName)
				{
					case "item_haste":
						GiveHasteOnStartTouch(trigger);
						break;
					case "item_enviro": // TODO: Not supported yet
						break;
					case "item_flight": // TODO: Not supported yet
						break;
					case "item_quad": // TODO: Not supported yet
						break;
					default:
						if (target.ClassName.StartsWith("weapon_"))
							GiveWeaponOnStartTouch(trigger, target);
						break;
				}

				removeEntities.Add(target);
			}
			trigger["spawnflags"] = "1";

			trigger.Remove("target");
		}

		private void GiveHasteOnStartTouch(Entity trigger)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = "SetHaste",
				param = "30", // TODO: Figure out how to get buff duration
				delay = 0f,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private void GiveWeaponOnStartTouch(Entity trigger, Entity target)
		{
			var weaponIndex = GetWeaponIndex(target.ClassName);
			if (weaponIndex == -1)
				return;
			
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = "GiveDFWeapon",
				param = weaponIndex.ToString(),
				delay = 0f,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private static void ConvertTriggerTeleport(Entity entity)
		{
			entity["spawnflags"] = "1";
			entity["mode"] = "5";
			entity["setspeed"] = "400";
		}

		private void ConvertWeapon(Entity entity, int weaponSlot)
		{
			entity.ClassName = "momentum_df_weaponspawner";
			entity["weapon_slot"] = weaponSlot.ToString();
		}

		private int GetWeaponIndex(string weaponName)
		{
			switch (weaponName)
			{
				case "weapon_machinegun":
					return 2;
				case "weapon_gauntlet":
					return 3;
				case "weapon_grenadelauncher":
					return 4;
				case "weapon_rocketlauncher":
					return 5;
				case "weapon_plasmagun":
					return 8;
				case "weapon_bfg":
					return 9;
				default:
					return -1;
			}
		}
	}
}
