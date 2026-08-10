using LibBSP;
using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Collections;
using System.Xml.Linq;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives;
using BSPConvert.Lib.Source;
using BSPConvert.Lib.Zones;

using Plane = System.Numerics.Plane;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using Color = System.Drawing.Color;

namespace BSPConvert.Lib
{
	public class BSPConverterOptions
	{
		public bool noPak;
		public bool noToolDisplacements;
		public bool patchesAsPrimitives;
		private int displacementPower;
		public int DisplacementPower
		{
			get { return displacementPower; }
			set { displacementPower = Math.Clamp(value, 2, 4); }
		}
		public int minDamageToRespawnPlayer;
		// Duplicate Quake 3 lava brushes (CONTENTS_LAVA) into trigger_hurt volumes so players are
		// killed/respawned on contact. See BSPConverter.ConvertLavaTriggers.
		public bool lavaTriggers;
		// Quake 3 fog brushes are converted by default: the default path (useObbFog = false) tags each fog brush
		// CONTENTS_FOG and drops its faces (nodraw). The engine composites a depth-clipped fog overlay for the
		// volume, reading the appearance from the brush's Fog material. See BSPConverter.ConvertPolygon / ConvertBrushes.
		// Use the legacy obb_volumefog entity path instead of the Fog shader. obb_volumefog is a froxel
		// volumetric that handles arbitrary brush shapes but flickers on thin volumes; kept behind this
		// flag for now. See BSPConverter.ConvertFogVolumes.
		public bool useObbFog;
		// Minimum vertical (Z) height, in units, for converted fog volumes. Thin fog layers are expanded
		// downward to this height so they span enough view froxels to reduce flickering. obb-fog only.
		// See ConvertFogVolumes.
		public float fogMinHeight;
		// Skip generating the fog overlay face for fog shaders with visible stages (e.g. the scrolling
		// clouds on textures/sfx/hellfog); the fog brush face is just dropped instead. See TryCreateFogOverlayFace.
		public bool noFogOverlay;
		public bool ignoreZones;
		public bool noEnvMap;
		public bool oldBSP;
		public string prefix;
		public string inputFile;
		public string outputDir;
		// Optional filter to convert only specific BSP(s) from a multi-BSP pk3. Matched against each
		// BSP's map name (without extension), case-insensitively. Null/empty converts every BSP.
		public string[] mapFilter;
		public string offModeEntityFallback;
		// When set, applies Quake 3's hue-preserving overbright clamp to lightmap luxels (flattens
		// over-bright highlights toward white). Off by default. See ColorUtil.ConvertQ3LightmapToColorRGBExp32.
		public bool clampOverbright;
		// Settings for baking Q3 multi-pass scrolling shaders (e.g. liquids water) into looping animated
		// flipbook VTFs. See FlipbookConverter.
		public FlipbookOptions flipbook = new FlipbookOptions();
	}

	public class BSPConverter
	{
		private BSPConverterOptions options;
		private ILogger logger;

		private BSP quakeBsp;
		private BSP sourceBsp;

		private ContentManager contentManager;

		private Dictionary<string, Shader> shaderDict = new Dictionary<string, Shader>();
		private IReadOnlySet<string> referencedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Texture paths the generated materials use, so unused pk3 images (lightmaps, levelshots) aren't converted/embedded
		private Dictionary<string, LightmapData> externalLightmaps = new Dictionary<string, LightmapData>();
		private Dictionary<TextureInfoKey, int> textureInfoDict = new Dictionary<TextureInfoKey, int>();
		private Dictionary<string, int> textureInfoLookup = new Dictionary<string, int>();
		private Dictionary<string, int> textureDataLookup = new Dictionary<string, int>();
		private Dictionary<int, int> invisibleDispTexDataByFlags = new Dictionary<int, int>(); // Maps a physics surface-flag set to its invisible-displacement texdata variant
		private Dictionary<int, int[]> splitFaceDict = new Dictionary<int, int[]>(); // Maps the original face index to the new face indices split by triangles
		private Dictionary<int, Texture> fogBrushSideTexture = new Dictionary<int, Texture>(); // Maps a fog brush's side index to the brush's fog texture, so every side carries the fog material
		private Dictionary<(Vector3, float), int> planeDict = new Dictionary<(Vector3, float), int>();
		private HashSet<int> triggerPatchFaces = new HashSet<int>(); // Q3 patch faces that belong to trigger entities; converted without collision (see MarkTriggerPatchFaces)
		private SkyboxSwapPlan? skyboxSwapPlan; // Multi-skybox swap regions for the current map (see ConvertSkyboxSwappers)

		// TODO: Replace weapon clip textures
		private static readonly Dictionary<string, string> replacementTextures = new Dictionary<string, string>()
		{
			{ "textures/common/caulk", "tools/toolsnodraw" },
			{ "textures/common/nodraw", "tools/toolsnodraw" },
			{ "textures/common/clip", "tools/toolsplayerclip" },
			{ "textures/common/full_clip", "tools/clip" },
			{ "textures/common/trigger", "tools/toolstrigger" },
			{ "textures/common/hint", "tools/toolshint" },
			{ "textures/common/skip", "tools/toolsskip" },
			{ "textures/common/areaportal", "tools/toolsareaportal" },
			{ "textures/common/weapclip", "tools/toolsblockbullets" }
		};

		private const int Q3_LIGHTMAP_SIZE = 128;
		// Border of duplicated edge luxels added around each face's lightmap block so bilinear filtering
		// at a face's edge samples its own (duplicated) edge color instead of bleeding in the adjacent
		// block packed next to it in the lightmap atlas page.
		private const int LIGHTMAP_BORDER = 1;
		// Maximum luxel extent, per axis, of a face's stored lightmap size (LightmapSize). The Source engine
		// fatally errors ("Bad surface extents") if a face's stored lightmap extent exceeds
		// MAX_BRUSH_LIGHTMAP_SIZE (1024), and the lightmap page it packs into is only 2048x1024 - the engine
		// allocates (extent + 1) luxels, so the page height caps the stored extent at 1023. Maps compiled with
		// large external lightmap atlases (e.g. 2048x2048) can give a single large surface a luxel block bigger
		// than this, so GetFaceLightmapBlock downscales such blocks to fit.
		private const int MAX_LIGHTMAP_EXTENT = 1023;
		private const string invisibleDisplacementTexture = "tools/toolsinvisibledisplacement";

		public BSPConverter(BSPConverterOptions options, ILogger logger)
		{
			this.options = options;
			this.logger = logger;
		}

		public void Convert()
		{
			if (!File.Exists(options.inputFile))
			{
				logger.Log("Error: Input BSP file does not exist");
				return;
			}

			CheckQ3Content();

			contentManager = new ContentManager(options.inputFile);
			shaderDict = LoadShaderDictionary();
			externalLightmaps = LoadExternalLightmaps();

			foreach (var bsp in contentManager.BSPFiles)
			{
				if (options.mapFilter != null && options.mapFilter.Length > 0 &&
					!options.mapFilter.Contains(bsp.MapName, StringComparer.OrdinalIgnoreCase))
				{
					logger.Log($"Skipping {bsp.MapName}.bsp (not in --maps filter)");
					continue;
				}

				ClearDictionaries();

				LoadBSP(bsp);

				MarkTriggerPatchFaces();
				ReplaceToolTextures();
				PrepareAssets();
				CreatePakFile();
				ConvertMaterials();
				ConvertTextureFiles();

				ConvertEntities();
				ConvertSounds();
				ConvertTextures();
				ConvertPlanes();
				//ConvertFaces_SplitFaces();
				ConvertFaces();
				ConvertLeaves_SplitFaces();
				ConvertLeafFaces_SplitFaces();
				//ConvertLeaves();
				//ConvertLeafFaces();
				ConvertLeafBrushes();
				ConvertNodes();
				ConvertModels();
				ConvertBrushes();
				ConvertBrushSides();
				if (options.useObbFog)
					ConvertObbFog();
				ConvertFuncDoorTriggers();
				ConvertSkyboxSwappers();
				if (options.lavaTriggers)
					ConvertLavaTriggers();
				ConvertLightmaps();

				// Must be called after all leaves have been created
				ConvertLightGrid();
				
				ConvertVisData();
				ConvertAreas();
				ConvertAreaPortals();
				ConvertWorldLights();

				WriteBSP();

				GenerateZones();
			}

			contentManager.Dispose();
		}

		private void ClearDictionaries()
		{
			textureInfoDict.Clear();
			textureInfoLookup.Clear();
			textureDataLookup.Clear();
			splitFaceDict.Clear();
			fogBrushSideTexture.Clear();
			planeDict.Clear();
			invisibleDispTexDataByFlags.Clear();
			triggerPatchFaces.Clear();
		}

		private void LoadBSP(BSP bsp)
		{
			var bspName = bsp.MapName + ".bsp";
			logger.Log($"Converting {bspName}...");

			quakeBsp = bsp;

			var mapType = options.oldBSP ? MapType.Source20 : MapType.Source25;
			sourceBsp = new BSP(bspName, mapType);
		}

		private void CheckQ3Content()
		{
			var files = Directory.GetFiles(ContentManager.GetQ3ContentDir(), "*.*", SearchOption.AllDirectories);
			if (files.Length <= 1)
				logger.Log("Warning: Q3Content folder is empty. Quake 3 assets will not be converted.");
		}

		private void ReplaceToolTextures()
		{
			for (var i = 0; i < quakeBsp.Textures.Count; i++)
			{
				var texture = quakeBsp.Textures[i];
				if (replacementTextures.TryGetValue(texture.Name, out var replacementTexture))
					texture.Name = replacementTexture;
			}
		}

		private void PrepareAssets()
		{
			// Patches converted to primitives use invisible displacements for collisions
			if (quakeBsp.Faces.Any(x => x.Type == FaceType.Patch &&
				(options.patchesAsPrimitives || x.Texture.Name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))))
			{
				// Copy invisible displacement assets to content dir
				FileUtil.CopyBuiltinMaterialAsset(Path.Combine("tools", "toolsinvisibledisplacement.vmt"), contentManager.ContentDir);
				FileUtil.CopyBuiltinMaterialAsset(Path.Combine("tools", "toolsinvisibledisplacement.vtf"), contentManager.ContentDir);
			}
		}

		private void CreatePakFile()
		{
			if (options.noPak)
				return;

			using (var archive = ZipArchive.Create())
			{
				sourceBsp.PakFile.SetZipArchive(archive, true);
			}
		}

		private void ConvertMaterials()
		{
			// The default (non-obb) fog path emits Q3 fog shaders as Fog VMTs. The fog brush faces are nodraw, so
			// these aren't drawn as surfaces; the engine reads the material off the CONTENTS_FOG brush for the
			// overlay's fog appearance ($fogcolor / $fogdepthforopaque).
			var generateFogMaterials = !options.useObbFog;
			var materialConverter = new MaterialConverter(contentManager.ContentDir, shaderDict, options.noEnvMap, options.flipbook, generateFogMaterials);
			foreach (var texture in quakeBsp.Textures)
				materialConverter.Convert(texture.Name);

			// The texture pass only converts images these materials reference (see TextureConverter).
			referencedTextures = materialConverter.ReferencedTextures;
		}

		private Dictionary<string, Shader> LoadShaderDictionary()
		{
			var q3Shaders = GetQ3Shaders();
			var pk3Shaders = GetPK3Shaders();
			var customShaders = GetCustomContentShaders();
			// CustomContent shaders go last so they only fill gaps (ShaderLoader keeps the first entry per key).
			var allShaders = q3Shaders.Concat(pk3Shaders).Concat(customShaders);

			var loader = new ShaderLoader(allShaders);
			return loader.LoadShaders();
		}

		private Dictionary<string, LightmapData> LoadExternalLightmaps()
		{
			var loader = new ExternalLightmapLoader(shaderDict, contentManager.ContentDir);
			return loader.LoadLightmaps();
		}

		private string[] GetQ3Shaders()
		{
			var q3ScriptsDir = Path.Combine(ContentManager.GetQ3ContentDir(), "scripts");
			if (Directory.Exists(q3ScriptsDir))
				return Directory.GetFiles(q3ScriptsDir, "*.shader");

			return new string[0];
		}

		private string[] GetPK3Shaders()
		{
			var pk3ScriptsDir = Path.Combine(contentManager.ContentDir, "scripts");
			if (Directory.Exists(pk3ScriptsDir))
				return Directory.GetFiles(pk3ScriptsDir, "*.shader");

			return new string[0];
		}

		private string[] GetCustomContentShaders()
		{
			var customScriptsDir = Path.Combine(ContentManager.GetCustomContentDir(), "scripts");
			if (Directory.Exists(customScriptsDir))
				return Directory.GetFiles(customScriptsDir, "*.shader");

			return new string[0];
		}

		private void ConvertTextureFiles()
		{
			var converter = options.noPak ?
				new TextureConverter(contentManager.ContentDir, options.outputDir, shaderDict, referencedTextures) :
				new TextureConverter(contentManager.ContentDir, sourceBsp, shaderDict, referencedTextures);
			converter.Convert();
		}

		private void ConvertEntities()
		{
			// Detect multiple skyboxes up front so the map spawns with the right one (worldspawn skyname) and
			// ConvertSkyboxSwappers can emit the swap triggers later, once the Source brushes/models exist.
			skyboxSwapPlan = new SkyboxSwapConverter(quakeBsp, ResolveSkyboxName, logger, GetSkyName()).Build();

			var converter = new EntityConverter(quakeBsp.Models, quakeBsp.Entities, sourceBsp.Entities, skyboxSwapPlan.DefaultSkyName!, options.minDamageToRespawnPlayer, options.ignoreZones, options.offModeEntityFallback);
			converter.Convert();
		}

		// The Source skyname a sky surface maps to (worldspawn skyname / sv_skyname value), or null if the
		// texture isn't a real skybox. Mirrors GetSkyName's routing per texture: an image-box sky uses its
		// outerBox, a baked cloud sky uses its shader basename. Fake single-texture skies (IsSkySurface
		// false) and non-sky surfaces return null. Used by SkyboxSwapConverter to group sky faces by skybox.
		private string? ResolveSkyboxName(Texture texture)
		{
			if (!IsSkySurface(texture) || !shaderDict.TryGetValue(texture.Name, out var shader))
				return null;

			if (shader.skyParms != null && shader.skyParms.HasImageBox)
				return shader.skyParms.outerBox;

			if (CloudSkyboxBaker.IsCloudSkyShader(shader))
				return CloudSkyboxBaker.GetSkyName(texture.Name);

			return null;
		}

		// Source uses a single global skybox (skyname), but Q3 sky brushes directly reference a sky
		// shader. q3map2 bakes that shader name into each BSP texture entry, so resolve skyname from
		// the sky shader actually applied to brushes rather than from all parsed shaders.
		private string GetSkyName()
		{
			string cloudSkyName = null;
			foreach (var texture in quakeBsp.Textures)
			{
				if (!shaderDict.TryGetValue(texture.Name, out var shader))
					continue;

				// A real image skybox (skyParms <outerBox>) maps directly to Source's global skybox and takes
				// priority over a baked cloud sky.
				if (shader.skyParms != null && shader.skyParms.HasImageBox)
					return shader.skyParms.outerBox;

				// A dynamic cloud sky (skyParms, no box) is baked into a static skybox by CloudSkyboxBaker;
				// point skyname at those baked faces. Single-texture fake skies are converted as ordinary
				// surfaces instead (see IsSingleTextureSky).
				if (cloudSkyName == null && CloudSkyboxBaker.IsCloudSkyShader(shader))
					cloudSkyName = CloudSkyboxBaker.GetSkyName(texture.Name);
			}

			return cloudSkyName;
		}

