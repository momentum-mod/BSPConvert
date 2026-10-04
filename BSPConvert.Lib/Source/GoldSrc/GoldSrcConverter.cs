using LibBSP;
using SharpCompress.Archives;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using Color = System.Drawing.Color;

namespace BSPConvert.Lib.GoldSrc
{
	public class GoldSrcConverterOptions
	{
		// Extra directories searched (recursively) for the WAD files maps take their textures from and their sky
		// images, e.g. a Half-Life install or a folder of community WADs. Searched after the input file's own
		// directories and before the default Steam Half-Life install.
		public string[] wadDirs;
		// The studiomdl.exe that compiles the models entities use. Found next to the output game folder or in a default
		// Steam Momentum Mod install if not given.
		public string studiomdlPath;
	}

	// Converts GoldSrc (Half-Life / Counter-Strike 1.6) BSPs (see IEngineConverter).
	//
	// Collision: GoldSrc doesn't expand the player box at runtime. The compiler bakes a clip hull per player size
	// (hull 1 standing, hull 3 ducking) and the engine traces a point through it. How the compiler expanded sloped
	// faces depends on the map's cliptype (legacy maps expand them less than the true box size), so collision
	// rebuilt from the visible geometry doesn't match on many maps, and clip brushes only exist in the clip hulls.
	// So the clip hulls become brushes of their own (see GoldSrcClipHull), while brushes built from hull 0 (the
	// visible geometry) keep the normal contents for every other trace.
	public class GoldSrcConverter : IEngineConverter
	{
		// Padding around a model's bounds when closing off the outermost BSP regions
		private const float BoundsPadding = 1f;
		// Distance from a node plane within which a region corner counts as on the plane
		public const float RegistrationEpsilon = 0.01f;

		// Worldspawn key marking a map whose brushes carry GoldSrc clip hulls
		public const string ClipHullsWorldspawnKey = "goldsrc_clip_hulls";

		private const int LightmapLuxelSize = 16;

		// Neutral gray for textures that weren't found. Strata tints the replacement for a missing material by its
		// texdata reflectivity (mat_error_texture_advanced), so black would render them black.
		private static readonly Color MissingTextureReflectivity = ColorExtensions.FromArgb(255, 128, 128, 128);

		// Where Steam installs Half-Life by default, searched for WADs last
		private static readonly string DefaultHalfLifeDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Half-Life");
		// Where Steam installs Momentum Mod by default, whose studiomdl compiles the models
		private static readonly string DefaultMomentumDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Momentum Mod Playtest");

		private readonly BSPConverterOptions options;
		private readonly ILogger logger;
		private readonly ContentManager contentManager;

		private GoldSrcBsp gs;
		private SourceBspBuilder builder;
		private BSP sourceBsp;

		private int[] texInfoMap;
		// Each miptex's pixels, or null where neither the BSP nor a WAD has them
		private MipTexture?[] mipTextures;
		private GoldSrcMaterialConverter materialConverter;
		private GoldSrcTextureFinder textureFinder;
		private readonly Dictionary<string, ConvertedMaterial> convertedMaterials = new Dictionary<string, ConvertedMaterial>(StringComparer.OrdinalIgnoreCase);
		// Brush models whose entity draws them differently from the world (see ConvertRenderMode), and the texinfos
		// made for them
		private readonly Dictionary<int, SurfaceStyle> modelStyles = new Dictionary<int, SurfaceStyle>();
		private readonly Dictionary<(int texInfo, SurfaceStyle style), int> styleTexInfos = new Dictionary<(int, SurfaceStyle), int>();
		private readonly Dictionary<int, int> noDrawTexInfos = new Dictionary<int, int>();

		// A converted material: the material whose VTF it draws (itself, or an animation's first frame), that
		// texture, and how the VTF animates
		private record ConvertedMaterial(string VtfMaterial, MipTexture Texture, TextureAnimation Animation);

		// How a brush entity draws its faces: blended by its rendermode (Amount is renderamt as 0-1), scrolling its
		// "scroll" textures at ScrollSpeed texels per second, and switching textures to their alternate frames when the
		// entity's texture frame index is 1 (see GoldSrcEntityConverter.AddTextureToggles)
		private record struct SurfaceStyle(BlendMode Mode, float Amount, float ScrollSpeed, bool ToggleTextures = false)
		{
			public static readonly SurfaceStyle World = new SurfaceStyle(BlendMode.Opaque, 1f, 0f);
		}
		// Brush models whose entity switches their textures to their alternate frames
		private readonly HashSet<int> textureToggleModels = new HashSet<int>();
		// The BSP's texture names, which include every frame of the animations its faces use
		private HashSet<string> mipTexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// Each texture's VTF holding its frame and its alternate frame, or null where it couldn't be made
		private readonly Dictionary<string, ConvertedMaterial?> toggledTextures = new Dictionary<string, ConvertedMaterial?>(StringComparer.OrdinalIgnoreCase);
		private int[] sourceLeafForGoldSrcLeaf;
		// Number of converted faces before each GoldSrc face, and of marksurfaces referencing them before each
		// GoldSrc marksurface (one past the end too), to remap face ranges once nodraw faces are dropped
		private int[] facesBefore;
		private int[] markSurfacesBefore;
		// The converted faces with lightmaps, with where theirs is in the GoldSrc lighting data and how big it is, for
		// ConvertLighting to place it in Source's
		private readonly List<(Face face, int lightOffset, int luxels, int styleCount)> litFaces = new List<(Face, int, int, int)>();
		private Dictionary<int, List<int>> leafBrushes = new Dictionary<int, List<int>>();
		private List<int>[] modelBrushes;
		// Brush models that are volumes of contents (func_water) mapped to their GoldSrc contents, and the regions of
		// their hull 0 brushes (see ConvertVolumeEntity)
		private Dictionary<int, int> volumeModelContents = new Dictionary<int, int>();
		private Dictionary<int, ConvexRegion> volumeBrushRegions = new Dictionary<int, ConvexRegion>();
		// Brush models of func_ladders, whose brushes become world ladder brushes (see ConvertLadder)
		private readonly HashSet<int> ladderModels = new HashSet<int>();
		// Finds the sounds and sprites the entities use
		private GoldSrcAssetFinder assetFinder;
		// The sounds the entities play (path under sound/) and their files, which go with the map (see FindSounds)
		private readonly Dictionary<string, string> soundFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		// Compiles the studio models entities use (see ConvertStudioModels), in a temp folder of its own
		private GoldSrcModelCompiler? modelCompiler;
		private string? modelWorkDir;
		// The folder the map's converted assets go in under materials, models and sound ("goldsrc/<map>"). Maps keep
		// theirs apart because Strata keeps assets loaded across maps by name, and GoldSrc maps often have different
		// textures, models and sounds of the same name.
		private string assetDir = "goldsrc";
		// The func_precipitation the map's weather became, which covers the whole map (see AddPrecipitationVolume)
		private Entity? precipitation;
		// The grid the space under cover is found on, and how many func_precipitation_blockers cover it at most
		private const float PrecipitationBlockerCellSize = 32f;
		private const int MaxPrecipitationBlockers = 128;

		public GoldSrcConverter(BSPConverterOptions options, ILogger logger, ContentManager contentManager)
		{
			this.options = options;
			this.logger = logger;
			this.contentManager = contentManager;
		}

		public void Convert(BSP inputBsp, SourceBspBuilder output)
		{
			gs = GoldSrcBsp.Read(inputBsp);
			assetDir = "goldsrc/" + SanitizeAssetName(inputBsp.MapName);
			builder = output;
			sourceBsp = output.Bsp;
			leafBrushes.Clear();
			litFaces.Clear();
			volumeModelContents.Clear();
			modelStyles.Clear();
			styleTexInfos.Clear();
			noDrawTexInfos.Clear();
			textureToggleModels.Clear();
			toggledTextures.Clear();
			mipTexNames = new HashSet<string>(gs.MipTextures.Select(mipTex => mipTex.name), StringComparer.OrdinalIgnoreCase);
			volumeBrushRegions.Clear();
			ladderModels.Clear();
			soundFiles.Clear();
			modelCompiler = null;
			modelBrushes = new List<int>[gs.Models.Length];
			for (var i = 0; i < modelBrushes.Length; i++)
				modelBrushes[i] = new List<int>();

			if (options.scale != 1f)
				logger.Log("Warning: --scale is not supported for GoldSrc maps and will be ignored.");

			SetLumpVersions();
			LogClipType();

			try
			{
				ConvertEntities();
				assetFinder = new GoldSrcAssetFinder(GetAssetSearchDirs(), GetModName());
				FindSounds();
				ConvertPlanes();
				ConvertTexInfos();
				ConvertSkybox();
				ConvertSprites();
				ConvertDecals();
				ConvertStudioModels();
				ConvertVertices();
				ConvertFaces();
				ConvertLighting();
				ConvertNodes();
				ConvertHull0();
				AddVolumeBrushesToWorld();
				foreach (var (hull, hullExtents, hullContents) in GoldSrcClipHull.PlayerHulls)
					ConvertClipHull(hull, hullExtents, hullContents);
				WriteLeafBrushes();
				ConvertModels();
				AddPrecipitationVolume();
				ConvertVisibility();
				new GoldSrcModelLighting(gs, sourceBsp, sourceLeafForGoldSrcLeaf).Convert();

				builder.AddPlaceholderArea();
				builder.AddPlaceholderAreaPortal();
				builder.AddPlaceholderWorldLight();

				WriteContent();
			}
			finally
			{
				if (modelWorkDir != null && Directory.Exists(modelWorkDir))
					Directory.Delete(modelWorkDir, true);
				modelWorkDir = null;
			}
		}

