using LibBSP;
using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives.Zip;

using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using Color = System.Drawing.Color;

namespace BSPConvert.Lib
{
	// Builds the output Source BSP. Engine converters (see IEngineConverter) translate their input map into calls
	// on this builder, which owns the Source lumps and the deduplication tables for planes, texture data and
	// texture infos. Nothing in here should depend on the input engine's formats or rules.
	public class SourceBspBuilder
	{
		private readonly string contentDir;
		private readonly ILogger? logger;

		private readonly Dictionary<TextureInfoKey, int> textureInfoDict = new Dictionary<TextureInfoKey, int>();
		private readonly Dictionary<string, int> textureInfoLookup = new Dictionary<string, int>();
		private readonly Dictionary<string, int> textureDataLookup = new Dictionary<string, int>();
		private readonly Dictionary<(Vector3, float), int> planeDict = new Dictionary<(Vector3, float), int>();
		// Brushes belonging to each brush model. Source BSPs don't store this (a model only references a node tree),
		// so it's recorded here for consumers that need a model's brush geometry, like zone generation.
		private readonly Dictionary<int, IReadOnlyList<int>> modelBrushes = new Dictionary<int, IReadOnlyList<int>>();

		public BSP Bsp { get; }
		public bool OldBSP { get; }

		// contentDir is where converted materials live; texdata entries read their size/reflectivity from the
		// VTF at <contentDir>/<textureName>.vtf.
		public SourceBspBuilder(string mapName, bool oldBSP, string contentDir, ILogger? logger = null)
		{
			this.contentDir = contentDir;
			this.logger = logger;
			OldBSP = oldBSP;

			var mapType = oldBSP ? MapType.Source20 : MapType.Source25;
			Bsp = new BSP(mapName + ".bsp", mapType);
		}

		public void SetLumpVersion(int lumpIndex, int lumpVersion)
		{
			var lumpInfo = Bsp[lumpIndex];
			lumpInfo.version = lumpVersion;
			Bsp[lumpIndex] = lumpInfo;
		}

		public void CreatePakFile()
		{
			using (var archive = ZipArchive.Create())
			{
				Bsp.PakFile.SetZipArchive(archive, true);
			}
		}

		public void Write(string bspPath, bool compress)
		{
			var writer = new BSPWriter(Bsp);
			writer.WriteBSP(bspPath, compress);
		}

		#region Texture data

		// Creates a texdata entry for the material and registers it as the material's default texdata (see
		// LookupTextureData). Returns the new entry's index. Size and reflectivity come from the material's VTF, or
		// from vtfName's when several materials share one VTF (e.g. the frames of an animated texture).
		public int AddTextureData(string textureName, string? vtfName = null)
		{
			var index = AddTextureDataVariant(textureName, vtfName);

			if (!textureDataLookup.ContainsKey(textureName))
				textureDataLookup.Add(textureName, index);

			return index;
		}

		// Creates a texdata entry for the given material and returns its index without
		// registering it in textureDataLookup. Use this when a distinct texdata is needed
		// for an already-named material (e.g. a surface-flag variant), so the name->index
		// lookup keeps pointing at the original/default entry.
		public int AddTextureDataVariant(string textureName, string? vtfName = null)
		{
			var vtfPath = Path.Combine(contentDir, (vtfName ?? textureName) + ".vtf");
			var textureData = GetTextureData(vtfPath);
			textureData.TextureStringOffsetIndex = CreateTextureDataStringTableEntry(textureName);

			Bsp.TextureData.Add(textureData);

			return Bsp.TextureData.Count - 1;
		}