		// A "fake sky" is a sky surface whose shader has no skyParms at all - it just draws a flat texture on
		// the brushes. Source supports only one global skybox, so we convert these as ordinary textured
		// surfaces (matching how Q3 renders them), which means they must NOT carry the Source sky flag.
		// Skies with skyParms are real skyboxes: an image box maps to skyname directly, and a dynamic cloud
		// sky (skyParms, no box) is baked into one by CloudSkyboxBaker - both keep the Source sky flag.
		private bool IsSingleTextureSky(string textureName)
		{
			return shaderDict.TryGetValue(textureName, out var shader) &&
				shader.skyParms == null &&
				shader.GetImageStages().Any();
		}

		// Decides whether a surface should reveal Source's single global skybox. q3map2 only bakes the
		// SURF_SKY flag into the BSP when the shader uses "surfaceparm sky", but a sky shader can render
		// purely from the "skyparms" keyword (the Q3 runtime marks it as sky from skyParms, no surfaceparm
		// needed). Those surfaces reach us without the BSP sky flag, so fall back to the shader: a real
		// image-box skybox or a baked cloud sky (the same skies GetSkyName resolves) must carry SURF_SKY,
		// or they render as ordinary - here untextured, white - world walls instead of the skybox.
		private bool IsSkySurface(Texture texture)
		{
			if (((Q3SurfaceFlags)texture.Flags).HasFlag(Q3SurfaceFlags.SURF_SKY))
				return !IsSingleTextureSky(texture.Name); // fake skies stay ordinary surfaces

			return shaderDict.TryGetValue(texture.Name, out var shader) &&
				((shader.skyParms != null && shader.skyParms.HasImageBox) ||
				CloudSkyboxBaker.IsCloudSkyShader(shader));
		}

		private void ConvertSounds()
		{
			var converter = options.noPak ?
				new SoundConverter(contentManager.ContentDir, options.outputDir, sourceBsp.Entities) :
				new SoundConverter(contentManager.ContentDir, sourceBsp, sourceBsp.Entities);
			converter.Convert();
		}

		private void ConvertTextures()
		{
			foreach (var texture in quakeBsp.Textures)
				CreateTextureData(texture.Name);
		}

		private void CreateTextureData(string textureName)
		{
			var index = CreateTextureDataEntry(textureName);

			if (!textureDataLookup.ContainsKey(textureName))
				textureDataLookup.Add(textureName, index);
		}

		// Creates a texdata entry for the given material and returns its index without
		// registering it in textureDataLookup. Use this when a distinct texdata is needed
		// for an already-named material (e.g. a surface-flag variant), so the name->index
		// lookup keeps pointing at the original/default entry.
		private int CreateTextureDataEntry(string textureName)
		{
			var vtfPath = Path.Combine(contentManager.ContentDir, textureName + ".vtf");
			var textureData = GetTextureData(vtfPath);
			textureData.TextureStringOffsetIndex = CreateTextureDataStringTableEntry(textureName);

			sourceBsp.TextureData.Add(textureData);

			return sourceBsp.TextureData.Count - 1;
		}

		private TextureData GetTextureData(string vtfPath)
		{
			if (!File.Exists(vtfPath))
				return GetDefaultTextureData();

			try
			{
				var textureData = CreateTextureData();

				var vtfFile = FileUtil.DeserializeFromFile(vtfPath, VTFFile.Deserialize);
				var header = vtfFile.header;

				var r = (int)Math.Round(header.reflectivityX * 255f);
				var g = (int)Math.Round(header.reflectivityY * 255f);
				var b = (int)Math.Round(header.reflectivityZ * 255f);
				textureData.Reflectivity = ColorExtensions.FromArgb(255, r, g, b);

				var size = new Vector2(vtfFile.header.width, vtfFile.header.height);
				textureData.Size = size;
				textureData.ViewSize = size;

				return textureData;
			}
			catch (Exception e)
			{
				Console.WriteLine($"Failed to load vtf file ({Path.GetFileName(vtfPath)})");
				Console.WriteLine(e.Message);

				return GetDefaultTextureData();
			}
		}

		private TextureData GetDefaultTextureData()
		{
			var textureData = CreateTextureData();

			textureData.Reflectivity = new Color();
			textureData.Size = new Vector2(128, 128);
			textureData.ViewSize = new Vector2(128, 128);

			return textureData;
		}

		private TextureData CreateTextureData()
		{
			var data = new byte[TextureData.GetStructLength(sourceBsp.MapType)];
			return new TextureData(data, sourceBsp.TextureData);
		}

		private int CreateTextureDataStringTableEntry(string textureName)
		{
			sourceBsp.TextureTable.Add(CreateTextureDataStringData(textureName));

			return sourceBsp.TextureTable.Count - 1;
		}

		// Note: Returns texture data byte offset instead of index
		private int CreateTextureDataStringData(string textureName)
		{
			var offset = sourceBsp.Textures.Length;

			var data = System.Text.Encoding.ASCII.GetBytes(textureName);
			var texture = new Texture(data, sourceBsp.Textures);

			sourceBsp.Textures.Add(texture);

			return offset;
		}

		private void ConvertPlanes()
		{
			foreach (var qPlane in quakeBsp.Planes)
			{
				var data = new byte[PlaneBSP.GetStructLength(sourceBsp.MapType)];
				var plane = new PlaneBSP(data, sourceBsp.Planes);

				if (!qPlane.Normal.IsValid())
				{
					// Degenerate Q3 plane (common for patch/fog faces); substitute a safe dummy
					plane.Normal = new Vector3(0f, 0f, 1f);
					plane.Distance = 0f;
					plane.Type = (int)PlaneBSP.AxisType.PlaneZ;

					sourceBsp.Planes.Add(plane);
					continue;
				}

				plane.Normal = qPlane.Normal;
				plane.Distance = qPlane.Distance;
				plane.Type = (int)GetVectorAxis(qPlane.Normal);

				sourceBsp.Planes.Add(plane);
			}
		}

		private PlaneBSP.AxisType GetVectorAxis(Vector3 normal)
		{
			// Note: This mirrors the logic in the engine, so the lack of an epsilon check is intentional
			if (normal.X() == 1.0 || normal.X() == -1.0)
				return PlaneBSP.AxisType.PlaneX;
			if (normal.Y() == 1.0 || normal.Y() == -1.0)
				return PlaneBSP.AxisType.PlaneY;
			if (normal.Z() == 1.0 || normal.Z() == -1.0)
				return PlaneBSP.AxisType.PlaneZ;

			var aX = Math.Abs(normal.X());
			var aY = Math.Abs(normal.Y());
			var aZ = Math.Abs(normal.Z());

			if (aX >= aY && aX >= aZ)
				return PlaneBSP.AxisType.PlaneAnyX;

			if (aY >= aX && aY >= aZ)
				return PlaneBSP.AxisType.PlaneAnyY;

			return PlaneBSP.AxisType.PlaneAnyZ;
		}

		// Note: This needs to be called after converting split faces in order to fix skyboxes not rendering
		private void ConvertNodes()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(Node.GetIndexForLump(sourceBsp.MapType), 1);

			foreach (var qNode in quakeBsp.Nodes)
			{
				var data = new byte[Node.GetStructLength(sourceBsp.MapType)];
				var node = new Node(data, sourceBsp.Nodes);

				node.PlaneIndex = qNode.PlaneIndex;
				node.Child1Index = qNode.Child1Index;
				node.Child2Index = qNode.Child2Index;
				node.Minimums = qNode.Minimums;
				node.Maximums = qNode.Maximums;

				// Note: On Source BSP's, these values are used to specify which faces are used to split the node (the face will have the "onNode" flag set to true)
				node.FirstFaceIndex = 0;
				node.NumFaceIndices = 0;

				node.AreaIndex = 0; // TODO: Figure out how to compute areas

				sourceBsp.Nodes.Add(node);
			}

