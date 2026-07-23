using LibBSP;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Globalization;

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
		private enum FuncDoorFlags
		{
			StartOpen = 1,
			Passable = 8,
			Toggle = 32,
			DamageOpens = 131072
		}

		[Flags]
		private enum FuncRotatingFlags
		{
			StartOn = 1,
			ReverseDirection = 2,
			// If enabled, the entity will spin at the X Axis.
			XAxis = 4,
			// If enabled, the entity will spin at the Y Axis.
			YAxis = 8,
			// If enabled, the entity will accelerate and decelerate from maximum speed based on the Friction property.
			AccDcc = 16,
			// With this enabled, the player will be hurt when coming into contact with the brush.
			FanPain = 32,
			NotSolid = 64,
			// Use ATTN_IDLE (60dB).
			SmallSoundRadius = 128,
			// Use ATTN_STATIC (~67dB).
			MediumSoundRadius = 256,
			// Use ATTN_NORM (~75dB). If the other two options are unset, this is used.
			LargeSoundRadius = 512,
			ClientSideAnimation = 1024,
		}

		[Flags]
		private enum Q3TriggerTeleportFlags
		{
			Spectator = 1,
			KeepSpeed = 2
		}

		[Flags]
		private enum Q3TriggerPushVelocityFlags
		{
			PLAYERDIR_XY = 1 << 0,
			ADD_XY = 1 << 1,
			PLAYERDIR_Z = 1 << 2,
			ADD_Z = 1 << 3,
			BIDIRECTIONAL_XY = 1 << 4,
			BIDIRECTIONAL_Z = 1 << 5,
			CLAMP_NEGATIVE_ADDS = 1 << 6
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

		[Flags]
		private enum TargetFragsFilterFlags
		{
			Remover = 1,
			Reset = 8,
			Match = 16
		}

		[Flags]
		private enum GamemodeFlags
		{
			Surf = 1 << 0,
			Bhop = 1 << 1,
			BhopHL = 1 << 2,
			ClimbMom = 1 << 3,
			ClimbKZT = 1 << 4,
			Climb16 = 1 << 5,
			RJ = 1 << 6,
			SJ = 1 << 7,
			Ahop = 1 << 8,
			Conc = 1 << 9,
			DefragCPM = 1 << 10,
			DefragVQ3 = 1 << 11,
			DefragVTG = 1 << 12,
			All = Surf | Bhop | BhopHL | ClimbMom | ClimbKZT | Climb16 | RJ | SJ | Ahop | Conc | DefragCPM | DefragVQ3 | DefragVTG
		}

		private Entities q3Entities;
		private Entities sourceEntities;
		private string skyName;
		private int minDamageToRespawnPlayer;
		private bool ignoreZones;
		private string offModeEntityFallback;
		private Dictionary<string, List<Entity>> entityDict = new Dictionary<string, List<Entity>>();
		private List<Entity> removeEntities = new List<Entity>(); // Entities to remove after conversion (ex: remove weapons after converting a trigger_multiple that references target_give). TODO: It might be better to convert entities by priority, such as trigger_multiples first so that target_give weapons can be ignored after
		private int currentCheckpointIndex = 2;
		private Lump<Model> q3Models;

		private const string MOMENTUM_START_ENTITY = "_momentum_player_start_";
		private const string MOMENTUM_MATH_COUNTER = "_momentum_math_counter_";
		private const int q3LipMod = 2; // Quake adds 2 units to button/door lip for some reason

		public EntityConverter(Lump<Model> q3Models, Entities q3Entities, Entities sourceEntities, string skyName, int minDamageToRespawnPlayer, bool ignoreZones, string offModeEntityFallback)
		{
			this.q3Entities = q3Entities;
			this.sourceEntities = sourceEntities;
			this.skyName = skyName;
			this.minDamageToRespawnPlayer = minDamageToRespawnPlayer;
			this.ignoreZones = ignoreZones;
			this.q3Models = q3Models;
			this.offModeEntityFallback = offModeEntityFallback;

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
			HandleGamemodeSpecificEntities();

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
					case "func_static":
						ConvertFuncStatic(entity);
						break;
					case "func_plat":
						ConvertFuncPlat(entity);
						break;
					// Ignore these entities since they have no use in Source engine
					case "target_speaker": // converting this entity without a trigger input currently does nothing, convert during trigger_multiple conversion instead for now
					case "target_startTimer":
					case "target_stopTimer":
					case "target_checkpoint":
					case "target_give":
					case "target_init":
					case "target_delay":
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

			foreach (var entity in sourceEntities)
				PrioritizeConnections(entity);

			foreach (var entity in removeEntities)
				sourceEntities.Remove(entity);
		}

		private static void PrioritizeConnections(Entity entity)
		{
			if (entity.connections.Count <= 1)
				return;

			if (!entity.connections.Any(x => x.target == "!player"))
				return;

			var priorityFirst = new List<Entity.EntityConnection>();
			var prioritySecond = new List<Entity.EntityConnection>();
			var priorityLast = new List<Entity.EntityConnection>();

			foreach (var connection in entity.connections)
			{
				var action = connection.action;
				var param = connection.param;
				var target = connection.target;

				if (target == "!player")
				{
					var isRemoveWeapon = string.Equals(action, "RemoveWeapon", StringComparison.OrdinalIgnoreCase);
					var isSetZero = action.StartsWith("Set", StringComparison.OrdinalIgnoreCase) && string.Equals(param, "0", StringComparison.OrdinalIgnoreCase);
					var isSetNonZero = action.StartsWith("Set", StringComparison.OrdinalIgnoreCase) && !string.Equals(param, "0", StringComparison.OrdinalIgnoreCase);

					if (isRemoveWeapon || isSetZero) // Remove old weapons/ammo/powerups first
						priorityFirst.Add(connection);
					else if (isSetNonZero) // Set new weapons/ammo/powerup values second
						prioritySecond.Add(connection);
					else
						priorityLast.Add(connection); // Other connections e.g. "AddCells" last to add on top of the initially "Set" outputs
				}
				else
					priorityLast.Add(connection);
			}

			if (priorityFirst.Count == 0 && prioritySecond.Count == 0)
				return;

			// Clear connections and add them back in order of importance
			entity.connections.Clear();
			entity.connections.AddRange(priorityLast);
			entity.connections.AddRange(prioritySecond);
			entity.connections.AddRange(priorityFirst);
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

			if (int.TryParse(funcRotating["spawnflags"], out var flags))
				funcRotating["spawnflags"] = (flags | (int)FuncRotatingFlags.StartOn).ToString(CultureInfo.InvariantCulture);
			else
				funcRotating["spawnflags"] = ((int)FuncRotatingFlags.StartOn).ToString(CultureInfo.InvariantCulture);

			funcRotating["maxspeed"] = speed.ToString(CultureInfo.InvariantCulture);
		}

		private void ConvertFuncStatic(Entity funcStatic)
		{
			funcStatic.ClassName = "func_brush";
		}

		private void ConvertFuncPlat(Entity entity)
		{
			var moveDistance = 0f;
			var brushThickness = GetBrushThickness(entity);

			if (float.TryParse(entity["height"], out var height))
				moveDistance = height + brushThickness;
			else if (float.TryParse(entity["lip"], out var lip))
				moveDistance = -(lip - q3LipMod - (brushThickness * 2));

			if (string.IsNullOrEmpty(entity.Name))
			{
				entity.Name = $"plat{entity.ModelNumber}";
				CreatePlatTrigger(entity);
			}
			entity.ClassName = "func_door";
			entity["lip"] = moveDistance.ToString(CultureInfo.InvariantCulture);
			entity["movedir"] = "-90 0 0";
			entity["spawnpos"] = "1";
			entity["spawnflags"] = "0";
			entity["wait"] = "-1";
		}

		private void CreatePlatTrigger(Entity entity)
		{
			var trigger = new Entity
			{
				ClassName = "trigger_multiple",
				Model = entity.Model,
				Spawnflags = 1,
				Origin = new Vector3(entity.Origin.X, entity.Origin.Y, entity.Origin.Z + 2)
			};
			trigger["parentname"] = entity.Name;
			sourceEntities.Add(trigger);

			AddPlatTriggerConnections(entity, trigger);
		}

		private void AddPlatTriggerConnections(Entity plat, Entity trigger)
		{
			var connection = new Entity.EntityConnection()
			{
				name = "onStartTouch",
				target = plat.Name,
				action = "close",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			trigger.connections.Add(connection);

			var connection2 = new Entity.EntityConnection()
			{
				name = "onFullyClosed",
				target = plat.Name,
				action = "open",
				param = null,
				delay = 3, // placeholder value TODO: replicate actual func_plat behaviour
				fireOnce = -1
			};
			plat.connections.Add(connection2);
		}

		private float GetBrushThickness(Entity entity)
		{
			var model = q3Models[entity.ModelNumber];

			return model.Maximums.Z - model.Minimums.Z;
		}

		private void ConvertFuncDoor(Entity door)
		{
			SetMoveDir(door);

			if (string.IsNullOrEmpty(door["wait"]))
				door["wait"] = "2";
			else if (door["wait"] == "-1") // A value of -1 in quake is instantly reset position, in source it is don't reset position.
				door["wait"] = "0.001"; // exactly 0 also behaves as don't reset in source, so the delay is as short as possible without being 0.

			if (string.IsNullOrEmpty(door["speed"]))
				door["speed"] = "400";
			else if (door["speed"] == "-1") // A value of -1 in quake is teleport to end position, in source it is don't move. Set speed as fast as possible in source.
				door["speed"] = "99999";

			if (!float.TryParse(door["lip"], out var lip))
				door["lip"] = "6";
			else
				door["lip"] = $"{lip - q3LipMod}";

			door["noise1"] = "movers/doors/dr1_strt.wav";
			door["noise2"] = "movers/doors/dr1_end.wav";

			var spawnflags = (FuncDoorFlags)door.Spawnflags;

			if (spawnflags.HasFlag(FuncDoorFlags.StartOpen))
			{
				door.Spawnflags &= ~(uint)FuncDoorFlags.StartOpen;
				FlipStartAndEndPositions(door); // start open is just broken and doesn't handle inputs properly, simpler to flip the start/end pos and the movedir
			}

			if (float.TryParse(door["health"], out var health) && health > 0)
				door.Spawnflags |= (int)FuncDoorFlags.DamageOpens;

			var target = GetTargetEntities(door).FirstOrDefault();
			if (target != null)
			{
				var input = "OnFullyOpen";
				ConvertEntityTargetsRecursive(door, door, input, 0, new HashSet<Entity>());
			}
		}

		private void FlipStartAndEndPositions(Entity door)
		{
			var angleString = door["movedir"].Split(new[] { ' ' });

			float pitch = 0f;
			float yaw = 0f;
			float.TryParse(angleString[0], CultureInfo.InvariantCulture, out pitch);
			float.TryParse(angleString[1], CultureInfo.InvariantCulture, out yaw);

			var dir = ConvertAnglesToVector(pitch, yaw);
			var absDir = new Vector3(Math.Abs(dir.X), Math.Abs(dir.Y), Math.Abs(dir.Z));

			var model = q3Models[door.ModelNumber];
			var size = model.Maximums - model.Minimums;
			var distance = Vector3.Dot(size, absDir) - (float.TryParse(door["lip"], out var lip) ? lip : 0);
			door.Origin += distance * dir;

			door["movedir"] = $"{pitch * -1} {yaw * -1} 0"; // invert movedir
		}

		private void ConvertFuncButton(Entity button)
		{
			SetMoveDir(button);
			SetButtonFlags(button);

			var delay = 0f;
			ConvertEntityTargetsRecursive(button, button, "OnIn", delay, new HashSet<Entity>());

			if (string.IsNullOrEmpty(button["speed"]))
				button["speed"] = "40";
			else if (button["speed"] == "-1") // A value of -1 in quake is teleport to end position, in source it is don't move. Set speed as fast as possible in source.
				button["speed"] = "99999";

			if (string.IsNullOrEmpty(button["wait"]))
				button["wait"] = "1";
			else if (button["wait"] == "-1") // A value of -1 in quake is instantly reset position, in source it is don't reset position.
				button["wait"] = "0.001"; // exactly 0 also behaves as don't reset in source, so the delay is as short as possible without being 0.

			if (!float.TryParse(button["lip"], out var lip))
				button["lip"] = "2";
			else
				button["lip"] = $"{lip - q3LipMod}";

			button["customsound"] = "movers/switches/butn2.wav";
			button["sounds"] = "-1";
		}

		private static void OpenDoorOnOutput(Entity entity, Entity door, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = door["targetname"],
				action = "Open",
				param = null,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void FireTargetSpeedOnOutput(Entity entity, Entity targetSpeed, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = targetSpeed["targetname"],
				action = "Fire",
				param = null,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);

			if (targetSpeed.ClassName != "player_speed")
				ConvertTargetSpeed(targetSpeed);
		}

		private static void SetButtonFlags(Entity button)
		{
			if (!float.TryParse(button["speed"], out var speed))
				speed = 40;

			var spawnflags = 0;

			if ((speed == -1 || speed >= 9999) && (button["wait"] == "-1")) // TODO: Add customization setting for the upper bounds potentially?
				spawnflags |= (int)FuncButtonFlags.DontMove;

			if (!float.TryParse(button["health"], out var health) || button["health"] == "0")
				spawnflags |= (int)FuncButtonFlags.TouchActivates;
			else
				spawnflags |= (int)FuncButtonFlags.DamageActivates;

			button["spawnflags"] = spawnflags.ToString(CultureInfo.InvariantCulture);
		}

		private static void SetMoveDir(Entity entity)
		{
			if (!float.TryParse(entity["angle"], out var angle))
			{
				if (!string.IsNullOrEmpty(entity["angles"]))
					entity["movedir"] = entity["angles"];
				else
					entity["movedir"] = "0 0 0";
			}
			else if (angle == -1) // UP
				entity["movedir"] = "-90 0 0";
			else if (angle == -2) // DOWN
				entity["movedir"] = "90 0 0";
			else
				entity["movedir"] = $"0 {angle} 0";

			entity.Remove("angle");
			entity.Remove("angles");
		}

		private void ConvertWorldspawn(Entity worldspawn)
		{
			if (!string.IsNullOrEmpty(skyName))
				worldspawn["skyname"] = skyName;
		}

		private void ConvertPlayerStart(Entity playerStart)
		{
			playerStart.ClassName = "info_player_start";
			playerStart.Name = MOMENTUM_START_ENTITY;

			var targets = GetTargetEntities(playerStart);
			if (targets.Any())
			{
				var logicAuto = new Entity();
				logicAuto.ClassName = "logic_auto";

				ConvertEntityTargetsRecursive(logicAuto, playerStart, "OnMapSpawn", 0, new HashSet<Entity>());

				sourceEntities.Add(logicAuto);
			}
		}

		private void ConvertTriggerHurt(Entity trigger)
		{
			if (int.TryParse(trigger["dmg"], out var damage))
			{
				// TODO: Remove this if we ever add health to mmdf
				if (damage >= minDamageToRespawnPlayer)
					trigger["damage"] = "200";

				trigger.Remove("dmg");
			}

			trigger["spawnflags"] = "1";
		}

		private void ConvertTriggerMultiple(Entity trigger)
		{
			var delay = 0f;
			ConvertEntityTargetsRecursive(trigger, trigger, "OnTrigger", delay, new HashSet<Entity>());

			trigger["spawnflags"] = "1";
		}

		private void ConvertEntityTargetsRecursive(Entity entity, Entity targetEntity, string output, float delay, HashSet<Entity> visited)
		{
			var targets = GetTargetEntities(targetEntity);
			foreach (var target in targets)
			{
				if (visited.Contains(target) || targetEntity == target)
					continue;

				var compatibleGamemode = CheckGamemodeCompatability(target, entity);
				if (!compatibleGamemode)
					continue;

				switch (target.ClassName)
				{
					case "target_startTimer":
						ConvertStartZoneTrigger(entity, targets);
						break;
					case "target_stopTimer":
						ConvertEndZoneTrigger(entity, targets);
						break;
					case "target_checkpoint":
						ConvertCheckpointTrigger(entity, currentCheckpointIndex, targets);
						currentCheckpointIndex++;
						break;
					case "target_delay":
						delay += ConvertTargetDelay(target);
						break;
					case "target_give":
						FireTargetGiveOnOutput(entity, target, output, delay);
						break;
					case "target_teleporter":
						FireTargetTeleporterOnOutput(entity, target, output, delay);
						break;
					case "target_kill":
						ConvertKillTrigger(entity);
						break;
					case "target_init":
						FireTargetInitOnOutput(entity, target, output, delay);
						break;
					case "target_speaker":
					case "ambient_generic":
						FireTargetSpeakerOnOutput(entity, target, output, delay);
						break;
					case "target_print":
					case "target_smallprint":
					case "game_text":
						FireTargetPrintOnOutput(entity, target, output, delay);
						break;
					case "target_speed":
					case "player_speed":
						FireTargetSpeedOnOutput(entity, target, output, delay);
						break;
					case "target_push":
						FireTargetPushOnOutput(entity, target, output, delay);
						break;
					case "target_remove_powerups":
						SetHasteOnOutput(entity, "0", output, delay);
						SetFlightOnOutput(entity, "0", output, delay);
						SetQuadOnOutput(entity, "0", output, delay);
						break;
					case "func_door":
						OpenDoorOnOutput(entity, target, output, delay);
						break;
					case "target_relay":
					case "logic_relay":
						FireTargetRelayOnOutput(entity, target, output, delay);
						break;
					case "target_fragsFilter":
						ConvertFragsFilter(entity, target, output, delay);
						break;
					case "target_score":
						ConvertTargetScore(entity, target, output, delay);
						break;
				}

				visited.Add(target);
				if (target.ClassName != "logic_relay" && target.ClassName != "func_door") // these entities move the next target's inputs to themselves and are handled elsewhere, break from the loop
					ConvertEntityTargetsRecursive(entity, target, output, delay, visited);
			}
		}

		private bool CheckGamemodeCompatability(Entity target, Entity entity)
		{
			var notcpm = target["notcpm"] == "1";
			var notvq3 = target["notvq3"] == "1";

			if (notcpm || notvq3)
			{
				var requiredGamemode = notcpm ? GamemodeFlags.DefragVQ3 : GamemodeFlags.DefragCPM;
				var entityGamemodes = int.TryParse(entity["gamemodes"], out var gamemodes) ? (GamemodeFlags)gamemodes : 0;

				if (!entityGamemodes.HasFlag(requiredGamemode))
					return false;
			}
			return true;
		}

		private void FireTargetTeleporterOnOutput(Entity entity, Entity targetTeleporter, string output, float delay)
		{
			var targets = GetTargetEntities(targetTeleporter);
			foreach (var target in targets)
			{
				var compatibleGamemode = CheckGamemodeCompatability(target, entity);
				if (!compatibleGamemode)
					continue;

				if (target.ClassName != "point_teleport")
				{
					if (target.ClassName != "info_teleport_destination") //if already a teleport_destination, origin has been fixed elsewhere
						SetTeleportOrigin(target);

					target.ClassName = "point_teleport";
					target["target"] = "!player";
					target["velocitymode"] = "3";
					target["setspeed"] = targetTeleporter.Spawnflags == 1 ? "0" : "400"; //spawnflag 1 is keep speed, else set speed to 400
					target.Spawnflags = 0;
					target["usedestinationangles"] = "1";
				}

				var connection = new Entity.EntityConnection()
				{
					name = output,
					target = target.Name,
					action = "Teleport",
					param = null,
					delay = delay,
					fireOnce = -1
				};
				entity.connections.Add(connection);
			}
		}

		private void ConvertTargetScore(Entity entity, Entity targetScore, string output, float delay)
		{
			if (!sourceEntities.Any(x => x.ClassName == "math_counter")) // Check if math_counter exists
				CreateMathCounter();

			if (!float.TryParse(targetScore["count"], out var count))
				count = 1;

			ModifyMathCounter(entity, output, "Add", count.ToString(CultureInfo.InvariantCulture), delay);
		}

		private void CreateLogicCase()
		{
			var maxFrags = GetHighestFrags(); // returns the highest frags value found on all target_fragsFilter entities
			var logicCasesNeeded = (int)Math.Ceiling((maxFrags + 1) / 16f); // each logic_case only supports 16 outputs so create enough logic_cases to cover all frag counts

			for (var i = 1; i <= logicCasesNeeded; i++)
			{
				var logicCase = new Entity();
				logicCase.ClassName = "logic_case";
				logicCase.Name = $"momentum_logic_case_{i}";

				var min = i * 16 - 15;
				var max = min + 15;

				for (var j = min; j <= max; j++)
				{
					var caseNum = $"case{j - min + 1:D2}";
					logicCase[caseNum] = (j-1).ToString(CultureInfo.InvariantCulture);
				}

				var connection = new Entity.EntityConnection()
				{
					name = "OnUsed",
					target = "*_mom_relay*",
					action = "Disable",
					param = null,
					delay = 0,
					fireOnce = -1
				};
				logicCase.connections.Add(connection);

				sourceEntities.Add(logicCase);
			}
		}

		private int GetHighestFrags()
		{
			var maxFrags = 0;

			foreach (var fragsFilter in q3Entities.FindAll(x => x.ClassName == "target_fragsFilter"))
			{
				if (fragsFilter.TryGetValue("frags", out var s) && int.TryParse(s, out var frags))
				{
					if (frags <= maxFrags)
						continue;
					maxFrags = frags;
				}
			}
			return maxFrags;
		}

		private void CreateMathCounter()
		{
			var counter = new Entity();
			counter.ClassName = "math_counter";
			counter.Name = MOMENTUM_MATH_COUNTER;
			counter["startvalue"] = "0";
			counter["min"] = "0";
			counter["max"] = "0";

			var connection = new Entity.EntityConnection()
			{
				name = "OutValue",
				target = "momentum_logic_case*",
				action = "InValue",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			counter.connections.Add(connection);

			sourceEntities.Add(counter);
		}

		private void ConvertFragsFilter(Entity entity, Entity targetFragsFilter, string output, float delay)
		{
			if (!int.TryParse(targetFragsFilter["frags"], out var frags))
				frags = 1; // Default number of frags is 1 if no value is specified

			targetFragsFilter["startdisabled"] = (frags > 0) ? "1" : "0"; // Players start with 0 frags, disable entities that require > 0

			targetFragsFilter.Name += $"_mom_relay{frags:D2}"; // Name needs a unique relay number for the logic_case to target

			var match = false;
			var spawnflags = (TargetFragsFilterFlags)targetFragsFilter.Spawnflags;

			if (spawnflags.HasFlag(TargetFragsFilterFlags.Reset)) // Reset frags to 0
				ModifyMathCounter(targetFragsFilter, "OnTrigger", "SetValue", "0", delay);
			else if (spawnflags.HasFlag(TargetFragsFilterFlags.Remover)) // Remove frags when used
				ModifyMathCounter(targetFragsFilter, "OnTrigger", "Subtract", frags.ToString(CultureInfo.InvariantCulture), delay);

			if (spawnflags.HasFlag(TargetFragsFilterFlags.Match))
				match = true;

			FireTargetRelayOnOutput(entity, targetFragsFilter, output, delay);

			AddLogicCaseOutput(targetFragsFilter.Name, frags, match);
		}

		private void FireTargetRelayOnOutput(Entity entity, Entity targetRelay, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = targetRelay.Name,
				action = "Trigger",
				param = null,
				delay = delay,
				fireOnce = -1
			};
			TryAddConnection(entity, connection);

			if (targetRelay.ClassName != "logic_relay")
			{
				targetRelay.ClassName = "logic_relay";
				targetRelay.Spawnflags = 2;

				ConvertEntityTargetsRecursive(targetRelay, targetRelay, "OnTrigger", 0, new HashSet<Entity>());
			}
		}

		private void AddLogicCaseOutput(string targetName, int frags, bool match)
		{
			if (!sourceEntities.Any(x => x.ClassName == "logic_case"))
				CreateLogicCase();

			var logicCaseList = sourceEntities.FindAll(x => x.ClassName == "logic_case");
			var caseEntityNum = 1; // may be more than 1 logic_case if there are more than 16 collectables

			foreach (var logicCase in logicCaseList)
			{
				var min = (caseEntityNum * 16) - 16;
				var max = match ? frags : 16 * caseEntityNum; // Either force frags to match case number on true, else allow any cases over the frag count to trigger

				for (var i = min; i <= max; i++)
				{
					if (i < frags)
						continue;

					var caseNum = $"case{(i - (16 * (caseEntityNum - 1))) + 1:D2}";

					var connection = new Entity.EntityConnection()
					{
						name = $"On{caseNum}",
						target = targetName,
						action = "Enable",
						param = null,
						delay = 0.008f,
						fireOnce = -1
					};

					TryAddConnection(logicCase, connection);
				}
				caseEntityNum++;
			}
		}

		private void ModifyMathCounter(Entity entity, string output, string input, string value, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = MOMENTUM_MATH_COUNTER,
				action = input,
				param = value,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private float ConvertTargetDelay(Entity targetDelay)
		{
			if (float.TryParse(targetDelay["delay"], out var delay))
				return delay;
			else if (float.TryParse(targetDelay["wait"], out var wait))
				return wait;
			else
				return 1;
		}

		private void FireTargetPushOnOutput(Entity entity, Entity targetPush, string output, float delay)
		{
			var launchVector = "0 0 0";
			var targetPosition = GetTargetEntities(targetPush).FirstOrDefault();

			if (targetPosition != null)
			{
				targetPosition.ClassName = "info_target";
				launchVector = GetLaunchVectorWithTarget(targetPush, targetPosition);
			}
			else
				launchVector = GetLaunchVector(targetPush);

			SetLocalVelocityOnOutput(entity, launchVector, output, delay);
		}

		private static void SetLocalVelocityOnOutput(Entity entity, string launchVector, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = "SetLocalVelocity",
				param = launchVector,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private static string GetLaunchVector(Entity targetPush)
		{
			var angles = "0 0 0";

			if (!string.IsNullOrEmpty(targetPush["angles"]))
				angles = targetPush["angles"];
			else if (float.TryParse(targetPush["angle"], out var angle))
				angles = $"0 {angle} 0";

			var angleString = angles.Split(' ');

			var pitchDegrees = float.Parse(angleString[0], CultureInfo.InvariantCulture);
			var yawDegrees = float.Parse(angleString[1], CultureInfo.InvariantCulture);

			var launchDir = ConvertAnglesToVector(pitchDegrees, yawDegrees);

			if (!float.TryParse(targetPush["speed"], out var speed))
				speed = 1000;
			else
				speed = float.Parse(targetPush["speed"], CultureInfo.InvariantCulture);

			var launchVector = launchDir * speed;
			return $"{launchVector.X} {launchVector.Y} {launchVector.Z}";
		}

		private string GetLaunchVectorWithTarget(Entity targetPush, Entity targetPosition)
		{
			var gravity = 800f;
			var height = targetPosition.Origin.Z - targetPush.Origin.Z;
			var time = Math.Sqrt(height / (.5 * gravity)); // Calculates how many seconds it takes to reach the apex of the launch

			var xDist = targetPosition.Origin.X - targetPush.Origin.X;
			var yDist = targetPosition.Origin.Y - targetPush.Origin.Y;

			var xSpeed = xDist / time;
			var ySpeed = yDist / time;
			var zSpeed = time * gravity;

			return $"{xSpeed} {ySpeed} {zSpeed}";
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
			targetSpeed.ClassName = "player_speed";

			if (!targetSpeed.TryGetValue("speed", out var speed))
				targetSpeed["speed"] = "100";
		}

		private void FireTargetPrintOnOutput(Entity entity, Entity targetPrint, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = targetPrint["targetname"],
				action = "Display",
				param = null,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);

			if (targetPrint.ClassName != "game_text")
				ConvertTargetPrint(targetPrint);
		}

		private void ConvertTargetPrint(Entity targetPrint)
		{
			var regex = new Regex("\\^[1-9]");
			targetPrint["message"] = regex.Replace(targetPrint["message"].Replace("\\n", "\n", StringComparison.InvariantCulture), ""); // Removes q3 colour codes from string and fixes broken newline character
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

		private void FireTargetSpeakerOnOutput(Entity entity, Entity targetSpeaker, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = targetSpeaker["targetname"],
				action = "PlaySound",
				param = null,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);

			if (targetSpeaker.ClassName != "ambient_generic")
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
			if (!noise.StartsWith(removeStr, StringComparison.OrdinalIgnoreCase))
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

			targetSpeaker["spawnflags"] = sourceflags.ToString(CultureInfo.InvariantCulture);
		}

		private void FireTargetInitOnOutput(Entity entity, Entity targetInit, string output, float delay)
		{
			var spawnflags = (TargetInitFlags)targetInit.Spawnflags;
			if (!spawnflags.HasFlag(TargetInitFlags.KeepPowerUps))
			{
				SetHasteOnOutput(entity, "0", output, delay);
				SetFlightOnOutput(entity, "0", output, delay);
				SetQuadOnOutput(entity, "0", output, delay);
			}
			if (!spawnflags.HasFlag(TargetInitFlags.KeepWeapons))
			{
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_grenadelauncher", output, delay);
				RemoveAmmoOnOutput(entity, "SetGrenades", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_rocketlauncher", output, delay);
				RemoveAmmoOnOutput(entity, "SetRockets", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_plasmagun", output, delay);
				RemoveAmmoOnOutput(entity, "SetCells", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_lightninggun", output, delay);
				RemoveAmmoOnOutput(entity, "SetLightning", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_railgun", output, delay);
				RemoveAmmoOnOutput(entity, "SetRails", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_bfg", output, delay);
				RemoveAmmoOnOutput(entity, "SetBfgRockets", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_shotgun", output, delay);
				RemoveAmmoOnOutput(entity, "SetShells", output, delay);
			}
			if (spawnflags.HasFlag(TargetInitFlags.RemoveMachineGun))
			{
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_machinegun", output, delay);
				RemoveAmmoOnOutput(entity, "SetBullets", output, delay);
			}
		}

		private static void RemoveWeaponOnOutput(Entity entity, string weaponName, string output, float delay)
		{
			if (entity.connections.Any(c => c.action == "GiveWeapon" && c.param == weaponName))
				return; // Don't remove weapon if it is also being given by the same entity

			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = "RemoveWeapon",
				param = weaponName,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void RemoveAmmoOnOutput(Entity entity, string ammoName, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = ammoName,
				param = "0",
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void ConvertKillTrigger(Entity trigger)
		{
			if (!trigger.ClassName.StartsWith("trigger", StringComparison.OrdinalIgnoreCase))
				return;

			trigger.ClassName = "trigger_hurt";
			trigger["damage"] = "200";
			trigger["spawnflags"] = "1";
		}

		private void ConvertStartZoneTrigger(Entity trigger, List<Entity> targets)
		{
			if (ignoreZones || !trigger.ClassName.StartsWith("trigger", StringComparison.OrdinalIgnoreCase))
				return;

			var newTrigger = new Entity();

			newTrigger.ClassName = "zone_timer_start";
			newTrigger.Model = trigger.Model;
			newTrigger["restart_destination"] = "_momentum_player_start_";
			newTrigger["checkpoints_required"] = "0";
			newTrigger["checkpoints_ordered"] = "0";
			newTrigger["safe_height"] = "-1"; // Full height

			sourceEntities.Add(newTrigger);

			if (!targets.Any(x => !string.Equals(x.ClassName, "target_startTimer", StringComparison.OrdinalIgnoreCase)))
				sourceEntities.Remove(trigger);
		}

		private void ConvertEndZoneTrigger(Entity trigger, List<Entity> targets)
		{
			if (ignoreZones || !trigger.ClassName.StartsWith("trigger", StringComparison.OrdinalIgnoreCase))
				return;

			var newTrigger = new Entity();

			newTrigger.ClassName = "zone_timer_end";
			newTrigger.Model = trigger.Model;

			sourceEntities.Add(newTrigger);

			if (!targets.Any(x => !string.Equals(x.ClassName, "target_stopTimer", StringComparison.OrdinalIgnoreCase)))
				sourceEntities.Remove(trigger);
		}

		private void ConvertCheckpointTrigger(Entity trigger, int checkpointNum, List<Entity> targets)
		{
			if (ignoreZones || !trigger.ClassName.StartsWith("trigger", StringComparison.OrdinalIgnoreCase))
				return;

			var newTrigger = new Entity();

			newTrigger.ClassName = "zone_timer_checkpoint";
			newTrigger.Model = trigger.Model;
			newTrigger["checkpoint_number"] = checkpointNum.ToString(CultureInfo.InvariantCulture);

			sourceEntities.Add(newTrigger);

			if (!targets.Any(x => !string.Equals(x.ClassName, "target_checkpoint", StringComparison.OrdinalIgnoreCase)))
				sourceEntities.Remove(trigger);
		}

		// TODO: Convert target_give for player spawn entities
		private void FireTargetGiveOnOutput(Entity entity, Entity targetGive, string output, float delay)
		{
			// TODO: Support more entities (health, armor, etc.)
			var targets = GetTargetEntities(targetGive);
			foreach (var target in targets)
			{
				var compatibleGamemode = CheckGamemodeCompatability(target, entity);
				if (!compatibleGamemode)
					continue;

				switch (target.ClassName)
				{
					case "item_haste":
						SetHasteOnOutput(entity, ConvertPowerupCount(target["count"]), output, delay);
						break;
					case "item_enviro": // TODO: Not supported yet
						break;
					case "item_flight":
						SetFlightOnOutput(entity, ConvertPowerupCount(target["count"]), output, delay);
						break;
					case "item_quad":
						SetQuadOnOutput(entity, ConvertPowerupCount(target["count"]), output, delay);
						break;
					default:
						if (target.ClassName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
							GiveWeaponOnOutput(entity, target, output, delay);
						else if (target.ClassName.StartsWith("ammo_", StringComparison.OrdinalIgnoreCase))
							GiveAmmoOnOutput(entity, target, output, delay);
						break;
				}

				removeEntities.Add(target);
			}
		}

		private void SetHasteOnOutput(Entity entity, string duration, string output, float delay)
		{
			SetPowerupOnOutput(entity, duration, output, delay, "SetHaste");
		}

		private void SetFlightOnOutput(Entity entity, string duration, string output, float delay)
		{
			SetPowerupOnOutput(entity, duration, output, delay, "SetFlight");
		}

		private void SetQuadOnOutput(Entity entity, string duration, string output, float delay)
		{
			SetPowerupOnOutput(entity, duration, output, delay, "SetDamageBoost");
		}

		private void SetPowerupOnOutput(Entity entity, string duration, string output, float delay, string action)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = action,
				param = duration,
				delay = delay,
				fireOnce = -1
			};

			entity.connections.Add(connection);
		}

		private void GiveWeaponOnOutput(Entity entity, Entity weaponEnt, string output, float delay)
		{
			var weaponName = GetMomentumWeaponName(weaponEnt.ClassName);
			if (string.IsNullOrEmpty(weaponName))
				return;

			// TODO: Support weapon count
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = "GiveWeapon",
				param = weaponName,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
			entity.connections.RemoveAll(c => c.action == "RemoveWeapon" && c.param == weaponName); // Remove any instances where the weapon being given is also being removed

			GiveWeaponAmmoOnOutput(entity, weaponEnt, output, delay);
		}

		private void GiveWeaponAmmoOnOutput(Entity entity, Entity weaponEnt, string output, float delay)
		{
			var count = ConvertWeaponAmmoCount(weaponEnt.ClassName, weaponEnt["count"]);
			if (float.Parse(count, CultureInfo.InvariantCulture) < 0)
				return;

			var ammoType = GetWeaponAmmoType(weaponEnt.ClassName);
			if (string.IsNullOrEmpty(ammoType))
				return;

			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = ammoType,
				param = count,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private string ConvertWeaponAmmoCount(string weaponName, string count)
		{
			if (!string.IsNullOrEmpty(count) && count != "0")
				return count;

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
				case "weapon_shotgun":
					return "10";
				default:
					return "-1";
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
				case "weapon_lightning":
					return "SetLightning";
				case "weapon_railgun":
					return "SetRails";
				case "weapon_bfg":
					return "SetBfgRockets";
				case "weapon_shotgun":
					return "SetShells";
				default:
					return string.Empty;
			}
		}

		private void GiveAmmoOnOutput(Entity entity, Entity ammoEnt, string output, float delay)
		{
			var ammoOutput = GetAmmoOutput(ammoEnt.ClassName);
			if (string.IsNullOrEmpty(ammoOutput))
				return;

			var count = ConvertAmmoCount(ammoEnt.ClassName, ammoEnt["count"]);
			if (float.Parse(count, CultureInfo.InvariantCulture) < 0)
				ammoOutput = ammoOutput.Replace("Add", "Set", StringComparison.OrdinalIgnoreCase); // Applies infinite ammo when count is set to a negative value to mimic q3 behaviour

			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = ammoOutput,
				param = count,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private string ConvertAmmoCount(string ammoName, string count)
		{
			if (!string.IsNullOrEmpty(count) && count != "0")
				return count;

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

		private string ConvertPowerupCount(string count)
		{
			if (float.TryParse(count, out var duration) && duration != 0 && duration < 99)
				return count;
			else if (duration >= 99)
				return "-1";
			else
				return "30";
		}

		private void ConvertTeleportTrigger(Entity trigger, Entity targetTele)
		{
			var target = GetTargetEntities(targetTele).FirstOrDefault();
			if (target != null)
			{
				trigger.ClassName = "trigger_teleport";
				trigger["target"] = target.Name;

				if (target.ClassName != "info_teleport_destination")
					ConvertTeleportDestination(target);
			}

			if (targetTele["spawnflags"] == "1")
			{
				trigger["velocitymode"] = "3";
				trigger["setspeed"] = "0";
			}
			else
			{
				trigger["velocitymode"] = "3";
				trigger["setspeed"] = "400";
			}
		}

		private void ConvertTriggerPush(Entity trigger)
		{
			var target = GetTargetEntities(trigger).FirstOrDefault();
			if (target != null)
			{
				target.ClassName = "info_target";
				ConvertTriggerJumppad(trigger, target.Name);
			}
		}

		private static void ConvertTriggerJumppad(Entity trigger, string target)
		{
			// TODO: Convert other trigger_push_velocity flags
			var spawnflags = (Q3TriggerPushVelocityFlags)trigger.Spawnflags;
			if (spawnflags.HasFlag(Q3TriggerPushVelocityFlags.ADD_XY))
				trigger["KeepHorizontalSpeed"] = "1";
			if (spawnflags.HasFlag(Q3TriggerPushVelocityFlags.ADD_Z))
				trigger["KeepVerticalSpeed"] = "1";

			trigger.ClassName = "trigger_jumppad";
			trigger["launchtarget"] = target;
			trigger["launchsound"] = "world/jumppad.wav";
			trigger["spawnflags"] = "1";
		}

		private void ConvertTriggerTeleport(Entity trigger)
		{
			var spawnflags = (Q3TriggerTeleportFlags)trigger.Spawnflags;

			if (spawnflags.HasFlag(Q3TriggerTeleportFlags.KeepSpeed))
			{
				trigger["velocitymode"] = "3";
				trigger["setspeed"] = "0";
			}
			else
			{
				if (spawnflags.HasFlag(Q3TriggerTeleportFlags.Spectator))
					return;

				trigger["velocitymode"] = "3";
				trigger["setspeed"] = "400";
			}
			trigger["spawnflags"] = "1";

			var targets = GetTargetEntities(trigger);
			foreach (var target in targets)
			{
				if (target.ClassName != "info_teleport_destination" && target.ClassName != "point_teleport")
					ConvertTeleportDestination(target);
			}
		}

		private void ConvertEquipment(Entity entity)
		{
			if (entity.ClassName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
				ConvertWeapon(entity);
			else if (entity.ClassName.StartsWith("ammo_", StringComparison.OrdinalIgnoreCase))
				ConvertAmmo(entity);
			else if (entity.ClassName.StartsWith("item_", StringComparison.OrdinalIgnoreCase))
				ConvertItem(entity);
		}

		private void ConvertWeapon(Entity weaponEnt)
		{
			var target = GetTargetEntities(weaponEnt).FirstOrDefault();
			if (target != null)
				ConvertEntityTargetsRecursive(weaponEnt, weaponEnt, "OnPickup", 0, new HashSet<Entity>());

			weaponEnt["resettime"] = GetWeaponRespawnTime(weaponEnt);
			weaponEnt["weaponname"] = GetMomentumWeaponName(weaponEnt.ClassName);
			weaponEnt["pickupammo"] = ConvertWeaponAmmoCount(weaponEnt.ClassName, weaponEnt["count"]);
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
					return "weapon_momentum_df_machinegun";
				case "weapon_gauntlet":
					return "weapon_momentum_df_knife";
				case "weapon_grenadelauncher":
					return "weapon_momentum_df_grenadelauncher";
				case "weapon_rocketlauncher":
					return "weapon_momentum_df_rocketlauncher";
				case "weapon_plasmagun":
					return "weapon_momentum_df_plasmagun";
				case "weapon_lightning":
					return "weapon_momentum_df_lightninggun";
				case "weapon_railgun":
					return "weapon_momentum_df_railgun";
				case "weapon_bfg":
					return "weapon_momentum_df_bfg";
				case "weapon_shotgun":
					return "weapon_momentum_df_shotgun";
				case "item_haste":
					return "momentum_powerup_haste";
				case "item_flight":
					return "momentum_powerup_flight";
				case "item_quad":
					return "momentum_powerup_damageboost";
				default:
					return string.Empty;
			}
		}

		private void ConvertAmmo(Entity ammoEnt)
		{
			var target = GetTargetEntities(ammoEnt).FirstOrDefault();
			if (target != null)
				ConvertEntityTargetsRecursive(ammoEnt, ammoEnt, "OnPickup", 0, new HashSet<Entity>());

			ammoEnt["resettime"] = ConvertAmmoRespawnTime(ammoEnt);
			ammoEnt["ammoname"] = GetMomentumAmmoName(ammoEnt.ClassName);
			ammoEnt["pickupammo"] = ConvertAmmoCount(ammoEnt.ClassName, ammoEnt["count"]);
			ammoEnt.ClassName = "momentum_pickup_ammo";
		}

		private string ConvertAmmoRespawnTime(Entity ammoEnt)
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
			var target = GetTargetEntities(itemEnt).FirstOrDefault();
			if (target != null)
			{
				ConvertEntityTargetsRecursive(itemEnt, itemEnt, "OnPickup", 0, new HashSet<Entity>());

				if (itemEnt.ClassName.StartsWith("item_armor", StringComparison.OrdinalIgnoreCase)
					|| itemEnt.ClassName.StartsWith("item_health", StringComparison.OrdinalIgnoreCase))
				{
					CreatePlaceHolderItem(itemEnt); // needs a placeholder pickup to trigger target entities since we dont have health/armor
					return;
				}
			}

			itemEnt.ClassName = GetMomentumItemName(itemEnt.ClassName);
			itemEnt["resettime"] = GetItemRespawnTime(itemEnt);

			switch (itemEnt.ClassName)
			{
				case "momentum_powerup_haste":
					itemEnt["hastetime"] = ConvertPowerupCount(itemEnt["count"]);
					break;
				case "momentum_powerup_flight":
					itemEnt["flighttime"] = ConvertPowerupCount(itemEnt["count"]);
					break;
				case "momentum_powerup_damageboost":
					itemEnt["damageboosttime"] = ConvertPowerupCount(itemEnt["count"]);
					break;
			}
		}

		private void CreatePlaceHolderItem(Entity itemEnt)
		{
			itemEnt.ClassName = "momentum_pickup_ammo";
			itemEnt["ammoname"] = "bullets";
			itemEnt["pickupammo"] = "0";
			itemEnt["resettime"] = GetItemRespawnTime(itemEnt);
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
				case "item_flight":
					return "momentum_powerup_flight";
				case "item_quad":
					return "momentum_powerup_damageboost";
				default:
					return q3ItemName; // Unsupported item, return original classname to avoid crash
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
			origin.Z -= 23; // Teleport destinations are 23 units too high once converted
			teleDest.Origin = origin;
		}

		private List<Entity> GetTargetEntities(Entity sourceEntity)
		{
			if (sourceEntity.TryGetValue("target", out var target) && entityDict.ContainsKey(target))
				return entityDict[target];

			return new List<Entity>();
		}

		private void HandleGamemodeSpecificEntities()
		{
			var gamemodEntities = new HashSet<Entity>();

			foreach (var entity in q3Entities)
			{
				if (entity.ClassName == "worldspawn")
					continue;

				if (entity["notcpm"] == "1" || entity["notvq3"] == "1")
				{
					var initialEntities = GetInitialEntitiesInChain(entity); // we can't disable specific outputs in a chain like in q3, need to find the initial entity firing outputs and filter that instead
					foreach (var initialEntity in initialEntities)
					{
						gamemodEntities.Add(initialEntity);
					}
				}
			}

			foreach (var entity in gamemodEntities)
			{
				if (entity["notcpm"] == "1")
					entity["gamemodes"] = GetGamemodeFlags("vq3");
				else if (entity["notvq3"] == "1")
					entity["gamemodes"] = GetGamemodeFlags("cpm");
				else
				{
					var newEntity = new Entity(); // split the entity into 2, have one entity handle cpm only outputs and the other vq3 only
					foreach (var kv in entity)
						newEntity[kv.Key] = kv.Value;
					newEntity["gamemodes"] = GetGamemodeFlags("vq3");
					entity["gamemodes"] = GetGamemodeFlags("cpm");
					q3Entities.Add(newEntity);
					entityDict[newEntity.Name].Add(newEntity);
				}
			}
		}

		private string GetGamemodeFlags(string gamemode)
		{
			var enabledGamemodeFlag = gamemode == "cpm" ? (uint)GamemodeFlags.DefragCPM : (uint)GamemodeFlags.DefragVQ3;
			var disabledGamemodeFlag = gamemode == "cpm" ? (uint)GamemodeFlags.DefragVQ3 : (uint)GamemodeFlags.DefragCPM;

			if (offModeEntityFallback == gamemode) // Enable all gamemodes except for the disabled one for offmode compatability
				return ((uint)GamemodeFlags.All - disabledGamemodeFlag).ToString(CultureInfo.InvariantCulture);	
			else
				return enabledGamemodeFlag.ToString(CultureInfo.InvariantCulture);
		}

		private HashSet<Entity> GetInitialEntitiesInChain(Entity startEntity)
		{
			var initialEntities = new HashSet<Entity>();
			var visited = new HashSet<Entity>();

			GetTargetingEntitiesRecursive(startEntity, visited, initialEntities);

			return initialEntities;
		}

		private void GetTargetingEntitiesRecursive(Entity currentEntity, HashSet<Entity> visited, HashSet<Entity> initialEntities)
		{
			if (visited.Contains(currentEntity))
				return;

			visited.Add(currentEntity);

			var targetingEntities = q3Entities.Where(x => x.TryGetValue("target", out var target) && target == currentEntity.Name).ToList();  // find all entities that target the current entity

			if (targetingEntities.Count == 0 || currentEntity.ClassName == "target_relay" || currentEntity.ClassName == "target_fragsFilter" || currentEntity.ClassName == "func_door") // these entities fire their own outputs, no need to step back further
			{
				initialEntities.Add(currentEntity);
			}
			else
			{
				foreach (var entity in targetingEntities)
				{
					GetTargetingEntitiesRecursive(entity, visited, initialEntities);
				}
			}
		}

		// Because gamemode specific entities are being split into 2, it's possible duplicate connections exist in some cases. Check for duplicates where needed.
		private void TryAddConnection(Entity entity, Entity.EntityConnection newConnection)
		{
			foreach (var existingConnection in entity.connections)
			{
				if (newConnection.ToString() == existingConnection.ToString())
					return;
			}
			entity.connections.Add(newConnection);
		}
	}
}