		// Creates a texdata entry with an explicit texture size, for materials whose VTF isn't in the content
		// directory (e.g. textures converted elsewhere or not converted at all). Registered like AddTextureData.
		// reflectivity is the texture's average linear color. Besides bounce lighting, Strata uses it to tint the
		// replacement for a missing material (mat_error_texture_advanced), so black renders missing materials black.
		public int AddTextureData(string textureName, int width, int height, Color reflectivity)
		{
			var textureData = CreateTextureData();
			textureData.Reflectivity = reflectivity;
			textureData.Size = new Vector2(width, height);
			textureData.ViewSize = new Vector2(width, height);
			textureData.TextureStringOffsetIndex = CreateTextureDataStringTableEntry(textureName);

			Bsp.TextureData.Add(textureData);
			var index = Bsp.TextureData.Count - 1;

			if (!textureDataLookup.ContainsKey(textureName))
				textureDataLookup.Add(textureName, index);

			return index;
		}

		public int LookupTextureData(string textureName)
		{
			if (textureDataLookup.TryGetValue(textureName, out var textureDataIndex))
				return textureDataIndex;

			return -1;
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
				logger?.Log($"Warning: Couldn't read {Path.GetFileName(vtfPath)}: {e.Message}");

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
			var data = new byte[TextureData.GetStructLength(Bsp.MapType)];
			return new TextureData(data, Bsp.TextureData);
		}

		private int CreateTextureDataStringTableEntry(string textureName)
		{
			Bsp.TextureTable.Add(CreateTextureDataStringData(textureName));

			return Bsp.TextureTable.Count - 1;
		}

		// Note: Returns texture data byte offset instead of index
		private int CreateTextureDataStringData(string textureName)
		{
			var offset = Bsp.Textures.Length;

			var data = System.Text.Encoding.ASCII.GetBytes(textureName);
			var texture = new Texture(data, Bsp.Textures);

			Bsp.Textures.Add(texture);

			return offset;
		}

		#endregion

		#region Texture info

		// Adds a texinfo, or returns the index of an identical one that was already added. lookupName registers a
		// newly added texinfo as that material's default (see LookupTextureInfo) if it doesn't have one yet.
		// translation/lightmapTranslation are the texel/luxel offsets added after projecting onto the axes.
		public int AddTextureInfo(Vector3 uAxis, Vector3 vAxis, Vector3 lightmapUAxis, Vector3 lightmapVAxis, int flags, int textureDataIndex, string? lookupName = null,
			Vector2 translation = default, Vector2 lightmapTranslation = default)
		{
			var data = new byte[TextureInfo.GetStructLength(Bsp.MapType)];
			var textureInfo = new TextureInfo(data, Bsp.TextureInfo);

			textureInfo.UAxis = uAxis;
			textureInfo.VAxis = vAxis;
			textureInfo.LightmapUAxis = lightmapUAxis;
			textureInfo.LightmapVAxis = lightmapVAxis;
			textureInfo.Translation = translation;
			textureInfo.LightmapTranslation = lightmapTranslation;
			textureInfo.Flags = flags;
			textureInfo.TextureIndex = textureDataIndex;

			// Avoid adding duplicate texture info
			var key = new TextureInfoKey(textureInfo);
			if (textureInfoDict.TryGetValue(key, out var textureInfoIndex))
				return textureInfoIndex;

			Bsp.TextureInfo.Add(textureInfo);

			textureInfoIndex = Bsp.TextureInfo.Count - 1;
			textureInfoDict.Add(key, textureInfoIndex);

			if (lookupName != null && !textureInfoLookup.ContainsKey(lookupName))
				textureInfoLookup.Add(lookupName, textureInfoIndex);

			return textureInfoIndex;
		}

		public int LookupTextureInfo(string textureName)
		{
			if (textureInfoLookup.TryGetValue(textureName, out var textureInfoIndex))
				return textureInfoIndex;

			return -1;
		}

		#endregion

		#region Planes

		// Adds a plane, or returns the index of an identical one that was already added
		public int AddPlane(Vector3 normal, float distance)
		{
			if (!normal.IsValid())
			{
				normal = new Vector3(0f, 0f, 1f);
				distance = 0f;
			}

			var key = (normal, distance);
			if (planeDict.TryGetValue(key, out var existingIndex))
				return existingIndex;

			var index = AppendPlane(normal, distance);
			planeDict[key] = index;

			return index;
		}