			UpdateSplitFaces();
		}

		// Assigns face ids to nodes by searching for the face that was used to split the node into its children
		// This is necessary to get skyboxes to render correctly (and perhaps some other engine optimizations)
		private void UpdateSplitFaces()
		{
			// TODO: Does this need to be done for all model head nodes?
			var rootNode = sourceBsp.Nodes[0];

			UpdateSplitFacesRecursive(rootNode.Child1);
			UpdateSplitFacesRecursive(rootNode.Child2);
		}

		private IEnumerable<int> UpdateSplitFacesRecursive(ILumpObject obj)
		{
			if (obj is Leaf)
				return ((Leaf)obj).MarkFaces;

			var node = (Node)obj;
			var faces = UpdateSplitFacesRecursive(node.Child1).Concat(
				UpdateSplitFacesRecursive(node.Child2));

			// Find the face that splits this node
			var vertices = sourceBsp.PrimitiveVertices;
			foreach (var faceIndex in faces)
			{
				var face = sourceBsp.Faces[faceIndex];

				var surfaceFlags = (SourceSurfaceFlags)face.TextureInfo.Flags;
				if (!surfaceFlags.HasFlag(SourceSurfaceFlags.SURF_SKY))
					continue;

				var primitive = sourceBsp.Primitives[face.FirstPrimitive];
				var plane = Plane.CreateFromVertices(
					vertices[primitive.FirstVertex],
					vertices[primitive.FirstVertex + 1],
					vertices[primitive.FirstVertex + 2]);

				// TODO: Should this check be more precise?
				// Splitting face will be in between each child node
				if (IsPlaneCoplanarWithNode(plane, node.Child1) && IsPlaneCoplanarWithNode(plane, node.Child2) &&
					IsPlaneBetweenNodes(plane, node.Child1, node.Child2))
				{
					node.FirstFaceIndex = faceIndex;
					node.NumFaceIndices = 1;

					break;
				}
			}

			return faces;
		}

		private bool IsPlaneCoplanarWithNode(Plane plane, ILumpObject node)
		{
			(var mins, var maxs) = GetMinsMaxs(node);
			return plane.HasPoint(mins) || plane.HasPoint(maxs);
		}

		private bool IsPlaneBetweenNodes(Plane plane, ILumpObject node1, ILumpObject node2)
		{
			(var mins1, var maxs1) = GetMinsMaxs(node1);
			(var mins2, var maxs2) = GetMinsMaxs(node2);
			var center1 = (mins1 + maxs1) / 2;
			var center2 = (mins2 + maxs2) / 2;

			return plane.GetSide(center1) != plane.GetSide(center2);
		}

		private (Vector3, Vector3) GetMinsMaxs(ILumpObject obj)
		{
			if (obj is Node)
			{
				var node = (Node)obj;
				return (node.Minimums, node.Maximums);
			}
			else
			{
				var leaf = (Leaf)obj;
				return (leaf.Minimums, leaf.Maximums);
			}
		}

		private void ConvertLeaves()
		{
			var version = options.oldBSP ? 1 : 2;
			SetLumpVersionNumber(Leaf.GetIndexForLump(sourceBsp.MapType), version);

			foreach (var qLeaf in quakeBsp.Leaves)
			{
				var data = new byte[Leaf.GetStructLength(sourceBsp.MapType)];
				var leaf = new Leaf(data, sourceBsp.Leaves);

				if (sourceBsp.Leaves.Count == 0)
				{
					leaf.Contents = (int)SourceContentsFlags.CONTENTS_SOLID; // First leaf is always solid, otherwise game crashes
					leaf.Flags = 0;
				}
				else
				{
					leaf.Contents = qLeaf.Area >= 0 ? 0 : 1; // Set to 0 when inside map, 1 when outside map or overlapping brush
					leaf.Flags = qLeaf.Area >= 0 ? (int)(LeafFlags.RADIAL | LeafFlags.SKY2D) : 0; // TODO: Detect if the leaf has a leaf face that has a texinfo with the SURF_SKY or SURF_SKY2D flag
				}
				leaf.Visibility = qLeaf.Visibility;
				leaf.Area = 0; // TODO: Convert Q3 areas?
				leaf.Minimums = qLeaf.Minimums;
				leaf.Maximums = qLeaf.Maximums;
				leaf.FirstMarkFaceIndex = qLeaf.FirstMarkFaceIndex;
				leaf.NumMarkFaceIndices = qLeaf.NumMarkFaceIndices;
				leaf.FirstMarkBrushIndex = qLeaf.FirstMarkBrushIndex;
				leaf.NumMarkBrushIndices = qLeaf.NumMarkBrushIndices;
				leaf.LeafWaterDataID = -1;

				sourceBsp.Leaves.Add(leaf);
			}
		}

		private void ConvertLeaves_SplitFaces()
		{
			var version = options.oldBSP ? 1 : 2;
			SetLumpVersionNumber(Leaf.GetIndexForLump(sourceBsp.MapType), version);

			var currentFaceIndex = 0;

			foreach (var qLeaf in quakeBsp.Leaves)
			{
				var data = new byte[Leaf.GetStructLength(sourceBsp.MapType)];
				var leaf = new Leaf(data, sourceBsp.Leaves);

				if (sourceBsp.Leaves.Count == 0)
				{
					leaf.Contents = (int)SourceContentsFlags.CONTENTS_SOLID; // First leaf is always solid, otherwise game crashes
					leaf.Flags = 0;
				}
				else
				{
					leaf.Contents = qLeaf.Area >= 0 ? 0 : 1; // Set to 0 when inside map, 1 when outside map or overlapping brush
					leaf.Flags = qLeaf.Area >= 0 ? (int)(LeafFlags.RADIAL | LeafFlags.SKY2D) : 0; // TODO: Detect if the leaf has a leaf face that has a texinfo with the SURF_SKY or SURF_SKY2D flag
				}
				leaf.Visibility = qLeaf.Visibility;
				leaf.Area = 0; // TODO: Convert Q3 areas?
				leaf.Minimums = qLeaf.Minimums;
				leaf.Maximums = qLeaf.Maximums;

				leaf.FirstMarkFaceIndex = currentFaceIndex;
				var numFaces = 0;
				for (var i = 0; i < qLeaf.NumMarkFaceIndices; i++)
				{
					// Unsupported face types (e.g. Billboard) are never added to splitFaceDict during face
					// conversion, so a leaf that marks one has no split faces for it. Treat it as zero faces
					// instead of throwing on the lookup (matches ConvertLeafFaces_SplitFaces below).
					if (splitFaceDict.TryGetValue((int)quakeBsp.LeafFaces[qLeaf.FirstMarkFaceIndex + i], out var splitFaces))
						numFaces += splitFaces.Length;
				}
				leaf.NumMarkFaceIndices = numFaces;
				currentFaceIndex += numFaces;

				leaf.FirstMarkBrushIndex = qLeaf.FirstMarkBrushIndex;
				leaf.NumMarkBrushIndices = qLeaf.NumMarkBrushIndices;
				leaf.LeafWaterDataID = -1;

				sourceBsp.Leaves.Add(leaf);
			}
		}

		private void ConvertLeafFaces()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafFacesLump(sourceBsp.MapType, out _), 1);

			foreach (var qLeafFace in quakeBsp.LeafFaces)
				sourceBsp.LeafFaces.Add(qLeafFace);
		}

		private void ConvertLeafFaces_SplitFaces()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafFacesLump(sourceBsp.MapType, out _), 1);

			foreach (var qLeafFace in quakeBsp.LeafFaces)
			{
				// Unsupported face types (e.g. Billboard) have no splitFaceDict entry; skip them so we emit the
				// same leaf face count that ConvertLeaves_SplitFaces sized the leaves for (and don't throw here).
				if (!splitFaceDict.TryGetValue((int)qLeafFace, out var splitFaceIndices))
					continue;

				for (var i = 0; i < splitFaceIndices.Length; i++)
					sourceBsp.LeafFaces.Add(splitFaceIndices[i]);
			}
		}

		private void ConvertLeafBrushes()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafBrushesLump(sourceBsp.MapType, out _), 1);

			foreach (var qLeafBrush in quakeBsp.LeafBrushes)
				sourceBsp.LeafBrushes.Add(qLeafBrush);
		}

		private void ConvertModels()
		{
			var exceededMaxExtents = false;

			for (var i = 0; i < quakeBsp.Models.Count; i++)
			{
				var qModel = quakeBsp.Models[i];

				var data = new byte[Model.GetStructLength(sourceBsp.MapType)];
				var sModel = new Model(data, sourceBsp.Models);

				if (i == 0)
					sModel.HeadNodeIndex = 0;
				else
				{
					if (!TryCreateHeadNode(qModel.FirstBrushIndex, out var nodeIndex))
					{
						logger.Log($"Failed to convert model: {i}");
						continue;
					}

					sModel.HeadNodeIndex = nodeIndex;
				}

				var mins = qModel.Minimums;
				var maxs = qModel.Maximums;
				var minExtents = options.oldBSP ? -16384 : -65536;
				var maxExtents = options.oldBSP ? 16384 : 65536;
				//sModel.Minimums = new Vector3(Math.Clamp(mins.X(), minExtents, maxExtents), Math.Clamp(mins.Y(), minExtents, maxExtents), Math.Clamp(mins.Z(), minExtents, maxExtents));
				//sModel.Maximums = new Vector3(Math.Clamp(maxs.X(), minExtents, maxExtents), Math.Clamp(maxs.Y(), minExtents, maxExtents), Math.Clamp(maxs.Z(), minExtents, maxExtents));

				if (mins.X() < minExtents || mins.Y() < minExtents || mins.Z() < minExtents ||
					maxs.X() > maxExtents || maxs.Y() > maxExtents || maxs.Z() > maxExtents)
				{
					exceededMaxExtents = true;
					logger.Log("Exceeded max extents: " + mins);
				}

				// TODO: Re-center mins/maxs and set trigger entity origin?
				sModel.Minimums = mins;
				sModel.Maximums = maxs;
				sModel.Origin = new Vector3(0f, 0f, 0f);
				if (qModel.FirstFaceIndex < splitFaceDict.Count)
				{
					// Conversion expands or drops faces (patches split into sub-patches, each emitting a visible
					// primitive face plus a collision face; meshes triangulated; fog faces dropped to nodraw), so
					// the Source face count differs from the Quake model's. Walk the model's face range to find the
					// first emitted Source face and sum the expanded counts, skipping faces that emitted nothing (a
					// dropped face has an empty split, so indexing [0] would throw); otherwise per-model surface
					// passes in the engine (e.g. Mod_ComputeBrushModelFlags) skip faces or index a dropped range.
					var firstFace = -1;
					var numFaces = 0;
					for (var f = 0; f < qModel.NumFaces; f++)
					{
						if (splitFaceDict.TryGetValue(qModel.FirstFaceIndex + f, out var splitFaces) && splitFaces.Length > 0)
						{
							if (firstFace < 0)
								firstFace = splitFaces[0];
							numFaces += splitFaces.Length;
						}
					}
					sModel.FirstFaceIndex = firstFace < 0 ? 0 : firstFace;
					sModel.NumFaces = numFaces;
				}
				else
				{
					sModel.FirstFaceIndex = qModel.FirstFaceIndex;
					sModel.NumFaces = qModel.NumFaces;
				}

				sourceBsp.Models.Add(sModel);
			}

			if (exceededMaxExtents)
				throw new Exception("Failed to convert BSP, exceeded max extents");
		}

		private void ConvertFuncDoorTriggers()
		{
			var doorEntities = sourceBsp.Entities
				.Where(e => e.ClassName == "func_door")
				.ToList();

			foreach (var door in doorEntities)
			{
				var modelNumber = door.ModelNumber;
				if (modelNumber <= 0 || modelNumber >= sourceBsp.Models.Count)
					continue;

				if (!string.IsNullOrEmpty(door["targetname"]) || (float.TryParse(door["health"], out var health) && health > 0)) // create trigger only if it isn't being targeted or can't be shot open
					continue;

				var model = sourceBsp.Models[modelNumber];
				var mins = model.Minimums;
				var maxs = model.Maximums;

				ExpandDoorTriggerBounds(ref mins, ref maxs);

				door.Name = $"door{modelNumber}";

				var triggerModelIndex = CreateBoxTrigger(mins, maxs);

				var trigger = new Entity();
				trigger.ClassName = "trigger_multiple";
				trigger["model"] = $"*{triggerModelIndex}";
				trigger["wait"] = "0";
				trigger["spawnflags"] = "1";
				trigger.connections.Add(new Entity.EntityConnection()
				{
					name = "OnStartTouch",
					target = door["targetname"],
					action = "Open",
					param = null,
					delay = 0,
					fireOnce = -1
				});
				sourceBsp.Entities.Add(trigger);
			}
		}

		// Emits the multi-skybox swap setup detected in ConvertEntities. Source has a single global 2D skybox
		// (sv_skyname); the map spawns with skyboxSwapPlan.DefaultSkyName (set on worldspawn) and each region
		// gets a skybox_swapper point entity plus a trigger_multiple covering that skybox's visibility volume.
		// Entering the trigger fires the swapper, which sets sv_skyname to that skybox. Regions are non-co-visible
		// (SkyboxSwapConverter guarantees it) so only one skybox is ever needed at a time. No-op for single-sky maps.
		private void ConvertSkyboxSwappers()
		{
			if (skyboxSwapPlan == null || skyboxSwapPlan.Regions.Count == 0)
				return;

			for (var i = 0; i < skyboxSwapPlan.Regions.Count; i++)
			{
				var region = skyboxSwapPlan.Regions[i];
				if (region.Boxes.Count == 0)
					continue;

				var swapperName = $"_skybox_swap_{i}";

				var swapper = new Entity();
				swapper.ClassName = "skybox_swapper";
				swapper["targetname"] = swapperName;
				swapper["SkyboxName"] = region.SkyName;
				// skybox_swapper is a point entity; place it at the first box center (position is irrelevant).
				swapper.Origin = (region.Boxes[0].mins + region.Boxes[0].maxs) * 0.5f;
				sourceBsp.Entities.Add(swapper);

				var triggerModelIndex = CreateBoxTriggerModel(region.Boxes);

				var trigger = new Entity();
				trigger.ClassName = "trigger_multiple";
				trigger["model"] = $"*{triggerModelIndex}";
				trigger["spawnflags"] = "1"; // SF_TRIGGER_ALLOW_CLIENTS
				trigger["wait"] = "0";
				trigger.connections.Add(new Entity.EntityConnection()
				{
					name = "OnStartTouch",
					target = swapperName,
					action = "Trigger",
					param = null,
					delay = 0,
					fireOnce = -1
				});
				sourceBsp.Entities.Add(trigger);
			}

			logger.Log($"Converted {skyboxSwapPlan.Regions.Count} skybox swap regions");
		}

		// Builds one brush model from several AABB boxes (a single leaf referencing all the box brushes), so a
		// single trigger entity can cover a skybox region's whole visibility volume. Returns the model index.
		private int CreateBoxTriggerModel(List<(Vector3 mins, Vector3 maxs)> boxes)
		{
			var firstLeafBrush = sourceBsp.LeafBrushes.Count;
			var mins = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
			var maxs = new Vector3(float.MinValue, float.MinValue, float.MinValue);

			foreach (var (boxMins, boxMaxs) in boxes)
			{
				var brushIndex = CreateBoxBrush(boxMins, boxMaxs);
				sourceBsp.LeafBrushes.Add(brushIndex);
				mins = Vector3.Min(mins, boxMins);
				maxs = Vector3.Max(maxs, boxMaxs);
			}

			return CreateBrushModel(firstLeafBrush, boxes.Count, mins, maxs);
		}

		// Replicates Q3's Think_SpawnNewDoorTrigger bounds expansion: find the thinnest axis and expand it by 120 units each direction
		private static void ExpandDoorTriggerBounds(ref Vector3 mins, ref Vector3 maxs)
		{
			var extentX = maxs.X() - mins.X();
			var extentY = maxs.Y() - mins.Y();
			var extentZ = maxs.Z() - mins.Z();

			int best = 0;
			var minExtent = extentX;
			if (extentY < minExtent) { minExtent = extentY; best = 1; }
			if (extentZ < minExtent) { best = 2; }

			if (best == 0)
			{
				mins = new Vector3(mins.X() - 120, mins.Y(), mins.Z());
				maxs = new Vector3(maxs.X() + 120, maxs.Y(), maxs.Z());
			}
			else if (best == 1)
			{
				mins = new Vector3(mins.X(), mins.Y() - 120, mins.Z());
				maxs = new Vector3(maxs.X(), maxs.Y() + 120, maxs.Z());
			}
			else
			{
				mins = new Vector3(mins.X(), mins.Y(), mins.Z() - 120);
				maxs = new Vector3(maxs.X(), maxs.Y(), maxs.Z() + 120);
			}
		}

		// Creates an AABB brush entity (6 planes) with its own orphan leaf + head node, returns the new model index
		private int CreateBoxTrigger(Vector3 mins, Vector3 maxs)
		{
			return CreateBrushModel(CreateBoxBrush(mins, maxs), mins, maxs);
		}

		// Creates a solid AABB brush (6 axis-aligned planes) and returns its brush index. The caller wraps it
		// (or several of them) in a brush model. CONTENTS_SOLID is required for trigger touch traces to hit it.
		private int CreateBoxBrush(Vector3 mins, Vector3 maxs)
		{
			var normals = new Vector3[]
			{
				new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
				new Vector3(0, 1, 0), new Vector3(0, -1, 0),
				new Vector3(0, 0, 1), new Vector3(0, 0, -1)
			};
			var distances = new float[]
			{
				maxs.X(), -mins.X(),
				maxs.Y(), -mins.Y(),
				maxs.Z(), -mins.Z()
			};

			var brushSideStart = sourceBsp.BrushSides.Count;
			for (var i = 0; i < 6; i++)
			{
				var planeIndex = CreatePlane(normals[i], distances[i]);

				var sideData = new byte[BrushSide.GetStructLength(sourceBsp.MapType)];
				var side = new BrushSide(sideData, sourceBsp.BrushSides);
				side.PlaneIndex = planeIndex;
				side.TextureIndex = 0;
				side.DisplacementIndex = 0;
				side.IsBevel = false;
				sourceBsp.BrushSides.Add(side);
			}

			var brushIndex = sourceBsp.Brushes.Count;
			var brushData = new byte[Brush.GetStructLength(sourceBsp.MapType)];
			var brush = new Brush(brushData, sourceBsp.Brushes);
			brush.FirstSideIndex = brushSideStart;
			brush.NumSides = 6;
			brush.Contents = (int)SourceContentsFlags.CONTENTS_SOLID;
			sourceBsp.Brushes.Add(brush);

			return brushIndex;
		}

		// Wraps a single brush in its own orphan leaf + degenerate head node + model, returning the new
		// model index (referenced by entities as "*index"). Shared by CreateBoxTrigger and the lava triggers.
		private int CreateBrushModel(int brushIndex, Vector3 mins, Vector3 maxs)
		{
			var leafBrushIndex = sourceBsp.LeafBrushes.Count;
			sourceBsp.LeafBrushes.Add(brushIndex);

			return CreateBrushModel(leafBrushIndex, 1, mins, maxs);
		}

		// Wraps a contiguous range of already-added leaf brushes in a single orphan leaf + degenerate head
		// node + model. Lets one brush model (one trigger entity) span several disjoint boxes.
		private int CreateBrushModel(int firstLeafBrush, int numLeafBrushes, Vector3 mins, Vector3 maxs)
		{
			var leafData = new byte[Leaf.GetStructLength(sourceBsp.MapType)];
			var leaf = new Leaf(leafData, sourceBsp.Leaves);
			leaf.Minimums = mins;
			leaf.Maximums = maxs;
			leaf.FirstMarkBrushIndex = firstLeafBrush;
			leaf.NumMarkBrushIndices = numLeafBrushes;
			leaf.FirstMarkFaceIndex = 0;
			leaf.NumMarkFaceIndices = 0;
			leaf.LeafWaterDataID = -1;
			leaf.Area = 0;
			leaf.Contents = 0;
			var leafIndex = sourceBsp.Leaves.Count;
			sourceBsp.Leaves.Add(leaf);

			var nodeData = new byte[Node.GetStructLength(sourceBsp.MapType)];
			var node = new Node(nodeData, sourceBsp.Nodes);
			node.Child1Index = -leafIndex - 1;
			node.Child2Index = -leafIndex - 1;
			sourceBsp.Nodes.Add(node);

			var modelData = new byte[Model.GetStructLength(sourceBsp.MapType)];
			var model = new Model(modelData, sourceBsp.Models);
			model.HeadNodeIndex = sourceBsp.Nodes.Count - 1;
			model.Minimums = mins;
			model.Maximums = maxs;
			model.Origin = new Vector3(0f, 0f, 0f);
			model.FirstFaceIndex = 0;
			model.NumFaces = 0;
			sourceBsp.Models.Add(model);

			return sourceBsp.Models.Count - 1;
		}

		// depthForOpaque (Q3 distance at which the fog becomes fully opaque) that maps to a volumetric
		// density of 1.0. Thicker fog (smaller depth) -> higher density. Source's density slider runs
		// 0..1.5, so this is a heuristic mapping that may need per-map tuning.
		private const float FogDensityReference = 512f;

		// Replaces each Quake 3 fog brush with an obb_volumefog point entity placed at the brush's center,
		// sized to its axis-aligned bounds. Q3 fog is an absorption model (flat color + opaque
		// distance); Strata's is a scattering volumetric, so the color maps to emissive_color and the opaque
		// distance is converted to an approximate density.
		private void ConvertObbFog()
		{
			// Quake brushes map 1:1 onto the leading source brushes (same index, shared brush side range).
			var brushCount = Math.Min(quakeBsp.Brushes.Count, sourceBsp.Brushes.Count);
			for (var i = 0; i < brushCount; i++)
			{
				if (!IsFogBrush(quakeBsp.Brushes[i], out var fogParms))
					continue;

				var fogBrush = sourceBsp.Brushes[i];
				if (!TryComputeBrushBounds(fogBrush.FirstSideIndex, fogBrush.NumSides, out var mins, out var maxs))
					continue;

				// Expand thin fog layers downward to a minimum height (lower the bottom, keep the original top
				// in place) so the volume spans enough view froxels to avoid flickering as the camera pans.
				if (maxs.Z() - mins.Z() < options.fogMinHeight)
					mins = new Vector3(mins.X(), mins.Y(), maxs.Z() - options.fogMinHeight);

				var center = (mins + maxs) * 0.5f;
				var size = maxs - mins;

				var color = fogParms.color;
				var density = Math.Clamp(FogDensityReference / Math.Max(fogParms.depthForOpaque, 1f), 0.01f, 1.5f);

				var fog = new Entity();
				fog.ClassName = "obb_volumefog";
				fog.Origin = center;
				// obb_volumefog stores full (not half) extents as width=X, depth=Y, height=Z (see COBBVolumeFog::Spawn)
				fog["width"] = FormatFloat(size.X());
				fog["depth"] = FormatFloat(size.Y());
				fog["height"] = FormatFloat(size.Z());
				fog["density"] = FormatFloat(density);
				// Q3 fog is a flat, light-independent color. The volumetric's scattering term only shows color
				// where the volume is lit, which converted Q3 maps usually aren't (no sky/dynamic light feeds the
				// volumetric), so scattering alone renders black. Map the color to emissive_color instead so it's
				// always visible, matching Q3's constant-color fog. KeyValue parses "R G B A" as 0-255; alpha
				// scales the color's contribution.
				fog["emissive_color"] = $"{FormatFloat(color.X() * 255f)} {FormatFloat(color.Y() * 255f)} {FormatFloat(color.Z() * 255f)} 255";
				sourceBsp.Entities.Add(fog);
			}
		}

		// A Quake 3 brush is a fog brush when its shader declares fogParms (matches IsFogFace).
		private bool IsFogBrush(Brush qBrush, out Shader.FogParms fogParms)
		{
			fogParms = null;
			if (string.IsNullOrEmpty(qBrush.Texture.Name))
				return false;

			if (shaderDict.TryGetValue(qBrush.Texture.Name, out var shader) && shader.fogParms != null)
			{
				fogParms = shader.fogParms;
				return true;
			}

			return false;
		}

		// Formats a float for an entity keyvalue using invariant culture so locales using ',' as the
		// decimal separator don't produce values the engine can't parse.
		private static string FormatFloat(float value)
		{
			return value.ToString(CultureInfo.InvariantCulture);
		}

		// Duplicates each Quake 3 lava brush (CONTENTS_LAVA) into a trigger_hurt brush entity that kills the
		// player on contact. The original lava brush is left intact; this adds a parallel trigger volume.
		private void ConvertLavaTriggers()
		{
			// Quake brushes map 1:1 onto the leading source brushes (same index, shared brush side range);
			// any brushes appended later (e.g. func_door box triggers) sit past quakeBsp.Brushes.Count.
			var brushCount = Math.Min(quakeBsp.Brushes.Count, sourceBsp.Brushes.Count);
			for (var i = 0; i < brushCount; i++)
			{
				var q3Contents = (Q3ContentsFlags)quakeBsp.Brushes[i].Texture.Contents;
				if (!q3Contents.HasFlag(Q3ContentsFlags.CONTENTS_LAVA))
					continue;

				var lavaBrush = sourceBsp.Brushes[i];
				if (!TryComputeBrushBounds(lavaBrush.FirstSideIndex, lavaBrush.NumSides, out var mins, out var maxs))
					continue;

				var triggerModelIndex = CreateLavaTriggerModel(lavaBrush, mins, maxs);

				var trigger = new Entity();
				trigger.ClassName = "trigger_hurt";
				trigger["model"] = $"*{triggerModelIndex}";
				trigger["damage"] = "200"; // Enough to kill/respawn the player on contact
				trigger["spawnflags"] = "1"; // SF_TRIGGER_ALLOW_CLIENTS
				sourceBsp.Entities.Add(trigger);
			}
		}

		// Duplicates a lava brush's geometry into a new brush (with its own side range) flagged CONTENTS_SOLID
		// so the engine's trigger touch trace registers, then wraps it in a brush model.
		private int CreateLavaTriggerModel(Brush lavaBrush, Vector3 mins, Vector3 maxs)
		{
			var brushSideStart = sourceBsp.BrushSides.Count;
			for (var i = 0; i < lavaBrush.NumSides; i++)
			{
				var src = sourceBsp.BrushSides[lavaBrush.FirstSideIndex + i];

				var sideData = new byte[BrushSide.GetStructLength(sourceBsp.MapType)];
				var side = new BrushSide(sideData, sourceBsp.BrushSides);
				side.PlaneIndex = src.PlaneIndex;
				side.TextureIndex = src.TextureIndex;
				side.DisplacementIndex = 0;
				side.IsBevel = false;
				sourceBsp.BrushSides.Add(side);
			}

			var brushIndex = sourceBsp.Brushes.Count;
			var brushData = new byte[Brush.GetStructLength(sourceBsp.MapType)];
			var brush = new Brush(brushData, sourceBsp.Brushes);
			brush.FirstSideIndex = brushSideStart;
			brush.NumSides = lavaBrush.NumSides;
			// CONTENTS_SOLID (a MASK_SOLID bit) is required for the trigger touch trace to hit the brush
			brush.Contents = (int)SourceContentsFlags.CONTENTS_SOLID;
			sourceBsp.Brushes.Add(brush);

			return CreateBrushModel(brushIndex, mins, maxs);
		}

		// Computes an AABB enclosing the convex brush defined by its outward-facing side planes, by intersecting
		// every triple of planes and keeping the corner points that lie inside (or on) all of them. Returns
		// false for degenerate brushes that produce no valid corner points.
		private bool TryComputeBrushBounds(int firstSide, int numSides, out Vector3 mins, out Vector3 maxs)
		{
			mins = new Vector3(0f, 0f, 0f);
			maxs = new Vector3(0f, 0f, 0f);

			if (numSides < 4)
				return false;

			var normals = new Vector3[numSides];
			var distances = new float[numSides];
			for (var i = 0; i < numSides; i++)
			{
				var plane = sourceBsp.Planes[sourceBsp.BrushSides[firstSide + i].PlaneIndex];
				normals[i] = plane.Normal;
				distances[i] = plane.Distance;
			}

			const float EPSILON = 0.1f;
			var found = false;
			float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
			float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

			for (var i = 0; i < numSides; i++)
			{
				for (var j = i + 1; j < numSides; j++)
				{
					for (var k = j + 1; k < numSides; k++)
					{
						if (!TryIntersectPlanes(normals[i], distances[i], normals[j], distances[j], normals[k], distances[k], out var point))
							continue;

						// Keep only corners that lie within the brush (inside every outward-facing half-space).
						var inside = true;
						for (var m = 0; m < numSides; m++)
						{
							if (Vector3.Dot(normals[m], point) - distances[m] > EPSILON)
							{
								inside = false;
								break;
							}
						}
						if (!inside)
							continue;

						minX = Math.Min(minX, point.X()); minY = Math.Min(minY, point.Y()); minZ = Math.Min(minZ, point.Z());
						maxX = Math.Max(maxX, point.X()); maxY = Math.Max(maxY, point.Y()); maxZ = Math.Max(maxZ, point.Z());
						found = true;
					}
				}
			}

			if (!found)
				return false;

			mins = new Vector3(minX, minY, minZ);
			maxs = new Vector3(maxX, maxY, maxZ);
			return true;
		}

		// Solves for the single point where three planes (Dot(normal, p) = distance) intersect, via Cramer's
		// rule. Returns false when the planes are parallel/coincident (no unique intersection).
		private static bool TryIntersectPlanes(Vector3 n1, float d1, Vector3 n2, float d2, Vector3 n3, float d3, out Vector3 point)
		{
			var cross23 = Vector3.Cross(n2, n3);
			var denom = Vector3.Dot(n1, cross23);
			if (Math.Abs(denom) < 1e-6f)
			{
				point = new Vector3(0f, 0f, 0f);
				return false;
			}

			var cross31 = Vector3.Cross(n3, n1);
			var cross12 = Vector3.Cross(n1, n2);
			point = (cross23 * d1 + cross31 * d2 + cross12 * d3) / denom;
			return true;
		}

		// TODO: Add face references in order for showtriggers_toggle to work?
		// Creates a head node using the leaf that references the brush index (seems to be required to get trigger collisions working)
		private bool TryCreateHeadNode(int brushIndex, out int nodeIndex)
		{
			var leafIndex = FindLeafIndex(brushIndex);
			if (leafIndex < 0)
			{
				nodeIndex = -1;
				return false;
			}

			var data = new byte[Node.GetStructLength(sourceBsp.MapType)];
			var node = new Node(data, sourceBsp.Nodes);

			node.Child1Index = -leafIndex - 1;
			node.Child2Index = -leafIndex - 1;

			// Note: This fixes translucent brush entities not rendering
			var leaf = sourceBsp.Leaves[leafIndex];
			if (leaf.NumMarkFaceIndices > 0)
			{
				// For some reason the faces are in reverse order
				node.FirstFaceIndex = (int)sourceBsp.LeafFaces[leaf.FirstMarkFaceIndex] - (leaf.NumMarkFaceIndices - 1);
				node.NumFaceIndices = leaf.NumMarkFaceIndices;
			}

			sourceBsp.Nodes.Add(node);

			nodeIndex = sourceBsp.Nodes.Count - 1;
			return true;
		}

		// Finds a leaf that references the brush index
		private int FindLeafIndex(int brushIndex)
		{
			var leafBrushes = sourceBsp.LeafBrushes;

			for (var i = 0; i < sourceBsp.Leaves.Count; i++)
			{
				var leaf = sourceBsp.Leaves[i];
				for (var j = 0; j < leaf.NumMarkBrushIndices; j++)
				{
					if (leafBrushes[leaf.FirstMarkBrushIndex + j] == brushIndex)
						return i;
				}
			}

			return -1;
		}

		private void ConvertBrushes()
		{
			foreach (var qBrush in quakeBsp.Brushes)
			{
				var data = new byte[Brush.GetStructLength(sourceBsp.MapType)];
				var sBrush = new Brush(data, sourceBsp.Brushes);

				sBrush.FirstSideIndex = qBrush.FirstSideIndex;
				sBrush.NumSides = qBrush.NumSides;
				sBrush.Contents = GetBrushContents(qBrush.Texture);

				// Tag fog brushes as CONTENTS_FOG (non-solid) so the engine picks them up as bounds-based fog
				// volumes (the %compileFog equivalent). The Fog surface still renders the outside view.
				if (IsFogBrush(qBrush, out _))
				{
					sBrush.Contents &= ~(int)SourceContentsFlags.CONTENTS_SOLID;
					sBrush.Contents |= (int)SourceContentsFlags.CONTENTS_FOG;

					// Point every side at the brush's fog texture. The engine reads a fog volume's appearance
					// ($fogcolor / $fogdepthforopaque) off the material of one of the brush's sides
					// (see the engine's LoadFogVolumes), but a Q3 fog brush defines the fog at the brush level -
					// R_LoadFogs takes the shader from the FOGS lump by brushNum, independent of the side textures -
					// so its sides can be noshader/caulk. Overriding all sides to the fog material (the sides are
					// nodraw, so this has no visual effect) guarantees the engine always finds the fog appearance.
					for (var j = 0; j < qBrush.NumSides; j++)
						fogBrushSideTexture[qBrush.FirstSideIndex + j] = qBrush.Texture;
				}

				sourceBsp.Brushes.Add(sBrush);
			}
		}

		private int GetBrushContents(Texture texture)
		{
			// TODO: Handle other texture contents flags
			var sourceContents = SourceContentsFlags.CONTENTS_EMPTY;
			var q3Contents = (Q3ContentsFlags)texture.Contents;

			if (q3Contents.HasFlag(Q3ContentsFlags.CONTENTS_SOLID))
				sourceContents |= SourceContentsFlags.CONTENTS_SOLID;

			if (q3Contents.HasFlag(Q3ContentsFlags.CONTENTS_LAVA) ||
				q3Contents.HasFlag(Q3ContentsFlags.CONTENTS_WATER))
			{
				sourceContents |= SourceContentsFlags.CONTENTS_WATER;
			}

			if (q3Contents.HasFlag(Q3ContentsFlags.CONTENTS_SLIME))
				sourceContents |= SourceContentsFlags.CONTENTS_SLIME;

			if (q3Contents.HasFlag(Q3ContentsFlags.CONTENTS_PLAYERCLIP))
				sourceContents |= SourceContentsFlags.CONTENTS_PLAYERCLIP;

			if (q3Contents.HasFlag(Q3ContentsFlags.CONTENTS_TRANSLUCENT))
				sourceContents |= SourceContentsFlags.CONTENTS_TRANSLUCENT;

			return (int)sourceContents;
		}

		private void ConvertBrushSides()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(BrushSide.GetIndexForLump(sourceBsp.MapType), 1);

			for (var i = 0; i < quakeBsp.BrushSides.Count; i++)
			{
				var qBrushSide = quakeBsp.BrushSides[i];
				var data = new byte[BrushSide.GetStructLength(sourceBsp.MapType)];
				var sBrushSide = new BrushSide(data, sourceBsp.BrushSides);

				// Fog brush sides all use the brush's fog texture so the engine reads $fogcolor off whichever side
				// it picks (see ConvertBrushes); every other side keeps its own texture.
				var texture = fogBrushSideTexture.TryGetValue(i, out var fogTexture) ? fogTexture : qBrushSide.Texture;

				sBrushSide.PlaneIndex = qBrushSide.PlaneIndex;
				sBrushSide.TextureIndex = GetBrushSideTextureInfoIndex(qBrushSide, texture);
				sBrushSide.DisplacementIndex = 0;
				sBrushSide.IsBevel = false;

				sourceBsp.BrushSides.Add(sBrushSide);
			}
		}

		// Lookup texture info index using the given texture's name. If it doesn't exist, create a new texture info
		// (using the brush side's plane for the UV axes).
		private int GetBrushSideTextureInfoIndex(BrushSide qBrushSide, Texture texture)
		{
			var textureIndex = LookupTextureInfoIndex(texture.Name);
			if (textureIndex > -1)
				return textureIndex;

			(var uAxis, var vAxis) = GetTextureVectorsWithNormal(qBrushSide.Plane.Normal);
			return CreateTextureInfo(texture, uAxis, vAxis);
		}

		private void ConvertFaces()
		{
			if (!options.oldBSP)
			{
				SetLumpVersionNumber(Face.GetIndexForLump(sourceBsp.MapType), 2);
				SetLumpVersionNumber(Displacement.GetIndexForLump(sourceBsp.MapType), 1);
				SetLumpVersionNumber(Edge.GetIndexForLump(sourceBsp.MapType), 1);
				SetLumpVersionNumber(NumList.GetIndexForIndicesLump(sourceBsp.MapType, out _), 1);
				SetLumpVersionNumber(Primitive.GetIndexForLump(sourceBsp.MapType), 1);
				SetLumpVersionNumber(NumList.GetIndexForPrimitiveIndicesLump(sourceBsp.MapType, out _), 1);

				AddPrimitiveTextureInfoToGameLumps();
			}
			else
				SetLumpVersionNumber(Face.GetIndexForLump(sourceBsp.MapType), 1);

			// Map needs at least one surface edge to load
			//CreateEdge(default, default, 0);

			for (var faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
				// TODO: Handle different face types
				var qFace = quakeBsp.Faces[faceIndex];

				sourceBsp.Normals.Add(qFace.Normal);

				switch (qFace.Type)
				{
					case FaceType.Polygon:
					case FaceType.Mesh: // Used for Q3 models
						ConvertPolygon(faceIndex);
						break;
					case FaceType.Patch:
						ConvertPatch(faceIndex);
						break;
					case FaceType.Billboard:
					default:
						logger.Log("Unsupported face type: " + qFace.Type);
						break;
				}
			}
		}

		private void AddPrimitiveTextureInfoToGameLumps()
		{
			var lumpInfo = new LumpInfo();
			lumpInfo.ident = (int)GameLumpType.pmti;
			lumpInfo.flags = 0; // Don't compress this game lump
			sourceBsp.GameLump.Add(GameLumpType.pmti, lumpInfo);
		}

		private void ConvertPolygon(int faceIndex)
		{
			var qFace = quakeBsp.Faces[faceIndex];

			if (IsFogFace(qFace))
			{
				// A fog shader that also carries visible stages (e.g. the scrolling clouds on textures/sfx/hellfog)
				// draws those over the fog boundary in Q3. Reproduce that as a one-sided overlay surface; the fog
				// volume itself is still the CONTENTS_FOG brush + Fog overlay. Skipped in obb fog mode (no overlay
				// material is generated there) and when disabled via options.noFogOverlay.
				if (!options.useObbFog && !options.noFogOverlay && TryCreateFogOverlayFace(faceIndex))
					return;

				// Otherwise fog brush faces are not drawn. The engine tracks the brush as a bounds-based fog volume
				// (CONTENTS_FOG) and composites a depth-clipped fog overlay for it, giving a single unified fog
				// that reads correctly from inside or outside the volume. A drawn translucent face would fog
				// everything behind it and double up with the overlay. The brush stays tagged CONTENTS_FOG and its
				// brush sides keep the fog material, so the engine still reads the fog appearance ($fogcolor /
				// $fogdepthforopaque). (obb fog mode also drops the face, building froxel volume
				// entities instead.)
				splitFaceDict[faceIndex] = Array.Empty<int>();
				return;
			}

			var sFace = CreateFace();
			// TODO: Re-use brush planes?
			sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
			sFace.TextureInfoIndex = CreateTextureInfo(qFace, qFace.FirstIndexIndex);
			sFace.DisplacementIndex = -1;

			// Surface edges
			(var surfEdgeIndex, var numEdges) = CreateSurfaceEdges(faceIndex);
			sFace.FirstEdgeIndexIndex = surfEdgeIndex;
			sFace.NumEdgeIndices = numEdges;

			// Primitives
			sFace.FirstPrimitive = CreatePrimitive(qFace.Vertices.ToArray(), qFace.Indices.ToArray(), qFace);
			sFace.NumPrimitives = 1;

			splitFaceDict[faceIndex] = new int[] { sourceBsp.Faces.Count - 1 };
		}

		// Draws a fog brush face as the fog shader's visible overlay stages (e.g. the scrolling cloud layers on
		// textures/sfx/hellfog) instead of dropping it. The overlay uses a sibling material (the fog texture name
		// is reserved for the Fog appearance material) and is left one-sided: Q3 fog shaders default to front-face
		// culling, so the overlay only shows on the boundary sides facing the viewer, never from inside the volume.
		// Returns false when the fog shader has no visible stages, so the caller drops the face as before.
		private bool TryCreateFogOverlayFace(int faceIndex)
		{
			var qFace = quakeBsp.Faces[faceIndex];
			if (!shaderDict.TryGetValue(qFace.Texture.Name, out var shader) || !MaterialConverter.FogShaderHasOverlay(shader))
				return false;

			var overlayName = MaterialConverter.GetFogOverlayTextureName(qFace.Texture.Name);
			if (!textureDataLookup.ContainsKey(overlayName))
				CreateTextureData(overlayName);

			var sFace = CreateFace();
			sFace.PlaneIndex = CreatePlane(qFace);
			sFace.TextureInfoIndex = CreateTextureInfo(qFace, qFace.FirstIndexIndex, overlayName);
			sFace.DisplacementIndex = -1;

			// Surface edges
			(var surfEdgeIndex, var numEdges) = CreateSurfaceEdges(faceIndex);
			sFace.FirstEdgeIndexIndex = surfEdgeIndex;
			sFace.NumEdgeIndices = numEdges;

			// Primitives
			sFace.FirstPrimitive = CreatePrimitive(qFace.Vertices.ToArray(), qFace.Indices.ToArray(), qFace);
			sFace.NumPrimitives = 1;

			splitFaceDict[faceIndex] = new int[] { sourceBsp.Faces.Count - 1 };
			return true;
		}

		private bool IsFogFace(Face qFace)
		{
			return shaderDict.TryGetValue(qFace.Texture.Name, out var shader) && shader.fogParms != null;
		}

		private void ConvertFaces_SplitFaces()
		{
			if (!options.oldBSP)
			{
				SetLumpVersionNumber(Face.GetIndexForLump(sourceBsp.MapType), 2);
				SetLumpVersionNumber(Displacement.GetIndexForLump(sourceBsp.MapType), 1);
				SetLumpVersionNumber(Edge.GetIndexForLump(sourceBsp.MapType), 1);
				SetLumpVersionNumber(NumList.GetIndexForIndicesLump(sourceBsp.MapType, out _), 1);
			}
			else
				SetLumpVersionNumber(Face.GetIndexForLump(sourceBsp.MapType), 1);

			for (var faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
				var qFace = quakeBsp.Faces[faceIndex];

				sourceBsp.Normals.Add(qFace.Normal);

				switch (qFace.Type)
				{
					case FaceType.Polygon:
					case FaceType.Mesh: // Used for Q3 models
					case FaceType.Billboard:
						ConvertPolygon_SplitFaces(faceIndex);
						break;
					case FaceType.Patch:
						ConvertPatch(faceIndex);
						break;
					default:
						logger.Log("Unsupported face type: " + qFace.Type);
						break;
				}
			}
		}

		private void ConvertPolygon_SplitFaces(int faceIndex)
		{
			// Create a face for each triangle
			var qFace = quakeBsp.Faces[faceIndex];

			// TODO: Remove ToArray() calls
			var vertices = qFace.Vertices.ToArray();
			var indices = qFace.Indices.ToArray();

			splitFaceDict[faceIndex] = new int[indices.Length / 3];

			for (var i = 0; i < indices.Length; i += 3)
			{
				var sFace = CreateFace();
				sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
				sFace.TextureInfoIndex = CreateTextureInfo(qFace, i);
				sFace.DisplacementIndex = -1;

				sFace.FirstEdgeIndexIndex = sourceBsp.FaceEdges.Count;
				sFace.NumEdgeIndices = 3;

				var v1 = vertices[indices[i]];
				var v2 = vertices[indices[i + 1]];
				var v3 = vertices[indices[i + 2]];

				CreateEdge(v1, v2, faceIndex);
				CreateEdge(v2, v3, faceIndex);
				CreateEdge(v3, v1, faceIndex);

				splitFaceDict[faceIndex][i / 3] = sourceBsp.Faces.Count - 1;
			}
		}

		private Face CreateFace()
		{
			var data = new byte[Face.GetStructLength(sourceBsp.MapType)];
			var face = new Face(data, sourceBsp.Faces);

			face.PlaneSide = true;
			face.IsOnNode = false; // Set to false in order for face to be visible across multiple leaves?
			face.SurfaceFogVolumeID = -1;
			face.LightmapStyles = new byte[4]
			{
				0,
				255,
				255,
				255
			};
			face.Lightmap = 0;
			face.Area = 0; // TODO: Check if this needs to be computed
			face.LightmapStart = new Vector2();
			face.LightmapSize = new Vector2(); // TODO: Set to 128x128?
			face.OriginalFaceIndex = -1; // Ignore since Quake 3 maps don't have split faces
			face.FirstPrimitive = 0;
			face.NumPrimitives = 0;
			face.SmoothingGroups = 0;

			sourceBsp.Faces.Add(face);

			return face;
		}

		// Records the patch faces belonging to trigger entities. A Quake 3 trigger volume can be built from
		// bezier patches instead of brushes, but a patch has no good Source equivalent: it converts to a
		// collision displacement, which is solid. That both fails to act as a trigger and blocks movement on
		// whatever sits beneath it (e.g. a common/slick floor under the trigger - see GetPhysicsSurfaceFlags).
		// We can't meaningfully convert these, so flag them to be emitted without collision in ConvertPatch.
		// Must run before ConvertEntities, which rewrites the Q3 trigger classnames.
		private void MarkTriggerPatchFaces()
		{
			foreach (var entity in quakeBsp.Entities)
			{
				if (!entity.ClassName.StartsWith("trigger", StringComparison.OrdinalIgnoreCase))
					continue;

				var modelNum = entity.ModelNumber;
				if (modelNum <= 0 || modelNum >= quakeBsp.Models.Count) // Skip worldspawn (0) and non-brush triggers
					continue;

				var model = quakeBsp.Models[modelNum];
				for (var f = 0; f < model.NumFaces; f++)
				{
					var faceIndex = model.FirstFaceIndex + f;
					if (faceIndex < 0 || faceIndex >= quakeBsp.Faces.Count)
						continue;

					if (quakeBsp.Faces[faceIndex].Type == FaceType.Patch)
						triggerPatchFaces.Add(faceIndex);
				}
			}
		}

		private void ConvertPatch(int faceIndex)
		{
			// Trigger patches can't be solid (they'd block the surface beneath and never fire), so emit them
			// as render-only primitives with no collision. Trigger entities don't render in Source, so the
			// leftover invisible faces are harmless - and keeping them preserves the model's face range.
			if (triggerPatchFaces.Contains(faceIndex))
			{
				ConvertPatchAsPrimitive(faceIndex, skipCollision: true);
				return;
			}

			if (options.patchesAsPrimitives)
			{
				ConvertPatchAsPrimitive(faceIndex);
				return;
			}

			var qFace = quakeBsp.Faces[faceIndex];
			var numPatchesWidth = ((int)qFace.PatchSize.X - 1) / 2;
			var numPatchesHeight = ((int)qFace.PatchSize.Y - 1) / 2;
			splitFaceDict[faceIndex] = new int[numPatchesWidth * numPatchesHeight];

			var currentPatch = 0;
			for (var y = 0; y < qFace.PatchSize.Y - 1; y += 2)
			{
				for (var x = 0; x < qFace.PatchSize.X - 1; x += 2)
				{
					var patchStartVertex = qFace.FirstVertexIndex + x + y * (int)qFace.PatchSize.X;
					var patchFaceIndex = CreatePatch(faceIndex, patchStartVertex);

					splitFaceDict[faceIndex][currentPatch] = patchFaceIndex;
					currentPatch++;
				}
			}
		}

		private void ConvertPatchAsPrimitive(int faceIndex, bool skipCollision = false)
		{
			var qFace = quakeBsp.Faces[faceIndex];

			// Each sub-patch produces a visible primitive face (which supports the per-vertex UV
			// mapping that displacements can't) plus an invisible, collision-only displacement face.
			// skipCollision omits the collision face (used for trigger patches - see ConvertPatch).
			var faceIndices = new List<int>();
			for (var y = 0; y < qFace.PatchSize.Y - 1; y += 2)
			{
				for (var x = 0; x < qFace.PatchSize.X - 1; x += 2)
				{
					var patchStartVertex = qFace.FirstVertexIndex + x + y * (int)qFace.PatchSize.X;

					faceIndices.Add(CreatePatchFaceAsPrimitive(faceIndex, patchStartVertex));

					if (skipCollision)
						continue;

					var collisionFaceIndex = CreatePatchCollisionFace(faceIndex, patchStartVertex);
					if (collisionFaceIndex >= 0)
						faceIndices.Add(collisionFaceIndex);
				}
			}

			splitFaceDict[faceIndex] = faceIndices.ToArray();
		}

		// Creates an invisible displacement that matches the patch geometry purely for collision.
		// Mirrors CreatePatch, but forces the invisible displacement material so it doesn't render
		// on top of the primitive visual mesh.
		private int CreatePatchCollisionFace(int qFaceIndex, int patchStartVertex)
		{
			var qFace = quakeBsp.Faces[qFaceIndex];

			// Mirror CreatePatchDisplacement's skip rule so we never emit a face that references a
			// displacement we don't end up creating. Note the primitive pass may already have
			// rewritten tool textures to the (tools/) invisible displacement material.
			if (options.noToolDisplacements && qFace.Texture.Name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
				return -1;

			var patchWidth = (int)qFace.PatchSize.X;
			var faceVerts = new Vertex[]
			{
				quakeBsp.Vertices[patchStartVertex],
				quakeBsp.Vertices[patchStartVertex + 2],
				quakeBsp.Vertices[patchStartVertex + 2 + 2 * patchWidth],
				quakeBsp.Vertices[patchStartVertex + 2 * patchWidth]
			};

			var sFaceIndex = CreatePatchFace(faceVerts, qFaceIndex, forceInvisible: true);
			CreatePatchDisplacement(sFaceIndex, faceVerts, patchWidth, patchStartVertex, qFace);

			return sFaceIndex;
		}

		private int CreatePatch(int qFaceIndex, int patchStartVertex)
		{
			var qFace = quakeBsp.Faces[qFaceIndex];
			var patchWidth = (int)qFace.PatchSize.X;
			var faceVerts = new Vertex[]
			{
				quakeBsp.Vertices[patchStartVertex],
				quakeBsp.Vertices[patchStartVertex + 2],
				quakeBsp.Vertices[patchStartVertex + 2 + 2 * patchWidth],
				quakeBsp.Vertices[patchStartVertex + 2 * patchWidth]
			};

			var sFaceIndex = CreatePatchFace(faceVerts, qFaceIndex);
			CreatePatchDisplacement(sFaceIndex, faceVerts, patchWidth, patchStartVertex, qFace);

			return sFaceIndex;
		}

		private int CreatePatchFace(Vertex[] faceVerts, int faceIndex, bool forceInvisible = false)
		{
			var sFace = CreateFace();

			var dispIndex = sourceBsp.Displacements.Count;
			sFace.DisplacementIndex = dispIndex;

			// Create face plane
			var v1 = faceVerts[0].position - faceVerts[1].position;
			var v2 = faceVerts[0].position - faceVerts[2].position;
			var normal = Vector3.Cross(v1, v2).GetNormalized();
			var dist = Vector3.Dot(faceVerts[0].position, normal);
			sFace.PlaneIndex = CreatePlane(normal, dist);

			(var uAxis, var vAxis) = GetTextureVectorsFromVertices(faceVerts[0], faceVerts[1], faceVerts[3], normal);

			if (forceInvisible)
			{
				// Collision-only face: use the invisible displacement material so it doesn't render,
				// but preserve physics-relevant surface flags (e.g. SURF_SLICK) from the original
				// patch texture so slick ice physics still apply to the collision displacement.
				var qFace = quakeBsp.Faces[faceIndex];
				sFace.TextureInfoIndex = CreateInvisibleDisplacementTextureInfo(uAxis, vAxis, GetPhysicsSurfaceFlags(qFace.Texture));
			}
			else
			{
				var qFace = quakeBsp.Faces[faceIndex];
				ReplaceToolTextureWithInvisibleDisplacement(qFace);
				sFace.TextureInfoIndex = CreateTextureInfo(qFace.Texture, uAxis, vAxis);
			}

			// Create face edges
			sFace.FirstEdgeIndexIndex = sourceBsp.FaceEdges.Count;
			sFace.NumEdgeIndices = 4;

			CreateEdge(faceVerts[0], faceVerts[3], faceIndex);
			CreateEdge(faceVerts[3], faceVerts[2], faceIndex);
			CreateEdge(faceVerts[2], faceVerts[1], faceIndex);
			CreateEdge(faceVerts[1], faceVerts[0], faceIndex);

			return sourceBsp.Faces.Count - 1;
		}

		private void ReplaceToolTextureWithInvisibleDisplacement(Face qFace)
		{
			var texture = qFace.Texture;
			if (texture.Name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
			{
				texture.Name = invisibleDisplacementTexture;
				if (LookupTextureDataIndex(invisibleDisplacementTexture) < 0)
					CreateTextureData(invisibleDisplacementTexture);
			}
		}

		private void CreatePatchDisplacement(int sFaceIndex, Vertex[] faceVerts, int patchWidth, int patchStartVertex, Face qFace)
		{
			if (options.noToolDisplacements && qFace.Texture.Name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
				return;

			var data = new byte[Displacement.GetStructLength(sourceBsp.MapType)];
			var displacement = new Displacement(data, sourceBsp.Displacements);

			var power = options.DisplacementPower;

			displacement.StartPosition = quakeBsp.Vertices[patchStartVertex].position;
			displacement.FirstVertexIndex = CreateDisplacementVertices(faceVerts, patchWidth, patchStartVertex, power);
			displacement.FirstTriangleIndex = CreateDisplacementTriangles(power);
			displacement.Power = power;
			displacement.MinimumTesselation = GetMinTesselation(qFace);
			displacement.SmoothingAngle = 0f;
			displacement.Contents = 1;
			displacement.FaceIndex = sFaceIndex;
			displacement.LightmapAlphaStart = 0;
			displacement.LightmapSamplePositionStart = 0;

			var allowedVerts = new uint[10];
			for (var i = 0; i < allowedVerts.Length; i++)
				allowedVerts[i] = 4294967295;

			displacement.AllowedVertices = allowedVerts;

			sourceBsp.Displacements.Add(displacement);
		}

		private int GetMinTesselation(Face qFace)
		{
			var texture = qFace.Texture.Name;
			var minTess = -2147483648;

			if (shaderDict.TryGetValue(texture, out var shader) && shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NONSOLID))
				minTess |= (int)DisplacementFlags.SURF_NOHULL_COLL | (int)DisplacementFlags.SURF_NORAY_COLL;
			else
				minTess |= (int)DisplacementFlags.SURF_NOBACKFACE_COLL; // Quake 3 patch collisions are one-sided

			return minTess;
		}

		private int CreateDisplacementVertices(Vertex[] faceVerts, int patchWidth, int patchStartVertex, int power)
		{
			var firstVertex = sourceBsp.DisplacementVertices.Count;

			var controlPoints = GetPatchControlPoints(patchStartVertex, patchWidth);
			var patch = new BezierPatch(controlPoints);

			// Create displacement vertices using bezier patch
			var subdiv = (1 << power) + 1;
			for (var y = 0; y < subdiv; y++)
			{
				for (var x = 0; x < subdiv; x++)
				{
					var widthT = x / (subdiv - 1f);
					var heightT = y / (subdiv - 1f);

					// Get point on quadratic bezier patch
					var point = patch.GetPoint(widthT, heightT);

					// Get interpolated position on face
					// Use scalar double lerp instead of Vector3.Lerp to avoid FMA rounding
					// differences in .NET 9+ that change the computed displacement offsets.
					var v1 = VectorUtil.LerpDouble(faceVerts[0].position, faceVerts[1].position, widthT);
					var v2 = VectorUtil.LerpDouble(faceVerts[3].position, faceVerts[2].position, widthT);
					var posOnFace = VectorUtil.LerpDouble(v1, v2, heightT);

					// Get point relative to face
					point -= posOnFace;

					CreateDisplacementVertex(point);
				}
			}

			return firstVertex;
		}

		// Get control points used to construct quadratic bezier patch
		private Vector3[] GetPatchControlPoints(int patchStartVertex, int patchWidth)
		{
			var controlPoints = new Vector3[9];
			for (var i = 0; i < 3; i++)
			{
				for (var j = 0; j < 3; j++)
				{
					controlPoints[i + j * 3] = quakeBsp.Vertices[patchStartVertex + i + j * patchWidth].position;
				}
			}

			return controlPoints;
		}

		private void CreateDisplacementVertex(Vector3 point)
		{
			var data = new byte[DisplacementVertex.GetStructLength(sourceBsp.MapType)];
			var dispVert = new DisplacementVertex(data, sourceBsp.DisplacementVertices);

			dispVert.Normal = VectorUtil.NormalizeDouble(point, out var mag);
			dispVert.Magnitude = mag;

			sourceBsp.DisplacementVertices.Add(dispVert);
		}

		private int CreateDisplacementTriangles(int power)
		{
			var firstTriangle = sourceBsp.DisplacementTriangles.Count;

			var numTriangles = (1 << (power)) * (1 << (power)) * 2;
			for (var i = 0; i < numTriangles; i++)
				sourceBsp.DisplacementTriangles.Add(6); // TODO: Set displacement flags?

			return firstTriangle;
		}

		private int CreatePatchFaceAsPrimitive(int qFaceIndex, int patchStartVertex)
		{
			var qFace = quakeBsp.Faces[qFaceIndex];
			var patchWidth = (int)qFace.PatchSize.X;

			var faceVerts = new Vertex[]
			{
				quakeBsp.Vertices[patchStartVertex],
				quakeBsp.Vertices[patchStartVertex + 2],
				quakeBsp.Vertices[patchStartVertex + 2 + 2 * patchWidth],
				quakeBsp.Vertices[patchStartVertex + 2 * patchWidth]
			};

			var sFace = CreateFace();
			sFace.DisplacementIndex = -1;

			var e1 = faceVerts[0].position - faceVerts[1].position;
			var e2 = faceVerts[0].position - faceVerts[2].position;
			var normal = Vector3.Cross(e1, e2).GetNormalized();
			var dist = Vector3.Dot(faceVerts[0].position, normal);
			sFace.PlaneIndex = CreatePlane(normal, dist);

			(var uAxis, var vAxis) = GetTextureVectorsFromVertices(faceVerts[0], faceVerts[1], faceVerts[3], normal);
			ReplaceToolTextureWithInvisibleDisplacement(qFace);
			sFace.TextureInfoIndex = CreateTextureInfo(qFace.Texture, uAxis, vAxis);

			sFace.FirstEdgeIndexIndex = sourceBsp.FaceEdges.Count;
			sFace.NumEdgeIndices = 4;
			CreateEdge(faceVerts[0], faceVerts[3], qFaceIndex);
			CreateEdge(faceVerts[3], faceVerts[2], qFaceIndex);
			CreateEdge(faceVerts[2], faceVerts[1], qFaceIndex);
			CreateEdge(faceVerts[1], faceVerts[0], qFaceIndex);

			if (options.noToolDisplacements && qFace.Texture.Name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
				return sourceBsp.Faces.Count - 1;

			var power = options.DisplacementPower;
			var subdiv = (1 << power) + 1;

			var posControlPoints = GetPatchControlPoints(patchStartVertex, patchWidth);
			var uv0ControlPoints = GetPatchUVControlPoints(patchStartVertex, patchWidth, v => v.uv0);
			var uv1ControlPoints = GetPatchUVControlPoints(patchStartVertex, patchWidth, v => v.uv1);

			var patch = new BezierPatch(posControlPoints);
			var vertexCount = subdiv * subdiv;
			var positions = new Vector3[vertexCount];
			var uvs = new Vector2[vertexCount];
			var lightmapUVs = new Vector2[vertexCount];

			for (var row = 0; row < subdiv; row++)
			{
				for (var col = 0; col < subdiv; col++)
				{
					var t = col / (subdiv - 1f);
					var s = row / (subdiv - 1f);
					var idx = col + row * subdiv;

					positions[idx] = patch.GetPoint(t, s);
					uvs[idx] = BezierPatch.GetUV(t, s, uv0ControlPoints);
					lightmapUVs[idx] = BezierPatch.GetUV(t, s, uv1ControlPoints);
				}
			}

			// Two CCW triangles per grid cell (i0=bottom-left, i1=bottom-right, i2=top-left, i3=top-right)
			var indices = new int[(subdiv - 1) * (subdiv - 1) * 6];
			var triIdx = 0;
			for (var row = 0; row < subdiv - 1; row++)
			{
				for (var col = 0; col < subdiv - 1; col++)
				{
					var i0 = col + row * subdiv;
					var i1 = (col + 1) + row * subdiv;
					var i2 = col + (row + 1) * subdiv;
					var i3 = (col + 1) + (row + 1) * subdiv;

					indices[triIdx++] = i0;
					indices[triIdx++] = i3;
					indices[triIdx++] = i1;

					indices[triIdx++] = i0;
					indices[triIdx++] = i2;
					indices[triIdx++] = i3;
				}
			}

			sFace.FirstPrimitive = CreatePrimitive(positions, uvs, lightmapUVs, indices, qFace);
			sFace.NumPrimitives = 1;

			return sourceBsp.Faces.Count - 1;
		}

		private Vector2[] GetPatchUVControlPoints(int patchStartVertex, int patchWidth, Func<Vertex, Vector2> selector)
		{
			var controlPoints = new Vector2[9];
			for (var i = 0; i < 3; i++)
			{
				for (var j = 0; j < 3; j++)
					controlPoints[i + j * 3] = selector(quakeBsp.Vertices[patchStartVertex + i + j * patchWidth]);
			}
			return controlPoints;
		}

		private int CreatePrimitive(Vector3[] positions, Vector2[] uvs, Vector2[] lightmapUVs, int[] indices, Face qFace)
		{
			var primitiveIndex = sourceBsp.Primitives.Count;

			var data = new byte[Primitive.GetStructLength(sourceBsp.MapType)];
			var primitive = new Primitive(data, sourceBsp.Primitives);
			primitive.Type = Primitive.PrimitiveType.PRIM_TRILIST;

			primitive.FirstVertex = CreatePrimitiveVertices(positions, uvs, lightmapUVs, qFace);
			primitive.VertexCount = positions.Length;

			primitive.FirstIndex = CreatePrimitiveIndices(indices);
			primitive.IndexCount = indices.Length;

			sourceBsp.Primitives.Add(primitive);
			return primitiveIndex;
		}

		private int CreatePrimitiveVertices(Vector3[] positions, Vector2[] uvs, Vector2[] lightmapUVs, Face qFace)
		{
			var firstPrimVertex = sourceBsp.PrimitiveVertices.Count;

			// Normalize lightmap coords against the SAME rect that ConvertInternalLightmaps copies for
			// this face: the whole patch's control-point lightmap UV extents. The engine feeds a
			// primitive vertex's lightCoord straight to the lightmap sampler (it doesn't use the face's
			// LightmapStart/vecs for prims), so [0,1] must span exactly that copied rect. Using the
			// local tessellated sub-patch extents here instead shifts each sub-patch's lightmap sideways.
			(var lmStart, var lmEnd, var lightmapSize) = GetFaceLightmapBlock(qFace);
			var extents = lmEnd - lmStart;

			for (var i = 0; i < positions.Length; i++)
			{
				sourceBsp.PrimitiveVertices.Add(positions[i]);

				var bytes = new byte[PrimitiveTextureInfo.GetStructLength(sourceBsp.MapType)];
				var texInfo = new PrimitiveTextureInfo(bytes, sourceBsp.PrimitiveTextureInfo);
				texInfo.TexCoord = uvs[i];
				texInfo.LightmapCoord = ComputePrimLightmapCoord(lightmapUVs[i], lmStart, extents, lightmapSize);

				sourceBsp.PrimitiveTextureInfo.Add(texInfo);
			}

			return firstPrimVertex;
		}

		private int CreatePlane(Face face)
		{
			var distance = Vector3.Dot(face.Vertices.First().position, face.Normal);
			return CreatePlane(face.Normal, distance);
		}

		private int CreatePlane(Vector3 normal, float distance)
		{
			if (!normal.IsValid())
			{
				normal = new Vector3(0f, 0f, 1f);
				distance = 0f;
			}

			var key = (normal, distance);
			if (planeDict.TryGetValue(key, out var existingIndex))
				return existingIndex;

			var data = new byte[PlaneBSP.GetStructLength(sourceBsp.MapType)];
			var plane = new PlaneBSP(data, sourceBsp.Planes);

			plane.Normal = normal;
			plane.Distance = distance;
			plane.Type = (int)GetVectorAxis(plane.Normal);

			sourceBsp.Planes.Add(plane);

			var index = sourceBsp.Planes.Count - 1;
			planeDict[key] = index;

			return index;
		}

		private int CreatePrimitive(Vertex[] vertices, int[] indices, Face qFace)
		{
			var primitiveIndex = sourceBsp.Primitives.Count;

			var data = new byte[Primitive.GetStructLength(sourceBsp.MapType)];
			var primitive = new Primitive(data, sourceBsp.Primitives);

			primitive.Type = Primitive.PrimitiveType.PRIM_TRILIST;

			primitive.FirstVertex = CreatePrimitiveVertices(vertices, qFace);
			primitive.VertexCount = vertices.Length;

			primitive.FirstIndex = CreatePrimitiveIndices(indices);
			primitive.IndexCount = indices.Length;

			sourceBsp.Primitives.Add(primitive);

			return primitiveIndex;
		}

		private int CreatePrimitiveVertices(Vertex[] vertices, Face qFace)
		{
			var firstPrimVertex = sourceBsp.PrimitiveVertices.Count;

			(var lmStart, var lmEnd, var lightmapSize) = GetFaceLightmapBlock(qFace);
			var extents = lmEnd - lmStart;

			foreach (var vertex in vertices)
			{
				sourceBsp.PrimitiveVertices.Add(vertex.position);

				var bytes = new byte[PrimitiveTextureInfo.GetStructLength(sourceBsp.MapType)];
				var texInfo = new PrimitiveTextureInfo(bytes, sourceBsp.PrimitiveTextureInfo);
				texInfo.TexCoord = vertex.uv0;
				texInfo.LightmapCoord = ComputePrimLightmapCoord(vertex.uv1, lmStart, extents, lightmapSize);

				sourceBsp.PrimitiveTextureInfo.Add(texInfo);
			}

			return firstPrimVertex;
		}

		// The luxel dimension (per axis) of the lightmap this face samples. Internal lightmaps use the
		// fixed 128x128 Q3 page; external lightmaps are per-shader images of varying size. This MUST match
		// the size ConvertExternalLightmaps uses for the same face's data copy and LightmapSize, or the
		// prim lightCoords won't line up with the copied block. (Uses size.X for both axes, like the rest
		// of the lightmap path - correct for square lightmaps, which is the norm.)
		private int GetFaceLightmapSize(Face qFace)
		{
			if (quakeBsp.Lightmaps.Data.Length > 0)
				return Q3_LIGHTMAP_SIZE;

			if (shaderDict.TryGetValue(qFace.Texture.Name, out var shader))
			{
				var stage = shader.stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_LIGHTMAP && x.bundles[0].images[0] != "$lightmap");
				if (stage != null && externalLightmaps.TryGetValue(stage.bundles[0].images[0], out var lmData))
					return (int)lmData.size.X;
			}

			return Q3_LIGHTMAP_SIZE;
		}

		// Computes a face's lightmap block in atlas luxel space: the start/end luxel coords and the atlas size
		// used to derive them. If the block would exceed what the engine can store/pack (MAX_LIGHTMAP_EXTENT),
		// it's downscaled uniformly to fit. Both the baked prim lightCoords (ComputePrimLightmapCoord) and the
		// copied lightmap luxels (ConvertExternalLightmaps) MUST derive from these same values, or they won't
		// line up. Lightmaps are low frequency, so the reduced resolution on an oversized surface isn't
		// noticeable. Internal Q3 lightmaps (128x128 pages) never exceed the limit, so this is a no-op for them.
		private (Vector2 lmStart, Vector2 lmEnd, float lightmapSize) GetFaceLightmapBlock(Face qFace)
		{
			float lightmapSize = GetFaceLightmapSize(qFace);
			(var lmStart, var lmEnd) = GetLightmapExtents(qFace.Vertices, lightmapSize);
			var extents = lmEnd - lmStart;

			// The stored LightmapSize grows by 2*LIGHTMAP_BORDER for the guard band (see ConvertExternalLightmaps),
			// so the raw block must leave room for it. Use the border unconditionally (prim faces); displacement
			// faces use no border and so end up capped slightly more conservatively, which is harmless.
			var maxContent = MAX_LIGHTMAP_EXTENT - 2 * LIGHTMAP_BORDER;
			var maxAxis = Math.Max(extents.X, extents.Y);
			if (maxAxis > maxContent)
			{
				lightmapSize *= maxContent / maxAxis;
				(lmStart, lmEnd) = GetLightmapExtents(qFace.Vertices, lightmapSize);

				// floor/ceil after scaling can round the block a luxel or two back over the target; hard-clamp
				// the end so the padded stored size can never exceed the engine limit.
				if (lmEnd.X - lmStart.X > maxContent)
					lmEnd.X = lmStart.X + maxContent;
				if (lmEnd.Y - lmStart.Y > maxContent)
					lmEnd.Y = lmStart.Y + maxContent;
			}

			return (lmStart, lmEnd, lightmapSize);
		}

		// Maps a Quake 3 lightmap UV to the [0,1] lightCoord the engine expects for a prim-mesh vertex.
		// The engine (GenerateTexCoordsForPrimVerts) computes the final atlas coord as
		//   offset + lightCoord * (LightmapExtents / pageSize)
		// and allocates/samples a block of (LightmapExtents + 1) luxels. ConvertInternalLightmaps copies
		// that block with a LIGHTMAP_BORDER-luxel duplicated-edge border on every side, so the real luxels
		// sit at block indices [border .. border + origExtents] and LightmapExtents was grown by 2*border.
		//   lightCoord = (luxel - lmStart + border) / (origExtents + 2*border)
		// NOTE: NO +0.5 half-luxel term. Quake 3 lightmap st coords already sample luxel CENTERS
		// (uv1*lightmapSize == luxelIndex + 0.5), so 'uv1*lightmapSize - lmStart' is already the
		// center-relative position. The engine adds +0.5 for displacements only because there it derives
		// coords from world position (grid-relative); adding it here too double-counts and pushes every
		// vertex a full luxel toward the high edge, so the surface edge samples the q3map2 gutter/neighbour
		// luxel left by ceil(maxSt) -> lightmap bleed. 'extents' is the original (lmEnd - lmStart).
		private Vector2 ComputePrimLightmapCoord(Vector2 uv1, Vector2 lmStart, Vector2 extents, float lightmapSize)
		{
			var paddedX = extents.X + 2 * LIGHTMAP_BORDER;
			var paddedY = extents.Y + 2 * LIGHTMAP_BORDER;
			var coordX = paddedX != 0 ? (uv1.X * lightmapSize - lmStart.X + LIGHTMAP_BORDER) / paddedX : 0f;
			var coordY = paddedY != 0 ? (uv1.Y * lightmapSize - lmStart.Y + LIGHTMAP_BORDER) / paddedY : 0f;
			return new Vector2(coordX, coordY);
		}

		private int CreatePrimitiveIndices(int[] indices)
		{
			var firstPrimIndex = sourceBsp.PrimitiveIndices.Count;

			foreach (var index in indices)
				sourceBsp.PrimitiveIndices.Add(index);

			return firstPrimIndex;
		}

		private (int surfEdgeIndex, int numEdges) CreateSurfaceEdges(int faceIndex)
		{
			var surfEdgeIndex = sourceBsp.FaceEdges.Count;

			var qFace = quakeBsp.Faces[faceIndex];
			var vertices = qFace.Vertices.ToArray();

			// Convert triangle meshes from Q3 to Source engine's edge loop format
			// Note: Some Q3 faces are concave polygons, so this approach does not always work
			var hullVerts = HullConverter.ConvertConvexHull(vertices, qFace.Normal);
			var numEdges = hullVerts.Count;

			// Note: Edges are continuous, so treating them as triangles will cause issues
			//for (var i = 0; i < indices.Length; i += 3)
			//{
			//	var v1 = vertices[indices[i]];
			//	var v2 = vertices[indices[i + 1]];
			//	var v3 = vertices[indices[i + 2]];

			//	var e1 = CreateEdge(v2, v1);
			//	var e2 = CreateEdge(v3, v2);
			//	var e3 = CreateEdge(v1, v3);

			//	sourceBsp.FaceEdges.Add(e1);
			//	sourceBsp.FaceEdges.Add(e2);
			//	sourceBsp.FaceEdges.Add(e3);
			//}

			for (var i = 0; i < hullVerts.Count; i++)
			{
				var nextIndex = (i + 1) % hullVerts.Count;
				CreateEdge(hullVerts[nextIndex], hullVerts[i], faceIndex);
			}

			return (surfEdgeIndex, numEdges);
		}

		private void CreateEdge(Vertex firstVertex, Vertex secondVertex, int faceIndex)
		{
			// TODO: Prevent adding duplicate edges?
			var data = new byte[Edge.GetStructLength(sourceBsp.MapType)];
			var edge = new Edge(data, sourceBsp.Edges);
			edge.FirstVertexIndex = CreateVertex(firstVertex, faceIndex);
			edge.SecondVertexIndex = CreateVertex(secondVertex, faceIndex);

			sourceBsp.Edges.Add(edge);
			sourceBsp.FaceEdges.Add(sourceBsp.Edges.Count - 1);
		}

		private int CreateVertex(Vertex vertex, int faceIndex)
		{
			sourceBsp.Indices.Add(faceIndex);

			// TODO: Prevent adding duplicate vertices
			sourceBsp.Vertices.Add(vertex);
			return sourceBsp.Vertices.Count - 1;
		}

		private int CreateTextureInfo(Face qFace, int firstIndex, string nameOverride = null)
		{
			(var uAxis, var vAxis) = GetTextureVectors(qFace, firstIndex);
			return CreateTextureInfo(qFace.Texture, uAxis, vAxis, nameOverride);
		}

		// nameOverride points the texinfo (and its name->index lookup) at a different material than the source
		// texture's name, keeping the source texture's surface flags. Used for fog overlay faces, whose geometry
		// comes from a fog brush face but whose material is the sibling overlay (see TryCreateFogOverlayFace).
		private int CreateTextureInfo(Texture texture, Vector3 uAxis, Vector3 vAxis, string nameOverride = null)
		{
			var textureName = nameOverride ?? texture.Name;

			var data = new byte[TextureInfo.GetStructLength(sourceBsp.MapType)];
			var textureInfo = new TextureInfo(data, sourceBsp.TextureInfo);

			// TODO: Get UV data from face vertices
			textureInfo.UAxis = uAxis;
			textureInfo.VAxis = vAxis;
			textureInfo.LightmapUAxis = uAxis / 32f;
			textureInfo.LightmapVAxis = vAxis / 32f;
			textureInfo.Flags = GetSourceSurfaceFlags(texture);

			// Tool-textured patches keep their surface flags after being rewritten onto the shared
			// invisible displacement material, so they need its per-flag texdata variants too - see
			// GetInvisibleDisplacementTextureDataIndex.
			textureInfo.TextureIndex = textureName == invisibleDisplacementTexture ?
				GetInvisibleDisplacementTextureDataIndex(textureInfo.Flags) :
				LookupTextureDataIndex(textureName);

			// Avoid adding duplicate texture info
			var key = new TextureInfoKey(textureInfo);
			if (textureInfoDict.TryGetValue(key, out var textureInfoIndex))
				return textureInfoIndex;

			sourceBsp.TextureInfo.Add(textureInfo);

			textureInfoIndex = sourceBsp.TextureInfo.Count - 1;
			textureInfoDict.Add(key, textureInfoIndex);

			if (!textureInfoLookup.ContainsKey(textureName))
				textureInfoLookup.Add(textureName, textureInfoIndex);

			return textureInfoIndex;
		}

		// Texture info pointing at the invisible displacement material, used for collision-only
		// displacement faces (see CreatePatchCollisionFace). physicsFlags carries surface flags
		// (e.g. SURF_SLICK) that must survive onto the collision surface even though it never renders.
		private int CreateInvisibleDisplacementTextureInfo(Vector3 uAxis, Vector3 vAxis, int physicsFlags = 0)
		{
			var data = new byte[TextureInfo.GetStructLength(sourceBsp.MapType)];
			var textureInfo = new TextureInfo(data, sourceBsp.TextureInfo);

			textureInfo.UAxis = uAxis;
			textureInfo.VAxis = vAxis;
			textureInfo.LightmapUAxis = uAxis / 32f;
			textureInfo.LightmapVAxis = vAxis / 32f;
			textureInfo.TextureIndex = GetInvisibleDisplacementTextureDataIndex(physicsFlags);
			textureInfo.Flags = physicsFlags;

			// Avoid adding duplicate texture info
			var key = new TextureInfoKey(textureInfo);
			if (textureInfoDict.TryGetValue(key, out var textureInfoIndex))
				return textureInfoIndex;

			sourceBsp.TextureInfo.Add(textureInfo);

			textureInfoIndex = sourceBsp.TextureInfo.Count - 1;
			textureInfoDict.Add(key, textureInfoIndex);

			return textureInfoIndex;
		}

		// The engine ORs surface flags per-texdata, not per-texinfo, and displacements read their
		// collision flags from there. Every patch shares the invisible material, so to stop one
		// patch's SURF_SLICK/SURF_NOIMPACT from bleeding onto all of them we give each distinct
		// flag set its own texdata entry.
		private int GetInvisibleDisplacementTextureDataIndex(int surfaceFlags)
		{
			if (invisibleDispTexDataByFlags.TryGetValue(surfaceFlags, out var index))
				return index;

			if (surfaceFlags == 0)
			{
				// Default variant: share the name-keyed texdata so other callers dedupe against it.
				if (LookupTextureDataIndex(invisibleDisplacementTexture) < 0)
					CreateTextureData(invisibleDisplacementTexture);

				index = LookupTextureDataIndex(invisibleDisplacementTexture);
			}
			else
			{
				// Distinct texdata for this flag set, still resolving to the invisible material.
				index = CreateTextureDataEntry(invisibleDisplacementTexture);
			}

			invisibleDispTexDataByFlags[surfaceFlags] = index;
			return index;
		}

		// Source surface flags a Q3 texture contributes to its texinfo.
		private int GetSourceSurfaceFlags(Texture texture)
		{
			var q3Flags = (Q3SurfaceFlags)texture.Flags;
			var flags = 0;

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_SLICK))
				flags |= (int)SourceSurfaceFlags.SURF_SLICK;

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP))
				flags |= (int)SourceSurfaceFlags.SURF_NOLIGHT;

			// Reveal Source's global skybox on real sky surfaces (see IsSkySurface).
			if (IsSkySurface(texture))
				flags |= (int)(SourceSurfaceFlags.SURF_SKY | SourceSurfaceFlags.SURF_NOLIGHT | SourceSurfaceFlags.SURF_SKYNOEMIT);

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_NODRAW))
				flags |= (int)SourceSurfaceFlags.SURF_NODRAW;

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_NOIMPACT))
				flags |= (int)SourceSurfaceFlags.SURF_NOIMPACT;

			return flags;
		}

		// Surface flags that affect movement/physics and must be carried onto collision-only
		// displacements. Rendering/lighting flags are irrelevant for a non-rendering surface.
		private static int GetPhysicsSurfaceFlags(Texture texture)
		{
			var q3Flags = (Q3SurfaceFlags)texture.Flags;
			var flags = 0;

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_SLICK))
				flags |= (int)SourceSurfaceFlags.SURF_SLICK;

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_NOIMPACT))
				flags |= (int)SourceSurfaceFlags.SURF_NOIMPACT;

			return flags;
		}

		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectors(Face qFace, int firstIndex)
		{
			var vertices = quakeBsp.Vertices;
			var indices = quakeBsp.Indices;

			var i0 = (int)indices[firstIndex];
			var i1 = (int)indices[firstIndex + 1];
			var i2 = (int)indices[firstIndex + 2];

			var v0 = vertices[qFace.FirstVertexIndex + i0];
			var v1 = vertices[qFace.FirstVertexIndex + i1];
			var v2 = vertices[qFace.FirstVertexIndex + i2];

			return GetTextureVectorsFromVertices(v0, v1, v2, qFace.Normal);
		}

		// Tangent basis vector derivation: https://www.cs.upc.edu/~virtual/G/1.%20Teoria/06.%20Textures/Tangent%20Space%20Calculation.pdf
		// Note: This only works for face-aligned textures. World-aligned textures will need to be handled differently
		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectorsFromVertices(Vertex v0, Vertex v1, Vertex v2, Vector3 faceNormal)
		{
			var deltaPos1 = v1.position - v0.position;
			var deltaPos2 = v2.position - v0.position;

			var deltaUV1 = v1.uv0 - v0.uv0;
			var deltaUV2 = v2.uv0 - v0.uv0;

			var den = deltaUV1.X * deltaUV2.Y - deltaUV2.X * deltaUV1.Y;
			if (Math.Abs(den) < 0.01f)
				return GetTextureVectorsWithNormal(faceNormal);

			var r = 1f / den;
			var tangent = (deltaPos1 * deltaUV2.Y - deltaPos2 * deltaUV1.Y) * r / 32f;
			var binormal = (deltaPos2 * deltaUV1.X - deltaPos1 * deltaUV2.X) * r / 32f;

			return (tangent, binormal);
		}

		// Fallback for when faces have unusual uv deltas
		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectorsWithNormal(Vector3 faceNormal)
		{
			var axis = GetVectorAxis(faceNormal);
			switch (axis)
			{
				case PlaneBSP.AxisType.PlaneX:
				case PlaneBSP.AxisType.PlaneAnyX:
					return (new Vector3(0f, 2f, 0f), new Vector3(0f, 0f, -2f));
				case PlaneBSP.AxisType.PlaneY:
				case PlaneBSP.AxisType.PlaneAnyY:
					return (new Vector3(2f, 0f, 0f), new Vector3(0f, 0f, -2f));
				case PlaneBSP.AxisType.PlaneZ:
				case PlaneBSP.AxisType.PlaneAnyZ:
					return (new Vector3(2f, 0f, 0f), new Vector3(0f, -2f, 0f));
				default:
					return (new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f));
			}
		}

		private int LookupTextureInfoIndex(string textureName)
		{
			if (textureInfoLookup.TryGetValue(textureName, out var textureInfoIndex))
				return textureInfoIndex;

			return -1;
		}

		private int LookupTextureDataIndex(string textureName)
		{
			if (textureDataLookup.TryGetValue(textureName, out var textureDataIndex))
				return textureDataIndex;

			return -1;
		}

		private void ConvertLightmaps()
		{
			SetLumpVersionNumber(Lightmaps.GetIndexForLump(sourceBsp.MapType), 1);

			if (quakeBsp.Lightmaps.Data.Length > 0)
				ConvertInternalLightmaps();
			else if (externalLightmaps.Any())
				ConvertExternalLightmaps();
		}

		// True if the Q3 face was emitted as primitive mesh face(s) (lightCoords baked by the converter)
		// rather than displacement(s) (lightCoords computed at runtime by the engine). A Q3 face's split
		// faces are homogeneous in the cases that matter here: non-patch polygons -> prims; patches ->
		// prims (+ invisible collision disps, prim listed first), or displacements under --patchdisps.
		private bool FaceOutputUsesPrimitives(int qFaceIndex)
		{
			if (!splitFaceDict.TryGetValue(qFaceIndex, out var splitFaces) || splitFaces.Length == 0)
				return false;

			return sourceBsp.Faces[splitFaces[0]].NumPrimitives > 0;
		}

		private void ConvertInternalLightmaps()
		{
			var qLightmapData = quakeBsp.Lightmaps.Data;
			var lmColors = new List<ColorRGBExp32>();

			for (var faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
				var qFace = quakeBsp.Faces[faceIndex];
				var lmIndex = qFace.Lightmap;
				if (lmIndex < 0)
					continue;

				// Unsupported face types (e.g. Billboard) are never added to splitFaceDict during face
				// conversion. Skip them here so we don't append orphan luxels or throw on the lookup below.
				if (!splitFaceDict.TryGetValue(faceIndex, out var splitFaces) || splitFaces.Length == 0)
					continue;

				(var lmStart, var lmEnd) = GetLightmapExtents(qFace.Vertices, Q3_LIGHTMAP_SIZE);
				var lmSize = lmEnd - lmStart;

				// TODO: Faces need to be split since Source lightmaps only go up to 35x35 luxels whereas Q3 goes up to 128x128
				//if (lmSize.X - 1 > 35 || lmSize.Y - 1 > 35)
				//	continue;

				var q3LightmapSize = Q3_LIGHTMAP_SIZE * Q3_LIGHTMAP_SIZE * 3;
				var q3LightmapOffset = lmIndex * q3LightmapSize;

				var sourceLightmapOffset = lmColors.Count * 4;

				// Only primitive faces get the guard-band border: their lightCoords are baked by the
				// converter (ComputePrimLightmapCoord) and shifted inward to match it. Displacement faces
				// have their lightCoords computed at runtime by the engine (SurfComputeLightmapCoordinate),
				// which is border-unaware, so a border there would just shift their lightmap into the
				// duplicated edge. See FaceOutputUsesPrimitives.
				var border = FaceOutputUsesPrimitives(faceIndex) ? LIGHTMAP_BORDER : 0;

				// Add lightmap colors. The engine allocates/samples a block of (extents + 1) luxels in
				// BOTH dimensions (RegisterLightmappedSurface). Expand the copied rect by 'border' on every
				// side, clamping the source luxel to [lmStart, lmEnd] so the border duplicates the nearest
				// edge color (a clamp-to-edge guard band that stops bilinear bleed from the neighbouring
				// block). Copying lmStart..lmEnd inclusive (<=) on both axes matches the (extents+1) block.
				for (var y = (int)lmStart.Y - border; y <= (int)lmEnd.Y + border; y++)
				{
					// Clamp to the face's rect, then to the page bounds: lmEnd = ceil(uvMax*128) can be one
					// past the last valid luxel when a face's lightmap UV reaches the page edge, which would
					// otherwise read into the adjacent lightmap page (or past the lump on the last page).
					var sy = Math.Clamp(Math.Clamp(y, (int)lmStart.Y, (int)lmEnd.Y), 0, Q3_LIGHTMAP_SIZE - 1);
					for (var x = (int)lmStart.X - border; x <= (int)lmEnd.X + border; x++)
					{
						var sx = Math.Clamp(Math.Clamp(x, (int)lmStart.X, (int)lmEnd.X), 0, Q3_LIGHTMAP_SIZE - 1);
						var index = sx + (sy * Q3_LIGHTMAP_SIZE);

						var color = ColorUtil.ConvertQ3LightmapToColorRGBExp32(
							qLightmapData[q3LightmapOffset + index * 3 + 0],
							qLightmapData[q3LightmapOffset + index * 3 + 1],
							qLightmapData[q3LightmapOffset + index * 3 + 2],
							options.clampOverbright);

						lmColors.Add(color);
					}
				}

				// Update face lightmap info. LightmapSize grows by 2*border to account for the guard band;
				// ComputePrimLightmapCoord shifts prim vertices inward by the same border so they sample the
				// real (interior) luxels.
				foreach (var splitFaceIndex in splitFaces)
				{
					var sFace = sourceBsp.Faces[splitFaceIndex];
					sFace.Lightmap = sourceLightmapOffset;
					sFace.LightmapStart = GetLightmapStart(sFace);
					sFace.LightmapSize = new Vector2(lmSize.X + 2 * border, lmSize.Y + 2 * border);
				}
			}

			// Copy colors into lightmap data
			var data = new byte[lmColors.Count * 4];
			for (var i = 0; i < lmColors.Count; i++)
			{
				var color = lmColors[i];
				var dataIndex = i * 4;
				data[dataIndex + 0] = color.r;
				data[dataIndex + 1] = color.g;
				data[dataIndex + 2] = color.b;
				data[dataIndex + 3] = (byte)color.exponent;
			}

			sourceBsp.Lightmaps.Data = data;
		}

		private void ConvertExternalLightmaps()
		{
			var lmColors = new List<ColorRGBExp32>();

			for (var faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
				var qFace = quakeBsp.Faces[faceIndex];
				var texture = qFace.Texture.Name;
				if (!shaderDict.TryGetValue(texture, out var shader))
					continue;

				var stage = shader.stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_LIGHTMAP && x.bundles[0].images[0] != "$lightmap");
				if (stage == null)
					continue;

				var lmImage = stage.bundles[0].images[0];
				if (!externalLightmaps.TryGetValue(lmImage, out var lmData))
					continue;

				// Unsupported face types (e.g. Billboard) are never added to splitFaceDict during face
				// conversion. Skip them here so we don't append orphan luxels or throw on the lookup below.
				if (!splitFaceDict.TryGetValue(faceIndex, out var splitFaces) || splitFaces.Length == 0)
					continue;

				// lmStart/lmEnd/lightmapSize are in the (possibly downscaled) atlas space GetFaceLightmapBlock
				// caps to the engine's max lightmap extent; the same values feed the baked prim lightCoords.
				(var lmStart, var lmEnd, var lightmapSize) = GetFaceLightmapBlock(qFace);
				var lmSize = lmEnd - lmStart;

				var lightmapOffset = lmColors.Count * 4;

				// Guard-band border only for primitive faces; see ConvertInternalLightmaps for the rationale.
				var border = FaceOutputUsesPrimitives(faceIndex) ? LIGHTMAP_BORDER : 0;
				var lmWidth = (int)lmData.size.X;
				var lmHeight = (int)lmData.size.Y;
				// When the block was downscaled, its luxel coords live in a smaller atlas (lightmapSize); map
				// each back to the original image to read its color. Without scaling this ratio is 1.
				var readScaleX = lmWidth / lightmapSize;
				var readScaleY = lmHeight / lightmapSize;
				for (var y = (int)lmStart.Y - border; y <= (int)lmEnd.Y + border; y++)
				{
					// Clamp to the face's rect (so the border duplicates the edge luxel), then map to the source
					// image and clamp to its bounds: lmEnd = ceil(uvMax*size) can be one past the last valid luxel
					// when a face's lightmap UV reaches the image edge, which would index past the end of the
					// (single) external lightmap image -> IndexOutOfRange.
					var cy = Math.Clamp(y, (int)lmStart.Y, (int)lmEnd.Y);
					var sy = Math.Clamp((int)Math.Round(cy * readScaleY), 0, lmHeight - 1);
					for (var x = (int)lmStart.X - border; x <= (int)lmEnd.X + border; x++)
					{
						var cx = Math.Clamp(x, (int)lmStart.X, (int)lmEnd.X);
						var sx = Math.Clamp((int)Math.Round(cx * readScaleX), 0, lmWidth - 1);
						var index = sx + sy * lmWidth;

						var color = ColorUtil.ConvertQ3LightmapToColorRGBExp32(
							lmData.data[index * 3 + 0],
							lmData.data[index * 3 + 1],
							lmData.data[index * 3 + 2],
							options.clampOverbright,
							applyOverbright: false); // Don't apply overbright to external lightmaps

						lmColors.Add(color);
					}
				}

				foreach (var splitFaceIndex in splitFaces)
				{
					var sFace = sourceBsp.Faces[splitFaceIndex];
					sFace.Lightmap = lightmapOffset;
					sFace.LightmapStart = GetLightmapStart(sFace);
					sFace.LightmapSize = new Vector2(lmSize.X + 2 * border, lmSize.Y + 2 * border);
				}
			}

			// Copy colors into lightmap data
			var data = new byte[lmColors.Count * 4];
			for (var i = 0; i < lmColors.Count; i++)
			{
				var color = lmColors[i];
				var dataIndex = i * 4;
				data[dataIndex + 0] = color.r;
				data[dataIndex + 1] = color.g;
				data[dataIndex + 2] = color.b;
				data[dataIndex + 3] = (byte)color.exponent;
			}

			sourceBsp.Lightmaps.Data = data;
		}

		private (Vector2, Vector2) GetLightmapExtents(IEnumerable<Vertex> vertices, float lightmapSize)
		{
			var uvMin = new Vector2(float.MaxValue, float.MaxValue);
			var uvMax = new Vector2(float.MinValue, float.MinValue);
			foreach (var vert in vertices)
			{
				if (vert.uv1.X < uvMin.X)
					uvMin.X = vert.uv1.X;
				if (vert.uv1.Y < uvMin.Y)
					uvMin.Y = vert.uv1.Y;

				if (vert.uv1.X > uvMax.X)
					uvMax.X = vert.uv1.X;
				if (vert.uv1.Y > uvMax.Y)
					uvMax.Y = vert.uv1.Y;
			}

			var lmStart = new Vector2((int)Math.Floor(uvMin.X * lightmapSize), (int)Math.Floor(uvMin.Y * lightmapSize));
			var lmEnd = new Vector2((int)Math.Ceiling(uvMax.X * lightmapSize), (int)Math.Ceiling(uvMax.Y * lightmapSize));

			return (lmStart, lmEnd);
		}

		// TODO: Use lightmap vecs from source face and vertices from quake 3 face (should be more efficient)
		private Vector2 GetLightmapStart(Face face)
		{
			var lightmapStart = new Vector2(float.MaxValue, float.MaxValue);
			var texInfo = face.TextureInfo;
			var lightmapUAxis = texInfo.LightmapUAxis;
			var lightmapVAxis = texInfo.LightmapVAxis;

			// Find the minimum values for world space uv offsets
			foreach (var edgeIndex in face.EdgeIndices)
			{
				var edge = sourceBsp.Edges[edgeIndex];
				var vertex = edge.FirstVertex.position;

				var uOffset = Vector3.Dot(vertex, lightmapUAxis);
				if (uOffset < lightmapStart.X)
					lightmapStart.X = uOffset;

				var vOffset = Vector3.Dot(vertex, lightmapVAxis);
				if (vOffset < lightmapStart.Y)
					lightmapStart.Y = vOffset;
			}

			return lightmapStart;
		}

		private void ConvertLightGrid()
		{
			var lightGridConverter = new LightGridConverter(quakeBsp, sourceBsp, options.clampOverbright);
			lightGridConverter.Convert();
		}

		private void ConvertVisData()
		{
			if (quakeBsp.Visibility.Data.Length == 0) // No VisData
			{
				sourceBsp.Visibility.Data = new byte[0];
				return;
			}

			var visDataList = new List<byte>();

			var numClusters = quakeBsp.Visibility.NumClusters;
			var clusterSize = quakeBsp.Visibility.ClusterSize;
			var numClusterBytes = (numClusters + 7) >> 3; // Number of bytes to store each cluster bit

			var byteOffsets = new int[numClusters][];
			var visDataStartLength = 4 + numClusters * 8; // Byte length of numClusters and byteOffsets
			var currentOffset = visDataStartLength;

			// Get byte offsets and vis data
			for (var i = 0; i < numClusters; i++)
			{
				byteOffsets[i] = new int[2];
				byteOffsets[i][0] = currentOffset; // PVS offset
				byteOffsets[i][1] = currentOffset; // PAS offset (PVS and PAS share the same data for now since the Quake BSP does not have any info on sound detection)

				var vecOffset = 8 + i * clusterSize;
				var uncompressed = new byte[numClusterBytes]; // Note: Use numClusterBytes instead of clusterSize since Source engine expects the length of the vis data to not exceed the number of cluster bits
				Buffer.BlockCopy(quakeBsp.Visibility.Data, vecOffset, uncompressed, 0, numClusterBytes);
				var compressed = Visibility.Compress(uncompressed); // This will compress Q3 vis data into something compatible for Source engine

				visDataList.AddRange(compressed);

				currentOffset += compressed.Length;
			}

			// Copy numClusters and byteOffsets to visData buffer
			var visData = new byte[visDataStartLength + visDataList.Count];
			BitConverter.GetBytes(numClusters).CopyTo(visData, 0);
			for (var i = 0; i < numClusters; i++)
			{
				BitConverter.GetBytes(byteOffsets[i][0]).CopyTo(visData, 4 + i * 8);
				BitConverter.GetBytes(byteOffsets[i][1]).CopyTo(visData, 8 + i * 8);
			}

			// Copy visData
			visDataList.CopyTo(visData, visDataStartLength);

			sourceBsp.Visibility.Data = visData;
		}

		private void ConvertAreas()
		{
			// Create an area in order to have valid node/leaf area references
			var areaBytes = new byte[Area.GetStructLength(sourceBsp.MapType)];
			var area = new Area(areaBytes, sourceBsp.Areas);
			sourceBsp.Areas.Add(area);
		}

		private void ConvertAreaPortals()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(AreaPortal.GetIndexForLump(sourceBsp.MapType), 1);

			// Create an area portal for the first area
			var areaPortalBytes = new byte[AreaPortal.GetStructLength(sourceBsp.MapType)];
			var areaPortal = new AreaPortal(areaPortalBytes, sourceBsp.AreaPortals);
			sourceBsp.AreaPortals.Add(areaPortal);
		}

		private void ConvertWorldLights()
		{
			SetLumpVersionNumber(WorldLight.GetIndexForLump(sourceBsp.MapType), 1);

			// Add worldlight to disable fullbright
			// TODO: How to check for fullbright maps?
			var worldLightBytes = new byte[WorldLight.GetStructLength(sourceBsp.MapType)];
			var worldLight = new WorldLight(worldLightBytes, sourceBsp.WorldLights);
			sourceBsp.WorldLights.Add(worldLight);
		}

		private void SetLumpVersionNumber(int lumpIndex, int lumpVersion)
		{
			var lumpInfo = sourceBsp[lumpIndex];
			lumpInfo.version = lumpVersion;
			sourceBsp[lumpIndex] = lumpInfo;
		}

		private void WriteBSP()
		{
			var mapsDir = Path.Combine(options.outputDir, "maps");
			if (!Directory.Exists(mapsDir))
				Directory.CreateDirectory(mapsDir);

			var writer = new BSPWriter(sourceBsp);
			var bspPath = Path.Combine(mapsDir, $"{options.prefix}{quakeBsp.MapName}.bsp");
			writer.WriteBSP(bspPath);

			logger.Log($"Wrote BSP File: {bspPath}");
		}

		private void GenerateZones()
		{
			if (options.ignoreZones)
				return;

			var zoneGenerator = new ZoneGenerator(sourceBsp, quakeBsp, logger);
			var zoneDefs = zoneGenerator.Generate();

			var zonesDir = Path.Combine(options.outputDir, "maps", "zones", "local");
			if (!Directory.Exists(zonesDir))
				Directory.CreateDirectory(zonesDir);

			var zonePath = Path.Combine(zonesDir, $"{options.prefix}{quakeBsp.MapName}.json");
			ZoneWriter.WriteToFile(zoneDefs, zonePath);

			logger.Log($"Wrote Zone File: {zonePath}");
		}
	}
}
