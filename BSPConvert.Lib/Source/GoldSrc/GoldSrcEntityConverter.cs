using LibBSP;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Vector3 = System.Numerics.Vector3;

namespace BSPConvert.Lib.GoldSrc
{
	// Converts GoldSrc entity keyvalues to the Source/Momentum entities that behave the same, where the keyvalues
	// mean something different in Source (spawnflags, directions)
	public class GoldSrcEntityConverter
	{
		// GoldSrc trigger spawnflags (triggers.cpp)
		private const int SF_TRIGGER_NOCLIENTS = 2;
		private const int SF_TRIGGER_PUSH_ONCE = 1;
		private const int SF_TRIGGER_PUSH_START_OFF = 2;
		private const int SF_TRIGGER_HURT_START_OFF = 2;
		private const int SF_TRIGGER_HURT_NO_CLIENTS = 8;

		// Source trigger spawnflags
		private const int SF_TRIGGER_ALLOW_CLIENTS = 1;
		private const int SF_TRIG_PUSH_ONCE = 128;

		// GoldSrc door spawnflags (doors.h). Bits 1-512 mean the same in Source, apart from 4 (unused in GoldSrc,
		// "non-solid to player" in Source).
		private const int GoldSrcDoorSharedFlags = 1 | 2 | 8 | 16 | 32 | 64 | 128 | 256 | 512;
		private const int SF_DOOR_USE_ONLY = 256;
		private const uint SF_DOOR_SILENT_GOLDSRC = 0x80000000;

		// Source door spawnflags (func_door.h)
		private const int SF_DOOR_PTOUCH = 1024;
		private const int SF_DOOR_SILENT = 4096;

		// GoldSrc button spawnflags (buttons.cpp)
		private const int SF_BUTTON_DONTMOVE = 1;
		private const int SF_BUTTON_TOGGLE = 32;
		private const int SF_BUTTON_SPARK_IF_OFF_GOLDSRC = 64;
		private const int SF_BUTTON_TOUCH_ONLY = 256;

		// Source button spawnflags (func_button.h)
		private const int SF_BUTTON_TOUCH_ACTIVATES = 256;
		private const int SF_BUTTON_DAMAGE_ACTIVATES = 512;
		private const int SF_BUTTON_USE_ACTIVATES = 1024;
		private const int SF_BUTTON_SPARK_IF_OFF = 4096;

		// GoldSrc entity spawnflags used by the target conversion
		private const int SF_DOOR_NO_AUTO_RETURN = 32;
		private const int SF_MULTIMAN_THREAD = 1;
		private const int SF_RELAY_FIREONCE = 1;
		private const int SF_AUTO_FIREONCE = 1;
		private const int SF_TRIGGER_HURT_TARGETONCE = 1;
		private const int SF_TRIGGER_HURT_CLIENTONLYFIRE = 16;

		// Source logic_relay spawnflags
		private const int SF_REMOVE_ON_FIRE = 1;
		private const int SF_ALLOW_FAST_RETRIGGER = 2;

		// A multi_manager fires at most this many targets
		private const int MaxMultiManagerTargets = 16;

		// Keys the engine stores in an entity's entvars before the entity sees them (gEntvarsDescription), so they
		// never become a multi_manager's targets
		private static readonly HashSet<string> EntvarsKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"absmax", "absmin", "aiment", "air_finished", "angle", "angles", "animtime", "armortype", "armorvalue",
			"avelocity", "basevelocity", "blending", "body", "button", "chain", "classname", "colormap", "controller",
			"deadflag", "dmg", "dmg_inflictor", "dmg_save", "dmg_take", "dmgtime", "effects", "enemy", "fixangle",
			"flags", "frags", "frame", "framerate", "friction", "globalname", "gravity", "groundentity", "health",
			"ideal_yaw", "idealpitch", "impulse", "light_level", "ltime", "max_health", "maxs", "message", "mins",
			"model", "modelindex", "movedir", "movetype", "netname", "nextthink", "noise", "noise1", "noise2",
			"noise3", "oldorigin", "origin", "owner", "pain_finished", "pitch_speed", "punchangle",
			"radsuit_finished", "renderamt", "rendercolor", "renderfx", "rendermode", "scale", "sequence", "size",
			"skin", "solid", "spawnflags", "speed", "takedamage", "target", "targetname", "team", "teleport_time",
			"v_angle", "velocity", "view_ofs", "viewmodel", "waterlevel", "watertype", "weaponmodel", "weapons",
			"yaw_speed",
		};