		// Adds a plane without deduplicating it, so an input plane lump can be copied over index for index
		public int AppendPlane(Vector3 normal, float distance)
		{
			var data = new byte[PlaneBSP.GetStructLength(Bsp.MapType)];
			var plane = new PlaneBSP(data, Bsp.Planes);

			if (!normal.IsValid())
			{
				// Degenerate plane (common for Q3 patch/fog faces); substitute a safe dummy
				normal = new Vector3(0f, 0f, 1f);
				distance = 0f;
			}

			plane.Normal = normal;
			plane.Distance = distance;
			plane.Type = (int)GetPlaneAxis(normal);

			Bsp.Planes.Add(plane);

			return Bsp.Planes.Count - 1;
		}

		public static PlaneBSP.AxisType GetPlaneAxis(Vector3 normal)
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

		#endregion

		#region Faces

		public Face AddFace()
		{
			var data = new byte[Face.GetStructLength(Bsp.MapType)];
			var face = new Face(data, Bsp.Faces);

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
			face.OriginalFaceIndex = -1; // Ignore since converted maps don't have split faces
			face.FirstPrimitive = 0;
			face.NumPrimitives = 0;
			face.SmoothingGroups = 0;

			Bsp.Faces.Add(face);

			return face;
		}

		// Adds an edge and appends it to the surface edge list, so consecutive calls build a face's edge loop
		public void AddEdge(Vertex firstVertex, Vertex secondVertex, int faceIndex)
		{
			// TODO: Prevent adding duplicate edges?
			var data = new byte[Edge.GetStructLength(Bsp.MapType)];
			var edge = new Edge(data, Bsp.Edges);
			edge.FirstVertexIndex = AddVertex(firstVertex, faceIndex);
			edge.SecondVertexIndex = AddVertex(secondVertex, faceIndex);

			Bsp.Edges.Add(edge);
			Bsp.FaceEdges.Add(Bsp.Edges.Count - 1);
		}

		private int AddVertex(Vertex vertex, int faceIndex)
		{
			Bsp.Indices.Add(faceIndex);

			// TODO: Prevent adding duplicate vertices
			Bsp.Vertices.Add(vertex);
			return Bsp.Vertices.Count - 1;
		}

		// Faces that draw from primitive meshes need the primitive texture info game lump
		public void AddPrimitiveTextureInfoGameLump()
		{
			var lumpInfo = new LumpInfo();
			lumpInfo.ident = (int)GameLumpType.pmti;
			lumpInfo.flags = 0; // Don't compress this game lump
			Bsp.GameLump.Add(GameLumpType.pmti, lumpInfo);
		}

		// Adds a triangle list primitive. lightmapCoords are the [0,1] coords within the face's lightmap block
		// that the engine feeds straight to the lightmap sampler.
		public int AddPrimitive(Vector3[] positions, Vector2[] texCoords, Vector2[] lightmapCoords, int[] indices)
		{
			var primitiveIndex = Bsp.Primitives.Count;

			var data = new byte[Primitive.GetStructLength(Bsp.MapType)];
			var primitive = new Primitive(data, Bsp.Primitives);
			primitive.Type = Primitive.PrimitiveType.PRIM_TRILIST;

			primitive.FirstVertex = AddPrimitiveVertices(positions, texCoords, lightmapCoords);
			primitive.VertexCount = positions.Length;

			primitive.FirstIndex = AddPrimitiveIndices(indices);
			primitive.IndexCount = indices.Length;

			Bsp.Primitives.Add(primitive);
			return primitiveIndex;
		}

