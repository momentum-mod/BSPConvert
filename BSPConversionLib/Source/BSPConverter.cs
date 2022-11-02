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
using BSPConversionLib.Source;

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

	public class BSPConverter
	{
		private string quakeFilePath;
		private string outputDir;
		private bool noPak;
		private bool skyFix;
		private ILogger logger;

		private BSP quakeBsp;
		private BSP sourceBsp;
		private string pk3Dir;
		private Dictionary<string, Shader> shaderDict = new Dictionary<string, Shader>();
		private Dictionary<int, int> textureInfoHashCodeDict = new Dictionary<int, int>(); // Maps TextureInfo hash codes to TextureInfo indices
		private Dictionary<string, int> textureInfoLookup = new Dictionary<string, int>();
		private Dictionary<string, int> textureDataLookup = new Dictionary<string, int>();
		private Dictionary<int, int[]> splitFaceDict = new Dictionary<int, int[]>(); // Maps the original face index to the new face indices split by triangles

		private const int CONTENTS_EMPTY = 0;
		private const int CONTENTS_SOLID = 0x1;
		private const int CONTENTS_STRUCTURAL = 0x10000000;

		public BSPConverter(string quakeFilePath, string outputDir, bool noPak, bool skyFix, ILogger logger)
		{
			this.quakeFilePath = quakeFilePath;
			this.outputDir = outputDir;
			this.noPak = noPak;
			this.skyFix = skyFix;
			this.logger = logger;
		}

		public void Convert()
		{
			LoadBSP();
			LoadShaders();

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

			DeletePk3Directory();
		}

		private void LoadBSP()
		{
			quakeBsp = LoadQuakeBsp(quakeFilePath);
			sourceBsp = new BSP(Path.GetFileName(outputDir), MapType.Source20);
		}

		private BSP LoadQuakeBsp(string quakeFilePath)
		{
			if (!File.Exists(quakeFilePath))
				throw new FileNotFoundException(quakeFilePath);

			if (Path.GetExtension(quakeFilePath) == ".bsp")
				return new BSP(new FileInfo(quakeFilePath));

			pk3Dir = ExtractPk3(quakeFilePath);

			// Find extracted BSP
			var pk3Bsp = Directory.GetFiles(pk3Dir, "*.bsp", SearchOption.AllDirectories).First();

			return new BSP(new FileInfo(pk3Bsp));
		}

		// Extract pk3 into temp directory
		private string ExtractPk3(string pk3FilePath)
		{
			var fileName = Path.GetFileNameWithoutExtension(pk3FilePath);
			var pk3Dir = Path.Combine(Path.GetTempPath(), fileName);

			// Delete any pre-existing pk3 contents
			if (Directory.Exists(pk3Dir))
				Directory.Delete(pk3Dir, true);

			ZipFile.ExtractToDirectory(pk3FilePath, pk3Dir);

			return pk3Dir;
		}

		private void LoadShaders()
		{
			if (!string.IsNullOrEmpty(pk3Dir))
			{
				var shaderConverter = new ShaderConverter(pk3Dir);
				shaderDict = shaderConverter.Convert();
			}
		}

		private void ConvertTextureFiles()
		{
			if (!string.IsNullOrEmpty(pk3Dir))
			{
				var textureConverter = noPak ? new TextureConverter(pk3Dir, outputDir, shaderDict, logger) : 
					new TextureConverter(pk3Dir, sourceBsp, shaderDict, logger);
				textureConverter.Convert();
			}
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

			if (skyFix)
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
			SetLumpVersionNumber(Leaf.GetIndexForLump(sourceBsp.MapType), 1);

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
			SetLumpVersionNumber(Leaf.GetIndexForLump(sourceBsp.MapType), 1);

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
			foreach (var qLeafFace in quakeBsp.LeafFaces)
				sourceBsp.LeafFaces.Add(qLeafFace);
		}

		private void ConvertLeafFaces_SplitFaces()
		{
			foreach (var qLeafFace in quakeBsp.LeafFaces)
			{
				var splitFaceIndices = splitFaceDict[(int)qLeafFace];
				for (var i = 0; i < splitFaceIndices.Length; i++)
					sourceBsp.LeafFaces.Add(splitFaceIndices[i]);
			}
		}

		private void ConvertLeafBrushes()
		{
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
			SetLumpVersionNumber(Face.GetIndexForLump(sourceBsp.MapType), 1);

			foreach (var qFace in quakeBsp.Faces)
			{
				var data = new byte[Face.GetStructLength(sourceBsp.MapType)];
				var sFace = new Face(data, sourceBsp.Faces);

				// TODO: Re-use brush planes?
				sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
				sFace.PlaneSide = true;
				sFace.IsOnNode = false; // Should this be true for faces that split visleafs?

				(var surfEdgeIndex, var numEdges) = CreateSurfaceEdges(qFace.Vertices.ToArray(), qFace.Normal);
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

				sFace.TextureInfoIndex = CreateTextureInfo(qFace);
				sFace.DisplacementIndex = -1;
				sFace.SurfaceFogVolumeID = -1;
				sFace.LightmapStyles = new byte[4];
				sFace.Lightmap = 0;
				sFace.Area = 0; // TODO: Check if this needs to be computed
				sFace.LightmapStart = new Vector2();
				sFace.LightmapSize = new Vector2(); // TODO: Set to 128x128?
				sFace.OriginalFaceIndex = -1; // Ignore since Quake 3 maps don't have split faces

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
				
				sFace.SmoothingGroups = 0;

				sourceBsp.Faces.Add(sFace);
			}
		}

		private void ConvertFaces_SplitFaces()
		{
			SetLumpVersionNumber(Face.GetIndexForLump(sourceBsp.MapType), 1);

			for (var faceIndex = 0; faceIndex < quakeBsp.Faces.Count; faceIndex++)
			{
				var qFace = quakeBsp.Faces[faceIndex];

				// Create a face for each triangle
				var vertices = qFace.Vertices.ToArray();
				var indices = qFace.Indices.ToArray();

				splitFaceDict[faceIndex] = new int[indices.Length / 3];
				
				for (var i = 0; i < indices.Length; i += 3)
				{
					var data = new byte[Face.GetStructLength(sourceBsp.MapType)];
					var sFace = new Face(data, sourceBsp.Faces);

					sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
					sFace.PlaneSide = true;
					sFace.IsOnNode = false; // Set to false in order for face to be visible across multiple leaves?
					sFace.TextureInfoIndex = CreateTextureInfo(qFace);
					sFace.DisplacementIndex = -1;
					sFace.SurfaceFogVolumeID = -1;
					sFace.LightmapStyles = new byte[4];
					sFace.Lightmap = 0;
					sFace.Area = 0; // TODO: Check if this needs to be computed
					sFace.LightmapStart = new Vector2();
					sFace.LightmapSize = new Vector2(); // TODO: Set to 128x128?
					sFace.OriginalFaceIndex = -1; // Ignore since Quake 3 maps don't have split faces
					sFace.FirstPrimitive = 0;
					sFace.NumPrimitives = 0;
					sFace.SmoothingGroups = 0;

					sFace.FirstEdgeIndexIndex = sourceBsp.FaceEdges.Count;
					sFace.NumEdgeIndices = 3;

					var v1 = vertices[indices[i]];
					var v2 = vertices[indices[i + 1]];
					var v3 = vertices[indices[i + 2]];

					var e1 = CreateEdge(v2, v1);
					var e2 = CreateEdge(v3, v2);
					var e3 = CreateEdge(v1, v3);

					sourceBsp.FaceEdges.Add(e1);
					sourceBsp.FaceEdges.Add(e2);
					sourceBsp.FaceEdges.Add(e3);

					sourceBsp.Faces.Add(sFace);

					splitFaceDict[faceIndex][i / 3] = sourceBsp.Faces.Count - 1;
				}
			}
		}

		private int CreatePlane(Face face)
		{
			// TODO: Avoid adding duplicate planes
			var data = new byte[PlaneBSP.GetStructLength(sourceBsp.MapType)];
			var plane = new PlaneBSP(data, sourceBsp.Planes);

			plane.Normal = face.Normal;
			plane.Distance = Vector3.Dot(face.Vertices.First().position, face.Normal);
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

		private (int surfEdgeIndex, int numEdges) CreateSurfaceEdges(Vertex[] vertices, Vector3 faceNormal)
		{
			var surfEdgeIndex = sourceBsp.FaceEdges.Count;
			
			// Convert triangle meshes from Q3 to Source engine's edge loop format
			// Note: Some Q3 faces are concave polygons, so this approach does not always work
			var hullVerts = HullConverter.ConvertConvexHull(vertices, faceNormal);
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
				var edgeIndex = CreateEdge(hullVerts[nextIndex], hullVerts[i]);

				sourceBsp.FaceEdges.Add(edgeIndex);
			}

			return (surfEdgeIndex, numEdges);
		}

		private int CreateEdge(Vertex firstVertex, Vertex secondVertex)
		{
			// TODO: Prevent adding duplicate edges?
			var data = new byte[Edge.GetStructLength(sourceBsp.MapType)];
			var edge = new Edge(data, sourceBsp.Edges);
			edge.FirstVertexIndex = CreateVertex(firstVertex);
			edge.SecondVertexIndex = CreateVertex(secondVertex);

			sourceBsp.Edges.Add(edge);

			return sourceBsp.Edges.Count - 1;
		}

		private int CreateVertex(Vertex vertex)
		{
			// TODO: Prevent adding duplicate vertices
			sourceBsp.Vertices.Add(vertex);
			return sourceBsp.Vertices.Count - 1;
		}

		private int CreateTextureInfo(Face face)
		{
			var data = new byte[TextureInfo.GetStructLength(sourceBsp.MapType)];
			var textureInfo = new TextureInfo(data, sourceBsp.TextureInfo);
			// TODO: Get UV data from face vertices
			(var uAxis, var vAxis) = GetTextureVectors(face.Normal);
			textureInfo.UAxis = uAxis;
			textureInfo.VAxis = vAxis;
			textureInfo.LightmapUAxis = uAxis / 4f; // TODO: Use lightmap scale
			textureInfo.LightmapVAxis = vAxis / 4f;
			textureInfo.TextureIndex = LookupTextureDataIndex(face.Texture.Name);
			
			if (IsSkyTexture(face.Texture.Name))
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
				
				if (!textureInfoLookup.ContainsKey(face.Texture.Name))
					textureInfoLookup.Add(face.Texture.Name, textureInfoIndex);

				return textureInfoIndex;
			}
		}

		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectors(Vector3 faceNormal)
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

			// TODO: Convert lightmap data
			
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
			var mapsDir = Path.Combine(outputDir, "maps");
			if (!Directory.Exists(mapsDir))
				Directory.CreateDirectory(mapsDir);

			var writer = new BSPWriter(sourceBsp);
			var bspPath = Path.Combine(mapsDir, quakeBsp.MapName + ".bsp");
			writer.WriteBSP(bspPath);
			
			logger.Log($"Converted BSP: {bspPath}");
		}

		// Delete temp pk3 directory if it exists
		private void DeletePk3Directory()
		{
			if (!string.IsNullOrEmpty(pk3Dir))
				Directory.Delete(pk3Dir, true);
		}
	}
}