		// The useType GoldSrc passes to the entities it fires
		private enum UseType
		{
			Off,
			On,
			Toggle
		}

		private readonly ILogger logger;
		// GoldSrc classname of each entity with a targetname, by targetname, from before any class is converted
		private ILookup<string, (Entity entity, string className)> targetsByName;
		private int outputCount;

		public GoldSrcEntityConverter(ILogger logger)
		{
			this.logger = logger;
		}

		// GoldSrc entities fire their "target" (SUB_UseTargets): after their "delay" they remove the entities named by
		// their "killtarget" and call Use on every entity named by their "target". Source entities don't, so each
		// place a GoldSrc entity fires its targets becomes an output, sending each target the input that does what
		// its Use did. Entities that only exist to fire targets (multi_manager, trigger_relay, trigger_auto) become
		// the Source logic entities that fire outputs. Needs every entity's GoldSrc keyvalues, so it runs before
		// Convert.
		// TODO: multisource masters, trigger_changetarget, game_counter, path_corner "message" targets
		public void ConvertTargets(IReadOnlyList<Entity> entities)
		{
			targetsByName = entities
				.Where(entity => !string.IsNullOrEmpty(entity["targetname"]))
				.ToLookup(entity => entity["targetname"], entity => (entity, entity.ClassName));
			outputCount = 0;

			foreach (var entity in entities)
			{
				var gsFlags = GetSpawnFlags(entity);
				switch (entity.ClassName)
				{
					case "trigger_multiple":
					case "trigger_once":
						AddTargetOutputs(entity, "OnTrigger", UseType.Toggle);
						break;
					case "trigger_hurt":
						AddTargetOutputs(entity, (gsFlags & SF_TRIGGER_HURT_CLIENTONLYFIRE) != 0 ? "OnHurtPlayer" : "OnHurt", UseType.Toggle,
							(gsFlags & SF_TRIGGER_HURT_TARGETONCE) != 0 ? 1 : -1);
						break;
					case "func_button":
						// Fired once the button is pressed in, and toggle buttons fire again once they're back out
						AddTargetOutputs(entity, "OnIn", UseType.Toggle);
						if ((gsFlags & SF_BUTTON_TOGGLE) != 0)
							AddTargetOutputs(entity, "OnOut", UseType.Toggle);
						break;
					case "func_door":
					case "func_door_rotating":
						// Fired once the door is fully open and once it's fully closed. "netname" is fired once it's closed.
						AddTargetOutputs(entity, "OnFullyOpen", UseType.Toggle);
						AddTargetOutputs(entity, "OnFullyClosed", UseType.Toggle);
						AddUseOutputs(entity, "OnFullyClosed", entity["netname"], UseType.Toggle, 0f, -1);
						entity.Remove("netname");
						break;
					case "func_breakable":
						AddTargetOutputs(entity, "OnBreak", UseType.Toggle);
						break;
					case "multi_manager":
						ConvertMultiManager(entity, gsFlags);
						break;
					case "trigger_relay":
						// Fires immediately every time it's used
						entity.ClassName = "logic_relay";
						AddTargetOutputs(entity, "OnTrigger", GetTriggerState(entity));
						SetSpawnFlags(entity, ((gsFlags & SF_RELAY_FIREONCE) != 0 ? SF_REMOVE_ON_FIRE : 0) | SF_ALLOW_FAST_RETRIGGER);
						entity.Remove("triggerstate");
						break;
					case "trigger_auto":
						// Source's logic_auto also only fires while its "globalstate" is on
						entity.ClassName = "logic_auto";
						AddTargetOutputs(entity, "OnMapSpawn", GetTriggerState(entity));
						SetSpawnFlags(entity, (gsFlags & SF_AUTO_FIREONCE) != 0 ? SF_REMOVE_ON_FIRE : 0);
						entity.Remove("triggerstate");
						break;
				}
			}

			logger.Log($"Converted GoldSrc targets into {outputCount} outputs");
		}

