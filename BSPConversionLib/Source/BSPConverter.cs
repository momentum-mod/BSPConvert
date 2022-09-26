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

		public BSPConverter(string quakeBspPath, string sourceBspPath, string outputPath)
		{
			quakeBsp = new BSP(new FileInfo(quakeBspPath));
			sourceBsp = new BSP(new FileInfo(sourceBspPath));

			this.outputPath = outputPath;
		}

		public void Convert()
		{
			//ConvertTextures();
			ConvertFaces();
			WriteBSP();
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
		}

		private int CreateTextureDataStringTableEntry(string textureName)
		{
			sourceBsp.TextureTable.Add(CreateTextureDataStringData(textureName));

			return sourceBsp.TextureTable.Count - 1;
		}

		private int CreateTextureDataStringData(string textureName)
		{
			var texture = new Texture();
			texture.Name = textureName;

			sourceBsp.Textures.Add(texture);

			return sourceBsp.Textures.Count - 1;
		}

		private void ConvertFaces()
		{
			foreach (var qFace in quakeBsp.Faces)
			{
				var data = new byte[Face.GetStructLength(sourceBsp.MapType)];
				var sFace = new Face(data, sourceBsp.Faces);

				sFace.PlaneIndex = CreatePlane(qFace);

				// TODO: Figure out how to compute these values
				sFace.PlaneSide = true;
				sFace.IsOnNode = false; // Set to false in order for face to be visible across multiple leaves?

				(var surfEdgeIndex, var numEdges) = CreateSurfaceEdges(qFace.Vertices.ToArray());
				sFace.FirstEdgeIndexIndex = surfEdgeIndex;
				sFace.NumEdgeIndices = numEdges;

				sFace.TextureInfoIndex = CreateTextureInfo(qFace);
				sFace.DisplacementIndex = -1;
				sFace.SurfaceFogVolumeID = -1;
				sFace.LightmapStyles = new byte[4];
				sFace.Lightmap = 0;
				sFace.Area = 4096; // TODO: Check if this needs to be computed
				sFace.LightmapStart = new Vector2();
				sFace.LightmapSize = new Vector2(); // TODO: Set to 128x128?
				sFace.OriginalFaceIndex = -1; // Split faces reference the original face index
				sFace.NumPrimitives = 0;
				sFace.FirstPrimitive = 0;
				sFace.SmoothingGroups = 0;

				sourceBsp.Faces.Add(sFace);

				TempAddLeafFace(sourceBsp.Faces.Count - 1);
			}

			TempFixVisLeafs(sourceBsp.Faces.Count);
			TempFixModel(sourceBsp.Faces.Count);
		}

		private void TempAddLeafFace(int faceIndex)
		{
			sourceBsp.LeafFaces.Add(faceIndex);
		}

		private void TempFixVisLeafs(int numFaces)
		{
			for (var i = 0; i < sourceBsp.Leaves.Count; i++)
			{
				var leaf = sourceBsp.Leaves[i];
				if (leaf.FirstMarkFaceIndex == 12)
				{
					//leaf.FirstMarkFaceIndex = 0;
					leaf.NumMarkFaceIndices += 6; // Add cube faces
				}
			}
		}

		private void TempFixModel(int numFaces)
		{
			for (var i = 0; i < sourceBsp.Models.Count; i++)
			{
				var model = sourceBsp.Models[i];
				model.NumFaces = numFaces;
			}
		}

		private int CreatePlane(Face face)
		{
			// TODO: Prevent adding duplicate planes
			var distance = Vector3.Dot(face.Vertices.First().position, face.Normal);
			var plane = new Plane(face.Normal, distance);
			sourceBsp.Planes.Add(plane);

			return sourceBsp.Planes.Count - 1;
		}

		private (int surfEdgeIndex, int numEdges) CreateSurfaceEdges(Vertex[] vertices)
		{
			var surfEdgeIndex = -1;
			var numEdges = vertices.Length;

			for (var i = 0; i < vertices.Length; i++)
			{
				var nextIndex = (i + 1) % vertices.Length;
				var edgeIndex = CreateEdge(vertices[nextIndex], vertices[i]);

				sourceBsp.FaceEdges.Add(edgeIndex);

				if (i == 0)
					surfEdgeIndex = sourceBsp.FaceEdges.Count - 1;
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
			// Note: 0.5 tex scale = 2, 0.25 scale = 4
			textureInfo.UAxis = new Vector3(0, 2, 0); // Get UV data from face vertices?
			textureInfo.VAxis = new Vector3(0, 0, -2); // Get UV data from face vertices?
			textureInfo.LightmapUAxis = new Vector3();
			textureInfo.LightmapVAxis = new Vector3();
			textureInfo.Flags = 0;
			textureInfo.TextureIndex = FindTextureDataIndex(face.Texture.Name);

			sourceBsp.TextureInfo.Add(textureInfo);

			return sourceBsp.TextureInfo.Count - 1;
		}

		private int FindTextureDataIndex(string textureName)
		{
			// TEMP? For some reason maps compiled from J.A.C.K. adds textures/ prefix. Not sure if this is typical for all Quake 3 maps
			textureName = textureName.Replace("textures/", "");

			for (var i = 0; i < sourceBsp.TextureData.Count; i++)
			{
				var stringTableTexture = GetTextureNameFromStringTable(sourceBsp.TextureData[i]);
				if (stringTableTexture.ToLower() == textureName.ToLower())
					return i;
			}

			return -1;
		}

		private string GetTextureNameFromStringTable(TextureData textureData)
		{
			var nameStringTableId = textureData.TextureStringOffsetIndex;
			var textureDataStringTableOffset = (int)sourceBsp.TextureTable[nameStringTableId];
			return sourceBsp.Textures.GetTextureAtOffset((uint)textureDataStringTableOffset);
		}

		private void WriteBSP()
		{
			var writer = new BSPWriter(sourceBsp);
			writer.WriteBSP(outputPath);

#if UNITY
			UnityEngine.Debug.Log($"Wrote BSP to path: {outputPath}");
#endif
		}
	}
}