		private int AddPrimitiveVertices(Vector3[] positions, Vector2[] texCoords, Vector2[] lightmapCoords)
		{
			var firstPrimVertex = Bsp.PrimitiveVertices.Count;

			for (var i = 0; i < positions.Length; i++)
			{
				Bsp.PrimitiveVertices.Add(positions[i]);

				var bytes = new byte[PrimitiveTextureInfo.GetStructLength(Bsp.MapType)];
				var texInfo = new PrimitiveTextureInfo(bytes, Bsp.PrimitiveTextureInfo);
				texInfo.TexCoord = texCoords[i];
				texInfo.LightmapCoord = lightmapCoords[i];

				Bsp.PrimitiveTextureInfo.Add(texInfo);
			}

			return firstPrimVertex;
		}

		private int AddPrimitiveIndices(int[] indices)
		{
			var firstPrimIndex = Bsp.PrimitiveIndices.Count;

			foreach (var index in indices)
				Bsp.PrimitiveIndices.Add(index);

			return firstPrimIndex;
		}

		#endregion

		#region Displacements

		public void AddDisplacementVertex(Vector3 offset)
		{
			var data = new byte[DisplacementVertex.GetStructLength(Bsp.MapType)];
			var dispVert = new DisplacementVertex(data, Bsp.DisplacementVertices);

			dispVert.Normal = VectorUtil.NormalizeDouble(offset, out var mag);
			dispVert.Magnitude = mag;

			Bsp.DisplacementVertices.Add(dispVert);
		}

		public int AddDisplacementTriangles(int power)
		{
			var firstTriangle = Bsp.DisplacementTriangles.Count;

			var numTriangles = (1 << (power)) * (1 << (power)) * 2;
			for (var i = 0; i < numTriangles; i++)
				Bsp.DisplacementTriangles.Add(6); // TODO: Set displacement flags?

			return firstTriangle;
		}

		// Adds the displacement for an already-created face. firstVertex/firstTriangle come from
		// AddDisplacementVertex/AddDisplacementTriangles.
		public void AddDisplacement(int faceIndex, Vector3 startPosition, int firstVertex, int firstTriangle, int power, int minTesselation)
		{
			var data = new byte[Displacement.GetStructLength(Bsp.MapType)];
			var displacement = new Displacement(data, Bsp.Displacements);

			displacement.StartPosition = startPosition;
			displacement.FirstVertexIndex = firstVertex;
			displacement.FirstTriangleIndex = firstTriangle;
			displacement.Power = power;
			displacement.MinimumTesselation = minTesselation;
			displacement.SmoothingAngle = 0f;
			displacement.Contents = 1;
			displacement.FaceIndex = faceIndex;
			displacement.LightmapAlphaStart = 0;
			displacement.LightmapSamplePositionStart = 0;

			var allowedVerts = new uint[10];
			for (var i = 0; i < allowedVerts.Length; i++)
				allowedVerts[i] = 4294967295;

			displacement.AllowedVertices = allowedVerts;

			Bsp.Displacements.Add(displacement);
		}

		#endregion

		#region Brushes and brush models

		// Records which brushes make up a brush model (see modelBrushes). Engine converters that add models
		// directly to Bsp.Models call this; models created through AddBrushModel are recorded automatically.
		public void SetModelBrushes(int modelIndex, IReadOnlyList<int> brushIndices)
		{
			modelBrushes[modelIndex] = brushIndices;
		}

		public IReadOnlyList<int> GetModelBrushes(int modelIndex)
		{
			if (modelBrushes.TryGetValue(modelIndex, out var brushIndices))
				return brushIndices;

			return Array.Empty<int>();
		}

		// Bevel sides only clip box traces: point traces skip them (see the engine's CM_ClipBoxToBrush)
		public int AddBrushSide(int planeIndex, int textureInfoIndex, bool isBevel = false)
		{
			var data = new byte[BrushSide.GetStructLength(Bsp.MapType)];
			var side = new BrushSide(data, Bsp.BrushSides);
			side.PlaneIndex = planeIndex;
			side.TextureIndex = textureInfoIndex;
			side.DisplacementIndex = 0;
			side.IsBevel = isBevel;
			Bsp.BrushSides.Add(side);

			return Bsp.BrushSides.Count - 1;
		}