		// A multi_manager fires each of its targets after its own delay: every key that isn't an entvars field is a
		// target ("name" or "name#n", to list a name more than once) whose value is the delay. Until it has fired them
		// all it ignores being used again, like a logic_relay, unless it's multithreaded.
		private void ConvertMultiManager(Entity entity, int gsFlags)
		{
			var targets = new List<(string key, string name, float delay)>();
			foreach (var (key, value) in entity)
			{
				if (EntvarsKeys.Contains(key) || key == "wait" || key.StartsWith('_') || targets.Count >= MaxMultiManagerTargets)
					continue;

				var hashIndex = key.IndexOf('#', StringComparison.Ordinal);
				targets.Add((key, hashIndex >= 0 ? key.Substring(0, hashIndex) : key, TryParseFloat(value, out var delay) ? delay : 0f));
			}

			entity.ClassName = "logic_relay";
			foreach (var (key, name, delay) in targets)
			{
				entity.Remove(key);
				AddUseOutputs(entity, "OnTrigger", name, UseType.Toggle, delay, -1);
			}

			entity.Remove("wait");
			SetSpawnFlags(entity, (gsFlags & SF_MULTIMAN_THREAD) != 0 ? SF_ALLOW_FAST_RETRIGGER : 0);
		}

		// SUB_UseTargets: after the entity's delay, kills its killtarget and uses its target
		private void AddTargetOutputs(Entity entity, string output, UseType useType, int timesToFire = -1)
		{
			var delay = TryParseFloat(entity["delay"], out var parsedDelay) ? parsedDelay : 0f;
			AddUseOutputs(entity, output, entity["target"], useType, delay, timesToFire);

			var killTarget = entity["killtarget"];
			if (!string.IsNullOrEmpty(killTarget))
				AddOutput(entity, output, killTarget, "Kill", "", delay, timesToFire);

			entity.Remove("target");
			entity.Remove("killtarget");
			entity.Remove("delay");
		}

		// FireTargets: uses every entity named targetName
		private void AddUseOutputs(Entity entity, string output, string targetName, UseType useType, float delay, int timesToFire)
		{
			if (string.IsNullOrEmpty(targetName))
				return;

			// Entities sharing a name can be different classes that need different inputs
			var inputs = targetsByName[targetName]
				.Select(target => GetUseInput(target.entity, target.className, useType))
				.Where(input => input != null)
				.Distinct();
			foreach (var input in inputs)
				AddOutput(entity, output, targetName, input!.Value.input, input.Value.param, delay, timesToFire);
		}

		// The input that does what the entity's Use does in GoldSrc, or null if its Use does nothing
		private static (string input, string param)? GetUseInput(Entity target, string gsClassName, UseType useType)
		{
			switch (gsClassName)
			{
				case "func_door":
				case "func_door_rotating":
					// Use opens a closed door, and closes an open one only if it doesn't close by itself
					return ((GetSpawnFlags(target) & SF_DOOR_NO_AUTO_RETURN) != 0 ? "Toggle" : "Open", "");
				case "func_button":
					return ("Press", "");
				case "func_wall_toggle":
				case "func_train":
				case "func_rotating":
				case "trigger_push":
				case "trigger_hurt":
					return ("Toggle", "");
				case "func_conveyor":
					return ("ToggleDirection", "");
				case "func_breakable":
					return ("Break", "");
				case "multi_manager":
				case "trigger_relay":
					return ("Trigger", "");
				case "ambient_generic":
					return ("ToggleSound", "");
				case "game_text":
					return ("Display", "");
				case "env_global":
					// Sets the global state by its trigger mode, whatever it's used with
					return (int.TryParse(target["triggermode"], out var triggerMode) ? triggerMode : 0) switch
					{
						1 => ("TurnOn", ""),
						2 => ("Remove", ""),
						3 => ("Toggle", ""),
						_ => ("TurnOff", ""),
					};
				case "light":
				case "light_spot":
					return (useType switch { UseType.On => "TurnOn", UseType.Off => "TurnOff", _ => "Toggle" }, "");
				case "env_sprite":
					return (useType switch { UseType.On => "ShowSprite", UseType.Off => "HideSprite", _ => "ToggleSprite" }, "");
				case "trigger_multiple":
				case "trigger_once":
				case "trigger_teleport":
				case "info_target":
				case "info_teleport_destination":
				case "path_corner":
				case "func_wall":
				case "func_illusionary":
				case "worldspawn":
					return null;
				default:
					return ("Use", "");
			}
		}

