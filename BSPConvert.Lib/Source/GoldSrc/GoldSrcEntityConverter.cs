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

		// trigger_teleport VelocityMode (TeleportVelocityMode_t) that resets the velocity
		private const string TeleportVelocityModeReset = "1";

		// GoldSrc ambient_generic spawnflags. Play everywhere, start silent and not looped mean the same in Source,
		// which takes a radius instead of the radius flags.
		private const int SF_AMBIENT_SOUND_EVERYWHERE = 1;
		private const int SF_AMBIENT_SOUND_SMALLRADIUS = 2;
		private const int SF_AMBIENT_SOUND_MEDIUMRADIUS = 4;
		private const int SF_AMBIENT_SOUND_LARGERADIUS = 8;
		private const int SF_AMBIENT_SOUND_START_SILENT = 16;
		private const int SF_AMBIENT_SOUND_NOT_LOOPING = 32;

		// GoldSrc sound attenuations (ATTN_IDLE, ATTN_STATIC, ATTN_NORM)
		private const float AttenuationSmall = 2f;
		private const float AttenuationMedium = 1.25f;
		private const float AttenuationLarge = 0.8f;

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
		private const int SF_CORNER_FIREONCE = 4;
		private const int SF_GAMECOUNT_FIREONCE = 1;
		private const int SF_GAMECOUNT_RESET = 2;
		private const int SF_GAMECOUNTSET_FIREONCE = 1;
		private const int SF_GLOBAL_SET = 1;

		// Source spawnflags that lock a door or button until it's unlocked
		private const int SF_DOOR_LOCKED = 2048;
		private const int SF_BUTTON_LOCKED = 2048;
		private const int SF_ROTBUTTON_NOTSOLID = 1;
		private const int SF_DOOR_ROTATE_BACKWARDS = 2;
		private const int SF_DOOR_ROTATE_Z = 64;
		private const int SF_DOOR_ROTATE_X = 128;

		// Source logic_relay spawnflags
		private const int SF_REMOVE_ON_FIRE = 1;
		private const int SF_ALLOW_FAST_RETRIGGER = 2;

		// A multi_manager fires at most this many targets
		private const int MaxMultiManagerTargets = 16;

		// A multisource registers at most this many inputs, and logic_branch_listener watches at most this many branches
		private const int MaxMultisourceInputs = 32;
		private const int MaxListenerBranches = 16;

		// A game_counter's count has no bounds, where math_counter's stays between its min and max
		private const string UnboundedCount = "1000000";

		// Classes whose target the conversion turns into outputs (see AddTargetOutputs)
		private static readonly HashSet<string> TargetFiringClasses = new HashSet<string>
		{
			"trigger_multiple", "trigger_once", "trigger_hurt", "func_button", "func_rot_button", "func_door",
			"func_door_rotating", "func_breakable", "trigger_relay", "trigger_auto", "game_counter", "game_counter_set",
		};

		// Classes that use their targets, which multisources expect an input to be able to do
		private static readonly HashSet<string> UsingClasses = new HashSet<string>(TargetFiringClasses) { "multi_manager", "multisource" };

		// Classes that keep their target in Source, where it's the next path_corner or the teleport destination
		private static readonly HashSet<string> SourceTargetClasses = new HashSet<string>
		{
			"func_train", "path_corner", "trigger_teleport",
		};

		// Suffix of the relays an entity whose target trigger_changetarget changes fires through (see
		// GetTargetRelayName)
		private const string TargetRelaySuffix = "__target";

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
			Toggle,
			// Sets a game_counter's count to the value passed along
			Set
		}

		private readonly ILogger logger;
		private List<Entity> entities;
		// GoldSrc classname of each entity with a targetname, by targetname, from before any class is converted
		private ILookup<string, (Entity entity, string className)> targetsByName;
		// The targets trigger_changetargets can give the entities with each name, in order
		private Dictionary<string, List<string>> newTargetsByName;
		// The entities given relays to fire their target through (see AddTargetOutputs)
		private readonly HashSet<Entity> entitiesWithTargetRelays = new HashSet<Entity>();
		// Multisources by name, and whether each global state they depend on starts on
		private Dictionary<string, Multisource> multisources;
		private Dictionary<string, bool> multisourceGlobalStates;
		// Doors and buttons whose master isn't triggered when they spawn, locked once their spawnflags are converted
		private readonly HashSet<Entity> startLocked = new HashSet<Entity>();
		// Entities the conversion gave a name (so their master can lock them, or their textures can switch), which
		// GoldSrc didn't name
		private readonly HashSet<Entity> addedNames = new HashSet<Entity>();
		// Whether a brush entity's faces have alternate textures (see AddTextureToggles)
		private Func<Entity, bool> hasAlternateTextures = _ => false;
		// The env_texturetoggle switching the textures of the entities with each name
		private readonly Dictionary<string, string> textureToggles = new Dictionary<string, string>();
		// The logic_branch holding whether the model env_sprites with each name are shown (see GetModelSpriteBranch)
		private readonly Dictionary<string, string> modelSpriteBranches = new Dictionary<string, string>();
		// The trigger_cameras something uses (see ConvertCamera)
		private readonly HashSet<Entity> usedCameras = new HashSet<Entity>();
		private int outputCount;
		// Whether an env_fog was converted (only the first one counts)
		private bool fogConverted;
		// Sets the noise of env_lasers whose noise env_laser's keyvalue can't hold (see ConvertLaserNoise)
		private Entity? laserNoiseAuto;

		// A multisource: its inputs (the entities that target it, in the order the engine registers them) and the
		// global state it also needs on
		private class Multisource
		{
			public List<Entity> Inputs = new List<Entity>();
			public string GlobalState = "";
			// The entities it's the master of, with their GoldSrc classname
			public List<(Entity entity, string className)> Users = new List<(Entity, string)>();
			// An input can never use it, so it never turns on
			public bool NeverTriggered;
		}

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
		// hasAlternateTextures tells whether a brush entity's faces have alternate textures (see AddTextureToggles).
		public void ConvertTargets(List<Entity> entities, Func<Entity, bool> hasAlternateTextures)
		{
			this.entities = entities;
			this.hasAlternateTextures = hasAlternateTextures;
			targetsByName = entities
				.Where(entity => !string.IsNullOrEmpty(entity["targetname"]))
				.ToLookup(entity => entity["targetname"], entity => (entity, entity.ClassName));
			newTargetsByName = entities
				.Where(entity => entity.ClassName == "trigger_changetarget" && !string.IsNullOrEmpty(entity["target"]) && !string.IsNullOrEmpty(entity["m_iszNewTarget"]))
				.GroupBy(entity => entity["target"])
				.ToDictionary(group => group.Key, group => group.Select(entity => entity["m_iszNewTarget"]).Distinct().ToList());
			entitiesWithTargetRelays.Clear();
			startLocked.Clear();
			addedNames.Clear();
			laserNoiseAuto = null;
			textureToggles.Clear();
			TextureToggleEntities.Clear();
			modelSpriteBranches.Clear();
			usedCameras.Clear();
			outputCount = 0;
			FindMultisources();
			AddTextureToggles();

			// Converting adds the relays for changeable targets to the list
			foreach (var entity in entities.ToList())
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
					case "func_rot_button":
						// Fired once the button is pressed in, and toggle buttons fire again once they're back out
						AddButtonReturnOutputs(entity);
						AddTextureToggleOutputs(entity, gsFlags);
						AddTargetOutputs(entity, "OnIn", UseType.Toggle);
						if ((gsFlags & SF_BUTTON_TOGGLE) != 0)
							AddTargetOutputs(entity, "OnOut", UseType.Toggle);
						break;
					case "func_door":
					case "func_door_rotating":
						// Fired once the door is fully open and once it's fully closed. "netname" is fired once it's closed.
						AddTargetOutputs(entity, "OnFullyOpen", UseType.Toggle);
						AddTargetOutputs(entity, "OnFullyClosed", UseType.Toggle);
						AddUseOutputs(entity, entity, "OnFullyClosed", entity["netname"], UseType.Toggle, 0f, -1);
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
					case "path_corner":
						// Fired when a train reaches it
						AddUseOutputs(entity, entity, "OnPass", entity["message"], UseType.Toggle, 0f, (gsFlags & SF_CORNER_FIREONCE) != 0 ? 1 : -1);
						entity.Remove("message");
						break;
					case "trigger_changetarget":
						ConvertChangeTarget(entity);
						break;
					case "game_counter":
						ConvertGameCounter(entity, gsFlags);
						break;
					case "game_counter_set":
						// Sets the count of the game_counters it targets to its "frags"
						entity.ClassName = "logic_relay";
						AddTargetOutputs(entity, "OnTrigger", UseType.Set, value: GetInt(entity, "frags"));
						SetSpawnFlags(entity, ((gsFlags & SF_GAMECOUNTSET_FIREONCE) != 0 ? SF_REMOVE_ON_FIRE : 0) | SF_ALLOW_FAST_RETRIGGER);
						entity.Remove("frags");
						break;
					case "multisource":
						ConvertMultisource(entity);
						break;
					case "env_laser":
						ConvertLaserNoise(entity, gsFlags);
						break;
				}
			}

			// Masters are converted to locks (see ConvertMultisource)
			foreach (var entity in entities)
				entity.Remove("master");

			logger.Log($"Converted GoldSrc targets into {outputCount} outputs");
		}

		// A multi_manager fires each of its targets after its own delay: every key that isn't an entvars field is a
		// target ("name" or "name#n", to list a name more than once) whose value is the delay. Until it has fired them
		// all it ignores being used again, like a logic_relay, unless it's multithreaded.
		private void ConvertMultiManager(Entity entity, int gsFlags)
		{
			var targets = GetMultiManagerTargets(entity);
			entity.ClassName = "logic_relay";
			foreach (var (key, name, delay) in targets)
			{
				entity.Remove(key);
				AddUseOutputs(entity, entity, "OnTrigger", name, UseType.Toggle, delay, -1);
			}

			entity.Remove("wait");
			SetSpawnFlags(entity, (gsFlags & SF_MULTIMAN_THREAD) != 0 ? SF_ALLOW_FAST_RETRIGGER : 0);
		}

		private static List<(string key, string name, float delay)> GetMultiManagerTargets(Entity entity)
		{
			var targets = new List<(string key, string name, float delay)>();
			foreach (var (key, value) in entity)
			{
				if (EntvarsKeys.Contains(key) || key == "wait" || key.StartsWith('_') || targets.Count >= MaxMultiManagerTargets)
					continue;

				var hashIndex = key.IndexOf('#', StringComparison.Ordinal);
				targets.Add((key, hashIndex >= 0 ? key.Substring(0, hashIndex) : key, TryParseFloat(value, out var delay) ? delay : 0f));
			}

			return targets;
		}

		// A multisource is a master: the doors, buttons, triggers and game entities naming it as their "master" only
		// work while it's triggered, which is while every one of its inputs is on and its global state (if it has one)
		// is on. Its inputs are the entities targeting it, each turned on and off by using it. Momentum has no
		// multisource, so its inputs and global state become logic_branches, and it becomes a logic_branch_listener
		// locking and unlocking the entities it's the master of.
		private void FindMultisources()
		{
			multisources = new Dictionary<string, Multisource>();
			multisourceGlobalStates = new Dictionary<string, bool>();
			foreach (var entity in entities)
			{
				var name = entity["targetname"];
				if (entity.ClassName != "multisource" || string.IsNullOrEmpty(name) || multisources.ContainsKey(name))
					continue;

				// The engine registers the entities with it as their target, then the multi_managers that target it
				var inputs = entities
					.Where(input => input["target"] == name)
					.Concat(entities.Where(input => input.ClassName == "multi_manager" && GetMultiManagerTargets(input).Any(target => target.name == name)))
					.Take(MaxMultisourceInputs)
					.ToList();

				var multisource = new Multisource
				{
					Inputs = inputs,
					GlobalState = entity["globalstate"],
					NeverTriggered = inputs.Any(input => !UsingClasses.Contains(input.ClassName)),
				};

				// The engine finds a master by name, and only the first entity with the name counts
				if (targetsByName[name].First().entity == entity)
				{
					multisource.Users = entities
						.Where(user => user["master"] == name)
						.Select(user => (user, user.ClassName))
						.ToList();
				}

				multisources[name] = multisource;
				if (multisource.NeverTriggered)
					continue;

				var maxInputs = MaxListenerBranches - (multisource.GlobalState.Length > 0 ? 1 : 0);
				if (inputs.Count > maxInputs)
				{
					logger.Log($"Warning: multisource {name} has {inputs.Count} inputs, only the first {maxInputs} are converted");
					inputs.RemoveRange(maxInputs, inputs.Count - maxInputs);
				}

				if (multisource.GlobalState.Length > 0)
					multisourceGlobalStates[multisource.GlobalState] = false;
			}

			// The first env_global set to set its state on spawn does, then later ones find it already set
			foreach (var globalState in multisourceGlobalStates.Keys.ToList())
			{
				var initial = entities.FirstOrDefault(entity => entity.ClassName == "env_global" && entity["globalstate"] == globalState &&
					(GetSpawnFlags(entity) & SF_GLOBAL_SET) != 0);
				var isOn = initial != null && GetInt(initial, "initialstate") == 1;
				multisourceGlobalStates[globalState] = isOn;

				var branch = new Entity();
				branch.ClassName = "logic_branch";
				branch["targetname"] = GetGlobalStateBranchName(globalState);
				branch["InitialValue"] = isOn ? "1" : "0";
				entities.Add(branch);
			}
		}

		private void ConvertMultisource(Entity entity)
		{
			var name = entity["targetname"];
			var target = entity["target"];
			entity.ClassName = "logic_branch_listener";
			entity.Remove("target");
			entity.Remove("globalstate");
			if (string.IsNullOrEmpty(name) || !multisources.TryGetValue(name, out var multisource))
				return;

			var users = multisource.Users;
			var initiallyTriggered = !multisource.NeverTriggered && multisource.Inputs.Count == 0 &&
				(multisource.GlobalState.Length == 0 || multisourceGlobalStates[multisource.GlobalState]);

			var branchNames = new List<string>();
			if (!multisource.NeverTriggered)
			{
				for (var i = 0; i < multisource.Inputs.Count; i++)
				{
					var branch = new Entity();
					branch.ClassName = "logic_branch";
					branch["targetname"] = GetInputBranchName(name, i);
					branch["InitialValue"] = "0";
					entities.Add(branch);
					branchNames.Add(branch["targetname"]);
				}

				if (multisource.GlobalState.Length > 0)
					branchNames.Add(GetGlobalStateBranchName(multisource.GlobalState));
			}

			for (var i = 0; i < branchNames.Count; i++)
				entity[FormattableString.Invariant($"Branch{i + 1:00}")] = branchNames[i];

			for (var i = 0; i < users.Count; i++)
			{
				var (user, className) = users[i];
				var (lockInput, unlockInput) = GetLockInputs(className);
				if (lockInput == null)
					continue;

				if (!initiallyTriggered)
				{
					if (lockInput == "Lock")
						startLocked.Add(user);
					else
						user["StartDisabled"] = "1";
				}

				if (branchNames.Count == 0)
					continue;

				if (string.IsNullOrEmpty(user["targetname"]))
				{
					user["targetname"] = FormattableString.Invariant($"{name}__user{i}");
					addedNames.Add(user);
				}

				AddOutput(entity, "OnAllTrue", user["targetname"], unlockInput!, "", 0f, -1);
				AddOutput(entity, "OnMixed", user["targetname"], lockInput, "", 0f, -1);
				AddOutput(entity, "OnAllFalse", user["targetname"], lockInput, "", 0f, -1);
			}

			// It uses its targets when it turns on
			AddUseOutputs(entity, entity, "OnAllTrue", target, multisource.GlobalState.Length > 0 ? UseType.On : UseType.Toggle, 0f, -1);
		}

		// The inputs that stop and start an entity working while its master is off, or null if it has none
		private static (string? lockInput, string? unlockInput) GetLockInputs(string gsClassName)
		{
			switch (gsClassName)
			{
				case "func_door":
				case "func_door_rotating":
				case "func_button":
				case "func_rot_button":
					return ("Lock", "Unlock");
				case "trigger_multiple":
				case "trigger_once":
				case "trigger_teleport":
				case "game_counter":
				case "game_counter_set":
					return ("Disable", "Enable");
				default:
					return (null, null);
			}
		}

		// The branch of the input a caller turns on and off by using a multisource, or null if it has none
		private string? GetMultisourceInputBranch(Entity multisourceEntity, Entity caller)
		{
			var name = multisourceEntity["targetname"];
			if (!multisources.TryGetValue(name, out var multisource) || multisource.NeverTriggered || multisource.Inputs.Count == 0)
				return null;

			// The engine looks the caller up in its inputs and, not finding it, ends up at the last one
			var index = multisource.Inputs.IndexOf(caller);
			return GetInputBranchName(name, index >= 0 ? index : multisource.Inputs.Count - 1);
		}

		// A button that returns uses the multisources it targets again (ButtonBackHome)
		// The brush entities whose textures switch between their primary and alternate frames (see AddTextureToggles)
		public HashSet<Entity> TextureToggleEntities { get; } = new HashSet<Entity>();

		// Brush entities draw a texture's alternate frames ("+a" for "+0" and back) while their frame is 1, which buttons
		// set while they're pressed in and func_walls toggle when they're used. Source draws the frame of a material's
		// texture that the entity's texture frame index picks (the ToggleTexture proxy), which env_texturetoggle sets.
		// So each of those entities whose faces have alternate textures gets an env_texturetoggle, named after it
		// (buttons without a name get one). func_walls without a name can't be used, so they never switch.
		private void AddTextureToggles()
		{
			for (var i = 0; i < entities.Count; i++)
			{
				var entity = entities[i];
				var isButton = entity.ClassName is "func_button" or "func_rot_button";
				if ((!isButton && entity.ClassName != "func_wall") || !hasAlternateTextures(entity))
					continue;

				if (string.IsNullOrEmpty(entity["targetname"]))
				{
					if (!isButton)
						continue;

					entity["targetname"] = FormattableString.Invariant($"__button{i}");
					addedNames.Add(entity);
				}

				TextureToggleEntities.Add(entity);
				var name = entity["targetname"];
				if (textureToggles.ContainsKey(name))
					continue;

				var textureToggle = new Entity();
				textureToggle.ClassName = "env_texturetoggle";
				textureToggle["targetname"] = name + "__texture";
				textureToggle["target"] = name;
				entities.Add(textureToggle);
				textureToggles[name] = textureToggle["targetname"];
			}
		}

		// Using an env_sprite shows it if it's hidden and hides it if it's shown (ShouldToggle). One drawing a studio
		// model becomes a prop_dynamic, which can only be turned on and off, so a logic_branch named after it holds
		// whether it's shown and turns it on or off, made once something uses it. A named env_sprite starts hidden unless
		// it's flagged to start on. Returns null for an env_sprite drawing a sprite.
		private string? GetModelSpriteBranch(Entity sprite)
		{
			var name = sprite["targetname"];
			if (!sprite["model"].Trim().EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
				return null;
			if (modelSpriteBranches.TryGetValue(name, out var branchName))
				return branchName;

			var branch = new Entity();
			branch.ClassName = "logic_branch";
			branch["targetname"] = name + "__shown";
			branch["InitialValue"] = (GetSpawnFlags(sprite) & SF_SPRITE_STARTON) != 0 ? "1" : "0";
			AddOutput(branch, "OnTrue", name, "TurnOn", "", 0f, -1);
			AddOutput(branch, "OnFalse", name, "TurnOff", "", 0f, -1);
			entities.Add(branch);
			modelSpriteBranches[name] = branch["targetname"];
			return branch["targetname"];
		}

		// A button switches to its alternate textures once it's pressed in, and back once it starts moving out: "wait"
		// seconds later (1 if it's 0), or for a toggle button when it's pressed again (approximated by when it's back
		// out). A button that stays pushed (a wait of -1) never switches back.
		private void AddTextureToggleOutputs(Entity button, int gsFlags)
		{
			if (!TextureToggleEntities.Contains(button) || !textureToggles.TryGetValue(button["targetname"], out var textureToggle))
				return;

			AddOutput(button, "OnIn", textureToggle, "SetTextureIndex", "1", 0f, -1);

			var wait = TryParseFloat(button["wait"], out var parsedWait) ? parsedWait : 0f;
			if (wait == -1f)
				return;

			if ((gsFlags & SF_BUTTON_TOGGLE) != 0)
				AddOutput(button, "OnOut", textureToggle, "SetTextureIndex", "0", 0f, -1);
			else
				AddOutput(button, "OnIn", textureToggle, "SetTextureIndex", "0", wait == 0f ? 1f : wait, -1);
		}

		private void AddButtonReturnOutputs(Entity button)
		{
			var targetName = button["target"];
			if (string.IsNullOrEmpty(targetName))
				return;

			foreach (var (target, className) in targetsByName[targetName])
			{
				if (className == "multisource" && GetMultisourceInputBranch(target, button) is string branch)
					AddOutput(button, "OnOut", branch, "Toggle", "", 0f, -1);
			}
		}

		private static string GetInputBranchName(string multisourceName, int inputIndex)
		{
			return FormattableString.Invariant($"{multisourceName}__input{inputIndex}");
		}

		private static string GetGlobalStateBranchName(string globalState)
		{
			return "__goldsrc_global_" + globalState;
		}

		// game_counter counts up when it's used (down when it's used with off, or to the value a game_counter_set sets)
		// and fires its target whenever the count reaches its limit: its "health", from its "frags". math_counter fires
		// once it reaches its max (or min, counting down), though it doesn't count past it.
		private void ConvertGameCounter(Entity entity, int gsFlags)
		{
			var initialCount = GetInt(entity, "frags");
			var limit = GetInt(entity, "health");
			var countsUp = limit >= initialCount;

			entity.ClassName = "math_counter";
			entity["startvalue"] = initialCount.ToString(CultureInfo.InvariantCulture);
			entity["max"] = countsUp ? limit.ToString(CultureInfo.InvariantCulture) : UnboundedCount;
			entity["min"] = countsUp ? "-" + UnboundedCount : limit.ToString(CultureInfo.InvariantCulture);
			entity.Remove("frags");
			entity.Remove("health");
			SetSpawnFlags(entity, 0);

			var output = countsUp ? "OnHitMax" : "OnHitMin";
			AddTargetOutputs(entity, output, UseType.Toggle);
			// SetValue rather than SetValueNoFire, which wouldn't let it fire at the limit again
			if ((gsFlags & SF_GAMECOUNT_RESET) != 0)
				AddOutput(entity, output, "!self", "SetValue", entity["startvalue"], 0f, -1);
			if ((gsFlags & SF_GAMECOUNT_FIREONCE) != 0)
				AddOutput(entity, output, "!self", "Kill", "", 0f, -1);
		}

		// trigger_changetarget sets the target of the entities named by its target. Entities that keep their target in
		// Source get it with AddOutput. The others fire their target through a relay per target they can have
		// (AddTargetOutputs), so it switches to the new target's relay.
		private void ConvertChangeTarget(Entity entity)
		{
			var targetName = entity["target"];
			var newTarget = entity["m_iszNewTarget"];
			entity.ClassName = "logic_relay";
			SetSpawnFlags(entity, SF_ALLOW_FAST_RETRIGGER);
			entity.Remove("target");
			entity.Remove("m_iszNewTarget");
			// It acts as soon as it's used
			entity.Remove("delay");
			if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(newTarget))
				return;

			var classNames = targetsByName[targetName].Select(target => target.className).ToHashSet();
			if (classNames.Overlaps(TargetFiringClasses))
			{
				var targetIndex = newTargetsByName[targetName].IndexOf(newTarget) + 1;
				AddOutput(entity, "OnTrigger", targetName + TargetRelaySuffix + "*", "Disable", "", 0f, -1);
				AddOutput(entity, "OnTrigger", GetTargetRelayName(targetName, targetIndex, null), "Enable", "", 0f, -1);
			}

			if (classNames.Overlaps(SourceTargetClasses))
				AddOutput(entity, "OnTrigger", targetName, "AddOutput", "target " + newTarget, 0f, -1);
		}

		// SUB_UseTargets: after the entity's delay, kills its killtarget and uses its target
		private void AddTargetOutputs(Entity entity, string output, UseType useType, int timesToFire = -1, int value = 0)
		{
			var delay = TryParseFloat(entity["delay"], out var parsedDelay) ? parsedDelay : 0f;
			var targetName = entity["targetname"];
			if (!string.IsNullOrEmpty(targetName) && newTargetsByName.TryGetValue(targetName, out var newTargets))
			{
				// trigger_changetarget can change the target, so fire every relay and let the current target's through
				AddTargetRelays(entity, newTargets, useType, value);
				for (var i = 0; i <= newTargets.Count; i++)
					AddOutput(entity, output, GetTargetRelayName(targetName, i, entity), "Trigger", "", delay, timesToFire);
			}
			else
			{
				AddUseOutputs(entity, entity, output, entity["target"], useType, delay, timesToFire, value);
			}

			var killTarget = entity["killtarget"];
			if (!string.IsNullOrEmpty(killTarget))
				AddOutput(entity, output, killTarget, "Kill", "", delay, timesToFire);

			entity.Remove("target");
			entity.Remove("killtarget");
			entity.Remove("delay");
		}

		// A relay for each target an entity can have, its own first, that fires that target and is only enabled
		// while it's the entity's target
		private void AddTargetRelays(Entity entity, List<string> newTargets, UseType useType, int value)
		{
			if (!entitiesWithTargetRelays.Add(entity))
				return;

			var targetName = entity["targetname"];
			var targets = new List<string> { entity["target"] };
			targets.AddRange(newTargets);
			for (var i = 0; i < targets.Count; i++)
			{
				var relay = new Entity();
				relay.ClassName = "logic_relay";
				relay["targetname"] = GetTargetRelayName(targetName, i, entity);
				relay["origin"] = entity["origin"];
				SetSpawnFlags(relay, SF_ALLOW_FAST_RETRIGGER);
				if (i > 0)
					relay["StartDisabled"] = "1";

				// Fires as the entity, as far as the entities it fires can tell
				AddUseOutputs(relay, entity, "OnTrigger", targets[i], useType, 0f, -1, value);
				entities.Add(relay);
			}
		}

		// "<name>__target<index>_<entity>": every entity with a name has its own relays, and a trigger_changetarget
		// enables the ones for a target on all of them at once with "<name>__target<index>_*"
		private string GetTargetRelayName(string targetName, int targetIndex, Entity? entity)
		{
			var entitySuffix = entity != null ? entities.IndexOf(entity).ToString(CultureInfo.InvariantCulture) : "*";
			return FormattableString.Invariant($"{targetName}{TargetRelaySuffix}{targetIndex}_{entitySuffix}");
		}

		// FireTargets: uses every entity named targetName, as caller
		private void AddUseOutputs(Entity entity, Entity caller, string output, string targetName, UseType useType, float delay, int timesToFire,
			int value = 0)
		{
			if (string.IsNullOrEmpty(targetName))
				return;

			// Entities sharing a name can be different classes that need different inputs
			var inputs = targetsByName[targetName]
				.SelectMany(target => GetUseInputs(target.entity, target.className, caller, useType, value))
				.Distinct();
			foreach (var (inputTarget, input, param) in inputs)
				AddOutput(entity, output, inputTarget ?? targetName, input, param, delay, timesToFire);
		}

		// The inputs that do what the entity's Use does, sent to the entity unless they name another target
		private IEnumerable<(string? target, string input, string param)> GetUseInputs(Entity target, string gsClassName, Entity caller,
			UseType useType, int value)
		{
			switch (gsClassName)
			{
				case "func_wall":
					// Switches its textures between their primary and alternate frames
					if (textureToggles.TryGetValue(target["targetname"], out var textureToggle))
					{
						yield return useType switch
						{
							UseType.On => (textureToggle, "SetTextureIndex", "1"),
							UseType.Off => (textureToggle, "SetTextureIndex", "0"),
							_ => (textureToggle, "IncrementTextureIndex", ""),
						};
					}
					yield break;
				case "trigger_camera":
					usedCameras.Add(target);
					break;
				case "env_sprite":
					// A model env_sprite is shown and hidden by its branch
					if (GetModelSpriteBranch(target) is string spriteBranch)
					{
						yield return useType switch
						{
							UseType.On => (spriteBranch, "SetValueTest", "1"),
							UseType.Off => (spriteBranch, "SetValueTest", "0"),
							_ => (spriteBranch, "ToggleTest", ""),
						};
						yield break;
					}
					break;
				case "multisource":
					// Turns the caller's input on or off, whatever it's used with
					if (GetMultisourceInputBranch(target, caller) is string branch)
						yield return (branch, "Toggle", "");
					yield break;
				case "env_global":
					// Multisources see the global state through a branch
					if (multisourceGlobalStates.ContainsKey(target["globalstate"]))
					{
						// Dead counts as off
						var (branchInput, branchParam) = GetInt(target, "triggermode") switch
						{
							1 => ("SetValue", "1"),
							3 => ("Toggle", ""),
							_ => ("SetValue", "0"),
						};
						yield return (GetGlobalStateBranchName(target["globalstate"]), branchInput, branchParam);
					}
					break;
			}

			if (GetUseInput(target, gsClassName, useType, value) is { } input)
				yield return (null, input.input, input.param);
		}

		// The input that does what the entity's Use does in GoldSrc, or null if its Use does nothing. Entities other
		// than game_counter treat a set like a toggle.
		private static (string input, string param)? GetUseInput(Entity target, string gsClassName, UseType useType, int value)
		{
			switch (gsClassName)
			{
				case "game_counter":
					return useType switch
					{
						UseType.Off => ("Subtract", "1"),
						UseType.Set => ("SetValue", value.ToString(CultureInfo.InvariantCulture)),
						_ => ("Add", "1"),
					};
				case "game_counter_set":
					return ("Trigger", "");
				case "func_door":
				case "func_door_rotating":
					// Use opens a closed door, and closes an open one only if it doesn't close by itself
					return ((GetSpawnFlags(target) & SF_DOOR_NO_AUTO_RETURN) != 0 ? "Toggle" : "Open", "");
				case "func_button":
				case "func_rot_button":
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
				case "trigger_changetarget":
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
				case "env_beam":
				case "env_laser":
					return (useType switch { UseType.On => "TurnOn", UseType.Off => "TurnOff", _ => "Toggle" }, "");
				case "trigger_camera":
					// Using it toggles it, but it turns itself off after its wait, which a toggle mostly comes after
					return (useType == UseType.Off ? "Disable" : "Enable", "");
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

		// Converts an entity's keyvalues. Returns false if it has no Source counterpart and should be dropped.
		public bool Convert(Entity entity)
		{
			if (UnusedClasses.Contains(entity.ClassName))
				return false;

			switch (entity.ClassName)
			{
				case "trigger_camera":
					return ConvertCamera(entity);
				case "env_fog":
					return ConvertFog(entity);
				case "env_rain":
				case "func_rain":
				case "env_snow":
				case "func_snow":
					return ConvertWeather(entity);
				case "env_beam":
					ScaleKey(entity, "BoltWidth", BeamWidthScale);
					ScaleKey(entity, "NoiseAmplitude", BeamNoiseScale);
					break;
				case "env_laser":
					ScaleKey(entity, "width", BeamWidthScale);
					break;
				case "func_door":
					ConvertAnglesToMoveDir(entity, "movedir");
					ConvertDoorSpawnFlags(entity, IsNamedInGoldSrc(entity));
					LockIfMasterOff(entity, SF_DOOR_LOCKED);
					break;
				case "func_door_rotating":
					// A rotating door's angles are its starting rotation, as in Source
					ConvertDoorSpawnFlags(entity, IsNamedInGoldSrc(entity));
					LockIfMasterOff(entity, SF_DOOR_LOCKED);
					break;
				case "func_button":
					ConvertAnglesToMoveDir(entity, "movedir");
					ConvertButtonSpawnFlags(entity);
					LockIfMasterOff(entity, SF_BUTTON_LOCKED);
					break;
				case "func_rot_button":
					// A rotating button's angles are its starting rotation, as in Source
					ConvertRotButtonSpawnFlags(entity);
					LockIfMasterOff(entity, SF_BUTTON_LOCKED);
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
					ConvertAnglesToMoveDir(entity, null);
					SetTriggerClientFlag(entity, (GetSpawnFlags(entity) & SF_TRIGGER_NOCLIENTS) == 0);
					break;
				case "trigger_teleport":
					ConvertAnglesToMoveDir(entity, null);
					SetTriggerClientFlag(entity, (GetSpawnFlags(entity) & SF_TRIGGER_NOCLIENTS) == 0);
					// GoldSrc teleports stop whatever they teleport, and turn it to the destination's angles like
					// Source's do by default
					entity["VelocityMode"] = TeleportVelocityModeReset;
					break;
				case "trigger_push":
					ConvertTriggerPush(entity);
					break;
				case "trigger_hurt":
					ConvertTriggerHurt(entity);
					break;
				case "ambient_generic":
					ConvertAmbientGeneric(entity);
					break;
			}

			switch (entity.ClassName)
			{
				case "func_door":
				case "func_door_rotating":
					ConvertDoorSounds(entity);
					break;
				case "func_button":
				case "func_rot_button":
					ConvertButtonSound(entity);
					break;
				case "func_train":
					ConvertTrainSounds(entity);
					break;
				case "func_rotating":
					ConvertRotatingSound(entity);
					break;
			}

			return true;
		}

		// GoldSrc's client scales a beam entity's width (its scale) by 0.1 (delta.lst) and its noise (its body) by 0.01,
		// where Source's uses them as they are. Both scale the texture scroll speed by 0.1.
		private const float BeamWidthScale = 0.1f;
		private const float BeamNoiseScale = 0.01f;
		private const int SF_BEAM_STARTON = 1;
		private const int SF_SPRITE_STARTON = 1;

		private static void ScaleKey(Entity entity, string key, float scale)
		{
			if (TryParseFloat(entity[key], out var value))
				entity[key] = (value * scale).ToString("0.###", CultureInfo.InvariantCulture);
		}

		// env_laser reads its noise amplitude as a whole number, which drops the fraction GoldSrc's noise has once it's
		// scaled (see BeamNoiseScale). So the noise is set by its Noise input when the map spawns instead. A laser
		// without a name, which always starts on, gets one, so it's flagged to start on.
		private void ConvertLaserNoise(Entity laser, int gsFlags)
		{
			var noise = TryParseFloat(laser["NoiseAmplitude"], out var parsedNoise) ? parsedNoise * BeamNoiseScale : 0f;
			laser["NoiseAmplitude"] = "0";
			if (noise <= 0f)
				return;

			if (string.IsNullOrEmpty(laser["targetname"]))
			{
				laser["targetname"] = FormattableString.Invariant($"__laser{entities.IndexOf(laser)}");
				addedNames.Add(laser);
				SetSpawnFlags(laser, gsFlags | SF_BEAM_STARTON);
			}

			if (laserNoiseAuto == null)
			{
				laserNoiseAuto = new Entity();
				laserNoiseAuto.ClassName = "logic_auto";
				entities.Add(laserNoiseAuto);
			}

			AddOutput(laserNoiseAuto, "OnMapSpawn", laser["targetname"], "Noise", noise.ToString("0.###", CultureInfo.InvariantCulture), 0f, -1);
		}

		private const int SF_FOG_MASTER = 1;
		// Densities above this turn Counter-Strike's fog off
		private const float MaxFogDensity = 0.01f;
		// Where a linear fog ramp starts and ends, times the density, to follow GoldSrc's exponentially squared fog
		// (see ConvertFog)
		private const float FogStartDensityDistance = 0.15f;
		private const float FogEndDensityDistance = 1.58f;

		// Counter-Strike's env_fog (CClientFog): the client fogs the world, but not the sky, with the first env_fog's
		// rendercolor, by its density (0 to 0.01, anything else turns the fog off) as OpenGL's exponentially squared
		// fog (1 - e^-(density * distance)^2 of the fog color), and ignores its other keys. Source's
		// env_fog_controller fogs linearly from fogstart to fogend, so it gets the ramp that best fits that curve,
		// within 4% of it at every distance. Fog doesn't change at runtime in either.
		private bool ConvertFog(Entity entity)
		{
			if (fogConverted)
				return false;

			fogConverted = true;
			var density = TryParseFloat(entity["density"], out var parsedDensity) ? parsedDensity : 0f;
			if (density <= 0f || density > MaxFogDensity)
				return false;

			var color = entity["rendercolor"].Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Select(part => int.TryParse(part, out var value) ? Math.Clamp(value, 0, 255) : 0)
				.Concat(new[] { 0, 0, 0 })
				.Take(3);

			entity.ClassName = "env_fog_controller";
			entity["fogenable"] = "1";
			entity["fogcolor"] = string.Join(' ', color);
			entity["fogstart"] = (FogStartDensityDistance / density).ToString("0", CultureInfo.InvariantCulture);
			entity["fogend"] = (FogEndDensityDistance / density).ToString("0", CultureInfo.InvariantCulture);
			entity["fogmaxdensity"] = "1";
			entity["farz"] = "-1";
			SetSpawnFlags(entity, SF_FOG_MASTER);
			foreach (var key in new[] { "density", "rendercolor", "startdist", "enddist" })
				entity.Remove(key);

			return true;
		}

		// Entities Momentum has no use for: Counter-Strike's game modes (bomb, hostage, VIP and escape maps, buy zones,
		// weapons on the ground and its map settings), and ones only the compile tools read
		private static readonly HashSet<string> UnusedClasses = new HashSet<string>
		{
			"func_bomb_target", "info_bomb_target", "func_buyzone", "func_hostage_rescue", "info_hostage_rescue", "hostage_entity",
			"func_vip_safetyzone", "info_vip_start", "func_escapezone", "armoury_entity", "info_map_parameters", "info_overview_point",
			"info_texlights", "info_compile_parameters", "gchimp_info",
		};

		// A trigger_camera shows a player the view from it, looking at its target, while it's on, like Source's
		// point_viewcontrol, which has the same keys and flags. Counter-Strike also uses the ones nothing triggers as
		// spectator views, which Momentum doesn't have, so those are dropped.
		private bool ConvertCamera(Entity entity)
		{
			if (!usedCameras.Contains(entity))
				return false;

			entity.ClassName = "point_viewcontrol";
			return true;
		}

		// The func_precipitation the map's weather entities became, or null if it has none (see ConvertWeather)
		public Entity? Precipitation { get; private set; }

		// Source's func_precipitation types
		private const string PrecipitationRain = "0";
		private const string PrecipitationSnow = "1";
		// Its density, as a percentage in renderamt
		private const string PrecipitationDensity = "100";

		// Counter-Strike rains on the whole map when it has any env_rain or func_rain, or else snows when it has an
		// env_snow or func_snow (CBasePlayer::SendWeatherInfo), whatever the entity's shape or keys: its client draws
		// drops around the player wherever the sky is above them. Source's func_precipitation rains within its brush's
		// bounds where nothing but sky is up to 512 units above the player. So the first weather entity becomes one that
		// covers the whole map, with func_precipitation_blockers where there's cover higher up (given their brushes once
		// brush models are converted, see GoldSrcConverter.AddPrecipitationVolume), and the rest are dropped.
		private bool ConvertWeather(Entity entity)
		{
			var isRain = entity.ClassName is "env_rain" or "func_rain";
			if (Precipitation != null)
			{
				// Rain wins over snow
				if (isRain)
					Precipitation["preciptype"] = PrecipitationRain;

				return false;
			}

			Precipitation = entity;
			entity.ClassName = "func_precipitation";
			entity["preciptype"] = isRain ? PrecipitationRain : PrecipitationSnow;
			entity["renderamt"] = PrecipitationDensity;
			foreach (var key in new[] { "model", "origin", "angles", "spawnflags", "rendercolor", "rendermode", "enddist" })
				entity.Remove(key);

			return true;
		}

		// GoldSrc ambient_generic's radius flags pick its attenuation (medium without one). Source's takes a radius,
		// which it turns into a sound level, so it gets the radius of the sound level the attenuation has in Source.
		private static void ConvertAmbientGeneric(Entity entity)
		{
			var gsFlags = GetSpawnFlags(entity);
			SetSpawnFlags(entity, gsFlags & (SF_AMBIENT_SOUND_EVERYWHERE | SF_AMBIENT_SOUND_START_SILENT | SF_AMBIENT_SOUND_NOT_LOOPING));
			if ((gsFlags & SF_AMBIENT_SOUND_EVERYWHERE) != 0)
				return;

			var attenuation = (gsFlags & SF_AMBIENT_SOUND_SMALLRADIUS) != 0 ? AttenuationSmall :
				(gsFlags & SF_AMBIENT_SOUND_MEDIUMRADIUS) != 0 ? AttenuationMedium :
				(gsFlags & SF_AMBIENT_SOUND_LARGERADIUS) != 0 ? AttenuationLarge :
				AttenuationMedium;

			// ATTN_TO_SNDLVL, then the inverse of the ambient_generic's ComputeSoundlevel (40dB at 36 units), aiming
			// half a level up so it doesn't truncate to the one below
			var soundLevel = (int)(50f + 20f / attenuation);
			var radius = 36f * MathF.Pow(10f, (soundLevel + 0.5f - 40f) / 20f);
			entity["radius"] = radius.ToString("0.#", CultureInfo.InvariantCulture);
		}

		// GoldSrc doors pick their moving and stopping sounds by number (CBaseDoor::Precache), and their locked and
		// unlocked sounds from the button sounds. Source doors take the sound names.
		private static void ConvertDoorSounds(Entity entity)
		{
			var moveSound = GetInt(entity, "movesnd");
			var stopSound = GetInt(entity, "stopsnd");
			entity["noise1"] = moveSound is >= 1 and <= 10 ? FormattableString.Invariant($"doors/doormove{moveSound}.wav") : NullSound;
			entity["noise2"] = stopSound is >= 1 and <= 8 ? FormattableString.Invariant($"doors/doorstop{stopSound}.wav") : NullSound;
			entity.Remove("movesnd");
			entity.Remove("stopsnd");

			foreach (var key in new[] { "locked_sound", "unlocked_sound" })
			{
				var sound = GetInt(entity, key);
				entity[key] = sound != 0 ? GetButtonSound(sound) : NullSound;
			}
		}

		// The silent sound GoldSrc plays where an entity has no sound. Source's doors and trains play default sounds
		// where theirs aren't set, which aren't silent for doors.
		private const string NullSound = "common/null.wav";

		// GoldSrc plats and trains pick their moving and stopping sounds from these by "movesnd" and "stopsnd"
		// (CBasePlatTrain::Precache)
		private static readonly string[] TrainMoveSounds =
		{
			"plats/bigmove1.wav", "plats/bigmove2.wav", "plats/elevmove1.wav", "plats/elevmove2.wav", "plats/elevmove3.wav",
			"plats/freightmove1.wav", "plats/freightmove2.wav", "plats/heavymove1.wav", "plats/rackmove1.wav", "plats/railmove1.wav",
			"plats/squeekmove1.wav", "plats/talkmove1.wav", "plats/talkmove2.wav",
		};
		private static readonly string[] TrainStopSounds =
		{
			"plats/bigstop1.wav", "plats/bigstop2.wav", "plats/freightstop1.wav", "plats/heavystop2.wav", "plats/rackstop1.wav",
			"plats/railstop1.wav", "plats/squeekstop1.wav", "plats/talkstop1.wav",
		};

		// Source's func_train plays "noise1" while moving and "noise2" when it stops, at the same default volume. Its
		// "sounds" picked preset sounds in Quake, which GoldSrc ignores.
		private static void ConvertTrainSounds(Entity entity)
		{
			var moveSound = GetInt(entity, "movesnd");
			var stopSound = GetInt(entity, "stopsnd");
			entity["noise1"] = moveSound >= 1 && moveSound <= TrainMoveSounds.Length ? TrainMoveSounds[moveSound - 1] : NullSound;
			entity["noise2"] = stopSound >= 1 && stopSound <= TrainStopSounds.Length ? TrainStopSounds[stopSound - 1] : NullSound;
			entity.Remove("movesnd");
			entity.Remove("stopsnd");
			entity.Remove("sounds");
		}

		// A func_rotating plays the sound in "message", or else the fan sound "sounds" picks, or nothing
		// (CFuncRotating::Precache). Source's plays the sound in "message" too.
		private static void ConvertRotatingSound(Entity entity)
		{
			var sound = GetInt(entity, "sounds");
			if (string.IsNullOrWhiteSpace(entity["message"]))
				entity["message"] = sound is >= 1 and <= 5 ? FormattableString.Invariant($"fans/fan{sound}.wav") : NullSound;

			entity.Remove("sounds");
		}

		// A GoldSrc button's "sounds" picks its sound. Source's picks one of the game's button sounds the same way, so
		// the GoldSrc sound is given as Momentum's custom sound instead.
		// Source's buttons play their custom sound only when "sounds" is negative, and are silent when it's 0
		private static void ConvertButtonSound(Entity entity)
		{
			var sound = GetInt(entity, "sounds") != 0 ? GetButtonSound(GetInt(entity, "sounds")) : null;
			SetSoundKey(entity, "customsound", sound);
			entity["sounds"] = sound != null ? "-1" : "0";
		}

		private static void SetSoundKey(Entity entity, string key, string? sound)
		{
			if (sound != null)
				entity[key] = sound;
			else
				entity.Remove(key);
		}

		// ButtonSound (buttons.cpp)
		private static string GetButtonSound(int sound)
		{
			return sound switch
			{
				>= 1 and <= 11 => FormattableString.Invariant($"buttons/button{sound}.wav"),
				12 => "buttons/latchlocked1.wav",
				13 => "buttons/latchunlocked1.wav",
				14 => "buttons/lightswitch2.wav",
				>= 21 and <= 25 => FormattableString.Invariant($"buttons/lever{sound - 20}.wav"),
				_ => "buttons/button9.wav",
			};
		}

		// GoldSrc doors open when a player touches them, unless they're use only or something targets them (they
		// have a targetname). Source doors only open on touch with "Touch Opens".
		private static void ConvertDoorSpawnFlags(Entity entity, bool isNamed)
		{
			var gsFlags = GetSpawnFlags(entity);
			var flags = gsFlags & GoldSrcDoorSharedFlags;
			if (((uint)gsFlags & SF_DOOR_SILENT_GOLDSRC) != 0)
				flags |= SF_DOOR_SILENT;
			if ((gsFlags & SF_DOOR_USE_ONLY) == 0 && !isNamed)
				flags |= SF_DOOR_PTOUCH;

			SetSpawnFlags(entity, flags);
		}

		private bool IsNamedInGoldSrc(Entity entity)
		{
			return !string.IsNullOrEmpty(entity["targetname"]) && !addedNames.Contains(entity);
		}

		private void LockIfMasterOff(Entity entity, int lockedFlag)
		{
			if (startLocked.Contains(entity))
				SetSpawnFlags(entity, GetSpawnFlags(entity) | lockedFlag);
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

		// Rotating buttons share the axis and direction flags with rotating doors, which mean the same in Source
		private static void ConvertRotButtonSpawnFlags(Entity entity)
		{
			var gsFlags = GetSpawnFlags(entity);
			var flags = gsFlags & (SF_ROTBUTTON_NOTSOLID | SF_DOOR_ROTATE_BACKWARDS | SF_BUTTON_TOGGLE | SF_DOOR_ROTATE_Z | SF_DOOR_ROTATE_X);
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

		// A number stored in a float entvars field that the entity uses as a whole number (game_counter's counts)
		private static int GetInt(Entity entity, string key)
		{
			return float.TryParse(entity[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? (int)value : 0;
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