		public int AddBrush(int firstSideIndex, int numSides, int contents)
		{
			var brushIndex = Bsp.Brushes.Count;
			var data = new byte[Brush.GetStructLength(Bsp.MapType)];
			var brush = new Brush(data, Bsp.Brushes);
			brush.FirstSideIndex = firstSideIndex;
			brush.NumSides = numSides;
			brush.Contents = contents;
			Bsp.Brushes.Add(brush);

			return brushIndex;
		}

		// Creates a solid AABB brush (6 axis-aligned planes) and returns its brush index. The caller wraps it
		// (or several of them) in a brush model. CONTENTS_SOLID is required for trigger touch traces to hit it.
		public int AddBoxBrush(Vector3 mins, Vector3 maxs)
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

			var brushSideStart = Bsp.BrushSides.Count;
			for (var i = 0; i < 6; i++)
				AddBrushSide(AddPlane(normals[i], distances[i]), 0);

			return AddBrush(brushSideStart, 6, (int)SourceContentsFlags.CONTENTS_SOLID);
		}

		// Creates an AABB brush entity (6 planes) with its own orphan leaf + head node, returns the new model index
		public int AddBoxTriggerModel(Vector3 mins, Vector3 maxs)
		{
			return AddBrushModel(AddBoxBrush(mins, maxs), mins, maxs);
		}

		// Builds one brush model from several AABB boxes (a single leaf referencing all the box brushes), so a
		// single trigger entity can cover several disjoint volumes. Returns the model index.
		public int AddBoxTriggerModel(List<(Vector3 mins, Vector3 maxs)> boxes)
		{
			var firstLeafBrush = Bsp.LeafBrushes.Count;
			var mins = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
			var maxs = new Vector3(float.MinValue, float.MinValue, float.MinValue);

			foreach (var (boxMins, boxMaxs) in boxes)
			{
				var brushIndex = AddBoxBrush(boxMins, boxMaxs);
				Bsp.LeafBrushes.Add(brushIndex);
				mins = Vector3.Min(mins, boxMins);
				maxs = Vector3.Max(maxs, boxMaxs);
			}

			return AddBrushModel(firstLeafBrush, boxes.Count, mins, maxs);
		}

		// Wraps a single brush in its own orphan leaf + degenerate head node + model, returning the new
		// model index (referenced by entities as "*index").
		public int AddBrushModel(int brushIndex, Vector3 mins, Vector3 maxs)
		{
			var leafBrushIndex = Bsp.LeafBrushes.Count;
			Bsp.LeafBrushes.Add(brushIndex);

			return AddBrushModel(leafBrushIndex, 1, mins, maxs);
		}