		// trigger_relay and trigger_auto's "triggerstate", which is off when it isn't set
		private static UseType GetTriggerState(Entity entity)
		{
			if (!int.TryParse(entity["triggerstate"], out var triggerState))
				return UseType.Off;

			return triggerState switch
			{
				0 => UseType.Off,
				2 => UseType.Toggle,
				_ => UseType.On,
			};
		}

		private void AddOutput(Entity entity, string output, string target, string input, string param, float delay, int timesToFire)
		{
			entity.connections.Add(new Entity.EntityConnection
			{
				name = output,
				target = target,
				action = input,
				param = param,
				delay = delay,
				fireOnce = timesToFire,
			});
			outputCount++;
		}

		public void Convert(Entity entity)
		{
			switch (entity.ClassName)
			{
				case "func_door":
					ConvertAnglesToMoveDir(entity, "movedir");
					ConvertDoorSpawnFlags(entity);
					break;
				case "func_door_rotating":
					// A rotating door's angles are its starting rotation, as in Source
					ConvertDoorSpawnFlags(entity);
					break;
				case "func_button":
					ConvertAnglesToMoveDir(entity, "movedir");
					ConvertButtonSpawnFlags(entity);
					break;
				case "func_conveyor":
				case "func_water":
					ConvertAnglesToMoveDir(entity, "movedir");
					break;
				case "func_wall":
				case "func_wall_toggle":
				case "func_illusionary":
				case "func_breakable":
				case "func_pushable":
					// These clear their angles when they spawn (func_breakable keeps the yaw as its gib direction)
					ConvertAnglesToMoveDir(entity, null);
					break;
				case "trigger_multiple":
				case "trigger_once":
				case "trigger_teleport":
					ConvertAnglesToMoveDir(entity, null);
					SetTriggerClientFlag(entity, (GetSpawnFlags(entity) & SF_TRIGGER_NOCLIENTS) == 0);
					break;
				case "trigger_push":
					ConvertTriggerPush(entity);
					break;
				case "trigger_hurt":
					ConvertTriggerHurt(entity);
					break;
			}
		}

		// GoldSrc doors open when a player touches them, unless they're use only or something targets them (they
		// have a targetname). Source doors only open on touch with "Touch Opens".
		private static void ConvertDoorSpawnFlags(Entity entity)
		{
			var gsFlags = GetSpawnFlags(entity);
			var flags = gsFlags & GoldSrcDoorSharedFlags;
			if (((uint)gsFlags & SF_DOOR_SILENT_GOLDSRC) != 0)
				flags |= SF_DOOR_SILENT;
			if ((gsFlags & SF_DOOR_USE_ONLY) == 0 && string.IsNullOrEmpty(entity["targetname"]))
				flags |= SF_DOOR_PTOUCH;

			SetSpawnFlags(entity, flags);
		}

