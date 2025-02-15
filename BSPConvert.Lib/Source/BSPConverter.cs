namespace BSPConvert.Lib;
#if UNITY_3_4 || UNITY_3_5 || UNITY_4_0 || UNITY_4_0_1 || UNITY_4_2 || UNITY_4_3 || UNITY_4_5 || UNITY_4_6 || UNITY_5 || UNITY_5_3_OR_NEWER
#define UNITY
#if !UNITY_5_6_OR_NEWER
#define OLDUNITY
#endif
#endif

using LibBSP;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using SharpCompress.Archives.Zip;
using BSPConvert.Lib.Source;

#if UNITY
	using Plane = UnityEngine.Plane;
	using Vector3 = UnityEngine.Vector3;
	using Vector2 = UnityEngine.Vector2;
	using Color = UnityEngine.Color32;
#if !OLDUNITY
	using Vertex = UnityEngine.UIVertex;
#endif
#elif GODOT
	using Plane = Godot.Plane;
	using Vector3 = Godot.Vector3;
	using Vector2 = Godot.Vector2;
	using Color = Godot.Color;
#elif NEOAXIS
	using Plane = NeoAxis.PlaneF;
	using Vector3 = NeoAxis.Vector3F;
	using Vector2 = NeoAxis.Vector2F;
	using Color = NeoAxis.ColorByte;
	using Vertex = NeoAxis.StandardVertex;
#else
using Plane = System.Numerics.Plane;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using Color = System.Drawing.Color;

#endif

public class BSPConverterOptions
	{
		public bool noPak;
		public bool noToolDisplacements;
		private int displacementPower;
		public int DisplacementPower
        {
            get => displacementPower;
            set => displacementPower = Math.Clamp(value, 2, 4);
        }
    public int minDamageToConvertTrigger;
		public bool ignoreZones;
		public bool oldBSP;
		public string prefix;
		public string inputFile;
		public string outputDir;
	}

	public class BSPConverter(BSPConverterOptions options, ILogger logger) : IDisposable
{
		private readonly BSPConverterOptions options = options;
		private readonly ILogger logger = logger;

		private BSP quakeBsp;
		private BSP sourceBsp;

		private ContentManager contentManager;
		
		private Dictionary<string, Shader> shaderDict = [];
		private Dictionary<string, LightmapData> externalLightmaps = [];
		private readonly Dictionary<int, int> textureInfoHashCodeDict = []; // Maps TextureInfo hash codes to TextureInfo indices
		private readonly Dictionary<string, int> textureInfoLookup = [];
		private readonly Dictionary<string, int> textureDataLookup = [];
		private readonly Dictionary<int, int[]> splitFaceDict = []; // Maps the original face index to the new face indices split by triangles

		// TODO: Replace weapon clip textures
		private static readonly Dictionary<string, string> replacementTextures = new()
		{
			{ "textures/common/caulk", "tools/toolsnodraw" },
			{ "textures/common/nodraw", "tools/toolsnodraw" },
			{ "textures/common/clip", "tools/toolsplayerclip" },
			{ "textures/common/full_clip", "tools/clip" },
			{ "textures/common/trigger", "tools/toolstrigger" },
			{ "textures/common/hint", "tools/toolshint" },
			{ "textures/common/skip", "tools/toolsskip" },
			{ "textures/common/areaportal", "tools/toolsareaportal" }
		};

		private const int Q3_LIGHTMAP_SIZE = 128;
		private const int LIGHTMAP_PADDING = 1; // Pixel padding to prevent lightmap bleeding
		private const string invisibleDisplacementTexture = "tools/toolsinvisibledisplacement";

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

			foreach (BSP bsp in contentManager.BSPFiles)
			{
				ClearDictionaries();

				LoadBSP(bsp);

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
				ConvertLightmaps();
				ConvertVisData();
				ConvertAreas();
				ConvertAreaPortals();
				ConvertWorldLights();

				WriteBSP();
			}

