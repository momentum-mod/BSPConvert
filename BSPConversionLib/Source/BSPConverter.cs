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
		private BSP quakeBsp;
		private BSP sourceBsp;
		private string outputPath;

		private const int CONTENTS_SOLID = 1; // TODO: Add contents enum

		public BSPConverter(string quakeBspPath, string sourceBspPath, string outputPath)
		{
			quakeBsp = new BSP(new FileInfo(quakeBspPath));
			sourceBsp = new BSP(new FileInfo(sourceBspPath));

			// TODO: Add missing lumps before creating a BSP from scratch (LUMP_OCCLUSION)
			//sourceBsp = new BSP(Path.GetFileName(outputPath), MapType.Source20);

			this.outputPath = outputPath;
		}

		public void Convert()
		{
			ConvertEntities();
			ConvertTextures();
			ConvertPlanes();
			ConvertNodes();
			ConvertLeaves();
			ConvertLeafFaces();
			ConvertLeafBrushes();
			ConvertModels();
			ConvertBrushes();
			ConvertBrushSides();
			ConvertFaces();
			ConvertVisData();
			//ConvertMiscLumps();

			WriteBSP();
		}

		private void ConvertEntities()
		{
			sourceBsp.Entities.Clear();

			foreach (var entity in quakeBsp.Entities)
				sourceBsp.Entities.Add(entity);
		}

		private void ConvertTextures()
		{
			sourceBsp.TextureData.Clear();
			sourceBsp.TextureTable.Clear();
			sourceBsp.Textures.Clear();

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

			textureName = FormatTextureName(textureName);

			var data = System.Text.Encoding.ASCII.GetBytes(textureName);
			var texture = new Texture(data, sourceBsp.Textures);

			sourceBsp.Textures.Add(texture);

			return offset;
		}

		private void ConvertPlanes()
		{
			sourceBsp.Planes.Clear();

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

		private void ConvertNodes()
		{
			sourceBsp.Nodes.Clear();

			foreach (var qNode in quakeBsp.Nodes)
			{
				var data = new byte[Node.GetStructLength(sourceBsp.MapType)];
				var node = new Node(data, sourceBsp.Nodes);

				node.PlaneIndex = qNode.PlaneIndex;
				node.Child1Index = qNode.Child1Index;
				node.Child2Index = qNode.Child2Index;
				node.Minimums = qNode.Minimums;
				node.Maximums = qNode.Maximums;

				// Note: These are used to specify which faces are used to split the node (the face will have the "onNode" flag set to true)
				// Not sure if these values are necessary as long as all faces have "onNode" set to false
				node.FirstFaceIndex = 0;
				node.NumFaceIndices = 0;

				node.AreaIndex = 0; // TODO: Figure out how to compute areas

				sourceBsp.Nodes.Add(node);
			}
		}

		private void ConvertLeaves()
		{
			sourceBsp.Leaves.Clear();

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
				leaf.Area = qLeaf.Area + 1; // TODO: Is + 1 necessary?
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

		private void ConvertLeafFaces()
		{
			sourceBsp.LeafFaces.Clear();

			foreach (var qLeafFace in quakeBsp.LeafFaces)
				sourceBsp.LeafFaces.Add(qLeafFace);
		}

		private void ConvertLeafBrushes()
		{
			sourceBsp.LeafBrushes.Clear();

			foreach (var qLeafBrush in quakeBsp.LeafBrushes)
				sourceBsp.LeafBrushes.Add(qLeafBrush);
		}

		private void ConvertModels()
		{
			sourceBsp.Models.Clear();

			foreach (var qModel in quakeBsp.Models)
			{
				// Modify model 0 on sourceBsp until ready to remove sourceBsp's models? Index 0 handles all world geometry, anything after is for brush entities
				var data = new byte[Model.GetStructLength(sourceBsp.MapType)];
				var sModel = new Model(data, sourceBsp.Models);

				//var mins = qModel.Minimums;
				//var maxs = qModel.Maximums;
				//var minExtents = -16384;
				//var maxExtents = 16384;
				//sModel.Minimums = new Vector3(Math.Clamp(mins.X(), minExtents, maxExtents), Math.Clamp(mins.Y(), minExtents, maxExtents), Math.Clamp(mins.Z(), minExtents, maxExtents));
				//sModel.Maximums = new Vector3(Math.Clamp(maxs.X(), minExtents, maxExtents), Math.Clamp(maxs.Y(), minExtents, maxExtents), Math.Clamp(maxs.Z(), minExtents, maxExtents));
				sModel.Minimums = qModel.Minimums;
				sModel.Maximums = qModel.Maximums;
				sModel.HeadNodeIndex = 0; // TODO: Find head node from leaf brushes?
				sModel.Origin = new Vector3(0f, 0f, 0f); // Recalculate origin?
				sModel.FirstFaceIndex = qModel.FirstFaceIndex;
				sModel.NumFaces = qModel.NumFaces;

				sourceBsp.Models.Add(sModel);
			}
		}

		private void ConvertBrushes()
		{
			sourceBsp.Brushes.Clear();

			foreach (var qBrush in quakeBsp.Brushes)
			{
				var data = new byte[Brush.GetStructLength(sourceBsp.MapType)];
				var sBrush = new Brush(data, sourceBsp.Brushes);

				sBrush.FirstSideIndex = qBrush.FirstSideIndex;
				sBrush.NumSides = qBrush.NumSides;
				sBrush.Contents = CONTENTS_SOLID; // TODO: Figure out how to get content flags from quake brush? This is important for water brushes

				sourceBsp.Brushes.Add(sBrush);
			}
		}

		private void ConvertBrushSides()
		{
			sourceBsp.BrushSides.Clear();

			foreach (var qBrushSide in quakeBsp.BrushSides)
			{
				var data = new byte[BrushSide.GetStructLength(sourceBsp.MapType)];
				var sBrushSide = new BrushSide(data, sourceBsp.BrushSides);

				sBrushSide.PlaneIndex = qBrushSide.PlaneIndex;
				sBrushSide.TextureIndex = 0; // TODO: Get texture info index from matching brush side
				sBrushSide.DisplacementIndex = 0;
				sBrushSide.IsBevel = false;

				sourceBsp.BrushSides.Add(sBrushSide);
			}
		}

		private void ConvertFaces()
		{
			sourceBsp.Faces.Clear();
			sourceBsp.FaceEdges.Clear();
			sourceBsp.Edges.Clear();
			sourceBsp.Vertices.Clear();
			sourceBsp.TextureInfo.Clear();
			sourceBsp.OriginalFaces.Clear();

			SetLumpVersionNumber(Face.GetIndexForLump(sourceBsp.MapType), 1);

			foreach (var qFace in quakeBsp.Faces)
			{
				var data = new byte[Face.GetStructLength(sourceBsp.MapType)];
				var sFace = new Face(data, sourceBsp.Faces);

				// TODO: Re-use brush planes?
				sFace.PlaneIndex = CreatePlane(qFace); // Quake faces don't have planes, so create one
				sFace.PlaneSide = true;
				sFace.IsOnNode = false; // Set to false in order for face to be visible across multiple leaves?

				(var surfEdgeIndex, var numEdges) = CreateSurfaceEdges(qFace.Vertices.ToArray(), qFace.Indices.ToArray());
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

		private (int surfEdgeIndex, int numEdges) CreateSurfaceEdges(Vertex[] vertices, int[] indices)
		{
			var surfEdgeIndex = sourceBsp.FaceEdges.Count;
			var numEdges = indices.Length;

			//var vertList = vertices.ToList();
			//var bounds = new UnityEngine.Bounds();
			//foreach (var vert in vertList)
			//	bounds.Encapsulate(vert.position);

			////bounds.max -= Vector3.one * 0.01f;
			////bounds.min += Vector3.one * 0.01f;

			//foreach (var vert in vertList)
			//{
			//	if (bounds.Contains(vert.position))
			//	{
			//		vertList.Remove(vert);
			//		UnityEngine.Debug.Log("removed vert");
			//		break;
			//	}
			//	//if (vert.position.x > bounds.min.x && vert.position.x < bounds.max.x &&
			//	//	vert.position.y > bounds.min.y && vert.position.y < bounds.max.y)
			//	//	//vert.position.z > bounds.min.z && vert.position.z < bounds.max.z)
			//	//{
			//	//	vertList.Remove(vert);
			//	//	break;
			//	//}
			//}

			//vertices = vertList.ToArray();
			//var numEdges = vertices.Length;

			// Note: Edges are continuous, so treating them as triangles will cause issues
			for (var i = 0; i < indices.Length; i += 3)
			{
				var v1 = vertices[indices[i]];
				var v2 = vertices[indices[i + 1]];
				var v3 = vertices[indices[i + 2]];

				var e1 = CreateEdge(v2, v1);
				var e2 = CreateEdge(v3, v2);
				var e3 = CreateEdge(v1, v3);

				sourceBsp.FaceEdges.Add(e1);
				sourceBsp.FaceEdges.Add(e2);
				sourceBsp.FaceEdges.Add(e3);
			}

			//for (var i = 0; i < vertices.Length; i++)
			//{
			//	var nextIndex = (i + 1) % vertices.Length;
			//	var edgeIndex = CreateEdge(vertices[nextIndex], vertices[i]);

			//	sourceBsp.FaceEdges.Add(edgeIndex);
			//}

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
			textureInfo.Flags = 0;
			textureInfo.TextureIndex = FindTextureDataIndex(face.Texture.Name);

			sourceBsp.TextureInfo.Add(textureInfo);

			return sourceBsp.TextureInfo.Count - 1;
		}

		private (Vector3 uAxis, Vector3 vAxis) GetTextureVectors(Vector3 faceNormal)
		{
			var axis = GetVectorAxis(faceNormal);
			switch (axis)
			{
				case PlaneBSP.AxisType.PlaneX:
				case PlaneBSP.AxisType.PlaneAnyX:
					return (new Vector3(0f, 4f, 0f), new Vector3(0f, 0f, -4f));
				case PlaneBSP.AxisType.PlaneY:
				case PlaneBSP.AxisType.PlaneAnyY:
					return (new Vector3(4f, 0f, 0f), new Vector3(0f, 0f, -4f));
				case PlaneBSP.AxisType.PlaneZ:
				case PlaneBSP.AxisType.PlaneAnyZ:
					return (new Vector3(4f, 0f, 0f), new Vector3(0f, -4f, 0f));
				default:
					return (new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f));
			}
		}

		private int FindTextureDataIndex(string textureName)
		{
			textureName = FormatTextureName(textureName);

			for (var i = 0; i < sourceBsp.TextureData.Count; i++)
			{
				var stringTableTexture = GetTextureNameFromStringTable(sourceBsp.TextureData[i]);
				if (stringTableTexture.ToLower() == textureName.ToLower())
					return i;
			}

			return -1;
		}

		/// <summary>
		/// Removed unnecessary prefixes from texture name
		/// </summary>
		private string FormatTextureName(string textureName)
		{
			// TEMP? For some reason maps compiled from J.A.C.K. adds textures/ prefix. Not sure if this is typical for all Quake 3 maps
			return textureName.Replace("textures/", "");
		}

		private string GetTextureNameFromStringTable(TextureData textureData)
		{
			var nameStringTableId = textureData.TextureStringOffsetIndex;
			var textureDataStringTableOffset = (int)sourceBsp.TextureTable[nameStringTableId];
			return sourceBsp.Textures.GetTextureAtOffset((uint)textureDataStringTableOffset);
		}

		private void ConvertVisData()
		{
			sourceBsp.Visibility.Data = new byte[0];
		}

		private void ConvertMiscLumps()
		{
			var cubemaps = sourceBsp.Cubemaps;
			var dispTris = sourceBsp.DisplacementTriangles;
			var dispVerts = sourceBsp.DisplacementVertices;
			var dispInfo = sourceBsp.Displacements;
			var gameLump = sourceBsp.GameLump;
			var lightmaps = sourceBsp.Lightmaps;
			var primitives = sourceBsp.Primitives;
			var primVerts = sourceBsp.PrimitiveVertices;
			var primIndices = sourceBsp.PrimitiveIndices;

			SetLumpVersionNumber(Lightmaps.GetIndexForLump(sourceBsp.MapType), 1);
			SetLumpVersionNumber(9, 2); // Occlusion data
		}

		private void SetLumpVersionNumber(int lumpIndex, int lumpVersion)
		{
			var lumpInfo = sourceBsp[lumpIndex];
			lumpInfo.version = lumpVersion;
			sourceBsp[lumpIndex] = lumpInfo;
		}

		private void WriteBSP()
		{
			var writer = new BSPWriter(sourceBsp);
			writer.WriteBSP(outputPath);

#if UNITY
			UnityEngine.Debug.Log($"Converted BSP: {outputPath}");
#endif
		}
	}
}