		// GoldSrc buttons are pressed with +use unless they're touch only, and shot when they have health
		private static void ConvertButtonSpawnFlags(Entity entity)
		{
			var gsFlags = GetSpawnFlags(entity);
			var flags = gsFlags & (SF_BUTTON_DONTMOVE | SF_BUTTON_TOGGLE);
			if ((gsFlags & SF_BUTTON_SPARK_IF_OFF_GOLDSRC) != 0)
				flags |= SF_BUTTON_SPARK_IF_OFF;
			flags |= (gsFlags & SF_BUTTON_TOUCH_ONLY) != 0 ? SF_BUTTON_TOUCH_ACTIVATES : SF_BUTTON_USE_ACTIVATES;
			if (float.TryParse(entity["health"], NumberStyles.Float, CultureInfo.InvariantCulture, out var health) && health > 0f)
				flags |= SF_BUTTON_DAMAGE_ACTIVATES;

			SetSpawnFlags(entity, flags);
		}

		// GoldSrc pushes everything, along its angles (+X when they're 0)
		private static void ConvertTriggerPush(Entity entity)
		{
			var gsFlags = GetSpawnFlags(entity);
			ConvertAnglesToMoveDir(entity, "pushdir");

			var flags = SF_TRIGGER_ALLOW_CLIENTS;
			if ((gsFlags & SF_TRIGGER_PUSH_ONCE) != 0)
				flags |= SF_TRIG_PUSH_ONCE;
			SetSpawnFlags(entity, flags);

			if ((gsFlags & SF_TRIGGER_PUSH_START_OFF) != 0)
				entity["StartDisabled"] = "1";
		}

		private static void ConvertTriggerHurt(Entity entity)
		{
			var gsFlags = GetSpawnFlags(entity);
			ConvertAnglesToMoveDir(entity, null);
			SetTriggerClientFlag(entity, (gsFlags & SF_TRIGGER_HURT_NO_CLIENTS) == 0);

			if ((gsFlags & SF_TRIGGER_HURT_START_OFF) != 0)
				entity["StartDisabled"] = "1";
		}

		// GoldSrc triggers fire for clients unless "No Clients" is set; Source triggers only fire for clients with
		// "Clients" set
		private static void SetTriggerClientFlag(Entity entity, bool clients)
		{
			SetSpawnFlags(entity, clients ? SF_TRIGGER_ALLOW_CLIENTS : 0);
		}

		// GoldSrc movers and triggers take their direction from their angles (SetMovedir) and don't rotate by them,
		// where Source rotates a brush entity by its angles. The direction moves to directionKey (or is dropped when
		// null) and the angles are removed.
		private static void ConvertAnglesToMoveDir(Entity entity, string? directionKey)
		{
			// The engine parses "angle" as "angles" "0 <angle> 0"
			var angles = Vector3.Zero;
			if (TryParseVector(entity["angles"], out var parsedAngles))
				angles = parsedAngles;
			else if (TryParseFloat(entity["angle"], out var yaw))
				angles = new Vector3(0f, yaw, 0f);

			entity.Remove("angles");
			entity.Remove("angle");

			if (directionKey == null)
				return;

			// Yaw -1 and -2 are straight up and down
			if (angles == new Vector3(0f, -1f, 0f))
				angles = new Vector3(-90f, 0f, 0f);
			else if (angles == new Vector3(0f, -2f, 0f))
				angles = new Vector3(90f, 0f, 0f);

			entity[directionKey] = FormattableString.Invariant($"{angles.X:0.###} {angles.Y:0.###} {angles.Z:0.###}");
		}

		private static int GetSpawnFlags(Entity entity)
		{
			// Some editors write flags above 2^31 (SF_DOOR_SILENT) as unsigned
			if (int.TryParse(entity["spawnflags"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags))
				return flags;
			return uint.TryParse(entity["spawnflags"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsignedFlags) ? (int)unsignedFlags : 0;
		}

		private static void SetSpawnFlags(Entity entity, int flags)
		{
			entity["spawnflags"] = flags.ToString(CultureInfo.InvariantCulture);
		}

		private static bool TryParseFloat(string value, out float result)
		{
			return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
		}

		private static bool TryParseVector(string value, out Vector3 result)
		{
			result = Vector3.Zero;
			var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 3 || !TryParseFloat(parts[0], out var x) || !TryParseFloat(parts[1], out var y) || !TryParseFloat(parts[2], out var z))
				return false;

			result = new Vector3(x, y, z);
			return true;
		}
	}
}