		// Logs which cliptype the map was compiled with, for porters: legacy maps expand sloped faces less than the
		// player box's true size, while the other cliptypes expand them by it like Source does. Maps don't store their
		// cliptype, so it's inferred from where the standing hull's planes are: each sloped face's plane
		// is pushed out by the box, legacy by Σ n_i² h_i (hlcsg's "normalized" offset) and the others by the box's
		// support distance Σ |n_i| h_i (precise pushes floors out by n_z h_z only). Faces whose plane has no
		// predicted match (clip-merged or detail faces, brush entities) don't count.
		private void LogClipType()
		{
			// Normal components and distance within which a clip hull plane matches a prediction
			const float normalEpsilon = 0.001f;
			const float distanceEpsilon = 0.05f;
			// How much the predictions have to differ to tell the cliptypes apart
			const float minOffsetDifference = 0.5f;
			const float floorNormalZ = 0.7f;

			// The clip hulls' plane distances, both ways round, by rounded normal
			(int, int, int) NormalKey(Vector3 normal) => ((int)MathF.Round(normal.X / normalEpsilon), (int)MathF.Round(normal.Y / normalEpsilon), (int)MathF.Round(normal.Z / normalEpsilon));
			var hullPlanes = new Dictionary<(int, int, int), List<float>>();
			void AddHullPlane(Vector3 normal, float dist)
			{
				if (!hullPlanes.TryGetValue(NormalKey(normal), out var dists))
					hullPlanes[NormalKey(normal)] = dists = new List<float>();

				dists.Add(dist);
			}

			foreach (var planeIndex in gs.ClipNodes.Select(clipNode => clipNode.planeIndex).Distinct())
			{
				var plane = gs.Planes[planeIndex];
				AddHullPlane(plane.normal, plane.dist);
				AddHullPlane(-plane.normal, -plane.dist);
			}

			bool HullHasPlane(Vector3 normal, float dist) =>
				hullPlanes.TryGetValue(NormalKey(normal), out var dists) && dists.Any(hullDist => MathF.Abs(hullDist - dist) < distanceEpsilon);

			var legacyFaces = 0;
			var otherFaces = 0;
			var seenPlanes = new HashSet<(int, bool)>();
			foreach (var face in gs.Faces)
			{
				if (!seenPlanes.Add((face.planeIndex, face.planeSide)))
					continue;

				var plane = gs.Planes[face.planeIndex];
				var normal = face.planeSide ? -plane.normal : plane.normal;
				var dist = face.planeSide ? -plane.dist : plane.dist;
				var h = GoldSrcClipHull.StandingHullExtents;
				var legacyOffset = normal.X * normal.X * h.X + normal.Y * normal.Y * h.Y + normal.Z * normal.Z * h.Z;
				var simpleOffset = MathF.Abs(normal.X) * h.X + MathF.Abs(normal.Y) * h.Y + MathF.Abs(normal.Z) * h.Z;
				var preciseOffset = normal.Z > floorNormalZ ? normal.Z * h.Z : simpleOffset;
				if (MathF.Abs(simpleOffset - legacyOffset) < minOffsetDifference || MathF.Abs(preciseOffset - legacyOffset) < minOffsetDifference)
					continue;

				if (HullHasPlane(normal, dist + legacyOffset))
					legacyFaces++;
				else if (HullHasPlane(normal, dist + simpleOffset) || HullHasPlane(normal, dist + preciseOffset))
					otherFaces++;
			}

			if (legacyFaces == 0 && otherFaces == 0)
				logger.Log("Cliptype: unknown (no sloped faces that the cliptypes expand differently, so collision is the same under any)");
			else if (legacyFaces > otherFaces)
				logger.Log($"Cliptype: legacy ({legacyFaces} of {legacyFaces + otherFaces} sloped planes)");
			else
				logger.Log($"Cliptype: simple or precise ({otherFaces} of {legacyFaces + otherFaces} sloped planes)");
		}

		// Matches the lump versions the Q3 converter writes for the same lumps
		private void SetLumpVersions()
		{
			if (builder.OldBSP)
			{
				builder.SetLumpVersion(Face.GetIndexForLump(sourceBsp.MapType), 1);
				builder.SetLumpVersion(Leaf.GetIndexForLump(sourceBsp.MapType), 1);
				return;
			}

			builder.SetLumpVersion(Face.GetIndexForLump(sourceBsp.MapType), 2);
			builder.SetLumpVersion(Edge.GetIndexForLump(sourceBsp.MapType), 1);
			builder.SetLumpVersion(NumList.GetIndexForIndicesLump(sourceBsp.MapType, out _), 1);
			builder.SetLumpVersion(Node.GetIndexForLump(sourceBsp.MapType), 1);
			builder.SetLumpVersion(Leaf.GetIndexForLump(sourceBsp.MapType), 2);
			builder.SetLumpVersion(NumList.GetIndexForLeafFacesLump(sourceBsp.MapType, out _), 1);
			builder.SetLumpVersion(NumList.GetIndexForLeafBrushesLump(sourceBsp.MapType, out _), 1);
			builder.SetLumpVersion(BrushSide.GetIndexForLump(sourceBsp.MapType), 1);
			builder.SetLumpVersion(Lightmaps.GetIndexForLump(sourceBsp.MapType), 1);
		}

		private void ConvertEntities()
		{
			var entities = new List<Entity>();
			foreach (var gsEntity in gs.Entities)
			{
				var entity = new Entity();
				foreach (var key in gsEntity.Keys)
					entity[key] = gsEntity[key];

				entities.Add(entity);
			}

			var entityConverter = new GoldSrcEntityConverter(logger);
			entityConverter.ConvertTargets(entities, HasAlternateTextures);
			foreach (var entity in entityConverter.TextureToggleEntities)
			{
				if (TryGetBrushModel(entity, out var modelIndex))
					textureToggleModels.Add(modelIndex);
			}

			foreach (var entity in entities)
			{
				ConvertRenderMode(entity);
				if (!entityConverter.Convert(entity))
					continue;

				switch (entity.ClassName)
				{
					case "worldspawn":
						entity.Remove("wad");
						entity.Remove("_wad");
						entity[ClipHullsWorldspawnKey] = "1";
						break;
					case "func_water":
						ConvertVolumeEntity(entity);
						break;
					case "func_ladder":
						// Becomes world brushes (see ConvertLadder)
						ConvertLadder(entity);
						continue;
				}

				sourceBsp.Entities.Add(entity);
			}

			precipitation = entityConverter.Precipitation;
		}

		// GoldSrc water is a brush entity whose "skin" holds its contents. Source's func_water needs the model's vphysics
		// collision data to create its fluid controller (and crashes without it), which converted maps don't have.
		// Instead the model's brushes are added to the world as static water (see AddVolumeBrushesToWorld), and the
		// entity is kept as a func_illusionary so it still renders.
		// TODO: Moving water (func_water used as a door) stays where it starts
		private void ConvertVolumeEntity(Entity entity)
		{
			if (!TryGetBrushModel(entity, out var modelIndex))
				return;

			var contents = int.TryParse(entity["skin"], out var skin) && skin < 0 ? skin : GoldSrcBsp.CONTENTS_WATER;
			volumeModelContents[modelIndex] = contents;

			if (!string.IsNullOrEmpty(entity["targetname"]))
				logger.Log($"Warning: {entity.ClassName} {entity["model"]} ({entity["targetname"]}) is converted as static water and won't move.");

			entity.ClassName = "func_illusionary";
		}

		// GoldSrc's func_ladder is a non-solid brush entity the player climbs while overlapping it. Momentum has no
		// func_ladder and climbs Source ladders instead: solid brushes with CONTENTS_LADDER that the player moves
		// into (like Valve's own conversions for Half-Life: Source). So the ladder's brushes, including its clip hull
		// brushes that GoldSrc-hull game modes trace, are added to the world with CONTENTS_LADDER and the entity is
		// dropped. Its faces are never drawn, like GoldSrc's ladders, which the game makes invisible.
		private void ConvertLadder(Entity entity)
		{
			if (!TryGetBrushModel(entity, out var modelIndex))
				return;

			if (TryParseVector(entity["origin"], out var origin) && origin != Vector3.Zero)
			{
				logger.Log($"Warning: {entity.ClassName} {entity["model"]} has an origin brush, which isn't supported, so it isn't converted");
				return;
			}

			ladderModels.Add(modelIndex);
		}

		private static bool TryParseVector(string value, out Vector3 result)
		{
			result = Vector3.Zero;
			var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 3 ||
				!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
				!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
				!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
				return false;

			result = new Vector3(x, y, z);
			return true;
		}

		// GoldSrc rendermodes (const.h)
		private const int RenderTransColor = 1;
		private const int RenderTransTexture = 2;
		private const int RenderTransAlpha = 4;
		private const int RenderTransAdd = 5;

		// How a brush entity draws its model's faces, baked into their materials (see GetStyleTexInfo) so the entity
		// itself renders normally. GoldSrc draws translucent and additive brush entities fullbright, blended by
		// renderamt, which defaults to 0 and hides them (a common way to make an invisible func_wall). It scrolls the
		// entity's "scroll" textures at the speed of a func_conveyor, which it encodes in the rendercolor that's
		// read for every other brush entity too.
		// TODO: Changing rendermode/renderamt at runtime (env_render) or reversing a conveyor won't affect the baked
		// materials
		private void ConvertRenderMode(Entity entity)
		{
			if (!TryGetBrushModel(entity, out var modelIndex))
				return;

			var style = new SurfaceStyle(BlendMode.Opaque, 1f, GetScrollSpeed(entity), textureToggleModels.Contains(modelIndex));

			// Source tints a brush entity by its rendercolor. GoldSrc only uses it for the Color rendermode (approximated
			// as Texture below), glow sprites and conveyor speeds, and editors default it to "0 0 0", which turned
			// brush entities black.
			entity.Remove("rendercolor");

			var amount = Math.Clamp(int.TryParse(entity["renderamt"], out var renderAmt) ? renderAmt : 0, 0, 255) / 255f;
			switch (int.TryParse(entity["rendermode"], out var renderMode) ? renderMode : 0)
			{
				case RenderTransColor:
				case RenderTransTexture:
					style = style with { Mode = BlendMode.Translucent, Amount = amount };
					entity["rendermode"] = "0";
					break;
				case RenderTransAdd:
					style = style with { Mode = BlendMode.Additive, Amount = amount };
					entity["rendermode"] = "0";
					break;
				case RenderTransAlpha:
					// Alpha testing '{' textures is part of their material already
					entity["rendermode"] = "0";
					break;
			}

			if (style != SurfaceStyle.World)
				modelStyles[modelIndex] = style;
		}

		// The speed a brush entity scrolls its "scroll" textures at: a func_conveyor's speed (100 if it has none),
		// which it encodes in its rendercolor for the client (UpdateSpeed), and the speed that decodes from any other
		// entity's rendercolor
		private static float GetScrollSpeed(Entity entity)
		{
			int speedCode;
			bool backwards;
			if (entity.ClassName == "func_conveyor")
			{
				var speed = float.TryParse(entity["speed"], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeed) && parsedSpeed != 0f ? parsedSpeed : 100f;
				speedCode = Math.Min((int)(MathF.Abs(speed) * 16f), 0xFFFF);
				backwards = speed < 0f;
			}
			else
			{
				if (!TryParseVector(entity["rendercolor"], out var color))
					return 0f;

				speedCode = ((int)color.Y & 0xFF) << 8 | ((int)color.Z & 0xFF);
				backwards = (int)color.X != 0;
			}

			return (backwards ? -speedCode : speedCode) / 16f;
		}

		// Whether a brush entity's faces have a texture that switches to an alternate one (see GetAlternateTexture)
		private bool HasAlternateTextures(Entity entity)
		{
			if (!TryGetBrushModel(entity, out var modelIndex))
				return false;

			var model = gs.Models[modelIndex];
			for (var i = model.firstFace; i < model.firstFace + model.numFaces && i < gs.Faces.Length; i++)
			{
				var texInfo = gs.Faces[i].texInfo;
				if (texInfo >= 0 && texInfo < gs.TexInfos.Length && GetAlternateTexture(GetMipTexName(gs.TexInfos[texInfo])) != null)
					return true;
			}

			return false;
		}

		// The texture a "+0name" texture switches to while its entity's frame is 1 ("+aname"), and the other way
		// around. Only for textures whose primary and alternate sequences are single frames: animated ones keep playing
		// their primary sequence.
		// TODO: Switching animated sequences
		private string? GetAlternateTexture(string mipTexName)
		{
			if (mipTexName.Length < 3 || mipTexName[0] != '+')
				return null;

			var baseName = mipTexName.Substring(2);
			var frame = char.ToLowerInvariant(mipTexName[1]);
			if (frame != '0' && frame != 'a')
				return null;
			if (mipTexNames.Contains("+1" + baseName) || mipTexNames.Contains("+b" + baseName))
				return null;

			var alternate = (frame == '0' ? "+a" : "+0") + baseName;
			return mipTexNames.TryGetValue(alternate, out var actualName) ? actualName : null;
		}