		// Wraps a contiguous range of already-added leaf brushes in a single orphan leaf + degenerate head
		// node + model. Lets one brush model (one trigger entity) span several disjoint boxes.
		public int AddBrushModel(int firstLeafBrush, int numLeafBrushes, Vector3 mins, Vector3 maxs)
		{
			var leafData = new byte[Leaf.GetStructLength(Bsp.MapType)];
			var leaf = new Leaf(leafData, Bsp.Leaves);
			leaf.Minimums = mins;
			leaf.Maximums = maxs;
			leaf.FirstMarkBrushIndex = firstLeafBrush;
			leaf.NumMarkBrushIndices = numLeafBrushes;
			leaf.FirstMarkFaceIndex = 0;
			leaf.NumMarkFaceIndices = 0;
			leaf.LeafWaterDataID = -1;
			leaf.Area = 0;
			leaf.Contents = 0;
			var leafIndex = Bsp.Leaves.Count;
			Bsp.Leaves.Add(leaf);

			var nodeData = new byte[Node.GetStructLength(Bsp.MapType)];
			var node = new Node(nodeData, Bsp.Nodes);
			node.Child1Index = -leafIndex - 1;
			node.Child2Index = -leafIndex - 1;
			Bsp.Nodes.Add(node);

			var modelData = new byte[Model.GetStructLength(Bsp.MapType)];
			var model = new Model(modelData, Bsp.Models);
			model.HeadNodeIndex = Bsp.Nodes.Count - 1;
			model.Minimums = mins;
			model.Maximums = maxs;
			model.Origin = new Vector3(0f, 0f, 0f);
			model.FirstFaceIndex = 0;
			model.NumFaces = 0;
			Bsp.Models.Add(model);

			var modelIndex = Bsp.Models.Count - 1;

			var brushIndices = new int[numLeafBrushes];
			for (var i = 0; i < numLeafBrushes; i++)
				brushIndices[i] = (int)Bsp.LeafBrushes[firstLeafBrush + i];

			SetModelBrushes(modelIndex, brushIndices);

			return modelIndex;
		}

		// Computes an AABB enclosing the convex brush defined by its outward-facing side planes, by intersecting
		// every triple of planes and keeping the corner points that lie inside (or on) all of them. Returns
		// false for degenerate brushes that produce no valid corner points.
		public bool TryComputeBrushBounds(int firstSide, int numSides, out Vector3 mins, out Vector3 maxs)
		{
			mins = new Vector3(0f, 0f, 0f);
			maxs = new Vector3(0f, 0f, 0f);

			if (numSides < 4)
				return false;

			var normals = new Vector3[numSides];
			var distances = new float[numSides];
			for (var i = 0; i < numSides; i++)
			{
				var plane = Bsp.Planes[Bsp.BrushSides[firstSide + i].PlaneIndex];
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

		#endregion

		#region Lightmaps

		public void SetLightmaps(List<ColorRGBExp32> lmColors)
		{
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

			Bsp.Lightmaps.Data = data;
		}

		// The minimum world-space lightmap offset over a face's edge loop, which the engine expects as the
		// face's LightmapStart
		public Vector2 GetLightmapStart(Face face)
		{
			var lightmapStart = new Vector2(float.MaxValue, float.MaxValue);
			var texInfo = face.TextureInfo;
			var lightmapUAxis = texInfo.LightmapUAxis;
			var lightmapVAxis = texInfo.LightmapVAxis;

			// Find the minimum values for world space uv offsets
			foreach (var edgeIndex in face.EdgeIndices)
			{
				var edge = Bsp.Edges[edgeIndex];
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

		#endregion

		#region Placeholder lumps

		// Create an area in order to have valid node/leaf area references
		public void AddPlaceholderArea()
		{
			var areaBytes = new byte[Area.GetStructLength(Bsp.MapType)];
			var area = new Area(areaBytes, Bsp.Areas);
			Bsp.Areas.Add(area);
		}

		// Create an area portal for the first area
		public void AddPlaceholderAreaPortal()
		{
			if (!OldBSP)
				SetLumpVersion(AreaPortal.GetIndexForLump(Bsp.MapType), 1);

			var areaPortalBytes = new byte[AreaPortal.GetStructLength(Bsp.MapType)];
			var areaPortal = new AreaPortal(areaPortalBytes, Bsp.AreaPortals);
			Bsp.AreaPortals.Add(areaPortal);
		}

		// Add worldlight to disable fullbright
		public void AddPlaceholderWorldLight()
		{
			SetLumpVersion(WorldLight.GetIndexForLump(Bsp.MapType), 1);

			// TODO: How to check for fullbright maps?
			var worldLightBytes = new byte[WorldLight.GetStructLength(Bsp.MapType)];
			var worldLight = new WorldLight(worldLightBytes, Bsp.WorldLights);
			Bsp.WorldLights.Add(worldLight);
		}

		#endregion
	}
}
