using LibBSP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib.Source
{
	public class EntityConverter
	{
		private Entities q3Entities;
		private Entities sourceEntities;
		
		private Dictionary<string, Entity> entityDict = new Dictionary<string, Entity>();
		private int currentCheckpointIndex = 2;

		private const string MOMENTUM_START_ENTITY = "_momentum_player_start_";

		public EntityConverter(Entities q3Entities, Entities sourceEntities)
		{
			this.q3Entities = q3Entities;
			this.sourceEntities = sourceEntities;
			
			foreach (var entity in q3Entities)
			{
				if (!entityDict.ContainsKey(entity.Name))
					entityDict.Add(entity.Name, entity);
			}
		}

		public void Convert()
		{
			foreach (var entity in q3Entities)
			{
				var ignoreEntity = false;

				switch (entity.ClassName)
				{
					case "info_player_start":
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
					// Ignore Defrag timer entities
					case "target_startTimer":
					case "target_stopTimer":
					case "target_checkpoint":
						ignoreEntity = true;
						break;
				}

				if (!ignoreEntity)
					sourceEntities.Add(entity);
			}
		}

		private bool TryGetTargetEntity(Entity sourceEntity, out Entity targetEntity)
		{
			if (sourceEntity.TryGetValue("target", out var target))
			{
				if (entityDict.ContainsKey(target))
				{
					targetEntity = entityDict[target];
					return true;
				}
			}

			targetEntity = null;
			return false;
		}

		private void ConvertTriggerMultiple(Entity trigger)
		{
			if (!TryGetTargetEntity(trigger, out var target))
				return;

			switch (target.ClassName)
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
			}
		}

		private static void ConvertTimerTrigger(Entity trigger, string className, int zoneNumber)
		{
			trigger.ClassName = className;
			//entity["track_number"] = "0";
			trigger["zone_number"] = zoneNumber.ToString();
			//entity["spawnflags"] = "0";

			trigger.Remove("target");
		}

		private void ConvertTriggerPush(Entity trigger)
		{
			if (!TryGetTargetEntity(trigger, out var target))
				return;

			trigger.ClassName = "trigger_catapult";
			trigger["launchtarget"] = target.Name;
			trigger["spawnflags"] = "1";
			trigger["playerspeed"] = "450";

			trigger.Remove("target");
		}

		private static void ConvertTriggerTeleport(Entity? entity)
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
	}
}