		// The VTF of a texture's frame followed by its alternate frame, which ToggleTexture materials pick from
		private ConvertedMaterial? GetToggledTexture(string mipTexName, string alternateName)
		{
			var vtfMaterial = GetMaterialName(mipTexName) + "_toggle";
			if (toggledTextures.TryGetValue(vtfMaterial, out var toggled))
				return toggled;

			toggled = null;
			if (convertedMaterials.TryGetValue(GetMaterialName(mipTexName), out var primary) && primary.Animation == TextureAnimation.None &&
				convertedMaterials.TryGetValue(GetMaterialName(alternateName), out var alternate) && alternate.Animation == TextureAnimation.None &&
				materialConverter.ConvertToggled(vtfMaterial, primary.Texture, alternate.Texture))
				toggled = new ConvertedMaterial(vtfMaterial, primary.Texture, TextureAnimation.None);

			toggledTextures[vtfMaterial] = toggled;
			return toggled;
		}

		private bool TryGetBrushModel(Entity entity, out int modelIndex)
		{
			modelIndex = -1;
			var model = entity["model"];
			if (!model.StartsWith('*') || !int.TryParse(model.Substring(1), out var index) || index <= 0 || index >= gs.Models.Length)
				return false;

			modelIndex = index;
			return true;
		}

		// Copied index for index: nodes and clip nodes reference these planes
		private void ConvertPlanes()
		{
			foreach (var plane in gs.Planes)
				builder.AppendPlane(plane.normal, plane.dist);
		}

		private void ConvertTexInfos()
		{
			FindTextures();
			ConvertTextures();

			var texDataForMipTex = new int[gs.MipTextures.Length];
			for (var i = 0; i < gs.MipTextures.Length; i++)
			{
				var mipTex = gs.MipTextures[i];
				var materialName = GetMaterialName(mipTex.name);
				var textureDataIndex = builder.LookupTextureData(materialName);
				if (textureDataIndex < 0)
				{
					// Converted textures take their size and reflectivity from the VTF
					textureDataIndex = convertedMaterials.TryGetValue(materialName, out var converted) ?
						builder.AddTextureData(materialName, converted.VtfMaterial) :
						builder.AddTextureData(materialName, mipTex.width, mipTex.height, MissingTextureReflectivity);
				}

				texDataForMipTex[i] = textureDataIndex;
			}

			texInfoMap = new int[gs.TexInfos.Length];
			for (var i = 0; i < gs.TexInfos.Length; i++)
			{
				var texInfo = gs.TexInfos[i];
				var textureDataIndex = texInfo.mipTex >= 0 && texInfo.mipTex < texDataForMipTex.Length ? texDataForMipTex[texInfo.mipTex] : 0;
				texInfoMap[i] = AddTexInfo(i, GetMaterialName(GetMipTexName(texInfo)), textureDataIndex);
			}
		}

		private int AddTexInfo(int texInfoIndex, string materialName, int textureDataIndex, int extraSurfaceFlags = 0)
		{
			var texInfo = gs.TexInfos[texInfoIndex];
			var uAxis = new Vector3(texInfo.s.X, texInfo.s.Y, texInfo.s.Z);
			var vAxis = new Vector3(texInfo.t.X, texInfo.t.Y, texInfo.t.Z);
			// GoldSrc ignores water's texture offset (R_TextureCoord)
			var textureOffset = GoldSrcMaterialConverter.IsTurbulent(GetMipTexName(texInfo)) ? Vector2.Zero : new Vector2(texInfo.s.W, texInfo.t.W);

			// GoldSrc lightmaps have one luxel per 16 texels
			return builder.AddTextureInfo(
				uAxis, vAxis,
				uAxis / LightmapLuxelSize, vAxis / LightmapLuxelSize,
				GetSurfaceFlags(texInfo, GetMipTexName(texInfo)) | extraSurfaceFlags,
				textureDataIndex,
				materialName,
				textureOffset,
				new Vector2(texInfo.s.W / LightmapLuxelSize, texInfo.t.W / LightmapLuxelSize));
		}

		private string GetMipTexName(GoldSrcBsp.TexInfo texInfo)
		{
			return texInfo.mipTex >= 0 && texInfo.mipTex < gs.MipTextures.Length ? gs.MipTextures[texInfo.mipTex].name : "";
		}

		// The texinfo for a face of a brush model its entity draws differently from the world: the same mapping with
		// a material that draws the texture translucent, additive or scrolling, or nodraw where renderamt hides it
		private int GetStyleTexInfo(int texInfoIndex, SurfaceStyle style)
		{
			var mipTexName = GetMipTexName(gs.TexInfos[texInfoIndex]);
			if (!IsScrollTexture(mipTexName))
				style = style with { ScrollSpeed = 0f };
			ConvertedMaterial? toggled = null;
			if (style.ToggleTextures && (GetAlternateTexture(mipTexName) is not string alternate || (toggled = GetToggledTexture(mipTexName, alternate)) == null))
				style = style with { ToggleTextures = false };
			if (style == SurfaceStyle.World)
				return texInfoMap[texInfoIndex];

			var key = (texInfoIndex, style);
			if (styleTexInfos.TryGetValue(key, out var styleTexInfo))
				return styleTexInfo;

			var baseMaterial = GetMaterialName(mipTexName);
			if (style.Mode != BlendMode.Opaque && style.Amount <= 0f)
			{
				styleTexInfo = GetNoDrawTexInfo(texInfoIndex);
			}
			else if (!IsToolTexture(mipTexName) && convertedMaterials.TryGetValue(baseMaterial, out var converted))
			{
				// A toggled texture draws its VTF of both frames
				converted = toggled ?? converted;
				var material = baseMaterial + GetStyleSuffix(style);
				if (!convertedMaterials.ContainsKey(material))
				{
					materialConverter.WriteVariant(material, converted.VtfMaterial, converted.Texture, converted.Animation, style.Mode, style.Amount,
						style.ScrollSpeed, toggled != null);
					convertedMaterials[material] = converted;
				}

				var textureDataIndex = builder.LookupTextureData(material);
				if (textureDataIndex < 0)
					textureDataIndex = builder.AddTextureData(material, converted.VtfMaterial);

				styleTexInfo = AddTexInfo(texInfoIndex, material, textureDataIndex);
			}
			else
			{
				// Tool and missing textures draw the same either way
				styleTexInfo = texInfoMap[texInfoIndex];
			}

			styleTexInfos[key] = styleTexInfo;
			return styleTexInfo;
		}

		// "_tex128" (translucent, renderamt 128), "_add255" (additive), "_scroll500" (scrolling at 500), or several
		private static string GetStyleSuffix(SurfaceStyle style)
		{
			var suffix = style.Mode switch
			{
				BlendMode.Translucent => FormattableString.Invariant($"_tex{(int)MathF.Round(style.Amount * 255f)}"),
				BlendMode.Additive => FormattableString.Invariant($"_add{(int)MathF.Round(style.Amount * 255f)}"),
				_ => "",
			};
			if (style.ScrollSpeed != 0f)
				suffix += FormattableString.Invariant($"_scroll{style.ScrollSpeed:0.##}");
			if (style.ToggleTextures)
				suffix += "_toggle";

			return suffix;
		}

		// Textures GoldSrc scrolls on brush entities (SURF_CONVEYOR)
		private static bool IsScrollTexture(string mipTexName)
		{
			return mipTexName.StartsWith("scroll", StringComparison.OrdinalIgnoreCase);
		}

		private int GetFaceTexInfo(GoldSrcBsp.Face face, int modelIndex)
		{
			if (modelIndex <= 0)
				return texInfoMap[face.texInfo];

			if (IsHiddenWaterFace(face, modelIndex))
				return GetNoDrawTexInfo(face.texInfo);

			return modelStyles.TryGetValue(modelIndex, out var style) ? GetStyleTexInfo(face.texInfo, style) : texInfoMap[face.texInfo];
		}

		// GoldSrc only draws the top of a brush entity's water: R_DrawBrushModel skips turbulent faces that aren't
		// horizontal or that are at the model's bottom. Drawing the sides would z-fight with the walls they're against.
		private bool IsHiddenWaterFace(GoldSrcBsp.Face face, int modelIndex)
		{
			if (!GoldSrcMaterialConverter.IsTurbulent(GetMipTexName(gs.TexInfos[face.texInfo])))
				return false;

			var plane = gs.Planes[face.planeIndex];
			return plane.type != GoldSrcBsp.PLANE_Z || gs.Models[modelIndex].mins.Z + 1f >= plane.dist;
		}

		private int GetNoDrawTexInfo(int texInfoIndex)
		{
			if (!noDrawTexInfos.TryGetValue(texInfoIndex, out var noDrawTexInfo))
			{
				noDrawTexInfo = AddTexInfo(texInfoIndex, NoDrawMaterial, GetNoDrawTextureData(),
					(int)(SourceSurfaceFlags.SURF_NODRAW | SourceSurfaceFlags.SURF_NOLIGHT));
				noDrawTexInfos[texInfoIndex] = noDrawTexInfo;
			}

			return noDrawTexInfo;
		}

		private int GetNoDrawTextureData()
		{
			var index = builder.LookupTextureData(NoDrawMaterial);
			return index >= 0 ? index : builder.AddTextureData(NoDrawMaterial, 64, 64, MissingTextureReflectivity);
		}

		private void FindTextures()
		{
			textureFinder = new GoldSrcTextureFinder(gs, GetAssetSearchDirs(), GetModName(), logger);
			mipTextures = new MipTexture?[gs.MipTextures.Length];
			var missing = new List<string>();
			for (var i = 0; i < mipTextures.Length; i++)
			{
				mipTextures[i] = textureFinder.Find(i);
				var name = gs.MipTextures[i].name;
				if (mipTextures[i] == null && !string.IsNullOrEmpty(name) && !IsToolTexture(name))
					missing.Add(name);
			}

			logger.Log($"Found {mipTextures.Length - missing.Count}/{mipTextures.Length} textures");
			if (missing.Count > 0)
				logger.Log($"Warning: Textures not found (pass the folder of the WADs that have them with --wads): {string.Join(", ", missing)}");
		}

