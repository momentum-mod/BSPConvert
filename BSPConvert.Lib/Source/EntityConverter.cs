using LibBSP;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BSPConvert.Lib
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

		[Flags]
		private enum TargetSpeakerFlags
		{
			LoopedOn = 1,
			LoopedOff = 2,
			Global = 4,
			Activator = 8
		}

		[Flags]
		private enum AmbientGenericFlags
		{
			InfiniteRange = 1,
			StartSilent = 16,
			IsNotLooped = 32
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
		private bool ignoreZones;
		private Dictionary<string, List<Entity>> entityDict = new Dictionary<string, List<Entity>>();
		private List<Entity> removeEntities = new List<Entity>(); // Entities to remove after conversion (ex: remove weapons after converting a trigger_multiple that references target_give). TODO: It might be better to convert entities by priority, such as trigger_multiples first so that target_give weapons can be ignored after
		private int currentCheckpointIndex = 2;
		private Lump<Model> q3Models;

		private const string MOMENTUM_START_ENTITY = "_momentum_player_start_";

		public EntityConverter(Lump<Model> q3Models, Entities q3Entities, Entities sourceEntities, Dictionary<string, Shader> shaderDict, int minDamageToConvertTrigger, bool ignoreZones)
		{
			this.q3Entities = q3Entities;
			this.sourceEntities = sourceEntities;
			this.shaderDict = shaderDict;
			this.minDamageToConvertTrigger = minDamageToConvertTrigger;
			this.ignoreZones = ignoreZones;
			this.q3Models = q3Models;

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
					case "trigger_push_velocity":
						ConvertTriggerPush(entity);
						break;
					case "trigger_teleport":
						ConvertTriggerTeleport(entity);
						break;
					case "misc_teleporter_dest":
						ConvertTeleportDestination(entity);
						break;
					case "func_door":
						ConvertFuncDoor(entity);
						break;
					case "func_button":
						ConvertFuncButton(entity);
						break;
					case "func_rotating":
						ConvertFuncRotating(entity);
						break;
					// Ignore these entities since they have no use in Source engine
					case "target_speaker": // converting this entity without a trigger input currently does nothing, convert during trigger_multiple conversion instead for now
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
			SetTeleportOrigin(entity);
			entity.ClassName = "info_teleport_destination";
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

		private void ConvertFuncRotating(Entity funcRotating)
		{
			if (!float.TryParse(funcRotating["speed"], out var speed))
				speed = 100;
			funcRotating["spawnflags"] = "1";
			funcRotating["maxspeed"] = speed.ToString();
		}

		private void ConvertFuncDoor(Entity entity)
		{
			SetMoveDir(entity);

			if (string.IsNullOrEmpty(entity["wait"]))
				entity["wait"] = "2";

			if (float.TryParse(entity["health"], out var health))
			{
				entity.ClassName = "func_button"; // Health is obsolete on func_door, maybe fix in engine and update this
				ConvertFuncButton(entity);
			}
		}

		private void ConvertFuncButton(Entity entity)
		{
			SetMoveDir(entity);
			SetButtonFlags(entity);
			ConvertButtonTargetsRecursive(entity, entity);

			if (entity["wait"] == "-1") // A value of -1 in quake is instantly reset position, in source it is don't reset position.
				entity["wait"] = "0.001"; // exactly 0 also behaves as don't reset in source, so the delay is as short as possible without being 0.
		}

		private void ConvertButtonTargetsRecursive(Entity button, Entity entity)
		{
			var targets = GetTargetEntities(entity);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "func_door":
						OpenDoorOnOutput(button, target, "OnPressed");
						break;
					case "target_speed":
						FireTargetSpeedOnOutput(button, target, "OnPressed");
						break;
					case "target_give":
						FireTargetGiveOnOutput(button, target, "OnPressed");
						break;
					case "target_init":
						FireTargetInitOnOutput(button, target, "OnPressed");
						break;
				}
				ConvertButtonTargetsRecursive(button, target);
			}
		}

		private static void OpenDoorOnOutput(Entity entity, Entity door, string output)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = door["targetname"],
				action = "Open",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void FireTargetSpeedOnOutput(Entity entity, Entity targetSpeed, string output)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = targetSpeed["targetname"],
				action = "Fire",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			entity.connections.Add(connection);

			ConvertTargetSpeed(targetSpeed);
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
			ConvertTriggerTargetsRecursive(trigger, trigger);

			trigger["spawnflags"] = "1";
		}

		private void ConvertTriggerTargetsRecursive(Entity trigger, Entity entity)
		{
			var targets = GetTargetEntities(entity);
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
						FireTargetGiveOnOutput(trigger, target, "OnStartTouch");
						break;
					case "target_teleporter":
						ConvertTeleportTrigger(trigger, target);
						break;
					case "target_kill":
						ConvertKillTrigger(trigger);
						break;
					case "target_init":
						FireTargetInitOnOutput(trigger, target, "OnStartTouch");
						break;
					case "target_speaker":
						ConvertTargetSpeakerTrigger(trigger, target);
						break;
					case "target_print":
					case "target_smallprint":
						ConvertTargetPrintTrigger(trigger, target);
						break;
					case "target_speed":
						FireTargetSpeedOnOutput(entity, target, "OnStartTouch");
						break;
					case "target_push":
						ConvertTargetPushTrigger(trigger, target);
						break;
					case "func_door":
						OpenDoorOnOutput(trigger, target, "OnStartTouch");
						break;
				}
				ConvertTriggerTargetsRecursive(trigger, target);
			}
		}

		private void ConvertTargetPushTrigger(Entity trigger, Entity targetPush)
		{
			var targets = GetTargetEntities(targetPush);
			var targetPosition = targets.FirstOrDefault();
			if (targetPosition != null)
			{
				targetPosition.Origin = CalculateTargetOrigin(trigger, targetPush, targetPosition);
				targetPosition.ClassName = "info_target";

				ConvertTriggerJumppad(trigger, targetPosition.Name);
			}
			else
				SetLocalVelocityTrigger(trigger, targetPush);
		}

		private static void SetLocalVelocityTrigger(Entity trigger, Entity targetPush)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "player",
				action = "SetLocalVelocity",
				param = GetLaunchVector(targetPush),
				delay = 0,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private Vector3 CalculateTargetOrigin(Entity trigger, Entity targetPush, Entity targetPosition)
		{
			var modelIndexStr = trigger["model"].Substring(1); // Removes * from model index
			if (!int.TryParse(modelIndexStr, out var modelIndex))
				return targetPosition.Origin;

			var model = q3Models[modelIndex];
			var center = (model.Minimums + model.Maximums) / 2f;

			var originDiff = targetPosition.Origin - targetPush.Origin;
			return center + originDiff;
		}

		private static string GetLaunchVector(Entity targetPush)
		{
			var angles = "0 0 0";

			if (!string.IsNullOrEmpty(targetPush["angles"]))
				angles = targetPush["angles"];
			else if (float.TryParse(targetPush["angle"], out var angle))
				angles = $"0 {angle} 0";

			var angleString = angles.Split(' ');

			var pitchDegrees = float.Parse(angleString[0]);
			var yawDegrees = float.Parse(angleString[1]);

			var launchDir = ConvertAnglesToVector(pitchDegrees, yawDegrees);

			if (!float.TryParse(targetPush["speed"], out var speed))
				speed = 1000;
			else
				speed = float.Parse(targetPush["speed"]);

			var launchVector = launchDir * speed;
			return $"{launchVector.X} {launchVector.Y} {launchVector.Z}";
		}

		private static Vector3 ConvertAnglesToVector(float pitchDegrees, float yawDegrees)
		{
			var yaw = Math.PI * yawDegrees / 180.0;
			var pitch = Math.PI * -pitchDegrees / 180.0;

			var x = Math.Cos(yaw) * Math.Cos(pitch);
			var y = Math.Sin(yaw) * Math.Cos(pitch);
			var z = Math.Sin(pitch);

			return new Vector3((float)x, (float)y, (float)z);
		}

		private void ConvertTargetSpeed(Entity targetSpeed)
		{
			if (targetSpeed["notcpm"] == "1") // TODO: Figure out how to handle gamemode specific entities more robustly
				return;

			targetSpeed.ClassName = "player_speed";

			if (!targetSpeed.TryGetValue("speed", out var speed))
				targetSpeed["speed"] = "100";
		}

		private void ConvertTargetPrintTrigger(Entity trigger, Entity targetPrint)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = targetPrint["targetname"],
				action = "Display",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
			
			ConvertTargetPrint(targetPrint);
		}

		private void ConvertTargetPrint(Entity targetPrint)
		{
			var regex = new Regex("\\^[1-9]");
			targetPrint["message"] = regex.Replace(targetPrint["message"].Replace("\\n", "\n"), ""); // Removes q3 colour codes from string and fixes broken newline character
			targetPrint.ClassName = "game_text";
			targetPrint["color"] = "255 255 255";
			targetPrint["color2"] = "255 255 255";
			targetPrint["effect"] = "0";
			targetPrint["fadein"] = "0.5";
			targetPrint["fadeout"] = "0.5";
			targetPrint["holdtime"] = "3";
			targetPrint["x"] = "-1";
			targetPrint["y"] = "0.2";
		}

		private void ConvertTargetSpeakerTrigger(Entity trigger, Entity targetSpeaker)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = targetSpeaker["targetname"],
				action = "PlaySound",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			trigger.connections.Add(connection);
			
			ConvertTargetSpeaker(targetSpeaker);
		}

		private void ConvertTargetSpeaker(Entity targetSpeaker)
		{
			var noise = targetSpeaker["noise"];
			noise = RemoveFirstOccurrence(noise, "sound/");

			targetSpeaker.ClassName = "ambient_generic";
			targetSpeaker["message"] = noise;
			targetSpeaker["health"] = "10"; // Volume
			targetSpeaker["radius"] = "1250";
			targetSpeaker["pitch"] = "100";

			SetAmbientGenericFlags(targetSpeaker);
		}

		private string RemoveFirstOccurrence(string noise, string removeStr)
		{
			if (!noise.StartsWith(removeStr))
				return noise;
			
			return noise.Remove(0, removeStr.Length);
		}

		private void SetAmbientGenericFlags(Entity targetSpeaker)
		{
			var q3flags = (TargetSpeakerFlags)targetSpeaker.Spawnflags;
			var sourceflags = 0;

			if (q3flags.HasFlag(TargetSpeakerFlags.LoopedOff))
				sourceflags |= (int)AmbientGenericFlags.StartSilent;
			else if (!q3flags.HasFlag(TargetSpeakerFlags.LoopedOn))
				sourceflags |= (int)AmbientGenericFlags.IsNotLooped;

			if (q3flags.HasFlag(TargetSpeakerFlags.Global) || q3flags.HasFlag(TargetSpeakerFlags.Activator))
				sourceflags |= (int)AmbientGenericFlags.InfiniteRange;

			targetSpeaker["spawnflags"] = sourceflags.ToString();
		}

		private void FireTargetInitOnOutput(Entity entity, Entity targetInit, string output)
		{
			var spawnflags = (TargetInitFlags)targetInit.Spawnflags;
			if (!spawnflags.HasFlag(TargetInitFlags.KeepPowerUps))
			{
				SetHasteOnOutput(entity, "0", output);
				SetQuadOnOutput(entity, "0", output);
			}
			if (!spawnflags.HasFlag(TargetInitFlags.KeepWeapons))
			{
				RemoveWeaponOnOutput(entity, (int)WeaponSlot.Gauntlet, output);
				RemoveWeaponOnOutput(entity, (int)WeaponSlot.GrenadeLauncher, output);
				RemoveWeaponOnOutput(entity, (int)WeaponSlot.RocketLauncher, output);
				RemoveWeaponOnOutput(entity, (int)WeaponSlot.PlasmaGun, output);
				RemoveWeaponOnOutput(entity, (int)WeaponSlot.BFG, output);
			}
			if (spawnflags.HasFlag(TargetInitFlags.RemoveMachineGun))
			{
				RemoveWeaponOnOutput(entity, (int)WeaponSlot.MachineGun, output);
			}
		}

		private static void RemoveWeaponOnOutput(Entity entity, int weaponIndex, string output)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!activator",
				action = "RemoveDFWeapon",
				param = weaponIndex.ToString(),
				delay = 0f,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void ConvertKillTrigger(Entity trigger)
		{
			trigger.ClassName = "trigger_teleport";
			trigger["target"] = MOMENTUM_START_ENTITY;
			trigger["mode"] = "1";
		}

		private void ConvertTimerTrigger(Entity trigger, string className, int zoneNumber)
		{
			if (ignoreZones)
				return;

			trigger.ClassName = className;
			//trigger["track_number"] = "0";
			trigger["zone_number"] = zoneNumber.ToString();
		}

		// TODO: Convert target_give for player spawn entities
		private void FireTargetGiveOnOutput(Entity entity, Entity targetGive, string output)
		{
			// TODO: Support more entities (health, armor, etc.)
			var targets = GetTargetEntities(targetGive);
			foreach (var target in targets)
			{
				switch (target.ClassName)
				{
					case "item_haste":
						GiveHasteOnOutput(entity, target["count"], output);
						break;
					case "item_enviro": // TODO: Not supported yet
						break;
					case "item_flight": // TODO: Not supported yet
						break;
					case "item_quad":
						GiveQuadOnOutput(entity, target["count"], output);
						break;
					default:
						if (target.ClassName.StartsWith("weapon_"))
							GiveWeaponOnOutput(entity, target, output);
						else if (target.ClassName.StartsWith("ammo_"))
							GiveAmmoOnStartTouch(entity, target, output);
						break;
				}

				removeEntities.Add(target);
			}
		}

		private void GiveHasteOnOutput(Entity entity, string duration, string output)
		{
			if (string.IsNullOrEmpty(duration) || duration == "0")
				duration = "30";

			SetHasteOnOutput(entity, duration, output);
		}

		private void SetHasteOnOutput(Entity entity, string duration, string output)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!activator",
				action = "SetHaste",
				param = duration,
				delay = 0f,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void GiveQuadOnOutput(Entity entity, string duration, string output)
		{
			if (string.IsNullOrEmpty(duration) || duration == "0")
				duration = "30";

			SetQuadOnOutput(entity, duration, output);
		}

		private static void SetQuadOnOutput(Entity entity, string duration, string output)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!activator",
				action = "SetDamageBoost",
				param = duration,
				delay = 0f,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void GiveWeaponOnOutput(Entity entity, Entity weaponEnt, string output)
		{
			var weaponIndex = GetWeaponIndex(weaponEnt.ClassName);
			if (weaponIndex == -1)
				return;

			// TODO: Support weapon count
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!activator",
				action = "GiveDFWeapon",
				param = weaponIndex.ToString(),
				delay = 0.01f, //hack to make sure that the weapon removal applies before weapon give
				fireOnce = -1
			};
			entity.connections.Add(connection);

			GiveWeaponAmmoOnOutput(entity, weaponEnt, output);
		}

		private void GiveWeaponAmmoOnOutput(Entity entity, Entity weaponEnt, string output)
		{
			if (!weaponEnt.TryGetValue("count", out var count) || count == "0") // Every quake weapon has a default ammo count when none is specified
				count = GetDefaultWeaponAmmoCount(weaponEnt.ClassName);

			if (count == "-1")
				return;

			var ammoType = GetWeaponAmmoType(weaponEnt.ClassName);
			if (string.IsNullOrEmpty(ammoType))
				return;

			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!activator",
				action = ammoType,
				param = count,
				delay = 0f,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private string GetDefaultWeaponAmmoCount(string weaponName)
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

		private void GiveAmmoOnStartTouch(Entity trigger, Entity ammoEnt, string output)
		{
			if (ammoEnt["notcpm"] == "1") // TODO: Figure out how to handle gamemode specific entities more robustly
				return;

			var ammoOutput = GetAmmoOutput(ammoEnt.ClassName);
			if (string.IsNullOrEmpty(ammoOutput))
				return;
			
			if (!ammoEnt.TryGetValue("count", out var count) || count == "0")
				count = GetDefaultAmmoCount(ammoEnt.ClassName);

			if (float.Parse(count) < 0)
				ammoOutput = ammoOutput.Replace("Add", "Set"); // Applies infinite ammo when count is set to a negative value to mimic q3 behaviour

			var connection = new Entity.EntityConnection()
			{
				name = "OnStartTouch",
				target = "!activator",
				action = ammoOutput,
				param = count,
				delay = 0.01f, //hack to make giving ammo happen after setting ammo
				fireOnce = -1
			};
			trigger.connections.Add(connection);
		}

		private string GetDefaultAmmoCount(string ammoName)
		{
			switch (ammoName)
			{
				case "ammo_bfg":
					return "15";
				case "ammo_bullets": // Machine gun
					return "50";
				case "ammo_cells": // Plasma gun
					return "30";
				case "ammo_grenades":
					return "5";
				case "ammo_lightning":
					return "60";
				case "ammo_rockets":
					return "5";
				case "ammo_shells": // Shotgun
					return "10";
				case "ammo_slugs": // Railgun
					return "10";
				default:
					return "0";
			}
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
			var firstTarget = targets.FirstOrDefault();
			if (firstTarget != null)
			{
				firstTarget.ClassName = "info_target";
				ConvertTriggerJumppad(trigger, firstTarget.Name);
			}
		}

		private static void ConvertTriggerJumppad(Entity trigger, string target)
		{
			trigger.ClassName = "trigger_jumppad";
			trigger["launchtarget"] = target;
			trigger["launchsound"] = "world/jumppad.wav";
			trigger["spawnflags"] = "1";
		}

		private void ConvertTriggerTeleport(Entity trigger)
		{
			var spawnflags = (Q3TriggerTeleportFlags)trigger.Spawnflags;

			if (spawnflags.HasFlag(Q3TriggerTeleportFlags.KeepSpeed))
				trigger["mode"] = "3";
			else
			{
				if (spawnflags.HasFlag(Q3TriggerTeleportFlags.Spectator))
					return;

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
			if (!weaponEnt.TryGetValue("count", out var count) || count == "0") // Every quake weapon has a default ammo count when none is specified
				count = GetDefaultWeaponAmmoCount(weaponEnt.ClassName);
			weaponEnt["pickupammo"] = count;
			weaponEnt.ClassName = "momentum_weapon_spawner";
		}

		private string GetWeaponRespawnTime(Entity weaponEnt)
		{
			if (weaponEnt.TryGetValue("wait", out var wait) && wait != "0")
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
			if (ammoEnt.TryGetValue("wait", out var wait) && wait != "0")
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
			if (!itemEnt.TryGetValue("count", out var count) || count == "0")
				count = "30";
			if (itemEnt.ClassName == "item_haste")
				itemEnt["hastetime"] = count;
			else if (itemEnt.ClassName == "item_quad")
				itemEnt["damageboosttime"] = count;

			itemEnt.ClassName = GetMomentumItemName(itemEnt.ClassName);
		}

		private string GetItemRespawnTime(Entity itemEnt)
		{
			if (itemEnt.TryGetValue("wait", out var wait) && wait != "0")
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
			var offset = 0;
			if (teleDest.ClassName == "misc_teleporter_dest")
				offset = 24;
			else if (teleDest.ClassName == "target_position")
				offset = 8;

			var origin = teleDest.Origin;
			origin.Z -= offset;
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
