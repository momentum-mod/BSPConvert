using LibBSP;
using System;
using System.Collections.Generic;
using System.Linq;

using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using Color = System.Drawing.Color;

namespace BSPConvert.Lib.GoldSrc
{
	// Converts GoldSrc (Half-Life / Counter-Strike 1.6) BSPs (see IEngineConverter).
	//
	// Collision: GoldSrc doesn't expand the player box at runtime. The compiler bakes a clip hull per player size
	// (hull 1 standing, hull 3 ducking) and the engine traces a point through it. How the compiler expanded sloped
	// faces depends on the map's cliptype (legacy maps expand them less than the true box size), so collision
	// rebuilt from the visible geometry doesn't match on many maps, and clip brushes only exist in the clip hulls.
	// So each clip hull leaf becomes a brush whose planes are pulled in by the player box: Source's box trace
	// pushes every brush plane back out by the box (CM_ClipBoxToBrush), which lands it exactly on the clip hull
	// plane. Standing and ducking brushes carry separate contents bits, because a hull's brushes only line up for
	// the box size they were shrunk for; GoldSrc-hull game modes pick the bit by the size of the box they trace.
	// Brushes built from hull 0 (the visible geometry) keep the normal contents for every other trace.
	public class GoldSrcConverter : IEngineConverter
	{
		// Half extents of the GoldSrc player hulls (Momentum's GoldSrc-hull game modes use the same sizes)
		private static readonly Vector3 StandingHullExtents = new Vector3(16f, 16f, 36f);
		private static readonly Vector3 DuckingHullExtents = new Vector3(16f, 16f, 18f);

		private const int StandingHull = 1;
		private const int DuckingHull = 3;

		// Padding around a model's bounds when closing off the outermost BSP regions
		private const float BoundsPadding = 1f;
		// Distance from a node plane within which a region corner counts as on the plane
		private const float RegistrationEpsilon = 0.01f;

		// Worldspawn key marking a map whose brushes carry GoldSrc clip hulls
		public const string ClipHullsWorldspawnKey = "goldsrc_clip_hulls";

		private const int LightmapLuxelSize = 16;

		private readonly BSPConverterOptions options;
		private readonly ILogger logger;

		private GoldSrcBsp gs;
		private SourceBspBuilder builder;
		private BSP sourceBsp;

		private int[] texInfoMap;
		private int[] sourceLeafForGoldSrcLeaf;
		// The converted faces with lightmaps, with where theirs is in the GoldSrc lighting data and how big it is, for
		// ConvertLighting to place it in Source's
		private readonly List<(Face face, int lightOffset, int luxels, int styleCount)> litFaces = new List<(Face, int, int, int)>();
		private Dictionary<int, List<int>> leafBrushes = new Dictionary<int, List<int>>();
		private List<int>[] modelBrushes;
		// Brush models that are volumes of contents (func_water) mapped to their GoldSrc contents, and the regions of
		// their hull 0 brushes (see ConvertVolumeEntity)
		private Dictionary<int, int> volumeModelContents = new Dictionary<int, int>();
		private Dictionary<int, ConvexRegion> volumeBrushRegions = new Dictionary<int, ConvexRegion>();

		public GoldSrcConverter(BSPConverterOptions options, ILogger logger)
		{
			this.options = options;
			this.logger = logger;
		}