		// Where WADs and sky images are searched for (recursively): the input's own directories first (an extracted
		// map archive, the bsp's folder and the mod folder above a maps folder), then the user's directories, then a
		// default Steam Half-Life install
		private IEnumerable<string> GetAssetSearchDirs()
		{
			yield return contentManager.ContentDir;

			var inputDir = Path.GetDirectoryName(Path.GetFullPath(options.inputFile));
			if (inputDir != null)
			{
				yield return inputDir;
				if (Path.GetFileName(inputDir).Equals("maps", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(inputDir) is string modDir)
					yield return modDir;
			}

			if (options.goldSrc.wadDirs != null)
			{
				foreach (var dir in options.goldSrc.wadDirs)
					yield return dir;
			}

			yield return DefaultHalfLifeDir;
		}

		private void ConvertTextures()
		{
			materialConverter = new GoldSrcMaterialConverter(contentManager.ContentDir);
			convertedMaterials.Clear();
			for (var i = 0; i < mipTextures.Length; i++)
			{
				var name = gs.MipTextures[i].name;
				var texture = mipTextures[i];
				if (texture == null || IsToolTexture(name))
					continue;

				var materialName = GetMaterialName(name);
				if (convertedMaterials.ContainsKey(materialName) || ConvertAnimation(name))
					continue;

				// Water's warp is baked into its texture
				var isWater = GoldSrcMaterialConverter.IsTurbulent(name);
				if (isWater ? materialConverter.ConvertWarped(materialName, texture) : materialConverter.Convert(materialName, texture))
					convertedMaterials[materialName] = new ConvertedMaterial(materialName, texture, isWater ? TextureAnimation.Warp : TextureAnimation.None);
				else
					logger.Log($"Warning: Failed to convert texture {texture.Name}");
			}

			logger.Log($"Converted {convertedMaterials.Count} textures");
		}

		// Converts the animation sequence a "+<frame><name>" texture belongs to: "+0name" to "+9name", or the
		// alternate sequence "+aname" to "+jname" that triggered brush entities switch to. Frames are found by name,
		// since a map only has to reference one of them. Returns false if the texture isn't part of a sequence with
		// more than one frame.
		// Brush entities switching between single frame primary and alternate textures draw both (see GetToggledTexture).
		private bool ConvertAnimation(string textureName)
		{
			if (textureName.Length < 3 || textureName[0] != '+')
				return false;

			var frameChar = char.ToLowerInvariant(textureName[1]);
			char firstFrame;
			if (frameChar is >= '0' and <= '9')
				firstFrame = '0';
			else if (frameChar is >= 'a' and <= 'j')
				firstFrame = 'a';
			else
				return false;

			var baseName = textureName.Substring(2);
			var frames = new List<MipTexture>();
			var frameMaterials = new List<string>();
			for (var frame = 0; frame < 10; frame++)
			{
				// Like the engine, the sequence ends at the first missing frame
				var frameName = "+" + (char)(firstFrame + frame) + baseName;
				var texture = textureFinder.Find(frameName);
				if (texture == null || (frames.Count > 0 && (texture.Width != frames[0].Width || texture.Height != frames[0].Height)))
					break;

				frames.Add(texture);
				frameMaterials.Add(GetMaterialName(frameName));
			}

			if (frames.Count < 2)
				return false;

			if (!materialConverter.ConvertAnimated(frameMaterials, frames))
			{
				logger.Log($"Warning: Failed to convert animated texture {textureName}");
				return false;
			}

			foreach (var frameMaterial in frameMaterials)
				convertedMaterials[frameMaterial] = new ConvertedMaterial(frameMaterials[0], frames[0], TextureAnimation.Sequence);

			return true;
		}

		// GoldSrc's default sv_skyname, for maps whose worldspawn doesn't set one
		private const string DefaultSkyName = "desert";
		private static readonly string[] SkyboxSuffixes = { "rt", "bk", "lf", "ft", "up", "dn" };

		// Converts the sky drawn on "sky" faces. GoldSrc loads gfx/env/<skyname><suffix>.tga (or .bmp) and Source
		// loads materials/skybox/<skyname><suffix>.vmt, with the same suffixes and face orientation. The sky name
		// becomes GoldSrc's in the map's asset folder.
		private void ConvertSkybox()
		{
			if (!gs.MipTextures.Any(mipTex => mipTex.name.Equals("sky", StringComparison.OrdinalIgnoreCase)))
				return;

			var worldspawn = sourceBsp.Entities.FirstOrDefault(e => e.ClassName == "worldspawn");
			if (worldspawn == null)
				return;

			var skyName = worldspawn["skyname"].Trim().ToLowerInvariant();
			if (skyName.Length == 0)
				skyName = DefaultSkyName;

			if (skyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				logger.Log($"Warning: Sky name {skyName} isn't a valid file name, skipping the skybox");
				return;
			}

			var images = FindSkyboxImages(skyName);
			var missing = SkyboxSuffixes.Where((suffix, i) => images[i] == null).Select(suffix => skyName + suffix).ToList();
			if (missing.Count > 0)
			{
				// Source falls back to its default sky if any face is missing
				logger.Log($"Warning: Sky images not found (pass the folder of the mod that has them with --wads): {string.Join(", ", missing)}");
				return;
			}

			var sourceSkyName = $"{assetDir}/{skyName}";
			for (var i = 0; i < SkyboxSuffixes.Length; i++)
			{
				if (!materialConverter.ConvertSkyboxFace($"skybox/{sourceSkyName}{SkyboxSuffixes[i]}", images[i]!))
				{
					logger.Log($"Warning: Failed to convert sky image {images[i]}");
					return;
				}
			}

			worldspawn["skyname"] = sourceSkyName;
			logger.Log($"Converted skybox {skyName}");
		}

		// Each face's gfx/env image, from the first search directory that has it. Like the engine, a TGA wins over a
		// BMP (the software renderer's 8-bit version).
		private string?[] FindSkyboxImages(string skyName)
		{
			var enumerationOptions = new EnumerationOptions
			{
				RecurseSubdirectories = true,
				MatchCasing = MatchCasing.CaseInsensitive,
				IgnoreInaccessible = true
			};

			// File name -> path. Earlier directories win.
			var imagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var dir in GetAssetSearchDirs())
			{
				if (!Directory.Exists(dir))
					continue;

				foreach (var path in Directory.EnumerateFiles(dir, skyName + "*", enumerationOptions))
				{
					var envDir = Path.GetDirectoryName(path);
					if (string.Equals(Path.GetFileName(envDir), "env", StringComparison.OrdinalIgnoreCase) &&
						string.Equals(Path.GetFileName(Path.GetDirectoryName(envDir)), "gfx", StringComparison.OrdinalIgnoreCase))
						imagePaths.TryAdd(Path.GetFileName(path), path);
				}
			}

			return SkyboxSuffixes
				.Select(suffix => imagePaths.GetValueOrDefault(skyName + suffix + ".tga") ?? imagePaths.GetValueOrDefault(skyName + suffix + ".bmp"))
				.ToArray();
		}

		// The entity keys that play a sound file, by Source classname
		private static readonly Dictionary<string, string[]> SoundKeys = new Dictionary<string, string[]>
		{
			["ambient_generic"] = new[] { "message" },
			["func_door"] = new[] { "noise1", "noise2", "locked_sound", "unlocked_sound" },
			["func_door_rotating"] = new[] { "noise1", "noise2", "locked_sound", "unlocked_sound" },
			["func_button"] = new[] { "customsound" },
			["func_rot_button"] = new[] { "customsound" },
			["func_train"] = new[] { "noise1", "noise2" },
			["func_rotating"] = new[] { "message" },
		};

		// Finds the files of the sounds the entities play, in the map's archive or the mod's and Half-Life's sound
		// folders, so they can go with the map in its asset folder. Paths are made lowercase with forward slashes, as
		// they're stored. Sounds that aren't found keep their path, which the game may have.
		private void FindSounds()
		{
			var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			var resampled = 0;
			foreach (var entity in sourceBsp.Entities)
			{
				if (!SoundKeys.TryGetValue(entity.ClassName, out var keys))
					continue;

				foreach (var key in keys)
				{
					// Sentences ("!name") are played from the game's sentences, which Momentum doesn't have
					var sound = entity[key].Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
					if (sound.Length == 0 || sound.StartsWith('!'))
						continue;

					var sourceSound = $"{assetDir}/{sound}";
					if (!soundFiles.ContainsKey(sourceSound) && !missing.Contains(sound))
					{
						if (assetFinder.Find("sound/" + sound) is string file)
							soundFiles[sourceSound] = ResampleIfNeeded(sound, file, ref resampled);
						else
							missing.Add(sound);
					}

					entity[key] = soundFiles.ContainsKey(sourceSound) ? sourceSound : sound;
				}
			}

			if (soundFiles.Count + missing.Count > 0)
				logger.Log($"Found {soundFiles.Count}/{soundFiles.Count + missing.Count} sounds");
			if (resampled > 0)
				logger.Log($"Resampled {resampled} sounds to sample rates Source plays");
			if (missing.Count > 0)
				logger.Log($"Warning: Sounds not found (pass the folder of the mod that has them with --wads): {string.Join(", ", missing)}");
		}

		// The file to embed for a sound: a copy resampled under the content directory if Source can't play its sample
		// rate (see GoldSrcWave), or else the sound itself
		private string ResampleIfNeeded(string sound, string file, ref int resampled)
		{
			if (!GoldSrcWave.NeedsResampling(file))
				return file;

			var resampledFile = Path.Combine(contentManager.ContentDir, "_resampled_sounds", sound.Replace('/', Path.DirectorySeparatorChar));
			if (!GoldSrcWave.Resample(file, resampledFile))
				return file;

			resampled++;
			return resampledFile;
		}

		private const int SF_SPRITE_STARTON = 1;

		// infodecals draw a decal from the mod's decals.wad (Half-Life's if the mod has none), by its name in
		// "texture", or failing that one of the map's textures. They become Source decal materials under decals/ in the
		// map's asset folder, which Source's infodecal puts on the surface it's against the same way.
		private void ConvertDecals()
		{
			var decalWad = assetFinder.Find("decals.wad") is string decalWadPath ? Wad3File.Open(decalWadPath) : null;
			var decals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
			var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var entity in sourceBsp.Entities)
			{
				if (entity.ClassName != "infodecal")
					continue;

				var name = entity["texture"].Trim();
				if (!decals.TryGetValue(name, out var material))
				{
					material = ConvertDecal(name, decalWad);
					decals[name] = material;
					if (material == null)
						missing.Add(name);
				}

				if (material != null)
					entity["texture"] = material;
			}

			if (decals.Count > 0)
				logger.Log($"Converted {decals.Count - missing.Count}/{decals.Count} decals");
			if (missing.Count > 0)
				logger.Log($"Warning: Decals not found (pass the folder of the mod that has them with --wads): {string.Join(", ", missing)}");
		}

		private string? ConvertDecal(string name, Wad3File? decalWad)
		{
			if (name.Length == 0)
				return null;

			var fromDecalWad = decalWad != null && decalWad.Contains(name);
			var texture = fromDecalWad ? decalWad!.ReadMipTexture(name) : textureFinder.Find(name);
			if (texture == null)
				return null;

			var material = $"{assetDir}/decals/{SanitizeAssetName(name.TrimStart('{'))}";
			if (!materialConverter.ConvertDecal(material, texture, fromDecalWad))
			{
				logger.Log($"Warning: Failed to convert decal {name}");
				return null;
			}

			return material;
		}

		// The entity keys that name a sprite, by classname
		private static readonly Dictionary<string, string[]> SpriteKeys = new Dictionary<string, string[]>
		{
			["env_sprite"] = new[] { "model" },
			["env_glow"] = new[] { "model" },
			// Beam textures, and the sprite at the end of a laser
			["env_beam"] = new[] { "texture" },
			["env_laser"] = new[] { "texture", "EndSprite" },
		};

