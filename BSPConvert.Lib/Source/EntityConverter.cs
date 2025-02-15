namespace BSPConvert.Lib;
using LibBSP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

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

		private readonly Entities q3Entities;
		private readonly Entities sourceEntities;
		private readonly Dictionary<string, Shader> shaderDict;
		private readonly int minDamageToConvertTrigger;
		private readonly bool ignoreZones;
		private readonly Dictionary<string, List<Entity>> entityDict = [];
		private readonly List<Entity> removeEntities = []; // Entities to remove after conversion (ex: remove weapons after converting a trigger_multiple that references target_give). TODO: It might be better to convert entities by priority, such as trigger_multiples first so that target_give weapons can be ignored after
		private int currentCheckpointIndex = 2;
		private readonly Lump<Model> q3Models;

		private const string MOMENTUM_START_ENTITY = "_momentum_player_start_";
		private const string MOMENTUM_MATH_COUNTER = "_momentum_math_counter_";
		private const string MOMENTUM_LOGIC_CASE = "_momentum_logic_case_";

		public EntityConverter(Lump<Model> q3Models, Entities q3Entities, Entities sourceEntities, Dictionary<string, Shader> shaderDict, int minDamageToConvertTrigger, bool ignoreZones)
		{
			this.q3Entities = q3Entities;
			this.sourceEntities = sourceEntities;
			this.shaderDict = shaderDict;
			this.minDamageToConvertTrigger = minDamageToConvertTrigger;
			this.ignoreZones = ignoreZones;
			this.q3Models = q3Models;

			foreach (Entity? entity in q3Entities)
			{
				if (!entityDict.ContainsKey(entity.Name))
					entityDict.Add(entity.Name, [entity]);
				else
					entityDict[entity.Name].Add(entity);
			}
		}

		public void Convert()
		{
        HashSet<string> giveTargets = GetGiveTargets();

			foreach (Entity? entity in q3Entities)
			{
            bool ignoreEntity = false;

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

			foreach (Entity entity in removeEntities)
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
			foreach (Entity? entity in q3Entities)
			{
				if (entity.ClassName == "target_give" && entity.TryGetValue("target", out string? target))
					targets.Add(target);
			}

			return targets;
		}

		private void ConvertFuncRotating(Entity funcRotating)
		{
			if (!float.TryParse(funcRotating["speed"], out float speed))
				speed = 100;
			
			funcRotating["spawnflags"] = "1";
			funcRotating["maxspeed"] = speed.ToString();
		}

		private void ConvertFuncStatic(Entity funcStatic)
		{
			if (funcStatic["notcpm"] == "1") // TODO: Figure out how to handle gamemode specific entities more robustly
				return;

			funcStatic.ClassName = "func_brush";
		}

		private void ConvertFuncPlat(Entity entity)
		{
			const int q3LipMod = 2; // Quake adds 2 units to lip for some reason
        float moveDistance = 0f;
        float brushThickness = GetBrushThickness(entity);

			if (float.TryParse(entity["height"], out float height))
				moveDistance = height + brushThickness;
			else if (float.TryParse(entity["lip"], out float lip))
				moveDistance = -(lip - q3LipMod - (brushThickness * 2));

			if (string.IsNullOrEmpty(entity.Name))
			{
				entity.Name = $"plat{entity.ModelNumber}";
				CreatePlatTrigger(entity);
			}
			entity.ClassName = "func_door";
			entity["lip"] = moveDistance.ToString();
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
        Model model = q3Models[entity.ModelNumber];

			return model.Maximums.Z - model.Minimums.Z;
		}

		private void ConvertFuncDoor(Entity door)
		{
			SetMoveDir(door);

			if (string.IsNullOrEmpty(door["wait"]))
				door["wait"] = "2";
			
			if (float.TryParse(door["health"], out _))
			{
				door.ClassName = "func_button"; // Health is obsolete on func_door, maybe fix in engine and update this
				ConvertFuncButton(door);
			}
		}

		private void ConvertFuncButton(Entity button)
		{
			SetMoveDir(button);
			SetButtonFlags(button);

        float delay = 0f;
			ConvertEntityTargetsRecursive(button, button, "OnPressed", delay, []);

			if (button["wait"] == "-1") // A value of -1 in quake is instantly reset position, in source it is don't reset position.
				button["wait"] = "0.001"; // exactly 0 also behaves as don't reset in source, so the delay is as short as possible without being 0.

			button["customsound"] = "movers/switches/butn2.wav";
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

			ConvertTargetSpeed(targetSpeed);
		}

		private static void SetButtonFlags(Entity button)
		{
			if (!float.TryParse(button["speed"], out float speed))
				speed = 40;

        int spawnflags = 0;

			if ((speed == -1 || speed >= 9999) && (button["wait"] == "-1")) // TODO: Add customization setting for the upper bounds potentially?
				spawnflags |= (int)FuncButtonFlags.DontMove;

			if (!float.TryParse(button["health"], out float health) || button["health"] == "0")
				spawnflags |= (int)FuncButtonFlags.TouchActivates;
			else
				spawnflags |= (int)FuncButtonFlags.DamageActivates;

			button["spawnflags"] = spawnflags.ToString();
		}

		private static void SetMoveDir(Entity entity)
		{
			if (!float.TryParse(entity["angle"], out float angle))
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
			foreach (Shader shader in shaderDict.Values)
			{
				if (shader.skyParms != null)
				{
                string skyName = shader.skyParms.outerBox;
					if (!string.IsNullOrEmpty(skyName))
						worldspawn["skyname"] = skyName;
				}
			}
		}

		private void ConvertPlayerStart(Entity playerStart)
		{
			playerStart.ClassName = "info_player_start";
			playerStart.Name = MOMENTUM_START_ENTITY;

        List<Entity> targets = GetTargetEntities(playerStart);
			if (targets.Any())
			{
            var logicAuto = new Entity
            {
                ClassName = "logic_auto"
            };

            ConvertEntityTargetsRecursive(logicAuto, playerStart, "OnMapSpawn", 0, []);

				sourceEntities.Add(logicAuto);
			}
		}

		private void ConvertTriggerHurt(Entity trigger)
		{
			if (int.TryParse(trigger["dmg"], out int damage))
			{
				if (damage >= minDamageToConvertTrigger)
				{
					trigger.ClassName = "trigger_teleport";
					trigger["target"] = MOMENTUM_START_ENTITY;
					trigger["spawnflags"] = "1";
					trigger["velocitymode"] = "1";
				}
			}
		}

		private void ConvertTriggerMultiple(Entity trigger)
		{
        float delay = 0f;
			ConvertEntityTargetsRecursive(trigger, trigger, "OnTrigger", delay, []);

			trigger["spawnflags"] = "1";
		}

		private void ConvertEntityTargetsRecursive(Entity entity, Entity targetEntity, string output, float delay, HashSet<Entity> visited)
		{
        List<Entity> targets = GetTargetEntities(targetEntity);
			foreach (Entity target in targets)
			{
				if (visited.Contains(target) || targetEntity == target)
					continue;

				switch (target.ClassName)
				{
					case "target_stopTimer":
						ConvertTimerTrigger(entity, "trigger_momentum_timer_stop", 0);
						break;
					case "target_checkpoint":
						ConvertTimerTrigger(entity, "trigger_momentum_timer_checkpoint", currentCheckpointIndex);
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
						FireTargetSpeedOnOutput(entity, target, output, delay);
						break;
					case "target_push":
						FireTargetPushOnOutput(entity, target, output, delay);
						break;
					case "target_remove_powerups":
						SetHasteOnOutput(entity, "0", output, delay);
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
				if (target.ClassName != "logic_relay") // logic_relay moves the next target's inputs to a separate entity instead, break from the loop
					ConvertEntityTargetsRecursive(entity, target, output, delay, visited);
			}
		}

		private void FireTargetTeleporterOnOutput(Entity entity, Entity targetTeleporter, string output, float delay)
		{
        List<Entity> targets = GetTargetEntities(targetTeleporter);
			foreach (Entity target in targets)
			{
				target.ClassName = "point_teleport";
				target["target"] = "!player";

				// TODO: Implement teleport velocity modes, fix teleport origin

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

			if (!float.TryParse(targetScore["count"], out float count))
				count = 1;

			ModifyMathCounter(entity, output, "Add", count.ToString(), delay);
		}

		private Entity CreateLogicCase()
		{
        var logicCase = new Entity
        {
            ClassName = "logic_case",
            Name = MOMENTUM_LOGIC_CASE
        };

        for (int i = 1; i <= 16; i++) // Logic_case supports 16 different outputs
			{
            string caseNum = $"case{i:D2}";
				logicCase[caseNum] = (i-1).ToString(); // case01 = 0, case02 = 1 etc
			}

			var connection = new Entity.EntityConnection()
			{
				name = "OnUsed",
				target = "*_mom_relay*", // Disable all logic_relays
				action = "Disable",
				param = null,
				delay = 0,
				fireOnce = -1
			};
			logicCase.connections.Add(connection);

			sourceEntities.Add(logicCase);

			return logicCase;
		}

		private void CreateMathCounter()
		{
        var counter = new Entity
        {
            ClassName = "math_counter",
            Name = MOMENTUM_MATH_COUNTER
        };
        counter["startvalue"] = "0";
			counter["min"] = "0";
			counter["max"] = "16";

			var connection = new Entity.EntityConnection()
			{
				name = "OutValue",
				target = MOMENTUM_LOGIC_CASE,
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
			if (!int.TryParse(targetFragsFilter["frags"], out int frags))
				frags = 1; // Default number of frags is 1 if no value is specified

			targetFragsFilter["startdisabled"] = (frags > 0) ? "1" : "0"; // Players start with 0 frags, disable entities that require > 0

			targetFragsFilter.Name += $"_mom_relay{frags:D2}"; // Name needs a unique relay number for the logic_case to target

        bool match = false;
			var spawnflags = (TargetFragsFilterFlags)targetFragsFilter.Spawnflags;

			if (spawnflags.HasFlag(TargetFragsFilterFlags.Reset)) // Reset frags to 0
				ModifyMathCounter(targetFragsFilter, "OnTrigger", "SetValue", "0", delay);
			else if (spawnflags.HasFlag(TargetFragsFilterFlags.Remover)) // Remove frags when used
				ModifyMathCounter(targetFragsFilter, "OnTrigger", "Subtract", frags.ToString(), delay);

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
			entity.connections.Add(connection);

			if (targetRelay.ClassName != "logic_relay")
			{
				targetRelay.ClassName = "logic_relay";
				targetRelay.Spawnflags = 2;

				ConvertEntityTargetsRecursive(targetRelay, targetRelay, "OnTrigger", 0, []);
			}
		}

		private void AddLogicCaseOutput(string targetName, int frags, bool match)
		{
        Entity logicCase = sourceEntities.Find(x => x.ClassName == "logic_case") ?? CreateLogicCase();

        int min = frags;
        int max = match ? frags : 16; // Either force frags to match case number on true, else allow any cases over the frag count to trigger

			for (int i = min; i <= max; i++)
			{
            string caseNum = $"case{i+1:D2}";

				var connection = new Entity.EntityConnection()
				{
					name = $"On{caseNum}",
					target = targetName,
					action = "Enable",
					param = null,
					delay = 0.008f,
					fireOnce = -1
				};
				logicCase.connections.Add(connection);
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
			if (float.TryParse(targetDelay["delay"], out float delay))
				return delay;
			else if (float.TryParse(targetDelay["wait"], out float wait))
				return wait;
			else
				return 1;
		}

		private void FireTargetPushOnOutput(Entity entity, Entity targetPush, string output, float delay)
		{
        string launchVector = "0 0 0";
        Entity? targetPosition = GetTargetEntities(targetPush).FirstOrDefault();

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
        string angles = "0 0 0";

			if (!string.IsNullOrEmpty(targetPush["angles"]))
				angles = targetPush["angles"];
			else if (float.TryParse(targetPush["angle"], out float angle))
				angles = $"0 {angle} 0";

        string[] angleString = angles.Split(' ');

        float pitchDegrees = float.Parse(angleString[0]);
        float yawDegrees = float.Parse(angleString[1]);

        Vector3 launchDir = ConvertAnglesToVector(pitchDegrees, yawDegrees);

			if (!float.TryParse(targetPush["speed"], out float speed))
				speed = 1000;
			else
				speed = float.Parse(targetPush["speed"]);

        Vector3 launchVector = launchDir * speed;
			return $"{launchVector.X} {launchVector.Y} {launchVector.Z}";
		}

		private string GetLaunchVectorWithTarget(Entity targetPush, Entity targetPosition)
		{
        float gravity = 800f;
        float height = targetPosition.Origin.Z - targetPush.Origin.Z;
        double time = Math.Sqrt(height / (.5 * gravity)); // Calculates how many seconds it takes to reach the apex of the launch

        float xDist = targetPosition.Origin.X - targetPush.Origin.X;
        float yDist = targetPosition.Origin.Y - targetPush.Origin.Y;

        double xSpeed = xDist / time;
        double ySpeed = yDist / time;
        double zSpeed = time * gravity;

			return $"{xSpeed} {ySpeed} {zSpeed}";
		}

		private static Vector3 ConvertAnglesToVector(float pitchDegrees, float yawDegrees)
		{
        double yaw = Math.PI * yawDegrees / 180.0;
        double pitch = Math.PI * -pitchDegrees / 180.0;

        double x = Math.Cos(yaw) * Math.Cos(pitch);
        double y = Math.Sin(yaw) * Math.Cos(pitch);
        double z = Math.Sin(pitch);

			return new Vector3((float)x, (float)y, (float)z);
		}

		private void ConvertTargetSpeed(Entity targetSpeed)
		{
			if (targetSpeed["notcpm"] == "1") // TODO: Figure out how to handle gamemode specific entities more robustly
				return;

			targetSpeed.ClassName = "player_speed";

			if (!targetSpeed.TryGetValue("speed", out string? speed))
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
        string noise = targetSpeaker["noise"];
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
        int sourceflags = 0;

			if (q3flags.HasFlag(TargetSpeakerFlags.LoopedOff))
				sourceflags |= (int)AmbientGenericFlags.StartSilent;
			else if (!q3flags.HasFlag(TargetSpeakerFlags.LoopedOn))
				sourceflags |= (int)AmbientGenericFlags.IsNotLooped;

			if (q3flags.HasFlag(TargetSpeakerFlags.Global) || q3flags.HasFlag(TargetSpeakerFlags.Activator))
				sourceflags |= (int)AmbientGenericFlags.InfiniteRange;

			targetSpeaker["spawnflags"] = sourceflags.ToString();
		}

		private void FireTargetInitOnOutput(Entity entity, Entity targetInit, string output, float delay)
		{
			var spawnflags = (TargetInitFlags)targetInit.Spawnflags;
			if (!spawnflags.HasFlag(TargetInitFlags.KeepPowerUps))
			{
				SetHasteOnOutput(entity, "0", output, delay);
				SetQuadOnOutput(entity, "0", output, delay);
			}
			if (!spawnflags.HasFlag(TargetInitFlags.KeepWeapons))
			{
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_knife", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_grenadelauncher", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_rocketlauncher", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_plasmagun", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_lightninggun", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_railgun", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_bfg", output, delay);
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_shotgun", output, delay);
			}
			if (spawnflags.HasFlag(TargetInitFlags.RemoveMachineGun))
			{
				RemoveWeaponOnOutput(entity, "weapon_momentum_df_machinegun", output, delay);
			}
		}

		private static void RemoveWeaponOnOutput(Entity entity, string weaponName, string output, float delay)
		{
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

		private void ConvertKillTrigger(Entity trigger)
		{
			if (!trigger.ClassName.StartsWith("trigger"))
				return;

			trigger.ClassName = "trigger_teleport";
			trigger["target"] = MOMENTUM_START_ENTITY;
			trigger["velocitymode"] = "1";
		}

		private void ConvertTimerTrigger(Entity trigger, string className, int zoneNumber)
		{
			if (ignoreZones || !trigger.ClassName.StartsWith("trigger"))
				return;

        var newTrigger = new Entity
        {
            ClassName = className,
            Model = trigger.Model,
            Spawnflags = 1
        };
        newTrigger["zone_number"] = zoneNumber.ToString();

			sourceEntities.Add(newTrigger);
		}

		// TODO: Convert target_give for player spawn entities
		private void FireTargetGiveOnOutput(Entity entity, Entity targetGive, string output, float delay)
		{
        // TODO: Support more entities (health, armor, etc.)
        List<Entity> targets = GetTargetEntities(targetGive);
			foreach (Entity target in targets)
			{
				switch (target.ClassName)
				{
					case "item_haste":
						SetHasteOnOutput(entity, ConvertPowerupCount(target["count"]), output, delay + 0.008f); //hack to make giving haste happen after target_init strip
						break;
					case "item_enviro": // TODO: Not supported yet
						break;
					case "item_flight": // TODO: Not supported yet
						break;
					case "item_quad":
						SetQuadOnOutput(entity, ConvertPowerupCount(target["count"]), output, delay + 0.008f); //hack to make giving quad happen after target_init strip
						break;
					default:
						if (target.ClassName.StartsWith("weapon_"))
							GiveWeaponOnOutput(entity, target, output, delay);
						else if (target.ClassName.StartsWith("ammo_"))
							GiveAmmoOnOutput(entity, target, output, delay);
						break;
				}

				removeEntities.Add(target);
			}
		}

		private void SetHasteOnOutput(Entity entity, string duration, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = "SetHaste",
				param = duration,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private static void SetQuadOnOutput(Entity entity, string duration, string output, float delay)
		{
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = "SetDamageBoost",
				param = duration,
				delay = delay,
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private void GiveWeaponOnOutput(Entity entity, Entity weaponEnt, string output, float delay)
		{
        string weaponName = GetMomentumWeaponName(weaponEnt.ClassName);
			if (string.IsNullOrEmpty(weaponName))
				return;

			// TODO: Support weapon count
			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = "GiveWeapon",
				param = weaponName,
				delay = delay + 0.008f, //hack to make giving weapon happen after target_init strip
				fireOnce = -1
			};
			entity.connections.Add(connection);

			GiveWeaponAmmoOnOutput(entity, weaponEnt, output, delay);
		}

		private void GiveWeaponAmmoOnOutput(Entity entity, Entity weaponEnt, string output, float delay)
		{
        string count = ConvertWeaponAmmoCount(weaponEnt.ClassName, weaponEnt["count"]);
			if (float.Parse(count) < 0)
				return;

        string ammoType = GetWeaponAmmoType(weaponEnt.ClassName);
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

        return weaponName switch
        {
            "weapon_machinegun" => "40",
            "weapon_grenadelauncher" => "10",
            "weapon_rocketlauncher" => "10",
            "weapon_plasmagun" => "50",
            "weapon_lightning" => "100",
            "weapon_bfg" => "20",
            "weapon_shotgun" => "10",
            _ => "-1",
        };
    }

		private string GetWeaponAmmoType(string weaponName)
		{
        return weaponName switch
        {
            "weapon_machinegun" => "SetBullets",
            "weapon_grenadelauncher" => "SetGrenades",
            "weapon_rocketlauncher" => "SetRockets",
            "weapon_plasmagun" => "SetCells",
            "weapon_lightning" => "SetLightning",
            "weapon_railgun" => "SetRails",
            "weapon_bfg" => "SetBfgRockets",
            "weapon_shotgun" => "SetShells",
            _ => string.Empty,
        };
    }

		private void GiveAmmoOnOutput(Entity entity, Entity ammoEnt, string output, float delay)
		{
			if (ammoEnt["notcpm"] == "1") // TODO: Figure out how to handle gamemode specific entities more robustly
				return;

        string ammoOutput = GetAmmoOutput(ammoEnt.ClassName);
			if (string.IsNullOrEmpty(ammoOutput))
				return;

        string count = ConvertAmmoCount(ammoEnt.ClassName, ammoEnt["count"]);
			if (float.Parse(count) < 0)
				ammoOutput = ammoOutput.Replace("Add", "Set"); // Applies infinite ammo when count is set to a negative value to mimic q3 behaviour

			var connection = new Entity.EntityConnection()
			{
				name = output,
				target = "!player",
				action = ammoOutput,
				param = count,
				delay = delay + 0.008f, //hack to make adding ammo happen after setting ammo
				fireOnce = -1
			};
			entity.connections.Add(connection);
		}

		private string ConvertAmmoCount(string ammoName, string count)
		{
			if (!string.IsNullOrEmpty(count) && count != "0")
				return count;

        return ammoName switch
        {
            "ammo_bfg" => "15",
            // Machine gun
            "ammo_bullets" => "50",
            // Plasma gun
            "ammo_cells" => "30",
            "ammo_grenades" => "5",
            "ammo_lightning" => "60",
            "ammo_rockets" => "5",
            // Shotgun
            "ammo_shells" => "10",
            // Railgun
            "ammo_slugs" => "10",
            _ => "0",
        };
    }

		private string GetAmmoOutput(string ammoName)
		{
        return ammoName switch
        {
            "ammo_bfg" => "AddBfgRockets",
            // Machine gun
            "ammo_bullets" => "AddBullets",
            // Plasma gun
            "ammo_cells" => "AddCells",
            "ammo_grenades" => "AddGrenades",
            "ammo_lightning" => "AddLightning",
            "ammo_rockets" => "AddRockets",
            // Shotgun
            "ammo_shells" => "AddShells",
            // Railgun
            "ammo_slugs" => "AddRails",
            _ => string.Empty,
        };
    }

		private string ConvertPowerupCount(string count)
		{
			if (!string.IsNullOrEmpty(count) && count != "0")
				return count;

			return "30";
		}

    private void ConvertTriggerPush(Entity trigger)
		{
        Entity? target = GetTargetEntities(trigger).FirstOrDefault();
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

        List<Entity> targets = GetTargetEntities(trigger);
			foreach (Entity target in targets)
			{
				if (target.ClassName is not "info_teleport_destination" and not "point_teleport")
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
			weaponEnt["pickupammo"] = ConvertWeaponAmmoCount(weaponEnt.ClassName, weaponEnt["count"]);
			weaponEnt.ClassName = "momentum_weapon_spawner";
		}

		private string GetWeaponRespawnTime(Entity weaponEnt)
		{
			if (weaponEnt.TryGetValue("wait", out string? wait) && wait != "0")
				return wait;

			return "5";
		}

		private string GetMomentumWeaponName(string q3WeaponName)
		{
        return q3WeaponName switch
        {
            "weapon_machinegun" => "weapon_momentum_df_machinegun",
            "weapon_gauntlet" => "weapon_momentum_df_knife",
            "weapon_grenadelauncher" => "weapon_momentum_df_grenadelauncher",
            "weapon_rocketlauncher" => "weapon_momentum_df_rocketlauncher",
            "weapon_plasmagun" => "weapon_momentum_df_plasmagun",
            "weapon_lightning" => "weapon_momentum_df_lightninggun",
            "weapon_railgun" => "weapon_momentum_df_railgun",
            "weapon_bfg" => "weapon_momentum_df_bfg",
            "weapon_shotgun" => "weapon_momentum_df_shotgun",
            "item_haste" => "momentum_powerup_haste",
            "item_quad" => "momentum_powerup_damage_boost",
            _ => string.Empty,
        };
    }

		private void ConvertAmmo(Entity ammoEnt)
		{
			ammoEnt["resettime"] = ConvertAmmoRespawnTime(ammoEnt);
			ammoEnt["ammoname"] = GetMomentumAmmoName(ammoEnt.ClassName);
			ammoEnt["pickupammo"] = ConvertAmmoCount(ammoEnt.ClassName, ammoEnt["count"]);
			ammoEnt.ClassName = "momentum_pickup_ammo";
		}

		private string ConvertAmmoRespawnTime(Entity ammoEnt)
		{
			if (ammoEnt.TryGetValue("wait", out string? wait) && wait != "0")
				return wait;

			return "40";
		}

		private string GetMomentumAmmoName(string q3AmmoName)
		{
        return q3AmmoName switch
        {
            "ammo_bfg" => "bfg_rockets",
            // Machine gun
            "ammo_bullets" => "bullets",
            // Plasma gun
            "ammo_cells" => "cells",
            "ammo_grenades" => "grenades",
            "ammo_lightning" => "lightning",
            "ammo_rockets" => "rockets",
            // Shotgun
            "ammo_shells" => "shells",
            // Railgun
            "ammo_slugs" => "rails",
            _ => string.Empty,
        };
    }

		private void ConvertItem(Entity itemEnt)
		{
			itemEnt.ClassName = GetMomentumItemName(itemEnt.ClassName);
			itemEnt["resettime"] = GetItemRespawnTime(itemEnt);

			if (itemEnt.ClassName == "momentum_powerup_haste")
				itemEnt["hastetime"] = ConvertPowerupCount(itemEnt["count"]);
			else if (itemEnt.ClassName == "momentum_powerup_damage_boost")
				itemEnt["damageboosttime"] = ConvertPowerupCount(itemEnt["count"]);
		}

		private string GetItemRespawnTime(Entity itemEnt)
		{
			if (itemEnt.TryGetValue("wait", out string? wait) && wait != "0")
				return wait;

			return "120";
		}

		private string GetMomentumItemName(string q3ItemName)
		{
        return q3ItemName switch
        {
            "item_haste" => "momentum_powerup_haste",
            "item_quad" => "momentum_powerup_damage_boost",
            _ => string.Empty,
        };
    }

		private void ConvertAngles(Entity entity)
		{
			if (float.TryParse(entity["angle"], out float angle))
			{
				entity.Angles = new Vector3(0f, angle, 0f);
				entity.Remove("angle");
			}
		}

		private void SetTeleportOrigin(Entity teleDest)
		{
        Vector3 origin = teleDest.Origin;
			origin.Z -= 23; // Teleport destinations are 23 units too high once converted
			teleDest.Origin = origin;
		}

		private List<Entity> GetTargetEntities(Entity sourceEntity)
		{
			if (sourceEntity.TryGetValue("target", out string? target) && entityDict.ContainsKey(target))
				return entityDict[target];

			return [];
		}
	}