		public void Convert(BSP inputBsp, SourceBspBuilder output)
		{
			gs = GoldSrcBsp.Read(inputBsp);
			builder = output;
			sourceBsp = output.Bsp;
			leafBrushes.Clear();
			litFaces.Clear();
			volumeModelContents.Clear();
			volumeBrushRegions.Clear();
			modelBrushes = new List<int>[gs.Models.Length];
			for (var i = 0; i < modelBrushes.Length; i++)
				modelBrushes[i] = new List<int>();

			if (options.scale != 1f)
				logger.Log("Warning: --scale is not supported for GoldSrc maps and will be ignored.");

			SetLumpVersions();

			ConvertEntities();
			ConvertPlanes();
			ConvertTexInfos();
			ConvertVertices();
			ConvertFaces();
			ConvertLighting();
			ConvertNodes();
			ConvertHull0();
			AddVolumeBrushesToWorld();
			ConvertClipHull(StandingHull, StandingHullExtents, SourceContentsFlags.CONTENTS_GOLDSRC_HULL_STANDING);
			ConvertClipHull(DuckingHull, DuckingHullExtents, SourceContentsFlags.CONTENTS_GOLDSRC_HULL_DUCKING);
			WriteLeafBrushes();
			ConvertModels();
			ConvertVisibility();

			builder.AddPlaceholderArea();
			builder.AddPlaceholderAreaPortal();
			builder.AddPlaceholderWorldLight();
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

		// TODO: Convert GoldSrc entity logic to Source/Momentum (doors' movedir, multi_manager, timers, ...).
		// For now entities are copied as is, which keeps brush entity collision and teleports working.
		private void ConvertEntities()
		{
			foreach (var gsEntity in gs.Entities)
			{
				var entity = new Entity();
				foreach (var key in gsEntity.Keys)
					entity[key] = gsEntity[key];

				switch (entity.ClassName)
				{
					case "worldspawn":
						entity.Remove("wad");
						entity.Remove("_wad");
						entity[ClipHullsWorldspawnKey] = "1";
						break;
					case "trigger_multiple":
					case "trigger_once":
					case "trigger_teleport":
					case "trigger_push":
					case "trigger_hurt":
						// GoldSrc triggers fire for clients unless "No Clients" (2) is set; Source triggers only fire
						// for clients with "Clients" (1) set.
						var gsFlags = int.TryParse(entity["spawnflags"], out var flags) ? flags : 0;
						entity["spawnflags"] = (gsFlags & 2) != 0 ? "0" : "1";
						break;
					case "func_water":
						ConvertVolumeEntity(entity);
						break;
				}

				sourceBsp.Entities.Add(entity);
			}
		}

		// GoldSrc water is a brush entity whose "skin" holds its contents. Source's func_water needs the model's vphysics
		// collision data to create its fluid controller (and crashes without it), which converted maps don't have.
		// Instead the model's brushes are added to the world as static water (see AddVolumeBrushesToWorld), and the
		// entity is kept as a func_illusionary so it still renders.
		// TODO: Moving water (func_water used as a door) stays where it starts
		private void ConvertVolumeEntity(Entity entity)
		{
			var model = entity["model"];
			if (!model.StartsWith('*') || !int.TryParse(model.Substring(1), out var modelIndex) || modelIndex <= 0 || modelIndex >= gs.Models.Length)
				return;

			var contents = int.TryParse(entity["skin"], out var skin) && skin < 0 ? skin : GoldSrcBsp.CONTENTS_WATER;
			volumeModelContents[modelIndex] = contents;

			if (!string.IsNullOrEmpty(entity["targetname"]))
				logger.Log($"Warning: {entity.ClassName} {model} ({entity["targetname"]}) is converted as static water and won't move.");

			entity.ClassName = "func_illusionary";
		}

		// Copied index for index: nodes and clip nodes reference these planes
		private void ConvertPlanes()
		{
			foreach (var plane in gs.Planes)
				builder.AppendPlane(plane.normal, plane.dist);
		}

		private void ConvertTexInfos()
		{
			// TODO: Convert the textures themselves (embedded and WAD miptex) to VTFs
			var texDataForMipTex = new int[gs.MipTextures.Length];
			for (var i = 0; i < gs.MipTextures.Length; i++)
			{
				var mipTex = gs.MipTextures[i];
				texDataForMipTex[i] = builder.AddTextureData(GetMaterialName(mipTex.name), mipTex.width, mipTex.height, GetReflectivity(i));
			}

			texInfoMap = new int[gs.TexInfos.Length];
			for (var i = 0; i < gs.TexInfos.Length; i++)
			{
				var texInfo = gs.TexInfos[i];
				var mipTexName = texInfo.mipTex >= 0 && texInfo.mipTex < gs.MipTextures.Length ? gs.MipTextures[texInfo.mipTex].name : "";
				var textureDataIndex = texInfo.mipTex >= 0 && texInfo.mipTex < texDataForMipTex.Length ? texDataForMipTex[texInfo.mipTex] : 0;

				var uAxis = new Vector3(texInfo.s.X, texInfo.s.Y, texInfo.s.Z);
				var vAxis = new Vector3(texInfo.t.X, texInfo.t.Y, texInfo.t.Z);

				// GoldSrc lightmaps have one luxel per 16 texels
				texInfoMap[i] = builder.AddTextureInfo(
					uAxis, vAxis,
					uAxis / LightmapLuxelSize, vAxis / LightmapLuxelSize,
					GetSurfaceFlags(texInfo, mipTexName),
					textureDataIndex,
					GetMaterialName(mipTexName),
					new Vector2(texInfo.s.W, texInfo.t.W),
					new Vector2(texInfo.s.W / LightmapLuxelSize, texInfo.t.W / LightmapLuxelSize));
			}
		}

		// Average linear color of an embedded texture, the way vtex computes a VTF's reflectivity. Textures that live
		// in a WAD get a neutral gray until WAD textures are converted.
		private Color GetReflectivity(int mipTexIndex)
		{
			const float DefaultReflectivity = 0.5f;
			if (!gs.TryGetMipTexPixels(mipTexIndex, out var pixels, out var palette))
				return ColorFromLinear(DefaultReflectivity, DefaultReflectivity, DefaultReflectivity);

			// '{' textures are alpha tested: palette index 255 is transparent
			var isAlphaTested = gs.MipTextures[mipTexIndex].name.StartsWith('{');

			double r = 0, g = 0, b = 0;
			var count = 0;
			foreach (var index in pixels)
			{
				if (isAlphaTested && index == 255)
					continue;

				r += GammaToLinear(palette[index * 3]);
				g += GammaToLinear(palette[index * 3 + 1]);
				b += GammaToLinear(palette[index * 3 + 2]);
				count++;
			}

			if (count == 0)
				return ColorFromLinear(DefaultReflectivity, DefaultReflectivity, DefaultReflectivity);

			return ColorFromLinear((float)(r / count), (float)(g / count), (float)(b / count));
		}

		private static double GammaToLinear(byte value)
		{
			return Math.Pow(value / 255.0, 2.2);
		}

		private static Color ColorFromLinear(float r, float g, float b)
		{
			return ColorExtensions.FromArgb(255, (int)MathF.Round(r * 255f), (int)MathF.Round(g * 255f), (int)MathF.Round(b * 255f));
		}

		private static string GetMaterialName(string mipTexName)
		{
			return "goldsrc/" + mipTexName.ToLowerInvariant();
		}

		private static int GetSurfaceFlags(GoldSrcBsp.TexInfo texInfo, string mipTexName)
		{
			var flags = 0;
			if ((texInfo.flags & GoldSrcBsp.TEX_SPECIAL) != 0)
				flags |= (int)SourceSurfaceFlags.SURF_NOLIGHT;

			var name = mipTexName.ToLowerInvariant();
			if (name == "sky")
				flags |= (int)(SourceSurfaceFlags.SURF_SKY | SourceSurfaceFlags.SURF_NOLIGHT | SourceSurfaceFlags.SURF_SKYNOEMIT);
			else if (name is "aaatrigger" or "null" or "clip" or "origin" or "bevel" or "hint" or "skip")
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
			for (var i = 0; i < gs.Faces.Length; i++)
			{
				var gsFace = gs.Faces[i];
				var texInfo = gs.TexInfos[gsFace.texInfo];
				var face = builder.AddFace();

				face.PlaneIndex = gsFace.planeIndex;
				face.PlaneSide = gsFace.planeSide;
				// GoldSrc faces all lie on nodes (marksurfaces reference them per leaf for visibility only)
				face.IsOnNode = true;
				face.FirstEdgeIndexIndex = gsFace.firstEdge;
				face.NumEdgeIndices = gsFace.numEdges;
				face.TextureInfoIndex = texInfoMap[gsFace.texInfo];
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
				sourceBsp.Normals.Add(gsFace.planeSide ? -normal : normal);
				for (var j = 0; j < gsFace.numEdges; j++)
					sourceBsp.Indices.Add(i);
			}
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
				node.FirstFaceIndex = gsNode.firstFace;
				node.NumFaceIndices = gsNode.numFaces;
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

			for (var i = 0; i < gs.MarkSurfaces.Length; i++)
				sourceBsp.LeafFaces.Add(gs.MarkSurfaces[i]);

			var hull0Brushes = 0;
			for (var modelIndex = 0; modelIndex < gs.Models.Length; modelIndex++)
			{
				var model = gs.Models[modelIndex];
				var headNode = model.headNodes[0];
				if (headNode < 0)
					continue;

				var path = GetBoundsHalfSpaces(model.mins, model.maxs, BoundsPadding);
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
			leaf.FirstMarkFaceIndex = gsLeaf.firstMarkSurface;
			leaf.NumMarkFaceIndices = gsLeaf.contents == GoldSrcBsp.CONTENTS_SOLID ? 0 : gsLeaf.numMarkSurfaces;
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
			var contents = GetSourceContents(isVolume ? volumeContents : gs.Leaves[gsLeafIndex].contents);
			var brushIndex = builder.AddBrush(firstSide, numSides, contents);
			if (isVolume)
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

		// World point contents only see brushes listed in world leaves, so list the volume entities' brushes in the
		// world leaves they overlap
		private void AddVolumeBrushesToWorld()
		{
			var worldHeadNode = gs.Models[0].headNodes[0];
			foreach (var (brushIndex, region) in volumeBrushRegions)
				RegisterInHull0Leaves(worldHeadNode, region, brushIndex);

			if (volumeBrushRegions.Count > 0)
				logger.Log($"Converted {volumeModelContents.Count} water entities into {volumeBrushRegions.Count} static world water brushes");
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
				var model = gs.Models[modelIndex];
				var headNode = model.headNodes[hull];

				// Clip hull planes sit up to the hull's extents outside the model's visible bounds
				var path = GetBoundsHalfSpaces(model.mins - hullExtents, model.maxs + hullExtents, BoundsPadding);
				brushCount += WalkClipHull(headNode, path, modelIndex, hullExtents, contents);
			}

			logger.Log($"Converted {brushCount} hull {hull} brushes");
		}

		private int WalkClipHull(int clipNodeIndex, List<HalfSpace> path, int modelIndex, Vector3 hullExtents, SourceContentsFlags contents)
		{
			if (clipNodeIndex < 0)
			{
				// Clip hulls only distinguish solid from non-solid (water etc. is empty to the player hulls)
				if (clipNodeIndex != GoldSrcBsp.CONTENTS_SOLID)
					return 0;

				return AddClipHullBrush(path, modelIndex, hullExtents, contents) ? 1 : 0;
			}

			var clipNode = gs.ClipNodes[clipNodeIndex];
			var plane = gs.Planes[clipNode.planeIndex];
			var brushCount = 0;

			path.Add(new HalfSpace(-plane.normal, -plane.dist));
			brushCount += WalkClipHull(clipNode.child0, path, modelIndex, hullExtents, contents);
			path[path.Count - 1] = new HalfSpace(plane.normal, plane.dist);
			brushCount += WalkClipHull(clipNode.child1, path, modelIndex, hullExtents, contents);
			path.RemoveAt(path.Count - 1);

			return brushCount;
		}

		private bool AddClipHullBrush(List<HalfSpace> path, int modelIndex, Vector3 hullExtents, SourceContentsFlags contents)
		{
			var region = ConvexRegion.Create(path);
			if (region == null)
				return false;

			// Pull each plane in by the box's extent along its normal, which Source's box trace adds back
			var firstSide = sourceBsp.BrushSides.Count;
			foreach (var face in region.Faces)
			{
				var n = face.Normal;
				var offset = MathF.Abs(n.X) * hullExtents.X + MathF.Abs(n.Y) * hullExtents.Y + MathF.Abs(n.Z) * hullExtents.Z;
				builder.AddBrushSide(builder.AddPlane(n, face.Dist - offset), -1);
			}

			var brushIndex = builder.AddBrush(firstSide, region.Faces.Count, (int)contents);
			modelBrushes[modelIndex].Add(brushIndex);

			// A box trace only tests the brushes of leaves it passes through, and its center is somewhere inside
			// the clip hull region whenever it collides with this brush, so list the brush in every hull 0 leaf
			// the region overlaps.
			var headNode = gs.Models[modelIndex].headNodes[0];
			if (headNode >= 0)
				RegisterInHull0Leaves(headNode, region, brushIndex);

			return true;
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
				model.FirstFaceIndex = gsModel.firstFace;
				model.NumFaces = gsModel.numFaces;

				builder.SetModelBrushes(sourceBsp.Models.Count, modelBrushes[i]);
				sourceBsp.Models.Add(model);
			}
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

		// The model's bounds as half-spaces, closing off the regions at the edge of its BSP tree
		private static List<HalfSpace> GetBoundsHalfSpaces(Vector3 mins, Vector3 maxs, float padding)
		{
			return new List<HalfSpace>
			{
				new HalfSpace(new Vector3(1, 0, 0), maxs.X + padding),
				new HalfSpace(new Vector3(-1, 0, 0), -(mins.X - padding)),
				new HalfSpace(new Vector3(0, 1, 0), maxs.Y + padding),
				new HalfSpace(new Vector3(0, -1, 0), -(mins.Y - padding)),
				new HalfSpace(new Vector3(0, 0, 1), maxs.Z + padding),
				new HalfSpace(new Vector3(0, 0, -1), -(mins.Z - padding))
			};
		}
	}
}
