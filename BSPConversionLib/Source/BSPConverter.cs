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
using System.IO.Compression;
using System.Diagnostics;
using System.Collections;

namespace BSPConversionLib
{
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
		public bool skyFix;
		private int displacementPower;
		public int DisplacementPower
		{
			get { return displacementPower; }
			set { displacementPower = Math.Clamp(value, 2, 4); }
		}
		public bool newBSP;
		public string inputFile;
		public string outputDir;
	}

	public class BSPConverter
	{
		private BSPConverterOptions options;
		private ILogger logger;

		private BSP quakeBsp;
		private BSP sourceBsp;

		private ContentManager contentManager;
		
		private Dictionary<string, Shader> shaderDict = new Dictionary<string, Shader>();
		private Dictionary<int, int> textureInfoHashCodeDict = new Dictionary<int, int>(); // Maps TextureInfo hash codes to TextureInfo indices
		private Dictionary<string, int> textureInfoLookup = new Dictionary<string, int>();
		private Dictionary<string, int> textureDataLookup = new Dictionary<string, int>();
		private Dictionary<int, int[]> splitFaceDict = new Dictionary<int, int[]>(); // Maps the original face index to the new face indices split by triangles

		// This is much more efficient than adding to NumList directly
		// TODO: Update LibBSP's NumList so that adding new elements doesn't kill performance
		private List<ushort> dispTriangles = new List<ushort>(); 

		private const int CONTENTS_EMPTY = 0;
		private const int CONTENTS_SOLID = 0x1;
		private const int CONTENTS_STRUCTURAL = 0x10000000;

		private const int Q3_LIGHTMAP_SIZE = 128;

		public BSPConverter(BSPConverterOptions options, ILogger logger)
		{
			this.options = options;
			this.logger = logger;
		}

		public void Convert()
		{
			contentManager = new ContentManager(options.inputFile);
			
			LoadBSP();
			
			ConvertShaders();
			ConvertTextureFiles();

			ConvertEntities();
			ConvertTextures();
			ConvertPlanes();
			ConvertFaces_SplitFaces();
			ConvertLeaves_SplitFaces();
			ConvertLeafFaces_SplitFaces();
			//ConvertLeaves();
			//ConvertLeafFaces();
			ConvertLeafBrushes();
			ConvertNodes();
			ConvertModels();
			//ConvertFaces();
			ConvertBrushes();
			ConvertBrushSides();
			ConvertLightmaps();
			ConvertVisData();
			ConvertAreas();
			ConvertAreaPortals();

			WriteBSP();
			
			contentManager.Dispose();
		}

		private void LoadBSP()
		{
			// TODO: Support converting multiple bsp's (some pk3's contain multiple bsp's)
			quakeBsp = contentManager.BSPFiles.First();
			
			var mapType = options.newBSP ? MapType.Source25 : MapType.Source20;
			sourceBsp = new BSP(Path.GetFileName(options.inputFile), mapType);
		}

		private void ConvertShaders()
		{
			shaderDict = LoadShaderDictionary();
			
			var materialConverter = new MaterialConverter(contentManager.ContentDir, shaderDict);
			foreach (var texture in quakeBsp.Textures)
				materialConverter.Convert(texture.Name);
		}

		private Dictionary<string, Shader> LoadShaderDictionary()
		{
			var q3Shaders = Directory.GetFiles(Path.Combine(ContentManager.GetQ3ContentDir(), "scripts"), "*.shader");
			var pk3Shaders = Directory.GetFiles(Path.Combine(contentManager.ContentDir, "scripts"), "*.shader");
			var allShaders = q3Shaders.Concat(pk3Shaders);

			var shaderLoader = new ShaderLoader(allShaders);
			return shaderLoader.LoadShaders();
		}

		private void ConvertTextureFiles()
		{
			var textureConverter = options.noPak ?
				new TextureConverter(contentManager.ContentDir, options.outputDir) :
				new TextureConverter(contentManager.ContentDir, sourceBsp);
			textureConverter.Convert();
		}

		private void ConvertEntities()
		{
			var converter = new EntityConverter(quakeBsp.Entities, sourceBsp.Entities, shaderDict);
			converter.Convert();
		}

		private void ConvertTextures()
		{
			foreach (var texture in quakeBsp.Textures)
				CreateTextureData(texture);
		}

		private void CreateTextureData(Texture texture)
		{
			var data = new byte[TextureData.GetStructLength(sourceBsp.MapType)];
			var textureData = new TextureData(data, sourceBsp.TextureData);

			textureData.Reflectivity = new Color(); // TODO: Get reflectivity from vtf
			textureData.TextureStringOffsetIndex = CreateTextureDataStringTableEntry(texture.Name);
			textureData.Size = new Vector2(128, 128); // TODO: Get size from vtf
			textureData.ViewSize = new Vector2(128, 128);

			sourceBsp.TextureData.Add(textureData);

			if (!textureDataLookup.ContainsKey(texture.Name))
				textureDataLookup.Add(texture.Name, sourceBsp.TextureData.Count - 1);
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

				plane.Normal = qPlane.Normal;
				plane.Distance = qPlane.Distance;
				plane.Type = (int)GetVectorAxis(qPlane.Normal);

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
			if (options.newBSP)
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

			if (options.skyFix)
				FixSkyboxRendering();
		}

		// Configuring all child1 nodes and the first child2 node to render all faces seems to fix skybox rendering
		// This hack breaks rendering on some maps, so it's disabled by default
		// TODO: Figure out a better way to do this, or at least only apply this fix for skybox faces. Could move all skybox faces to be at the end of the face array to target them specifically
		// TODO: Set onNode to true for all skybox faces? onNode is typically only set to true for faces that split visleafs
		// TODO: Does this need to be done for all model head nodes?
		private void FixSkyboxRendering()
		{
			var rootNode = sourceBsp.Nodes[0];
			
			var child2 = sourceBsp.Nodes[rootNode.Child2Index];
			child2.NumFaceIndices = sourceBsp.Faces.Count;

			FixSkyboxRenderingRecursive(rootNode);
		}

		private void FixSkyboxRenderingRecursive(Node node)
		{
			node.NumFaceIndices = sourceBsp.Faces.Count;
			if (node.Child1Index > 0)
				FixSkyboxRenderingRecursive(sourceBsp.Nodes[node.Child1Index]);
		}

		private void ConvertLeaves()
		{
			var version = options.newBSP ? 2 : 1;
			SetLumpVersionNumber(Leaf.GetIndexForLump(sourceBsp.MapType), version);

			foreach (var qLeaf in quakeBsp.Leaves)
			{
				var data = new byte[Leaf.GetStructLength(sourceBsp.MapType)];
				var leaf = new Leaf(data, sourceBsp.Leaves);

				if (sourceBsp.Leaves.Count == 0)
				{
					leaf.Contents = CONTENTS_SOLID; // First leaf is always solid, otherwise game crashes
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
			var version = options.newBSP ? 2 : 1;
			SetLumpVersionNumber(Leaf.GetIndexForLump(sourceBsp.MapType), version);

			var currentFaceIndex = 0;

			foreach (var qLeaf in quakeBsp.Leaves)
			{
				var data = new byte[Leaf.GetStructLength(sourceBsp.MapType)];
				var leaf = new Leaf(data, sourceBsp.Leaves);

				if (sourceBsp.Leaves.Count == 0)
				{
					leaf.Contents = CONTENTS_SOLID; // First leaf is always solid, otherwise game crashes
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
				var numFaces = 0;
				for (var i = 0; i < qLeaf.NumMarkFaceIndices; i++)
					numFaces += splitFaceDict[(int)quakeBsp.LeafFaces[qLeaf.FirstMarkFaceIndex + i]].Length;
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
			if (options.newBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafFacesLump(sourceBsp.MapType, out _), 1);

			foreach (var qLeafFace in quakeBsp.LeafFaces)
				sourceBsp.LeafFaces.Add(qLeafFace);
		}

		private void ConvertLeafFaces_SplitFaces()
		{
			if (options.newBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafFacesLump(sourceBsp.MapType, out _), 1);

			foreach (var qLeafFace in quakeBsp.LeafFaces)
			{
				var splitFaceIndices = splitFaceDict[(int)qLeafFace];
				for (var i = 0; i < splitFaceIndices.Length; i++)
					sourceBsp.LeafFaces.Add(splitFaceIndices[i]);
			}
		}

		private void ConvertLeafBrushes()
		{
			if (options.newBSP)
				SetLumpVersionNumber(NumList.GetIndexForLeafBrushesLump(sourceBsp.MapType, out _), 1);

			foreach (var qLeafBrush in quakeBsp.LeafBrushes)
				sourceBsp.LeafBrushes.Add(qLeafBrush);
		}

		private void ConvertModels()
		{
			foreach (var qModel in quakeBsp.Models)
			{
				// Modify model 0 on sourceBsp until ready to remove sourceBsp's models? Index 0 handles all world geometry, anything after is for brush entities
				var data = new byte[Model.GetStructLength(sourceBsp.MapType)];
				var sModel = new Model(data, sourceBsp.Models);

				var mins = qModel.Minimums;
				var maxs = qModel.Maximums;
				var minExtents = -16384;
				var maxExtents = 16384;
				//sModel.Minimums = new Vector3(Math.Clamp(mins.X(), minExtents, maxExtents), Math.Clamp(mins.Y(), minExtents, maxExtents), Math.Clamp(mins.Z(), minExtents, maxExtents));
				//sModel.Maximums = new Vector3(Math.Clamp(maxs.X(), minExtents, maxExtents), Math.Clamp(maxs.Y(), minExtents, maxExtents), Math.Clamp(maxs.Z(), minExtents, maxExtents));
				if (mins.X() < minExtents || mins.Y() < minExtents || mins.Z() < minExtents)
					logger.Log("Exceeded min extents: " + mins);

				if (maxs.X() > maxExtents || maxs.Y() > maxExtents || maxs.Z() > maxExtents)
					logger.Log("Exceeded max extents: " + maxs);

				sModel.Minimums = qModel.Minimums;
				sModel.Maximums = qModel.Maximums;
				if (sourceBsp.Models.Count == 0) // First model always references first node?
					sModel.HeadNodeIndex = 0;
				else
					sModel.HeadNodeIndex = CreateHeadNode(qModel.FirstBrushIndex, mins, maxs);
				sModel.Origin = new Vector3(0f, 0f, 0f); // Recalculate origin?
				sModel.FirstFaceIndex = qModel.FirstFaceIndex;
				sModel.NumFaces = qModel.NumFaces;

				sourceBsp.Models.Add(sModel);
			}
		}

		// TODO: Add face references in order for showtriggers_toggle to work?
		// Creates a head node using the leaf that references the brush index (seems to be required to get trigger collisions working)
		private int CreateHeadNode(int firstBrushIndex, Vector3 mins, Vector3 maxs)
		{
			var leafIndex = FindLeafIndex(firstBrushIndex);
			if (leafIndex < 0)
				return 0;
			
			var leaf = sourceBsp.Leaves[leafIndex];
			leaf.Contents = CONTENTS_SOLID;
			leaf.Visibility = -1; // Cluster index
			leaf.Area = 0;
			leaf.Minimums = mins;
			leaf.Maximums = maxs;
			
			var data = new byte[Node.GetStructLength(sourceBsp.MapType)];
			var node = new Node(data, sourceBsp.Nodes);
			
			node.Child1Index = -leafIndex - 1;
			node.Child2Index = -leafIndex - 1;

			node.PlaneIndex = 0;
			node.Minimums = mins;
			node.Maximums = maxs;
			node.FirstFaceIndex = 0;
			node.NumFaceIndices = 0;
			node.AreaIndex = 0;

			sourceBsp.Nodes.Add(node);

			return sourceBsp.Nodes.Count - 1;
		}

		// Finds a leaf that references the brush index
		private int FindLeafIndex(int firstBrushIndex)
		{
			var leafBrushes = sourceBsp.LeafBrushes;

			for (var i = 0; i < sourceBsp.Leaves.Count; i++)
			{
				var leaf = sourceBsp.Leaves[i];
				if (leafBrushes[leaf.FirstMarkBrushIndex] == firstBrushIndex)
					return i;
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

				sourceBsp.Brushes.Add(sBrush);
			}
		}

		private int GetBrushContents(Texture texture)
		{
			// TODO: Handle other texture contents flags
			// TODO: Remove tool brushes instead?
			if ((texture.Contents & CONTENTS_STRUCTURAL) == CONTENTS_STRUCTURAL)
				return CONTENTS_EMPTY;

			return CONTENTS_SOLID;
		}

		private void ConvertBrushSides()
		{
			if (options.newBSP)
				SetLumpVersionNumber(BrushSide.GetIndexForLump(sourceBsp.MapType), 1);

			foreach (var qBrushSide in quakeBsp.BrushSides)
			{
				var data = new byte[BrushSide.GetStructLength(sourceBsp.MapType)];
				var sBrushSide = new BrushSide(data, sourceBsp.BrushSides);

				sBrushSide.PlaneIndex = qBrushSide.PlaneIndex;
				sBrushSide.TextureIndex = LookupTextureInfoIndex(qBrushSide.Texture.Name);
				sBrushSide.DisplacementIndex = 0;
				sBrushSide.IsBevel = false;

				sourceBsp.BrushSides.Add(sBrushSide);
			}
		}

		private void ConvertFaces()
		{
			if (options.newBSP)
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

				var sFace = CreateFace();
				// TODO: Re-use brush planes?
				sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
				sFace.TextureInfoIndex = CreateTextureInfo(qFace);
				sFace.DisplacementIndex = -1;

				(var surfEdgeIndex, var numEdges) = CreateSurfaceEdges(faceIndex);
				//var usePrims = sourceBsp.Primitives.Count < 6;
				//if (usePrims)
				//{
				//	sFace.FirstEdgeIndexIndex = 0;
				//	sFace.NumEdgeIndices = 0;
				//}
				//else
				{
					sFace.FirstEdgeIndexIndex = surfEdgeIndex;
					sFace.NumEdgeIndices = numEdges;
				}

				//if (usePrims)
				//{
				//	sFace.FirstPrimitive = CreatePrimitive(qFace.Vertices.ToArray(), qFace.Indices.ToArray());
				//	sFace.NumPrimitives = 1;
				//}
				//else
				{
					sFace.FirstPrimitive = 0;
					sFace.NumPrimitives = 0;
				}
			}
		}

		private void ConvertFaces_SplitFaces()
		{
			if (options.newBSP)
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
						ConvertPatch_SplitFaces(faceIndex);
						break;
					default:
						logger.Log("Unsupported face type: " + qFace.Type);
						break;
				}
			}

			// Update displacement triangles
			sourceBsp.DisplacementTriangles = BSPUtil.CreateNumList(dispTriangles.ToArray(), NumList.DataType.UInt16, sourceBsp);
		}

		private void ConvertPolygon_SplitFaces(int faceIndex)
		{
			// Create a face for each triangle
			var qFace = quakeBsp.Faces[faceIndex];
			var vertices = qFace.Vertices.ToArray();
			var indices = qFace.Indices.ToArray();

			splitFaceDict[faceIndex] = new int[indices.Length / 3];

			for (var i = 0; i < indices.Length; i += 3)
			{
				var sFace = CreateFace();
				sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
				sFace.TextureInfoIndex = CreateTextureInfo(qFace);
				sFace.DisplacementIndex = -1;

				sFace.FirstEdgeIndexIndex = sourceBsp.FaceEdges.Count;
				sFace.NumEdgeIndices = 3;

				var v1 = vertices[indices[i]];
				var v2 = vertices[indices[i + 1]];
				var v3 = vertices[indices[i + 2]];

				CreateEdge(v2, v1, faceIndex);
				CreateEdge(v3, v2, faceIndex);
				CreateEdge(v1, v3, faceIndex);

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

		private void ConvertPatch_SplitFaces(int faceIndex)
		{
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
			CreatePatchDisplacement(sFaceIndex, faceVerts, patchWidth, patchStartVertex);

			return sFaceIndex;
		}

		private int CreatePatchFace(Vertex[] faceVerts, int faceIndex)
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

			// TODO: Improve UV mapping
			var uAxis = (faceVerts[1].position - faceVerts[0].position).GetNormalized() * 2f;
			var vAxis = (faceVerts[3].position - faceVerts[0].position).GetNormalized() * 2f;

			var qFace = quakeBsp.Faces[faceIndex];
			sFace.TextureInfoIndex = CreateTextureInfo(qFace.Texture.Name, uAxis, vAxis);

			// Create face edges
			sFace.FirstEdgeIndexIndex = sourceBsp.FaceEdges.Count;
			sFace.NumEdgeIndices = 4;

			CreateEdge(faceVerts[3], faceVerts[2], faceIndex);
			CreateEdge(faceVerts[2], faceVerts[1], faceIndex);
			CreateEdge(faceVerts[1], faceVerts[0], faceIndex);
			CreateEdge(faceVerts[0], faceVerts[3], faceIndex);

			return sourceBsp.Faces.Count - 1;
		}

		private void CreatePatchDisplacement(int faceIndex, Vertex[] faceVerts, int patchWidth, int patchStartVertex)
		{
			var data = new byte[Displacement.GetStructLength(sourceBsp.MapType)];
			var displacement = new Displacement(data, sourceBsp.Displacements);

			var power = options.DisplacementPower;

			displacement.StartPosition = quakeBsp.Vertices[patchStartVertex].position;
			displacement.FirstVertexIndex = CreateDisplacementVertices(faceVerts, patchWidth, patchStartVertex, power);
			displacement.FirstTriangleIndex = CreateDisplacementTriangles(power);
			displacement.Power = power;
			displacement.MinimumTesselation = 0;
			displacement.SmoothingAngle = 0f;
			displacement.Contents = 1;
			displacement.FaceIndex = faceIndex;
			displacement.LightmapAlphaStart = 0;
			displacement.LightmapSamplePositionStart = 0;

			var allowedVerts = new uint[10];
			for (var i = 0; i < allowedVerts.Length; i++)
				allowedVerts[i] = 4294967295;

			displacement.AllowedVertices = allowedVerts;

			sourceBsp.Displacements.Add(displacement);
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

			dispVert.Normal = point.GetNormalized();
			dispVert.Magnitude = point.Magnitude();

			sourceBsp.DisplacementVertices.Add(dispVert);
		}

		private int CreateDisplacementTriangles(int power)
		{
			var firstTriangle = sourceBsp.DisplacementTriangles.Count;

			var numTriangles = (1 << (power)) * (1 << (power)) * 2;
			for (var i = 0; i < numTriangles; i++)
				dispTriangles.Add(0); // TODO: Set displacement flags?

			return firstTriangle;
		}

		private int CreatePlane(Face face)
		{
			// TODO: Avoid adding duplicate planes
			var distance = Vector3.Dot(face.Vertices.First().position, face.Normal);
			return CreatePlane(face.Normal, distance);
		}

		private int CreatePlane(Vector3 normal, float distance)
		{
			var data = new byte[PlaneBSP.GetStructLength(sourceBsp.MapType)];
			var plane = new PlaneBSP(data, sourceBsp.Planes);

			plane.Normal = normal;
			plane.Distance = distance;
			plane.Type = (int)GetVectorAxis(plane.Normal);

			sourceBsp.Planes.Add(plane);

			return sourceBsp.Planes.Count - 1;
		}

		private int CreatePrimitive(Vertex[] vertices, int[] indices)
		{
			var primitiveIndex = sourceBsp.Primitives.Count;

			var data = new byte[Primitive.GetStructLength(sourceBsp.MapType)];
			var primitive = new Primitive(data, sourceBsp.Primitives);

			primitive.Type = Primitive.PrimitiveType.PRIM_TRILIST;

			primitive.FirstVertex = CreatePrimitiveVertices(vertices);
			primitive.VertexCount = vertices.Length;

			primitive.FirstIndex = CreatePrimitiveIndices(indices, primitive.FirstVertex);
			primitive.IndexCount = indices.Length;

			sourceBsp.Primitives.Add(primitive);

			return primitiveIndex;
		}

		private int CreatePrimitiveIndices(int[] indices, int firstVertex)
		{
			var firstPrimIndex = sourceBsp.PrimitiveIndices.Count;

			foreach (var index in indices)
				sourceBsp.PrimitiveIndices.Add(index + firstVertex);

			return firstPrimIndex;
		}

		private int CreatePrimitiveVertices(Vertex[] vertices)
		{
			var firstPrimVertex = sourceBsp.PrimitiveVertices.Count;

			foreach (var vertex in vertices)
				sourceBsp.PrimitiveVertices.Add(vertex.position);

			return firstPrimVertex;
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

		private int CreateTextureInfo(Face face)
		{
			(var uAxis, var vAxis) = GetTextureVectors(face);
			return CreateTextureInfo(face.Texture.Name, uAxis, vAxis);
		}

		private int CreateTextureInfo(string textureName, Vector3 uAxis, Vector3 vAxis)
		{
			var data = new byte[TextureInfo.GetStructLength(sourceBsp.MapType)];
			var textureInfo = new TextureInfo(data, sourceBsp.TextureInfo);
			
			// TODO: Get UV data from face vertices
			textureInfo.UAxis = uAxis;
			textureInfo.VAxis = vAxis;
			textureInfo.LightmapUAxis = uAxis / 32f;
			textureInfo.LightmapVAxis = vAxis / 32f;
			textureInfo.TextureIndex = LookupTextureDataIndex(textureName);

			if (IsSkyTexture(textureName))
				textureInfo.Flags = 132100; // TODO: Find flag definition in Source engine
			else
				textureInfo.Flags = 0;

			// Avoid adding duplicate texture info
			var hashCode = BSPUtil.GetHashCode(textureInfo);
			if (textureInfoHashCodeDict.TryGetValue(hashCode, out var textureInfoIndex))
				return textureInfoIndex;
			else
			{
				sourceBsp.TextureInfo.Add(textureInfo);

				textureInfoIndex = sourceBsp.TextureInfo.Count - 1;
				textureInfoHashCodeDict.Add(hashCode, textureInfoIndex);

				if (!textureInfoLookup.ContainsKey(textureName))
					textureInfoLookup.Add(textureName, textureInfoIndex);

				return textureInfoIndex;
			}
		}

		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectors(Face face)
		{
			var vertices = face.Vertices.ToArray();
			var indices = face.Indices.ToArray();

			var v0 = vertices[indices[0]];
			var v1 = vertices[indices[1]];
			var v2 = vertices[indices[2]];

			var deltaPos1 = v1.position - v0.position;
			var deltaPos2 = v2.position - v0.position;

			var deltaUV1 = v1.uv0 - v0.uv0;
			var deltaUV2 = v2.uv0 - v0.uv0;

			var r = 1f / (deltaUV1.X * deltaUV2.Y - deltaUV1.Y * deltaUV2.X);
			var tangent = (deltaPos1 * deltaUV2.Y - deltaPos2 * deltaUV1.Y) * r / 32f;
			var binormal = (deltaPos2 * deltaUV1.X - deltaPos1 * deltaUV2.X) * r / 32f;

			return (tangent, binormal);
		}

		private bool IsSkyTexture(string textureName)
		{
			if (shaderDict.TryGetValue(textureName, out var shader))
				return shader.skyParms != null;

			return false;
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

			var qLightmapData = quakeBsp.Lightmaps.Data;
			var lmColors = new List<ColorRGBExp32>();
			
			for (var faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
				var qFace = quakeBsp.Faces[faceIndex];
				var lmIndex = qFace.Lightmap;
				if (lmIndex < 0)
					continue;

				(var lmStart, var lmEnd) = GetLightmapExtents(qFace);
				var lmSize = lmEnd - lmStart;

				// TODO: Faces need to be split since Source lightmaps only go up to 35x35 luxels whereas Q3 goes up to 128x128
				if (lmSize.X - 1 > 35 || lmSize.Y - 1 > 35)
					continue;

				var q3LightmapSize = Q3_LIGHTMAP_SIZE * Q3_LIGHTMAP_SIZE * 3;
				var q3LightmapOffset = lmIndex * q3LightmapSize;

				var sourceLightmapOffset = lmColors.Count * 4;

				// Add lightmap colors
				for (var y = (int)lmStart.Y; y < lmEnd.Y; y++)
				{
					for (var x = (int)lmStart.X; x < lmEnd.X; x++)
					{
						var color = new ColorRGBExp32();

						var index = x + y * Q3_LIGHTMAP_SIZE;
						color.r = qLightmapData[q3LightmapOffset + index * 3 + 0];
						color.g = qLightmapData[q3LightmapOffset + index * 3 + 1];
						color.b = qLightmapData[q3LightmapOffset + index * 3 + 2];
						color.exponent = 0;

						lmColors.Add(color);
					}
				}

				// Update face lightmap info
				foreach (var splitFaceIndex in splitFaceDict[faceIndex])
				{
					var sFace = sourceBsp.Faces[splitFaceIndex];
					sFace.Lightmap = sourceLightmapOffset;
					sFace.LightmapStart = GetLightmapStart(sFace);
					sFace.LightmapSize = new Vector2(lmSize.X - 1, lmSize.Y - 1);
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

		private (Vector2, Vector2) GetLightmapExtents(Face face)
		{
			var uvMin = new Vector2(1f, 1f);
			var uvMax = new Vector2(0f, 0f);
			foreach (var vert in face.Vertices)
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

			var lmStart = new Vector2((int)Math.Floor(uvMin.X * Q3_LIGHTMAP_SIZE), (int)Math.Floor(uvMin.Y * Q3_LIGHTMAP_SIZE));
			var lmEnd = new Vector2((int)Math.Ceiling(uvMax.X * Q3_LIGHTMAP_SIZE), (int)Math.Ceiling(uvMax.Y * Q3_LIGHTMAP_SIZE));

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

		private void ConvertVisData()
		{
			sourceBsp.Visibility.Data = new byte[0];
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
			if (options.newBSP)
				SetLumpVersionNumber(AreaPortal.GetIndexForLump(sourceBsp.MapType), 1);

			// Create an area portal for the first area
			var areaPortalBytes = new byte[AreaPortal.GetStructLength(sourceBsp.MapType)];
			var areaPortal = new AreaPortal(areaPortalBytes, sourceBsp.AreaPortals);
			sourceBsp.AreaPortals.Add(areaPortal);
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
			var bspPath = Path.Combine(mapsDir, quakeBsp.MapName + ".bsp");
			writer.WriteBSP(bspPath);
			
			logger.Log($"Converted BSP: {bspPath}");
		}
	}
}