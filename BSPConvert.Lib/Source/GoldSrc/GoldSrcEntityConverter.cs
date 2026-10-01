using LibBSP;
using System;
using System.Globalization;

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