		// Sprite entities draw .spr sprites, and beams are textured with them. They become Sprite materials under
		// sprites/ in the map's asset folder, which Source draws sprites and beams with. Sprites are drawn the same way,
		// apart from what's handled here: GoldSrc draws them alpha tested even in the normal rendermode, and takes a
		// black rendercolor (the editors' default) as white.
		private void ConvertSprites()
		{
			var sprites = new Dictionary<string, GoldSrcSprite?>(StringComparer.OrdinalIgnoreCase);
			var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var entity in sourceBsp.Entities)
			{
				if (!SpriteKeys.TryGetValue(entity.ClassName, out var keys))
					continue;

				foreach (var key in keys)
				{
					var model = entity[key].Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
					if (!model.EndsWith(".spr", StringComparison.Ordinal))
						continue;

					if (!sprites.TryGetValue(model, out var sprite))
					{
						sprite = ConvertSprite(model);
						sprites[model] = sprite;
						if (sprite == null)
							missing.Add(model);
					}

					if (sprite == null)
						continue;

					entity[key] = GetSpriteMaterialName(model) + ".vmt";
					if (key == "model")
						ConvertSpriteRendering(entity, sprite);
				}
			}

			if (sprites.Count > 0)
				logger.Log($"Converted {sprites.Count - missing.Count}/{sprites.Count} sprites");
			if (missing.Count > 0)
				logger.Log($"Warning: Sprites not found (pass the folder of the mod that has them with --wads): {string.Join(", ", missing)}");
		}

		private GoldSrcSprite? ConvertSprite(string model)
		{
			if (assetFinder.Find(model) is not string file)
				return null;

			var sprite = GoldSrcSprite.Read(file);
			if (sprite == null)
			{
				logger.Log($"Warning: {file} isn't a sprite this can read");
				return null;
			}

			return materialConverter.ConvertSprite(GetSpriteMaterialName(model), sprite) ? sprite : null;
		}

		// "sprites/glow01.spr" -> "sprites/goldsrc/<map>/glow01"
		private string GetSpriteMaterialName(string model)
		{
			var name = Path.ChangeExtension(model, null);
			if (name.StartsWith("sprites/", StringComparison.Ordinal))
				name = name.Substring("sprites/".Length);

			return $"sprites/{assetDir}/{name}";
		}

		private static void ConvertSpriteRendering(Entity entity, GoldSrcSprite sprite)
		{
			// env_glow is always on in GoldSrc, where Source's starts off when it has a name, like env_sprite
			if (entity.ClassName == "env_glow")
			{
				var flags = int.TryParse(entity["spawnflags"], out var parsedFlags) ? parsedFlags : 0;
				entity["spawnflags"] = (flags | SF_SPRITE_STARTON).ToString(CultureInfo.InvariantCulture);
			}

			// Source's normal rendermode draws the sprite opaque. Alpha blending at full renderamt draws its cutout.
			var renderMode = int.TryParse(entity["rendermode"], out var parsedRenderMode) ? parsedRenderMode : 0;
			if (renderMode == 0 && sprite.TextureFormat is SpriteTextureFormat.AlphaTest or SpriteTextureFormat.IndexAlpha)
			{
				renderMode = RenderTransAlpha;
				entity["rendermode"] = RenderTransAlpha.ToString(CultureInfo.InvariantCulture);
				entity["renderamt"] = "255";
			}

			// GoldSrc doesn't color sprites in the normal rendermode, and draws a black one white
			if (renderMode == 0 || !TryParseVector(entity["rendercolor"], out var color) || color == Vector3.Zero)
				entity["rendercolor"] = "255 255 255";
		}

		// Entities that draw a studio model, which become prop_dynamics
		private static readonly HashSet<string> StudioModelClasses = new HashSet<string> { "cycler_sprite", "cycler", "env_sprite", "item_generic" };

		// Studio models (.mdl) of entities are compiled into Source models (see GoldSrcModelCompiler), and the entities
		// drawing them become non-solid prop_dynamics playing the sequence the way GoldSrc's client does. They're lit
		// like GoldSrc lights them by the leaves' ambient lighting (see GoldSrcModelLighting).
		// cycler_sprite is solid in GoldSrc but has no size (SET_MODEL gives studio models none), which player movement
		// skips (SV_AddLinksToPM), so the props aren't solid. cycler sets a size, so it's a solid box (see CyclerMins).
		// func_trains drawing a model (made with zhlt_usemodel) become props too (see ConvertModelTrain).
		private void ConvertStudioModels()
		{
			var users = new List<(Entity entity, string sourceModel, string sequence)>();
			var models = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
			var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			var movingTrains = 0;
			foreach (var entity in sourceBsp.Entities)
			{
				var isTrain = entity.ClassName == "func_train";
				if (!StudioModelClasses.Contains(entity.ClassName) && !isTrain)
					continue;

				var modelPath = entity["model"].Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
				if (!modelPath.EndsWith(".mdl", StringComparison.Ordinal))
					continue;

				if (modelCompiler == null && !CreateModelCompiler())
					return;

				if (!models.TryGetValue(modelPath, out var sourceModel))
				{
					sourceModel = AddStudioModel(modelPath, missing);
					models[modelPath] = sourceModel;
				}

				if (sourceModel == null)
					continue;

				if (entity.ClassName == "cycler")
					modelCompiler!.SetCollisionBox(sourceModel, CyclerMins, CyclerMaxs);

				var (sequence, framerate) = GetStudioModelSequence(entity);
				var spinSpeed = isTrain ? GetTrainSpinSpeed(entity) : 0f;
				users.Add((entity, sourceModel, spinSpeed != 0f ?
					modelCompiler!.GetSpinningSequence(sourceModel, sequence, spinSpeed) :
					modelCompiler!.GetSequence(sourceModel, sequence, framerate)));
			}

			if (models.Count == 0)
				return;

			var compiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var sourceModel in users.Select(user => user.sourceModel).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				if (modelCompiler!.Compile(sourceModel))
					compiled.Add(sourceModel);
			}

			foreach (var (entity, sourceModel, sequence) in users)
			{
				if (!compiled.Contains(sourceModel))
					continue;

				if (entity.ClassName == "func_train" && !ConvertModelTrain(entity))
					movingTrains++;

				ConvertStudioModelEntity(entity, sourceModel, sequence);
			}

			logger.Log($"Converted {compiled.Count}/{models.Count} models");
			if (movingTrains > 0)
				logger.Log($"Warning: {movingTrains} func_trains drawing a model stay at the start of their paths");
			if (missing.Count > 0)
				logger.Log($"Warning: Models not found (pass the folder of the mod that has them with --wads): {string.Join(", ", missing)}");
		}

		private bool CreateModelCompiler()
		{
			var studiomdl = FindStudiomdl();
			if (studiomdl == null)
			{
				logger.Log("Warning: Models aren't converted because studiomdl.exe wasn't found. Pass the one in Momentum Mod's bin/win64 folder with --studiomdl.");
				return false;
			}

			modelWorkDir = Path.Combine(Path.GetTempPath(), "BSPConvert_models_" + Path.GetRandomFileName());
			modelCompiler = new GoldSrcModelCompiler(studiomdl, modelWorkDir, assetDir, materialConverter, logger);
			return true;
		}

		// The given studiomdl, or the one of the game the output folder is in, or of a default Steam Momentum Mod install
		private string? FindStudiomdl()
		{
			if (!string.IsNullOrEmpty(options.goldSrc.studiomdlPath))
				return File.Exists(options.goldSrc.studiomdlPath) ? options.goldSrc.studiomdlPath : null;

			var candidates = new List<string>();
			if (Path.GetDirectoryName(Path.GetFullPath(options.outputDir)) is string gameDir)
				candidates.Add(Path.Combine(gameDir, "bin", "win64", "studiomdl.exe"));
			candidates.Add(Path.Combine(DefaultMomentumDir, "bin", "win64", "studiomdl.exe"));

			return candidates.FirstOrDefault(File.Exists);
		}

		// Reads a model and adds it to the compiler, returning its path in the game, or null if it can't be read
		private string? AddStudioModel(string modelPath, ISet<string> missing)
		{
			if (assetFinder.Find(modelPath) is not string file)
			{
				missing.Add(modelPath);
				return null;
			}

			// Its texture and sequence group files are next to it, or elsewhere in the mod
			var modelDir = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? "";
			string? FindModelFile(string fileName)
			{
				var nextTo = Path.Combine(Path.GetDirectoryName(file)!, fileName);
				return File.Exists(nextTo) ? nextTo : assetFinder.Find(modelDir.Length > 0 ? modelDir + "/" + fileName : fileName);
			}

			var model = GoldSrcModel.Read(file, FindModelFile, out var error);
			if (model == null)
			{
				logger.Log($"Warning: Couldn't read {modelPath}: {error}");
				return null;
			}

			return modelCompiler!.AddModel(modelPath, model);
		}

		// The sequence an entity plays and its frame rate. cycler_sprite and env_sprite play theirs at their framerate,
		// which they don't change. cycler animates itself at the normal rate, but only if it plays sequence 0.
		private static (int sequence, float framerate) GetStudioModelSequence(Entity entity)
		{
			var sequence = int.TryParse(entity["sequence"], out var parsedSequence) ? parsedSequence : 0;
			var framerate = float.TryParse(entity["framerate"], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFramerate) ? parsedFramerate : 0f;
			switch (entity.ClassName)
			{
				case "cycler":
					framerate = sequence == 0 ? 1f : 0f;
					break;
				case "item_generic":
					framerate = 1f;
					break;
			}

			return (sequence, framerate);
		}

		// How fast a func_train drawing a model spins about its z axis in degrees per second: its avelocity, which GoldSrc
		// turns it by as it moves (SV_Physics_Pusher). Models are spun in their sequence (see GetSpinningSequence), which
		// only turns about the model's own z axis, so it's only the yaw of a train that's upright.
		// TODO: Spinning about other axes
		private static float GetTrainSpinSpeed(Entity entity)
		{
			if (!TryParseVector(entity["avelocity"], out var angularVelocity) || angularVelocity.X != 0f || angularVelocity.Z != 0f)
				return 0f;

			var angles = TryParseVector(entity["angles"], out var parsedAngles) ? parsedAngles : Vector3.Zero;
			return MathF.Abs(angles.X) < 0.01f && MathF.Abs(angles.Z) < 0.01f ? angularVelocity.Y : 0f;
		}

		// A func_train drawing a model is placed where GoldSrc moves it when it starts, its first path_corner (with its
		// origin there, since studio models have no size), and becomes a prop like the other model entities. It's the
		// way GoldSrc maps spin a model (with a path a fraction of a unit long), and Source's func_train can't draw one.
		// Returns false if its path goes anywhere, which the prop doesn't follow.
		// TODO: Trains moving a model along their path
		private bool ConvertModelTrain(Entity train)
		{
			var corner = sourceBsp.Entities.FirstOrDefault(entity => entity.ClassName == "path_corner" &&
				!string.IsNullOrEmpty(train["target"]) && entity["targetname"] == train["target"]);
			var stays = true;
			if (corner != null)
			{
				train["origin"] = corner["origin"];

				// Follow the path to see whether it goes anywhere
				var start = TryParseVector(corner["origin"], out var startOrigin) ? startOrigin : Vector3.Zero;
				var visited = new HashSet<Entity>();
				for (var next = corner; next != null && visited.Add(next);)
				{
					if (TryParseVector(next["origin"], out var origin) && Vector3.Distance(origin, start) > 1f)
						stays = false;

					next = sourceBsp.Entities.FirstOrDefault(entity => entity.ClassName == "path_corner" &&
						!string.IsNullOrEmpty(next["target"]) && entity["targetname"] == next["target"]);
				}
			}

			foreach (var key in new[] { "target", "speed", "avelocity", "dmg", "noise1", "noise2", "volume", "zhlt_usemodel" })
				train.Remove(key);

			return stays;
		}

		// The box a cycler is, whatever its model (GenericCyclerSpawn). It becomes a prop colliding as a box
		// (SOLID_BBOX), which is its model's collision box (see SetCollisionBox), and like GoldSrc's ignores its angles.
		private static readonly Vector3 CyclerMins = new Vector3(-16f, -16f, 0f);
		private static readonly Vector3 CyclerMaxs = new Vector3(16f, 16f, 72f);
		private const string SolidBbox = "2";

		private void ConvertStudioModelEntity(Entity entity, string sourceModel, string sequence)
		{
			var isCycler = entity.ClassName == "cycler";

			// An env_sprite with a name starts off unless it's flagged to start on, like a sprite
			var startsOff = entity.ClassName == "env_sprite" && !string.IsNullOrEmpty(entity["targetname"]) &&
				((int.TryParse(entity["spawnflags"], out var flags) ? flags : 0) & SF_SPRITE_STARTON) == 0;

			entity.ClassName = "prop_dynamic";
			entity["model"] = sourceModel;
			entity["DefaultAnim"] = sequence;
			entity["solid"] = isCycler ? SolidBbox : "0";
			if (startsOff)
				entity["StartDisabled"] = "1";

			// GoldSrc draws studio models with their pitch flipped (StudioSetUpTransform)
			var angles = TryParseVector(entity["angles"], out var parsedAngles) ? parsedAngles : Vector3.Zero;
			if (!TryParseVector(entity["angles"], out _) && float.TryParse(entity["angle"], NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw))
				angles.Y = yaw;
			entity["angles"] = string.Create(CultureInfo.InvariantCulture, $"{(angles.X == 0f ? 0f : -angles.X):0.###} {angles.Y:0.###} {angles.Z:0.###}");

			foreach (var key in new[] { "sequence", "sequencename", "framerate", "spawnflags", "angle" })
				entity.Remove(key);

			// Source tints models by their rendercolor, which GoldSrc doesn't, and editors default it to black
			entity.Remove("rendercolor");

			// GoldSrc blends models in any rendermode but the normal and additive ones by renderamt. Fully opaque ones
			// are drawn normally, so they aren't sorted with translucent objects.
			var renderMode = int.TryParse(entity["rendermode"], out var parsedRenderMode) ? parsedRenderMode : 0;
			var renderAmt = int.TryParse(entity["renderamt"], out var parsedRenderAmt) ? parsedRenderAmt : 0;
			if (renderMode is RenderTransColor or RenderTransTexture or RenderTransAlpha)
				entity["rendermode"] = renderAmt >= 255 ? "0" : RenderTransTexture.ToString(CultureInfo.InvariantCulture);
		}

		// The mod the map is for: the folder above its maps folder (without a _downloads or similar suffix), or
		// Counter-Strike's when the map isn't in one, since that's what KZ and bhop maps are for
		private string GetModName()
		{
			var inputDir = Path.GetDirectoryName(Path.GetFullPath(options.inputFile));
			if (inputDir == null || !Path.GetFileName(inputDir).Equals("maps", StringComparison.OrdinalIgnoreCase) ||
				Path.GetFileName(Path.GetDirectoryName(inputDir)) is not string modDir || modDir.Length == 0)
				return "cstrike";

			var suffixIndex = modDir.IndexOf('_', StringComparison.Ordinal);
			return suffixIndex > 0 ? modDir.Substring(0, suffixIndex) : modDir;
		}

		// Embeds the converted materials, sounds and models in the BSP, or with --nopak puts them in the output's
		// materials, sound and models folders
		private void WriteContent()
		{
			var materialFiles = materialConverter.WrittenFiles;
			var modelFiles = modelCompiler?.CompiledFiles ?? Array.Empty<(string, string)>();
			if (options.noPak)
			{
				foreach (var file in materialFiles)
					FileUtil.MoveFile(file, Path.Combine(options.outputDir, "materials", Path.GetRelativePath(contentManager.ContentDir, file)));
				foreach (var (sound, file) in soundFiles)
					FileUtil.CopyFile(file, Path.Combine(options.outputDir, "sound", sound.Replace('/', Path.DirectorySeparatorChar)));
				foreach (var (modelPath, file) in modelFiles)
					FileUtil.CopyFile(file, Path.Combine(options.outputDir, modelPath.Replace('/', Path.DirectorySeparatorChar)));
				return;
			}

			builder.CreatePakFile();
			using var archive = sourceBsp.PakFile.GetZipArchive();
			foreach (var file in materialFiles)
				archive.AddEntry("materials/" + Path.GetRelativePath(contentManager.ContentDir, file).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(file));
			foreach (var (sound, file) in soundFiles)
				archive.AddEntry("sound/" + sound, new FileInfo(file));
			foreach (var (modelPath, file) in modelFiles)
				archive.AddEntry(modelPath, new FileInfo(file));
			sourceBsp.PakFile.SetZipArchive(archive, true);
		}

		private const string NoDrawMaterial = "tools/toolsnodraw";

		// Compiler tool textures, drawn with the game's tool materials instead of being converted
		private static readonly Dictionary<string, string> ToolMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["sky"] = "tools/toolsskybox",
			["aaatrigger"] = "tools/toolstrigger",
			["clip"] = "tools/toolsplayerclip",
			["null"] = NoDrawMaterial,
			["bevel"] = NoDrawMaterial,
			["skip"] = "tools/toolsskip",
			["hint"] = "tools/toolshint",
			["origin"] = "tools/toolsorigin",
		};

		private static bool IsToolTexture(string mipTexName)
		{
			return ToolMaterials.ContainsKey(mipTexName);
		}

		private string GetMaterialName(string mipTexName)
		{
			if (ToolMaterials.TryGetValue(mipTexName, out var toolMaterial))
				return toolMaterial;

			return $"{assetDir}/{SanitizeAssetName(mipTexName)}";
		}

		// A name as a lowercase file name
		private static string SanitizeAssetName(string name)
		{
			name = name.ToLowerInvariant();
			foreach (var c in Path.GetInvalidFileNameChars())
				name = name.Replace(c, '_');

			return name;
		}

		private static int GetSurfaceFlags(GoldSrcBsp.TexInfo texInfo, string mipTexName)
		{
			var flags = 0;
			if ((texInfo.flags & GoldSrcBsp.TEX_SPECIAL) != 0)
				flags |= (int)SourceSurfaceFlags.SURF_NOLIGHT;

			if (mipTexName.Equals("sky", StringComparison.OrdinalIgnoreCase))
				flags |= (int)(SourceSurfaceFlags.SURF_SKY | SourceSurfaceFlags.SURF_NOLIGHT | SourceSurfaceFlags.SURF_SKYNOEMIT);
			else if (IsToolTexture(mipTexName))
				flags |= (int)(SourceSurfaceFlags.SURF_NODRAW | SourceSurfaceFlags.SURF_NOLIGHT);

			return flags;
		}

		private void ConvertVertices()
		{
			foreach (var position in gs.Vertices)
				sourceBsp.Vertices.Add(new Vertex { position = position });

			foreach (var (v0, v1) in gs.Edges)
			{
				var edge = new Edge(new byte[Edge.GetStructLength(sourceBsp.MapType)], sourceBsp.Edges);
				edge.FirstVertexIndex = v0;
				edge.SecondVertexIndex = v1;
				sourceBsp.Edges.Add(edge);
			}

			foreach (var surfEdge in gs.SurfEdges)
				sourceBsp.FaceEdges.Add(surfEdge);
		}

		private void ConvertFaces()
		{
			// Brush model (0 = world) each face belongs to
			var faceModels = new int[gs.Faces.Length];
			for (var modelIndex = 1; modelIndex < gs.Models.Length; modelIndex++)
			{
				var model = gs.Models[modelIndex];
				for (var i = model.firstFace; i < model.firstFace + model.numFaces && i < faceModels.Length; i++)
					faceModels[i] = modelIndex;
			}

			facesBefore = new int[gs.Faces.Length + 1];
			var keptFaces = new bool[gs.Faces.Length];
			for (var i = 0; i < gs.Faces.Length; i++)
			{
				facesBefore[i] = sourceBsp.Faces.Count;
				var gsFace = gs.Faces[i];
				var texInfo = gs.TexInfos[gsFace.texInfo];

				// Like VBSP, drop faces that shouldn't draw: the engine draws every face it's given, nodraw or not
				var texInfoIndex = GetFaceTexInfo(gsFace, faceModels[i]);
				if ((sourceBsp.TextureInfo[texInfoIndex].Flags & (int)SourceSurfaceFlags.SURF_NODRAW) != 0)
					continue;

				keptFaces[i] = true;
				var face = builder.AddFace();

				face.PlaneIndex = gsFace.planeIndex;
				face.PlaneSide = gsFace.planeSide;
				// GoldSrc faces all lie on nodes (marksurfaces reference them per leaf for visibility only)
				face.IsOnNode = true;
				face.FirstEdgeIndexIndex = gsFace.firstEdge;
				face.NumEdgeIndices = gsFace.numEdges;
				face.TextureInfoIndex = texInfoIndex;
				face.DisplacementIndex = -1;
				face.Area = (float)GetFaceArea(gsFace);

				// hlrad gives faces without lightmap styles the offset the lighting data had reached, which is past the
				// end when they come last. The engine ignores it, but Source rejects any offset outside the data.
				if ((texInfo.flags & GoldSrcBsp.TEX_SPECIAL) != 0 || gsFace.lightOffset < 0 || gsFace.lightOffset >= gs.Lighting.Length || gsFace.styles[0] == 255)
				{
					face.Lightmap = -1;
					face.LightmapStyles = new byte[] { 255, 255, 255, 255 };
				}
				else
				{
					// Its lightmap is placed in Source's lighting data by ConvertLighting
					face.LightmapStyles = gsFace.styles;

					var (mins, size) = GetLightmapExtents(gsFace, texInfo);
					face.LightmapStart = mins;
					face.LightmapSize = size;
					var styleCount = gsFace.styles.TakeWhile(style => style != 255).Count();
					litFaces.Add((face, gsFace.lightOffset, ((int)size.X + 1) * ((int)size.Y + 1), styleCount));
				}

				// Vertex normals: one per face, indexed once per face vertex (the engine walks this list by edge count)
				var normal = gs.Planes[gsFace.planeIndex].normal;
				var normalIndex = sourceBsp.Normals.Count;
				sourceBsp.Normals.Add(gsFace.planeSide ? -normal : normal);
				for (var j = 0; j < gsFace.numEdges; j++)
					sourceBsp.Indices.Add(normalIndex);
			}
			facesBefore[gs.Faces.Length] = sourceBsp.Faces.Count;

			// Leaves list the faces in them through marksurfaces, which only keep the converted faces
			markSurfacesBefore = new int[gs.MarkSurfaces.Length + 1];
			for (var i = 0; i < gs.MarkSurfaces.Length; i++)
			{
				markSurfacesBefore[i] = sourceBsp.LeafFaces.Count;
				var faceIndex = gs.MarkSurfaces[i];
				if (faceIndex >= 0 && faceIndex < keptFaces.Length && keptFaces[faceIndex])
					sourceBsp.LeafFaces.Add(facesBefore[faceIndex]);
			}
			markSurfacesBefore[gs.MarkSurfaces.Length] = sourceBsp.LeafFaces.Count;
		}

		// The converted faces in a range of GoldSrc faces (nodes and models list theirs as one range). Dropping faces
		// keeps the rest in order, so the range stays contiguous.
		private (int first, int count) RemapFaceRange(int first, int count)
		{
			var start = Math.Clamp(first, 0, gs.Faces.Length);
			var end = Math.Clamp(first + count, start, gs.Faces.Length);
			return (facesBefore[start], facesBefore[end] - facesBefore[start]);
		}

		private (int first, int count) RemapMarkSurfaceRange(int first, int count)
		{
			var start = Math.Clamp(first, 0, gs.MarkSurfaces.Length);
			var end = Math.Clamp(first + count, start, gs.MarkSurfaces.Length);
			return (markSurfacesBefore[start], markSurfacesBefore[end] - markSurfacesBefore[start]);
		}

		private Vector3 GetFaceVertex(int surfEdgeIndex)
		{
			var surfEdge = gs.SurfEdges[surfEdgeIndex];
			var edge = gs.Edges[Math.Abs(surfEdge)];
			return gs.Vertices[surfEdge >= 0 ? edge.v0 : edge.v1];
		}

		// Matches GoldSrc's CalcSurfaceExtents: luxel mins and size (luxel count - 1) along each texture axis. The
		// lighting data is laid out by these sizes, so they have to match the compiler's exactly (see
		// GetTexCoordForExtents).
		private (Vector2 mins, Vector2 size) GetLightmapExtents(GoldSrcBsp.Face face, GoldSrcBsp.TexInfo texInfo)
		{
			var minS = double.MaxValue;
			var minT = double.MaxValue;
			var maxS = double.MinValue;
			var maxT = double.MinValue;
			for (var i = 0; i < face.numEdges; i++)
			{
				var v = GetFaceVertex(face.firstEdge + i);
				var s = GetTexCoordForExtents(v, texInfo.s);
				var t = GetTexCoordForExtents(v, texInfo.t);
				minS = Math.Min(minS, s);
				minT = Math.Min(minT, t);
				maxS = Math.Max(maxS, s);
				maxT = Math.Max(maxT, t);
			}

			var lumMinS = Math.Floor(minS / LightmapLuxelSize);
			var lumMinT = Math.Floor(minT / LightmapLuxelSize);
			var lumMaxS = Math.Ceiling(maxS / LightmapLuxelSize);
			var lumMaxT = Math.Ceiling(maxT / LightmapLuxelSize);

			return (new Vector2((float)lumMinS, (float)lumMinT), new Vector2((float)(lumMaxS - lumMinS), (float)(lumMaxT - lumMinT)));
		}

		// A vertex's texture coordinate as the engine computes it for lightmap extents: the engine was built with x87
		// math, which keeps the products and sums in double before storing the result as a float, and the compilers
		// copy that (VHLT's CalculatePointVecsProduct). Computing it all in float gets a luxel more or less on some
		// faces, which shifts the lightmaps after them.
		public static double GetTexCoordForExtents(Vector3 v, Vector4 axis)
		{
			return (float)((double)v.X * axis.X + (double)v.Y * axis.Y + (double)v.Z * axis.Z + axis.W);
		}

		private double GetFaceArea(GoldSrcBsp.Face face)
		{
			if (face.numEdges < 3)
				return 0;

			var v0 = GetFaceVertex(face.firstEdge);
			var total = Vector3.Zero;
			for (var i = 2; i < face.numEdges; i++)
			{
				var v1 = GetFaceVertex(face.firstEdge + i - 1);
				var v2 = GetFaceVertex(face.firstEdge + i);
				total += Vector3.Cross(v1 - v0, v2 - v0);
			}

			return total.Length() * 0.5;
		}

		// Source stores 4 bytes per luxel (ColorRGBExp32) where GoldSrc stores 3 (RGB), and puts the average color of
		// each of a face's light styles right before its lightmap, last style first (VRAD's AddSampleToLightmap
		// layout). The engine reads those for the light under a point (R_LightVec: particles, blood, smoke and the
		// light cache), so each lightmap is copied with its averages in front.
		private void ConvertLighting()
		{
			var colors = new List<ColorRGBExp32>(gs.Lighting.Length / 3 + litFaces.Count);
			var sourceOffsets = new Dictionary<int, int>();
			foreach (var (litFace, lightOffset, luxels, styleCount) in litFaces)
			{
				// Faces share their data with the BSP's copy
				var face = litFace;
				if (!sourceOffsets.TryGetValue(lightOffset, out var sourceOffset))
				{
					// A lightmap cut short by the end of the data is padded with black, so the engine doesn't read past it
					var lightmap = new byte[luxels * styleCount * 3];
					gs.Lighting.AsSpan(lightOffset, Math.Min(lightmap.Length, gs.Lighting.Length - lightOffset)).CopyTo(lightmap);
					for (var style = styleCount - 1; style >= 0; style--)
						colors.Add(ColorUtil.AverageGoldSrcLightmapColor(lightmap.AsSpan(style * luxels * 3, luxels * 3)));

					sourceOffset = colors.Count * 4;
					for (var i = 0; i < lightmap.Length; i += 3)
						colors.Add(ColorUtil.ConvertGoldSrcLightmapToColorRGBExp32(lightmap[i], lightmap[i + 1], lightmap[i + 2]));

					sourceOffsets[lightOffset] = sourceOffset;
				}

				face.Lightmap = sourceOffset;
			}

			builder.SetLightmaps(colors);
		}

		// Nodes are copied index for index. Their leaf children are filled in by ConvertHull0, since GoldSrc
		// leaves don't map 1:1 to Source leaves.
		private void ConvertNodes()
		{
			foreach (var gsNode in gs.Nodes)
			{
				var node = new Node(new byte[Node.GetStructLength(sourceBsp.MapType)], sourceBsp.Nodes);
				node.PlaneIndex = gsNode.planeIndex;
				node.Child1Index = gsNode.child0;
				node.Child2Index = gsNode.child1;
				node.Minimums = gsNode.mins;
				node.Maximums = gsNode.maxs;
				(node.FirstFaceIndex, node.NumFaceIndices) = RemapFaceRange(gsNode.firstFace, gsNode.numFaces);
				node.AreaIndex = 0;
				sourceBsp.Nodes.Add(node);
			}
		}

		#region Hull 0 (visible geometry)

		// Walks each model's hull 0 tree, creating the Source leaves and a brush for every non-empty leaf.
		// Every solid region in a GoldSrc tree points at the shared solid leaf 0, but each needs its own Source
		// leaf to hold its own brush, so solid regions get a new leaf per reference.
		private void ConvertHull0()
		{
			sourceLeafForGoldSrcLeaf = Enumerable.Repeat(-1, gs.Leaves.Length).ToArray();
			builder.SetLumpVersion(Leaf.GetIndexForLump(sourceBsp.MapType), builder.OldBSP ? 1 : 2);

			// Source leaf 0 is the shared solid leaf, like GoldSrc's
			sourceLeafForGoldSrcLeaf[0] = AddLeaf(gs.Leaves[0], -1);

			var hull0Brushes = 0;
			for (var modelIndex = 0; modelIndex < gs.Models.Length; modelIndex++)
			{
				var model = gs.Models[modelIndex];
				var headNode = model.headNodes[0];
				if (headNode < 0)
					continue;

				var path = GoldSrcClipHull.GetBoundsHalfSpaces(model.mins, model.maxs, BoundsPadding);
				hull0Brushes += WalkHull0(headNode, path, modelIndex);
			}

			logger.Log($"Converted {hull0Brushes} hull 0 brushes");
		}

		private int WalkHull0(int nodeIndex, List<HalfSpace> path, int modelIndex)
		{
			var gsNode = gs.Nodes[nodeIndex];
			var plane = gs.Planes[gsNode.planeIndex];
			var brushCount = 0;

			for (var side = 0; side < 2; side++)
			{
				// Front child (0) is Dot(normal, p) >= dist, so its inside half-space is the flipped plane
				var halfSpace = side == 0 ? new HalfSpace(-plane.normal, -plane.dist) : new HalfSpace(plane.normal, plane.dist);
				path.Add(halfSpace);

				var child = side == 0 ? gsNode.child0 : gsNode.child1;
				if (child >= 0)
				{
					brushCount += WalkHull0(child, path, modelIndex);
				}
				else
				{
					var gsLeafIndex = -child - 1;
					var sourceLeafIndex = GetOrCreateSourceLeaf(gsLeafIndex, modelIndex);
					SetNodeChild(nodeIndex, side, -sourceLeafIndex - 1);

					if (gs.Leaves[gsLeafIndex].contents != GoldSrcBsp.CONTENTS_EMPTY && AddHull0Brush(path, gsLeafIndex, sourceLeafIndex, modelIndex))
						brushCount++;
				}

				path.RemoveAt(path.Count - 1);
			}

			return brushCount;
		}

		private int GetOrCreateSourceLeaf(int gsLeafIndex, int modelIndex)
		{
			// The shared solid leaf gets a new Source leaf for every region that references it
			if (gsLeafIndex == 0)
				return AddLeaf(gs.Leaves[0], -1);

			if (sourceLeafForGoldSrcLeaf[gsLeafIndex] < 0)
			{
				// Only the world's leaves 1..visLeafs have visibility data, and leaf N is cluster N - 1
				var cluster = modelIndex == 0 && gsLeafIndex <= gs.Models[0].visLeafs ? gsLeafIndex - 1 : -1;
				sourceLeafForGoldSrcLeaf[gsLeafIndex] = AddLeaf(gs.Leaves[gsLeafIndex], cluster);
			}

			return sourceLeafForGoldSrcLeaf[gsLeafIndex];
		}

		private int AddLeaf(GoldSrcBsp.Leaf gsLeaf, int cluster)
		{
			var leaf = new Leaf(new byte[Leaf.GetStructLength(sourceBsp.MapType)], sourceBsp.Leaves);
			leaf.Contents = GetSourceContents(gsLeaf.contents);
			leaf.Visibility = cluster;
			leaf.Area = 0;
			leaf.Flags = cluster >= 0 ? (int)(LeafFlags.RADIAL | LeafFlags.SKY2D) : 0;
			leaf.Minimums = gsLeaf.mins;
			leaf.Maximums = gsLeaf.maxs;
			var (firstMarkFace, numMarkFaces) = RemapMarkSurfaceRange(gsLeaf.firstMarkSurface, gsLeaf.numMarkSurfaces);
			leaf.FirstMarkFaceIndex = firstMarkFace;
			leaf.NumMarkFaceIndices = gsLeaf.contents == GoldSrcBsp.CONTENTS_SOLID ? 0 : numMarkFaces;
			leaf.FirstMarkBrushIndex = 0;
			leaf.NumMarkBrushIndices = 0;
			leaf.LeafWaterDataID = -1;
			sourceBsp.Leaves.Add(leaf);

			return sourceBsp.Leaves.Count - 1;
		}

		private void SetNodeChild(int nodeIndex, int side, int child)
		{
			var node = sourceBsp.Nodes[nodeIndex];
			if (side == 0)
				node.Child1Index = child;
			else
				node.Child2Index = child;
		}

		private bool AddHull0Brush(List<HalfSpace> path, int gsLeafIndex, int sourceLeafIndex, int modelIndex)
		{
			var region = ConvexRegion.Create(path);
			if (region == null)
				return false;

			var firstSide = sourceBsp.BrushSides.Count;
			foreach (var face in region.Faces)
				builder.AddBrushSide(builder.AddPlane(face.Normal, face.Dist), -1);

			// Axial bevels, so box traces don't catch on the corners of sloped sides (point traces skip them)
			var numSides = region.Faces.Count;
			numSides += AddAxialBevels(region);

			var isVolume = volumeModelContents.TryGetValue(modelIndex, out var volumeContents);
			var isLadder = ladderModels.Contains(modelIndex);
			var contents = GetSourceContents(isVolume ? volumeContents : gs.Leaves[gsLeafIndex].contents);
			if (isLadder)
				contents |= (int)SourceContentsFlags.CONTENTS_LADDER;

			var brushIndex = builder.AddBrush(firstSide, numSides, contents);
			if (isVolume || isLadder)
				volumeBrushRegions[brushIndex] = region;

			// A solid region's Source leaf is its own, so it can take the region's real bounds
			var leaf = sourceBsp.Leaves[sourceLeafIndex];
			if (gsLeafIndex == 0)
			{
				leaf.Minimums = region.Mins;
				leaf.Maximums = region.Maxs;
			}

			AddLeafBrush(sourceLeafIndex, brushIndex);
			modelBrushes[modelIndex].Add(brushIndex);
			return true;
		}

		// World point contents and traces only see brushes listed in world leaves, so list the brushes of water and
		// ladder entities in the world leaves they overlap
		private void AddVolumeBrushesToWorld()
		{
			var worldHeadNode = gs.Models[0].headNodes[0];
			foreach (var (brushIndex, region) in volumeBrushRegions)
				RegisterInHull0Leaves(worldHeadNode, region, brushIndex);

			if (volumeModelContents.Count > 0)
				logger.Log($"Converted {volumeModelContents.Count} water entities into static world water brushes");
			if (ladderModels.Count > 0)
				logger.Log($"Converted {ladderModels.Count} ladder entities into world ladder brushes");
		}

		private int AddAxialBevels(ConvexRegion region)
		{
			var added = 0;
			for (var axis = 0; axis < 3; axis++)
			{
				for (var sign = -1; sign <= 1; sign += 2)
				{
					var normal = Vector3.Zero;
					normal[axis] = sign;
					var dist = sign > 0 ? region.Maxs[axis] : -region.Mins[axis];

					if (region.Faces.Any(x => x.Normal == normal))
						continue;

					builder.AddBrushSide(builder.AddPlane(normal, dist), -1, isBevel: true);
					added++;
				}
			}

			return added;
		}

		private static int GetSourceContents(int gsContents)
		{
			switch (gsContents)
			{
				case GoldSrcBsp.CONTENTS_EMPTY:
					return (int)SourceContentsFlags.CONTENTS_EMPTY;
				case GoldSrcBsp.CONTENTS_WATER:
				case GoldSrcBsp.CONTENTS_LAVA:
					return (int)SourceContentsFlags.CONTENTS_WATER;
				case GoldSrcBsp.CONTENTS_SLIME:
					return (int)SourceContentsFlags.CONTENTS_SLIME;
				case GoldSrcBsp.CONTENTS_LADDER:
					return (int)SourceContentsFlags.CONTENTS_LADDER;
				case GoldSrcBsp.CONTENTS_TRANSLUCENT:
					return (int)SourceContentsFlags.CONTENTS_EMPTY;
				default:
					if (gsContents <= GoldSrcBsp.CONTENTS_CURRENT_0 && gsContents >= GoldSrcBsp.CONTENTS_CURRENT_DOWN)
						return (int)SourceContentsFlags.CONTENTS_WATER;

					// Solid, sky and anything unknown
					return (int)SourceContentsFlags.CONTENTS_SOLID;
			}
		}

		#endregion

		#region Clip hulls (player collision)

		private void ConvertClipHull(int hull, Vector3 hullExtents, SourceContentsFlags contents)
		{
			var brushCount = 0;
			for (var modelIndex = 0; modelIndex < gs.Models.Length; modelIndex++)
			{
				foreach (var brush in GoldSrcClipHull.GetBrushes(gs, modelIndex, hull, hullExtents))
				{
					AddClipHullBrush(brush, modelIndex, contents);
					brushCount++;
				}
			}

			logger.Log($"Converted {brushCount} hull {hull} brushes");
		}

		private void AddClipHullBrush(GoldSrcClipHull.Brush brush, int modelIndex, SourceContentsFlags contents)
		{
			var firstSide = sourceBsp.BrushSides.Count;
			foreach (var side in brush.Sides)
				builder.AddBrushSide(builder.AddPlane(side.Normal, side.Dist), -1);

			// Ladders are world brushes (see ConvertLadder)
			var isLadder = ladderModels.Contains(modelIndex);
			var brushContents = (int)contents | (isLadder ? (int)SourceContentsFlags.CONTENTS_LADDER : 0);
			var brushIndex = builder.AddBrush(firstSide, brush.Sides.Count, brushContents);
			modelBrushes[modelIndex].Add(brushIndex);

			// A box trace only tests the brushes of leaves it passes through, and its center is somewhere inside
			// the clip hull region whenever it collides with this brush, so list the brush in every hull 0 leaf
			// the region overlaps.
			var headNode = gs.Models[isLadder ? 0 : modelIndex].headNodes[0];
			if (headNode >= 0)
				RegisterInHull0Leaves(headNode, brush.Region, brushIndex);
		}

		private void RegisterInHull0Leaves(int nodeIndex, ConvexRegion region, int brushIndex)
		{
			var node = sourceBsp.Nodes[nodeIndex];
			var plane = gs.Planes[gs.Nodes[nodeIndex].planeIndex];
			var (front, back) = region.Classify(plane.normal, plane.dist, RegistrationEpsilon);
			if (!front && !back)
				front = back = true; // flat against the plane

			if (front)
				RegisterInChild(node.Child1Index, region, brushIndex);
			if (back)
				RegisterInChild(node.Child2Index, region, brushIndex);
		}

		private void RegisterInChild(int child, ConvexRegion region, int brushIndex)
		{
			if (child >= 0)
				RegisterInHull0Leaves(child, region, brushIndex);
			else
				AddLeafBrush(-child - 1, brushIndex);
		}

		#endregion

		private void AddLeafBrush(int leafIndex, int brushIndex)
		{
			if (!leafBrushes.TryGetValue(leafIndex, out var brushes))
			{
				brushes = new List<int>();
				leafBrushes[leafIndex] = brushes;
			}

			brushes.Add(brushIndex);
		}

		private void WriteLeafBrushes()
		{
			for (var leafIndex = 0; leafIndex < sourceBsp.Leaves.Count; leafIndex++)
			{
				var leaf = sourceBsp.Leaves[leafIndex];
				leaf.FirstMarkBrushIndex = sourceBsp.LeafBrushes.Count;

				if (leafBrushes.TryGetValue(leafIndex, out var brushes))
				{
					foreach (var brushIndex in brushes)
						sourceBsp.LeafBrushes.Add(brushIndex);
				}

				leaf.NumMarkBrushIndices = brushes?.Count ?? 0;
			}
		}

		private void ConvertModels()
		{
			for (var i = 0; i < gs.Models.Length; i++)
			{
				var gsModel = gs.Models[i];
				var model = new Model(new byte[Model.GetStructLength(sourceBsp.MapType)], sourceBsp.Models);
				model.HeadNodeIndex = gsModel.headNodes[0];
				model.Minimums = gsModel.mins;
				model.Maximums = gsModel.maxs;
				model.Origin = gsModel.origin;
				(model.FirstFaceIndex, model.NumFaces) = RemapFaceRange(gsModel.firstFace, gsModel.numFaces);

				builder.SetModelBrushes(sourceBsp.Models.Count, modelBrushes[i]);
				sourceBsp.Models.Add(model);
			}
		}

		// Gives the func_precipitation the map's weather became (see GoldSrcEntityConverter.ConvertWeather) a box brush
		// model around the whole world, and stops it under cover with func_precipitation_blockers (see GoldSrcSkyCover).
		// They're added before the leaves' ambient lighting is written, as each brush model adds a leaf.
		private void AddPrecipitationVolume()
		{
			if (precipitation == null)
				return;

			var world = gs.Models[0];
			precipitation["model"] = "*" + builder.AddBoxTriggerModel(world.mins, world.maxs).ToString(CultureInfo.InvariantCulture);

			// Each blocker is an entity sent to every client, so coarser cells are used until there are few enough
			var skyCover = new GoldSrcSkyCover(gs);
			List<(Vector3 mins, Vector3 maxs)> boxes;
			var cellSize = PrecipitationBlockerCellSize;
			while ((boxes = skyCover.FindCoveredBoxes(cellSize)).Count > MaxPrecipitationBlockers)
				cellSize *= 2f;

			foreach (var (mins, maxs) in boxes)
			{
				var blocker = new Entity();
				blocker.ClassName = "func_precipitation_blocker";
				blocker["model"] = "*" + builder.AddBoxTriggerModel(mins, maxs).ToString(CultureInfo.InvariantCulture);
				sourceBsp.Entities.Add(blocker);
			}

			if (boxes.Count > 0)
				logger.Log($"Added {boxes.Count} func_precipitation_blockers under cover ({cellSize} unit cells)");
		}

		// GoldSrc stores one compressed PVS row per world leaf (leaf N is cluster N - 1), which is the same
		// run-length encoding Source uses per cluster.
		private void ConvertVisibility()
		{
			var numClusters = gs.Models[0].visLeafs;
			if (numClusters <= 0)
			{
				sourceBsp.Visibility.Data = new byte[0];
				return;
			}

			var rowBytes = (numClusters + 7) >> 3;
			var headerLength = 4 + numClusters * 8;
			var data = new List<byte>();
			var offsets = new int[numClusters];

			for (var cluster = 0; cluster < numClusters; cluster++)
			{
				var row = DecompressVisRow(gs.Leaves[cluster + 1].visOffset, rowBytes);
				offsets[cluster] = headerLength + data.Count;
				data.AddRange(Visibility.Compress(row));
			}

			var visData = new byte[headerLength + data.Count];
			BitConverter.GetBytes(numClusters).CopyTo(visData, 0);
			for (var i = 0; i < numClusters; i++)
			{
				// PVS and PAS share the same data, as GoldSrc has no PAS
				BitConverter.GetBytes(offsets[i]).CopyTo(visData, 4 + i * 8);
				BitConverter.GetBytes(offsets[i]).CopyTo(visData, 8 + i * 8);
			}

			data.CopyTo(visData, headerLength);
			sourceBsp.Visibility.Data = visData;
		}

		private byte[] DecompressVisRow(int offset, int rowBytes)
		{
			var row = new byte[rowBytes];

			// No visibility data means everything is visible
			if (offset < 0 || gs.Visibility.Length == 0)
			{
				Array.Fill(row, (byte)0xFF);
				return row;
			}

			var input = offset;
			var output = 0;
			while (output < rowBytes && input < gs.Visibility.Length)
			{
				var value = gs.Visibility[input++];
				if (value != 0)
				{
					row[output++] = value;
					continue;
				}

				// A zero byte is followed by how many zero bytes it stands for
				var count = input < gs.Visibility.Length ? gs.Visibility[input++] : 0;
				output += count;
			}

			return row;
		}
	}
}