			contentManager.Dispose();
		}

		private void ClearDictionaries()
		{
			textureInfoHashCodeDict.Clear();
			textureInfoLookup.Clear();
			textureDataLookup.Clear();
			splitFaceDict.Clear();
		}

		private void LoadBSP(BSP bsp)
		{
        string bspName = bsp.MapName + ".bsp";
			logger.Log($"Converting {bspName}...");

			quakeBsp = bsp;

        MapType mapType = options.oldBSP ? MapType.Source20 : MapType.Source25;
			sourceBsp = new BSP(bspName, mapType);
		}

		private void CheckQ3Content()
		{
        string[] files = Directory.GetFiles(ContentManager.GetQ3ContentDir(), "*.*", SearchOption.AllDirectories);
			if (files.Length <= 1)
				logger.Log("Warning: Q3Content folder is empty. Quake 3 assets will not be converted.");
		}

		private void ReplaceToolTextures()
		{
			for (int i = 0; i < quakeBsp.Textures.Count; i++)
			{
            Texture texture = quakeBsp.Textures[i];
				if (replacementTextures.TryGetValue(texture.Name, out string? replacementTexture))
					texture.Name = replacementTexture;
			}
		}

		private void PrepareAssets()
		{
			if (quakeBsp.Faces.Any(x => x.Type == FaceType.Patch && x.Texture.Name.StartsWith("tools/")))
			{
				// Copy invisible displacement assets to content dir
				Directory.CreateDirectory(Path.Combine(contentManager.ContentDir, "tools"));

            string invisDisplacementVmt = @"tools\toolsinvisibledisplacement.vmt";
				File.Copy(Path.Combine(@"Assets\materials", invisDisplacementVmt), Path.Combine(contentManager.ContentDir, invisDisplacementVmt), true);

            string invisDisplacementVtf = @"tools\toolsinvisibledisplacement.vtf";
				File.Copy(Path.Combine(@"Assets\materials", invisDisplacementVtf), Path.Combine(contentManager.ContentDir, invisDisplacementVtf), true);
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
			var materialConverter = new MaterialConverter(contentManager.ContentDir, shaderDict);
			foreach (Texture texture in quakeBsp.Textures)
				materialConverter.Convert(texture.Name);
		}

		private Dictionary<string, Shader> LoadShaderDictionary()
		{
        string[] q3Shaders = GetQ3Shaders();
        string[] pk3Shaders = GetPK3Shaders();
        IEnumerable<string> allShaders = q3Shaders.Concat(pk3Shaders);

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
        string q3ScriptsDir = Path.Combine(ContentManager.GetQ3ContentDir(), "scripts");
			if (Directory.Exists(q3ScriptsDir))
				return Directory.GetFiles(q3ScriptsDir, "*.shader");

			return [];
		}

		private string[] GetPK3Shaders()
		{
        string pk3ScriptsDir = Path.Combine(contentManager.ContentDir, "scripts");
			if (Directory.Exists(pk3ScriptsDir))
				return Directory.GetFiles(pk3ScriptsDir, "*.shader");

			return [];
		}

		private void ConvertTextureFiles()
		{
        TextureConverter converter = options.noPak ?
				new TextureConverter(contentManager.ContentDir, options.outputDir) :
				new TextureConverter(contentManager.ContentDir, sourceBsp);
			converter.Convert();
		}

		private void ConvertEntities()
		{
			var converter = new EntityConverter(quakeBsp.Models, quakeBsp.Entities, sourceBsp.Entities, shaderDict, options.minDamageToConvertTrigger, options.ignoreZones);
			converter.Convert();
		}

		private void ConvertSounds()
		{
        SoundConverter converter = options.noPak ?
				new SoundConverter(contentManager.ContentDir, options.outputDir, sourceBsp.Entities) :
				new SoundConverter(contentManager.ContentDir, sourceBsp, sourceBsp.Entities);
			converter.Convert();
		}

		private void ConvertTextures()
		{
			foreach (Texture texture in quakeBsp.Textures)
				CreateTextureData(texture.Name);
		}

		private void CreateTextureData(string textureName)
		{
        string vtfPath = Path.Combine(contentManager.ContentDir, textureName + ".vtf");
        TextureData textureData = GetTextureData(vtfPath);
			textureData.TextureStringOffsetIndex = CreateTextureDataStringTableEntry(textureName);

			sourceBsp.TextureData.Add(textureData);

			if (!textureDataLookup.ContainsKey(textureName))
				textureDataLookup.Add(textureName, sourceBsp.TextureData.Count - 1);
		}

		private TextureData GetTextureData(string vtfPath)
		{
			if (!File.Exists(vtfPath))
				return GetDefaultTextureData();

			try
			{
            TextureData textureData = CreateTextureData();

            VTFFile vtfFile = FileUtil.DeserializeFromFile(vtfPath, VTFFile.Deserialize);
            VTFFile.Header header = vtfFile.header;

            int r = (int)Math.Round(header.reflectivityX * 255f);
            int g = (int)Math.Round(header.reflectivityY * 255f);
            int b = (int)Math.Round(header.reflectivityZ * 255f);
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
        TextureData textureData = CreateTextureData();

			textureData.Reflectivity = new Color();
			textureData.Size = new Vector2(128, 128);
			textureData.ViewSize = new Vector2(128, 128);

			return textureData;
		}

		private TextureData CreateTextureData()
		{
        byte[] data = new byte[TextureData.GetStructLength(sourceBsp.MapType)];
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
        int offset = sourceBsp.Textures.Length;

        byte[] data = System.Text.Encoding.ASCII.GetBytes(textureName);
			var texture = new Texture(data, sourceBsp.Textures);

			sourceBsp.Textures.Add(texture);

			return offset;
		}

		private void ConvertPlanes()
		{
			foreach (PlaneBSP qPlane in quakeBsp.Planes)
			{
            byte[] data = new byte[PlaneBSP.GetStructLength(sourceBsp.MapType)];
            var plane = new PlaneBSP(data, sourceBsp.Planes)
            {
                Normal = qPlane.Normal,
                Distance = qPlane.Distance,
                Type = (int)GetVectorAxis(qPlane.Normal)
            };

            sourceBsp.Planes.Add(plane);
			}
		}

		private PlaneBSP.AxisType GetVectorAxis(Vector3 normal)
		{
			// Note: Should these have an epsilon around 1.0?
			if (normal.X() == 1f || normal.X() == -1f)
				return PlaneBSP.AxisType.PlaneX;
			if (normal.Y() == 1f || normal.Y() == -1f)
				return PlaneBSP.AxisType.PlaneY;
			if (normal.Z() == 1f || normal.Z() == -1f)
				return PlaneBSP.AxisType.PlaneZ;

        float aX = Math.Abs(normal.X());
        float aY = Math.Abs(normal.Y());
        float aZ = Math.Abs(normal.Z());

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

			foreach (Node qNode in quakeBsp.Nodes)
			{
            byte[] data = new byte[Node.GetStructLength(sourceBsp.MapType)];
            var node = new Node(data, sourceBsp.Nodes)
            {
                PlaneIndex = qNode.PlaneIndex,
                Child1Index = qNode.Child1Index,
                Child2Index = qNode.Child2Index,
                Minimums = qNode.Minimums,
                Maximums = qNode.Maximums,

                // Note: On Source BSP's, these values are used to specify which faces are used to split the node (the face will have the "onNode" flag set to true)
                FirstFaceIndex = 0,
                NumFaceIndices = 0,

                AreaIndex = 0 // TODO: Figure out how to compute areas
            };

            sourceBsp.Nodes.Add(node);
			}

			UpdateSplitFaces();
		}

		// Assigns face ids to nodes by searching for the face that was used to split the node into its children
		// This is necessary to get skyboxes to render correctly (and perhaps some other engine optimizations)
		private void UpdateSplitFaces()
		{
        // TODO: Does this need to be done for all model head nodes?
        Node rootNode = sourceBsp.Nodes[0];

			UpdateSplitFacesRecursive(rootNode.Child1);
			UpdateSplitFacesRecursive(rootNode.Child2);
		}

		private IEnumerable<int> UpdateSplitFacesRecursive(ILumpObject obj)
		{
			if (obj is Leaf)
				return ((Leaf)obj).MarkFaces;

			var node = (Node)obj;
        IEnumerable<int> faces = UpdateSplitFacesRecursive(node.Child1).Concat(
				UpdateSplitFacesRecursive(node.Child2));

        // Find the face that splits this node
        Lump<Vector3> vertices = sourceBsp.PrimitiveVertices;
			foreach (int faceIndex in faces)
			{
            Face face = sourceBsp.Faces[faceIndex];

				var surfaceFlags = (SourceSurfaceFlags)face.TextureInfo.Flags;
				if (!surfaceFlags.HasFlag(SourceSurfaceFlags.SURF_SKY))
					continue;

            Primitive primitive = sourceBsp.Primitives[face.FirstPrimitive];
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
			(Vector3 mins, Vector3 maxs) = GetMinsMaxs(node);
			return plane.HasPoint(mins) || plane.HasPoint(maxs);
		}

		private bool IsPlaneBetweenNodes(Plane plane, ILumpObject node1, ILumpObject node2)
		{
			(Vector3 mins1, Vector3 maxs1) = GetMinsMaxs(node1);
			(Vector3 mins2, Vector3 maxs2) = GetMinsMaxs(node2);
        Vector3 center1 = (mins1 + maxs1) / 2;
        Vector3 center2 = (mins2 + maxs2) / 2;

			return plane.GetSide(center1) != plane.GetSide(center2);
		}

		private (Vector3, Vector3) GetMinsMaxs(ILumpObject obj)
		{
        if (obj is Node node)
        {
            return (node.Minimums, node.Maximums);
        }
        else
        {
            var leaf = (Leaf)obj;
            return (leaf.Minimums, leaf.Maximums);
        }
    }

    private void ConvertLeaves_SplitFaces()
		{
        int version = options.oldBSP ? 1 : 2;
			SetLumpVersionNumber(Leaf.GetIndexForLump(sourceBsp.MapType), version);

        int currentFaceIndex = 0;

			foreach (Leaf qLeaf in quakeBsp.Leaves)
			{
            byte[] data = new byte[Leaf.GetStructLength(sourceBsp.MapType)];
				var leaf = new Leaf(data, sourceBsp.Leaves);

				if (sourceBsp.Leaves.Count == 0)
				{
					leaf.Contents = (int)SourceContentsFlags.CONTENTS_SOLID; // First leaf is always solid, otherwise game crashes
					leaf.Flags = 0;
				}
				else
				{
					leaf.Contents = qLeaf.Area >= 0 ? 0 : 1; // Set to 0 when inside map, 1 when outside map or overlapping brush
					leaf.Flags = 2; // Not sure what the flags do, but 2 shows up on all leaves besides the first one
				}
				leaf.Visibility = qLeaf.Visibility;
				leaf.Area = 0; // TODO: Convert Q3 areas?
				leaf.Minimums = qLeaf.Minimums;
				leaf.Maximums = qLeaf.Maximums;

				leaf.FirstMarkFaceIndex = currentFaceIndex;
            int numFaces = 0;
				for (int i = 0; i < qLeaf.NumMarkFaceIndices; i++)
					numFaces += splitFaceDict[(int)quakeBsp.LeafFaces[qLeaf.FirstMarkFaceIndex + i]].Length;
				leaf.NumMarkFaceIndices = numFaces;
				currentFaceIndex += numFaces;
				
				leaf.FirstMarkBrushIndex = qLeaf.FirstMarkBrushIndex;
				leaf.NumMarkBrushIndices = qLeaf.NumMarkBrushIndices;
				leaf.LeafWaterDataID = -1;

				sourceBsp.Leaves.Add(leaf);
			}
		}

    private void ConvertLeafFaces_SplitFaces()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafFacesLump(sourceBsp.MapType, out _), 1);

			foreach (long qLeafFace in quakeBsp.LeafFaces)
			{
            int[] splitFaceIndices = splitFaceDict[(int)qLeafFace];
				for (int i = 0; i < splitFaceIndices.Length; i++)
					sourceBsp.LeafFaces.Add(splitFaceIndices[i]);
			}
		}

		private void ConvertLeafBrushes()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafBrushesLump(sourceBsp.MapType, out _), 1);

			foreach (long qLeafBrush in quakeBsp.LeafBrushes)
				sourceBsp.LeafBrushes.Add(qLeafBrush);
		}

		private void ConvertModels()
		{
        bool exceededMaxExtents = false;

			for (int i = 0; i < quakeBsp.Models.Count; i++)
			{
            Model qModel = quakeBsp.Models[i];

            byte[] data = new byte[Model.GetStructLength(sourceBsp.MapType)];
				var sModel = new Model(data, sourceBsp.Models);

				if (i == 0)
					sModel.HeadNodeIndex = 0;
				else
				{
					if (!TryCreateHeadNode(qModel.FirstBrushIndex, out int nodeIndex))
					{
						logger.Log($"Failed to convert model: {i}");
						continue;
					}
					
					sModel.HeadNodeIndex = nodeIndex;
				}

            Vector3 mins = qModel.Minimums;
            Vector3 maxs = qModel.Maximums;
            int minExtents = options.oldBSP ? -16384 : -65536;
            int maxExtents = options.oldBSP ? 16384 : 65536;
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
					sModel.FirstFaceIndex = splitFaceDict[qModel.FirstFaceIndex][0];
				else
					sModel.FirstFaceIndex = qModel.FirstFaceIndex;
				sModel.NumFaces = qModel.NumFaces;

				sourceBsp.Models.Add(sModel);
			}

			if (exceededMaxExtents)
				throw new Exception("Failed to convert BSP, exceeded max extents");
		}

		// TODO: Add face references in order for showtriggers_toggle to work?
		// Creates a head node using the leaf that references the brush index (seems to be required to get trigger collisions working)
		private bool TryCreateHeadNode(int brushIndex, out int nodeIndex)
		{
        int leafIndex = FindLeafIndex(brushIndex);
			if (leafIndex < 0)
			{
				nodeIndex = -1;
				return false;
			}

        byte[] data = new byte[Node.GetStructLength(sourceBsp.MapType)];
        var node = new Node(data, sourceBsp.Nodes)
        {
            Child1Index = -leafIndex - 1,
            Child2Index = -leafIndex - 1
        };

        // Note: This fixes translucent brush entities not rendering
        Leaf leaf = sourceBsp.Leaves[leafIndex];
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
        NumList leafBrushes = sourceBsp.LeafBrushes;

			for (int i = 0; i < sourceBsp.Leaves.Count; i++)
			{
            Leaf leaf = sourceBsp.Leaves[i];
				for (int j = 0; j < leaf.NumMarkBrushIndices; j++)
				{
					if (leafBrushes[leaf.FirstMarkBrushIndex + j] == brushIndex)
						return i;
				}
			}

			return -1;
		}

		private void ConvertBrushes()
		{
			foreach (Brush qBrush in quakeBsp.Brushes)
			{
            byte[] data = new byte[Brush.GetStructLength(sourceBsp.MapType)];
            var sBrush = new Brush(data, sourceBsp.Brushes)
            {
                FirstSideIndex = qBrush.FirstSideIndex,
                NumSides = qBrush.NumSides,
                Contents = GetBrushContents(qBrush.Texture)
            };

            sourceBsp.Brushes.Add(sBrush);
			}
		}

		private int GetBrushContents(Texture texture)
		{
        // TODO: Handle other texture contents flags
        SourceContentsFlags sourceContents = SourceContentsFlags.CONTENTS_EMPTY;
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

			foreach (BrushSide qBrushSide in quakeBsp.BrushSides)
			{
            byte[] data = new byte[BrushSide.GetStructLength(sourceBsp.MapType)];
            var sBrushSide = new BrushSide(data, sourceBsp.BrushSides)
            {
                PlaneIndex = qBrushSide.PlaneIndex,
                TextureIndex = GetBrushSideTextureInfoIndex(qBrushSide),
                DisplacementIndex = 0,
                IsBevel = false
            };

            sourceBsp.BrushSides.Add(sBrushSide);
			}
		}

		// Lookup texture info index using brush side's texture name. If it doesn't exist, create a new texture info.
		private int GetBrushSideTextureInfoIndex(BrushSide qBrushSide)
		{
        int textureIndex = LookupTextureInfoIndex(qBrushSide.Texture.Name);
			if (textureIndex > -1)
				return textureIndex;
			
			(Vector3 uAxis, Vector3 vAxis) = GetTextureVectorsWithNormal(qBrushSide.Plane.Normal);
			return CreateTextureInfo(qBrushSide.Texture, uAxis, vAxis);
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

			for (int faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
            // TODO: Handle different face types
            Face qFace = quakeBsp.Faces[faceIndex];

				sourceBsp.Normals.Add(qFace.Normal);

				switch (qFace.Type)
				{
					case FaceType.Polygon:
					case FaceType.Mesh: // Used for Q3 models
					case FaceType.Billboard:
						ConvertPolygon(faceIndex);
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

		private void AddPrimitiveTextureInfoToGameLumps()
		{
        var lumpInfo = new LumpInfo
        {
            ident = (int)GameLumpType.pmti,
            flags = 0 // Don't compress this game lump
        };
        sourceBsp.GameLump.Add(GameLumpType.pmti, lumpInfo);
		}

		private void ConvertPolygon(int faceIndex)
		{
        Face qFace = quakeBsp.Faces[faceIndex];

        Face sFace = CreateFace();
			// TODO: Re-use brush planes?
			sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
			sFace.TextureInfoIndex = CreateTextureInfo(qFace, qFace.FirstIndexIndex);
			sFace.DisplacementIndex = -1;

			// Surface edges
			(int surfEdgeIndex, int numEdges) = CreateSurfaceEdges(faceIndex);
			sFace.FirstEdgeIndexIndex = surfEdgeIndex;
			sFace.NumEdgeIndices = numEdges;

			// Primitives
			sFace.FirstPrimitive = CreatePrimitive(qFace.Vertices.ToArray(), qFace.Indices.ToArray());
			sFace.NumPrimitives = 1;

			splitFaceDict[faceIndex] = [sourceBsp.Faces.Count - 1];
		}

    private void ConvertPolygon_SplitFaces(int faceIndex)
		{
        // Create a face for each triangle
        Face qFace = quakeBsp.Faces[faceIndex];

        // TODO: Remove ToArray() calls
        Vertex[] vertices = qFace.Vertices.ToArray();
        int[] indices = qFace.Indices.ToArray();

			splitFaceDict[faceIndex] = new int[indices.Length / 3];

			for (int i = 0; i < indices.Length; i += 3)
			{
            Face sFace = CreateFace();
				sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
				sFace.TextureInfoIndex = CreateTextureInfo(qFace, i);
				sFace.DisplacementIndex = -1;

				sFace.FirstEdgeIndexIndex = sourceBsp.FaceEdges.Count;
				sFace.NumEdgeIndices = 3;

            Vertex v1 = vertices[indices[i]];
            Vertex v2 = vertices[indices[i + 1]];
            Vertex v3 = vertices[indices[i + 2]];

				CreateEdge(v1, v2, faceIndex);
				CreateEdge(v2, v3, faceIndex);
				CreateEdge(v3, v1, faceIndex);

				splitFaceDict[faceIndex][i / 3] = sourceBsp.Faces.Count - 1;
			}
		}

		private Face CreateFace()
		{
        byte[] data = new byte[Face.GetStructLength(sourceBsp.MapType)];
        var face = new Face(data, sourceBsp.Faces)
        {
            PlaneSide = true,
            IsOnNode = false, // Set to false in order for face to be visible across multiple leaves?
            SurfaceFogVolumeID = -1,
            LightmapStyles =
        [
                0,
                255,
                255,
                255
        ],
            Lightmap = 0,
            Area = 0, // TODO: Check if this needs to be computed
            LightmapStart = new Vector2(),
            LightmapSize = new Vector2(), // TODO: Set to 128x128?
            OriginalFaceIndex = -1, // Ignore since Quake 3 maps don't have split faces
            FirstPrimitive = 0,
            NumPrimitives = 0,
            SmoothingGroups = 0
        };

        sourceBsp.Faces.Add(face);
			
			return face;
		}

		private void ConvertPatch(int faceIndex)
		{
        Face qFace = quakeBsp.Faces[faceIndex];
        int numPatchesWidth = ((int)qFace.PatchSize.X - 1) / 2;
        int numPatchesHeight = ((int)qFace.PatchSize.Y - 1) / 2;
			splitFaceDict[faceIndex] = new int[numPatchesWidth * numPatchesHeight];

        int currentPatch = 0;
			for (int y = 0; y < qFace.PatchSize.Y - 1; y += 2)
			{
				for (int x = 0; x < qFace.PatchSize.X - 1; x += 2)
				{
                int patchStartVertex = qFace.FirstVertexIndex + x + (y * (int)qFace.PatchSize.X);
                int patchFaceIndex = CreatePatch(faceIndex, patchStartVertex);

					splitFaceDict[faceIndex][currentPatch] = patchFaceIndex;
					currentPatch++;
				}
			}
		}

		private int CreatePatch(int qFaceIndex, int patchStartVertex)
		{
        Face qFace = quakeBsp.Faces[qFaceIndex];
        int patchWidth = (int)qFace.PatchSize.X;
			var faceVerts = new Vertex[]
			{
				quakeBsp.Vertices[patchStartVertex],
				quakeBsp.Vertices[patchStartVertex + 2],
				quakeBsp.Vertices[patchStartVertex + 2 + (2 * patchWidth)],
				quakeBsp.Vertices[patchStartVertex + (2 * patchWidth)]
			};

        int sFaceIndex = CreatePatchFace(faceVerts, qFaceIndex);
			CreatePatchDisplacement(sFaceIndex, faceVerts, patchWidth, patchStartVertex, qFace);

			return sFaceIndex;
		}

		private int CreatePatchFace(Vertex[] faceVerts, int faceIndex)
		{
        Face sFace = CreateFace();

        int dispIndex = sourceBsp.Displacements.Count;
			sFace.DisplacementIndex = dispIndex;

        // Create face plane
        Vector3 v1 = faceVerts[0].position - faceVerts[1].position;
        Vector3 v2 = faceVerts[0].position - faceVerts[2].position;
        Vector3 normal = Vector3.Cross(v1, v2).GetNormalized();
        float dist = Vector3.Dot(faceVerts[0].position, normal);
			sFace.PlaneIndex = CreatePlane(normal, dist);

        // TODO: Improve UV mapping
        Vector3 uAxis = (faceVerts[1].position - faceVerts[0].position).GetNormalized() * 2f;
        Vector3 vAxis = (faceVerts[3].position - faceVerts[0].position).GetNormalized() * 2f;

        Face qFace = quakeBsp.Faces[faceIndex];
			ReplaceToolTextureWithInvisibleDisplacement(qFace);
			sFace.TextureInfoIndex = CreateTextureInfo(qFace.Texture, uAxis, vAxis);

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
        Texture texture = qFace.Texture;
			if (texture.Name.StartsWith("tools/"))
			{
				texture.Name = invisibleDisplacementTexture;
				if (LookupTextureDataIndex(invisibleDisplacementTexture) < 0)
					CreateTextureData(invisibleDisplacementTexture);
			}
		}

		private void CreatePatchDisplacement(int sFaceIndex, Vertex[] faceVerts, int patchWidth, int patchStartVertex, Face qFace)
		{
			if (options.noToolDisplacements && qFace.Texture.Name.StartsWith("tools/"))
				return;

        byte[] data = new byte[Displacement.GetStructLength(sourceBsp.MapType)];
			var displacement = new Displacement(data, sourceBsp.Displacements);

        int power = options.DisplacementPower;

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

        uint[] allowedVerts = new uint[10];
			for (int i = 0; i < allowedVerts.Length; i++)
				allowedVerts[i] = 4294967295;

			displacement.AllowedVertices = allowedVerts;

			sourceBsp.Displacements.Add(displacement);
		}

		private int GetMinTesselation(Face qFace)
		{
        string texture = qFace.Texture.Name;
        int minTess = -2147483648;

			if (shaderDict.TryGetValue(texture, out Shader? shader) && shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NONSOLID))
				minTess |= (int)DisplacementFlags.SURF_NOHULL_COLL | (int)DisplacementFlags.SURF_NORAY_COLL;
			
			return minTess;
		}

		private int CreateDisplacementVertices(Vertex[] faceVerts, int patchWidth, int patchStartVertex, int power)
		{
        int firstVertex = sourceBsp.DisplacementVertices.Count;

        Vector3[] controlPoints = GetPatchControlPoints(patchStartVertex, patchWidth);
			var patch = new BezierPatch(controlPoints);

        // Create displacement vertices using bezier patch
        int subdiv = (1 << power) + 1;
			for (int y = 0; y < subdiv; y++)
			{
				for (int x = 0; x < subdiv; x++)
				{
                float widthT = x / (subdiv - 1f);
                float heightT = y / (subdiv - 1f);

                // Get point on quadratic bezier patch
                Vector3 point = patch.GetPoint(widthT, heightT);

					// Get interpolated position on face
					var v1 = Vector3.Lerp(faceVerts[0].position, faceVerts[1].position, widthT);
					var v2 = Vector3.Lerp(faceVerts[3].position, faceVerts[2].position, widthT);
					var posOnFace = Vector3.Lerp(v1, v2, heightT);

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
			for (int i = 0; i < 3; i++)
			{
				for (int j = 0; j < 3; j++)
				{
					controlPoints[i + (j * 3)] = quakeBsp.Vertices[patchStartVertex + i + (j * patchWidth)].position;
				}
			}

			return controlPoints;
		}

		private void CreateDisplacementVertex(Vector3 point)
		{
        byte[] data = new byte[DisplacementVertex.GetStructLength(sourceBsp.MapType)];
        var dispVert = new DisplacementVertex(data, sourceBsp.DisplacementVertices)
        {
            Normal = point.GetNormalized(),
            Magnitude = point.Magnitude()
        };

        sourceBsp.DisplacementVertices.Add(dispVert);
		}

		private int CreateDisplacementTriangles(int power)
		{
        int firstTriangle = sourceBsp.DisplacementTriangles.Count;

        int numTriangles = (1 << (power)) * (1 << (power)) * 2;
			for (int i = 0; i < numTriangles; i++)
				sourceBsp.DisplacementTriangles.Add(6); // TODO: Set displacement flags?

			return firstTriangle;
		}

		private int CreatePlane(Face face)
		{
        // TODO: Avoid adding duplicate planes
        float distance = Vector3.Dot(face.Vertices.First().position, face.Normal);
			return CreatePlane(face.Normal, distance);
		}

		private int CreatePlane(Vector3 normal, float distance)
		{
        byte[] data = new byte[PlaneBSP.GetStructLength(sourceBsp.MapType)];
        var plane = new PlaneBSP(data, sourceBsp.Planes)
        {
            Normal = normal,
            Distance = distance
        };
        plane.Type = (int)GetVectorAxis(plane.Normal);

			sourceBsp.Planes.Add(plane);

			return sourceBsp.Planes.Count - 1;
		}

		private int CreatePrimitive(Vertex[] vertices, int[] indices)
		{
        int primitiveIndex = sourceBsp.Primitives.Count;

        byte[] data = new byte[Primitive.GetStructLength(sourceBsp.MapType)];
        var primitive = new Primitive(data, sourceBsp.Primitives)
        {
            Type = Primitive.PrimitiveType.PRIM_TRILIST,

            FirstVertex = CreatePrimitiveVertices(vertices),
            VertexCount = vertices.Length,

            FirstIndex = CreatePrimitiveIndices(indices),
            IndexCount = indices.Length
        };

        sourceBsp.Primitives.Add(primitive);

			return primitiveIndex;
		}

		private int CreatePrimitiveVertices(Vertex[] vertices)
		{
        int firstPrimVertex = sourceBsp.PrimitiveVertices.Count;

        int lightmapSize = Q3_LIGHTMAP_SIZE;
			if (quakeBsp.Lightmaps.Data.Length == 0 && externalLightmaps.Count != 0)
				lightmapSize = (int)externalLightmaps.First().Value.size.X;

			(Vector2 min, Vector2 max) = GetLightmapExtents(vertices, lightmapSize);
			min /= lightmapSize;
			max /= lightmapSize;

			foreach (Vertex vertex in vertices)
			{
				sourceBsp.PrimitiveVertices.Add(vertex.position);

            byte[] bytes = new byte[PrimitiveTextureInfo.GetStructLength(sourceBsp.MapType)];
            var texInfo = new PrimitiveTextureInfo(bytes, sourceBsp.PrimitiveTextureInfo)
            {
                TexCoord = vertex.uv0
            };

            Vector2 lightCoord = vertex.uv1;
				texInfo.LightmapCoord = (lightCoord - min) / (max - min);

				sourceBsp.PrimitiveTextureInfo.Add(texInfo);
			}

			return firstPrimVertex;
		}

		private int CreatePrimitiveIndices(int[] indices)
		{
        int firstPrimIndex = sourceBsp.PrimitiveIndices.Count;

			foreach (int index in indices)
				sourceBsp.PrimitiveIndices.Add(index);

			return firstPrimIndex;
		}

		private (int surfEdgeIndex, int numEdges) CreateSurfaceEdges(int faceIndex)
		{
        int surfEdgeIndex = sourceBsp.FaceEdges.Count;

        Face qFace = quakeBsp.Faces[faceIndex];
        Vertex[] vertices = qFace.Vertices.ToArray();

        // Convert triangle meshes from Q3 to Source engine's edge loop format
        // Note: Some Q3 faces are concave polygons, so this approach does not always work
        List<Vertex> hullVerts = HullConverter.ConvertConvexHull(vertices, qFace.Normal);
        int numEdges = hullVerts.Count;

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

			for (int i = 0; i < hullVerts.Count; i++)
			{
            int nextIndex = (i + 1) % hullVerts.Count;
				CreateEdge(hullVerts[nextIndex], hullVerts[i], faceIndex);
			}

			return (surfEdgeIndex, numEdges);
		}

		private void CreateEdge(Vertex firstVertex, Vertex secondVertex, int faceIndex)
		{
        // TODO: Prevent adding duplicate edges?
        byte[] data = new byte[Edge.GetStructLength(sourceBsp.MapType)];
        var edge = new Edge(data, sourceBsp.Edges)
        {
            FirstVertexIndex = CreateVertex(firstVertex, faceIndex),
            SecondVertexIndex = CreateVertex(secondVertex, faceIndex)
        };

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

		private int CreateTextureInfo(Face qFace, int firstIndex)
		{
			(Vector3 uAxis, Vector3 vAxis) = GetTextureVectors(qFace, firstIndex);
			return CreateTextureInfo(qFace.Texture, uAxis, vAxis);
		}

		private int CreateTextureInfo(Texture texture, Vector3 uAxis, Vector3 vAxis)
		{
        byte[] data = new byte[TextureInfo.GetStructLength(sourceBsp.MapType)];
        var textureInfo = new TextureInfo(data, sourceBsp.TextureInfo)
        {
            // TODO: Get UV data from face vertices
            UAxis = uAxis,
            VAxis = vAxis,
            LightmapUAxis = uAxis / 32f,
            LightmapVAxis = vAxis / 32f,
            TextureIndex = LookupTextureDataIndex(texture.Name)
        };

        var q3Flags = (Q3SurfaceFlags)texture.Flags;
			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_SLICK))
				textureInfo.Flags |= (int)SourceSurfaceFlags.SURF_SLICK;

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP))
				textureInfo.Flags |= (int)SourceSurfaceFlags.SURF_NOLIGHT;
			
			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_SKY))
				textureInfo.Flags |= (int)(SourceSurfaceFlags.SURF_SKY | SourceSurfaceFlags.SURF_NOLIGHT | SourceSurfaceFlags.SURF_SKYNOEMIT);

			if (q3Flags.HasFlag(Q3SurfaceFlags.SURF_NODRAW))
				textureInfo.Flags |= (int)SourceSurfaceFlags.SURF_NODRAW;

        // Avoid adding duplicate texture info
        int hashCode = BSPUtil.GetHashCode(textureInfo);
			if (textureInfoHashCodeDict.TryGetValue(hashCode, out int textureInfoIndex))
				return textureInfoIndex;
			else
			{
				sourceBsp.TextureInfo.Add(textureInfo);

				textureInfoIndex = sourceBsp.TextureInfo.Count - 1;
				textureInfoHashCodeDict.Add(hashCode, textureInfoIndex);

				textureInfoLookup.TryAdd(texture.Name, textureInfoIndex);
            return textureInfoIndex;
			}
		}

		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectors(Face qFace, int firstIndex)
		{
        // Tangent basis vector derivation: https://www.cs.upc.edu/~virtual/G/1.%20Teoria/06.%20Textures/Tangent%20Space%20Calculation.pdf
        // Note: This only works for face-aligned textures. World-aligned textures will need to be handled differently
        Lump<Vertex> vertices = quakeBsp.Vertices;
        NumList indices = quakeBsp.Indices;

        int i0 = (int)indices[firstIndex];
        int i1 = (int)indices[firstIndex + 1];
        int i2 = (int)indices[firstIndex + 2];

        Vertex v0 = vertices[qFace.FirstVertexIndex + i0];
        Vertex v1 = vertices[qFace.FirstVertexIndex + i1];
        Vertex v2 = vertices[qFace.FirstVertexIndex + i2];

        Vector3 deltaPos1 = v1.position - v0.position;
        Vector3 deltaPos2 = v2.position - v0.position;

        Vector2 deltaUV1 = v1.uv0 - v0.uv0;
        Vector2 deltaUV2 = v2.uv0 - v0.uv0;

        float den = (deltaUV1.X * deltaUV2.Y) - (deltaUV2.X * deltaUV1.Y);
			if (Math.Abs(den) < 0.01f)
				return GetTextureVectorsWithNormal(qFace.Normal);

        float r = 1f / den;
        Vector3 tangent = ((deltaPos1 * deltaUV2.Y) - (deltaPos2 * deltaUV1.Y)) * r / 32f;
        Vector3 binormal = ((deltaPos2 * deltaUV1.X) - (deltaPos1 * deltaUV2.X)) * r / 32f;

			return (tangent, binormal);
		}

		// Fallback for when faces have unusual uv deltas
		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectorsWithNormal(Vector3 faceNormal)
		{
        PlaneBSP.AxisType axis = GetVectorAxis(faceNormal);
        return axis switch
        {
            PlaneBSP.AxisType.PlaneX or PlaneBSP.AxisType.PlaneAnyX => (new Vector3(0f, 2f, 0f), new Vector3(0f, 0f, -2f)),
            PlaneBSP.AxisType.PlaneY or PlaneBSP.AxisType.PlaneAnyY => (new Vector3(2f, 0f, 0f), new Vector3(0f, 0f, -2f)),
            PlaneBSP.AxisType.PlaneZ or PlaneBSP.AxisType.PlaneAnyZ => (new Vector3(2f, 0f, 0f), new Vector3(0f, -2f, 0f)),
            _ => (new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f)),
        };
    }

		private int LookupTextureInfoIndex(string textureName)
		{
			if (textureInfoLookup.TryGetValue(textureName, out int textureInfoIndex))
				return textureInfoIndex;

			return -1;
		}

		private int LookupTextureDataIndex(string textureName)
		{
			if (textureDataLookup.TryGetValue(textureName, out int textureDataIndex))
				return textureDataIndex;

			return -1;
		}

		private void ConvertLightmaps()
		{
			SetLumpVersionNumber(Lightmaps.GetIndexForLump(sourceBsp.MapType), 1);

			if (quakeBsp.Lightmaps.Data.Length > 0)
				ConvertInternalLightmaps();
			else if (externalLightmaps.Count != 0)
				ConvertExternalLightmaps();
		}

		private void ConvertInternalLightmaps()
		{
        byte[] qLightmapData = quakeBsp.Lightmaps.Data;
			var lmColors = new List<ColorRGBExp32>();

			for (int faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
            Face qFace = quakeBsp.Faces[faceIndex];
            int lmIndex = qFace.Lightmap;
				if (lmIndex < 0)
					continue;

				(Vector2 lmStart, Vector2 lmEnd) = GetLightmapExtents(qFace.Vertices, Q3_LIGHTMAP_SIZE);
            Vector2 lmSize = lmEnd - lmStart;

            // TODO: Faces need to be split since Source lightmaps only go up to 35x35 luxels whereas Q3 goes up to 128x128
            //if (lmSize.X - 1 > 35 || lmSize.Y - 1 > 35)
            //	continue;

            int q3LightmapSize = Q3_LIGHTMAP_SIZE * Q3_LIGHTMAP_SIZE * 3;
            int q3LightmapOffset = lmIndex * q3LightmapSize;

            int sourceLightmapOffset = lmColors.Count * 4;

				// Add lightmap colors
				for (int y = (int)lmStart.Y; y < lmEnd.Y; y++)
				{
					for (int x = (int)lmStart.X; x < lmEnd.X; x++)
					{
                    // Remove padding
                    int xCoord = Math.Clamp(x, (int)lmStart.X + LIGHTMAP_PADDING, (int)lmEnd.X - LIGHTMAP_PADDING);
                    int yCoord = Math.Clamp(y, (int)lmStart.Y + LIGHTMAP_PADDING, (int)lmEnd.Y - LIGHTMAP_PADDING);
                    int index = xCoord + (yCoord * Q3_LIGHTMAP_SIZE);

                    ColorRGBExp32 color = ColorUtil.ConvertQ3LightmapToColorRGBExp32(
							qLightmapData[q3LightmapOffset + (index * 3) + 0],
							qLightmapData[q3LightmapOffset + (index * 3) + 1],
							qLightmapData[q3LightmapOffset + (index * 3) + 2]);

						lmColors.Add(color);
					}
				}

				// Update face lightmap info
				foreach (int splitFaceIndex in splitFaceDict[faceIndex])
				{
                Face sFace = sourceBsp.Faces[splitFaceIndex];
					sFace.Lightmap = sourceLightmapOffset;
					sFace.LightmapStart = GetLightmapStart(sFace);
					sFace.LightmapSize = new Vector2(lmSize.X - LIGHTMAP_PADDING, lmSize.Y - LIGHTMAP_PADDING);
				}
			}

        // Copy colors into lightmap data
        byte[] data = new byte[lmColors.Count * 4];
			for (int i = 0; i < lmColors.Count; i++)
			{
            ColorRGBExp32 color = lmColors[i];
            int dataIndex = i * 4;
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

			for (int faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
            Face qFace = quakeBsp.Faces[faceIndex];
            string texture = qFace.Texture.Name;
				if (!shaderDict.TryGetValue(texture, out Shader? shader))
					continue;

            ShaderStage? stage = shader.stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_LIGHTMAP && x.bundles[0].images[0] != "$lightmap");
				if (stage == null)
					continue;

            string lmImage = stage.bundles[0].images[0];
				if (!externalLightmaps.TryGetValue(lmImage, out LightmapData? lmData))
					continue;

				(Vector2 lmStart, Vector2 lmEnd) = GetLightmapExtents(qFace.Vertices, lmData.size.X);
            Vector2 lmSize = lmEnd - lmStart;

            int lightmapOffset = lmColors.Count * 4;

				// Add lightmap colors
				for (int y = (int)lmStart.Y; y < lmEnd.Y; y++)
				{
					for (int x = (int)lmStart.X; x < lmEnd.X; x++)
					{
                    // Remove padding
                    int xCoord = Math.Clamp(x, (int)lmStart.X + LIGHTMAP_PADDING, (int)lmEnd.X - LIGHTMAP_PADDING);
                    int yCoord = Math.Clamp(y, (int)lmStart.Y + LIGHTMAP_PADDING, (int)lmEnd.Y - LIGHTMAP_PADDING);
                    int index = xCoord + (yCoord * (int)lmData.size.X);

                    ColorRGBExp32 color = ColorUtil.ConvertQ3LightmapToColorRGBExp32(
							lmData.data[(index * 3) + 0],
							lmData.data[(index * 3) + 1],
							lmData.data[(index * 3) + 2]);

						lmColors.Add(color);
					}
				}

				foreach (int splitFaceIndex in splitFaceDict[faceIndex])
				{
                Face sFace = sourceBsp.Faces[splitFaceIndex];
					sFace.Lightmap = lightmapOffset;
					sFace.LightmapStart = GetLightmapStart(sFace);
					sFace.LightmapSize = new Vector2(lmSize.X - LIGHTMAP_PADDING, lmSize.Y - LIGHTMAP_PADDING);
				}
			}

        // Copy colors into lightmap data
        byte[] data = new byte[lmColors.Count * 4];
			for (int i = 0; i < lmColors.Count; i++)
			{
            ColorRGBExp32 color = lmColors[i];
            int dataIndex = i * 4;
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
			foreach (Vertex vert in vertices)
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

			var lmStart = new Vector2((int)Math.Floor(uvMin.X * (lightmapSize - 1)) - LIGHTMAP_PADDING, (int)Math.Floor(uvMin.Y * (lightmapSize - 1)) - LIGHTMAP_PADDING);
			var lmEnd = new Vector2((int)Math.Ceiling(uvMax.X * (lightmapSize - 1)) + LIGHTMAP_PADDING, (int)Math.Ceiling(uvMax.Y * (lightmapSize - 1)) + LIGHTMAP_PADDING);

			return (lmStart, lmEnd);
		}

		// TODO: Use lightmap vecs from source face and vertices from quake 3 face (should be more efficient)
		private Vector2 GetLightmapStart(Face face)
		{
			var lightmapStart = new Vector2(float.MaxValue, float.MaxValue);
        TextureInfo texInfo = face.TextureInfo;
        Vector3 lightmapUAxis = texInfo.LightmapUAxis;
        Vector3 lightmapVAxis = texInfo.LightmapVAxis;

			// Find the minimum values for world space uv offsets
			foreach (int edgeIndex in face.EdgeIndices)
			{
            Edge edge = sourceBsp.Edges[edgeIndex];
            Vector3 vertex = edge.FirstVertex.position;

            float uOffset = Vector3.Dot(vertex, lightmapUAxis);
				if (uOffset < lightmapStart.X)
					lightmapStart.X = uOffset;

            float vOffset = Vector3.Dot(vertex, lightmapVAxis);
				if (vOffset < lightmapStart.Y)
					lightmapStart.Y = vOffset;
			}

			return lightmapStart;
		}

		private void ConvertVisData()
		{
			if (quakeBsp.Visibility.Data.Length == 0) // No VisData
			{
				sourceBsp.Visibility.Data = [];
				return;
			}

			var visDataList = new List<byte>();

        int numClusters = quakeBsp.Visibility.NumClusters;
        int clusterSize = quakeBsp.Visibility.ClusterSize;
        int numClusterBytes = (numClusters + 7) >> 3; // Number of bytes to store each cluster bit

        int[][] byteOffsets = new int[numClusters][];
        int visDataStartLength = 4 + (numClusters * 8); // Byte length of numClusters and byteOffsets
        int currentOffset = visDataStartLength;

			// Get byte offsets and vis data
			for (int i = 0; i < numClusters; i++)
			{
				byteOffsets[i] = new int[2];
				byteOffsets[i][0] = currentOffset; // PVS offset
				byteOffsets[i][1] = currentOffset; // PAS offset (PVS and PAS share the same data for now since the Quake BSP does not have any info on sound detection)

            int vecOffset = 8 + (i * clusterSize);
            byte[] uncompressed = new byte[numClusterBytes]; // Note: Use numClusterBytes instead of clusterSize since Source engine expects the length of the vis data to not exceed the number of cluster bits
				Buffer.BlockCopy(quakeBsp.Visibility.Data, vecOffset, uncompressed, 0, numClusterBytes);
            byte[] compressed = Visibility.Compress(uncompressed); // This will compress Q3 vis data into something compatible for Source engine

				visDataList.AddRange(compressed);

				currentOffset += compressed.Length;
			}

        // Copy numClusters and byteOffsets to visData buffer
        byte[] visData = new byte[visDataStartLength + visDataList.Count];
			BitConverter.GetBytes(numClusters).CopyTo(visData, 0);
			for (int i = 0; i < numClusters; i++)
			{
				BitConverter.GetBytes(byteOffsets[i][0]).CopyTo(visData, 4 + (i * 8));
				BitConverter.GetBytes(byteOffsets[i][1]).CopyTo(visData, 8 + (i * 8));
			}

			// Copy visData
			visDataList.CopyTo(visData, visDataStartLength);

			sourceBsp.Visibility.Data = visData;
		}

		private void ConvertAreas()
		{
        // Create an area in order to have valid node/leaf area references
        byte[] areaBytes = new byte[Area.GetStructLength(sourceBsp.MapType)];
			var area = new Area(areaBytes, sourceBsp.Areas);
			sourceBsp.Areas.Add(area);
		}

		private void ConvertAreaPortals()
		{
			if (!options.oldBSP)
				SetLumpVersionNumber(AreaPortal.GetIndexForLump(sourceBsp.MapType), 1);

        // Create an area portal for the first area
        byte[] areaPortalBytes = new byte[AreaPortal.GetStructLength(sourceBsp.MapType)];
			var areaPortal = new AreaPortal(areaPortalBytes, sourceBsp.AreaPortals);
			sourceBsp.AreaPortals.Add(areaPortal);
		}

		private void ConvertWorldLights()
		{
			SetLumpVersionNumber(WorldLight.GetIndexForLump(sourceBsp.MapType), 1);

        // Add worldlight to disable fullbright
        // TODO: How to check for fullbright maps?
        byte[] worldLightBytes = new byte[WorldLight.GetStructLength(sourceBsp.MapType)];
			var worldLight = new WorldLight(worldLightBytes, sourceBsp.WorldLights);
			sourceBsp.WorldLights.Add(worldLight);
		}

		private void SetLumpVersionNumber(int lumpIndex, int lumpVersion)
		{
        LumpInfo lumpInfo = sourceBsp[lumpIndex];
			lumpInfo.version = lumpVersion;
			sourceBsp[lumpIndex] = lumpInfo;
		}

		private void WriteBSP()
		{
        string mapsDir = Path.Combine(options.outputDir, "maps");
			if (!Directory.Exists(mapsDir))
				Directory.CreateDirectory(mapsDir);

			var writer = new BSPWriter(sourceBsp);
        string bspPath = Path.Combine(mapsDir, $"{options.prefix}{quakeBsp.MapName}.bsp");
			writer.WriteBSP(bspPath);
			
			logger.Log($"Converted BSP: {bspPath}");
		}

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}