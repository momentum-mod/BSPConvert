using LibBSP;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Vector3 = System.Numerics.Vector3;
using Color = System.Drawing.Color;

namespace BSPConvert.Lib.GoldSrc
{
	// Builds the leaves' ambient lighting, which Source lights models with, from the way GoldSrc lights studio models
	// (R_StudioDynamicLight; Xash3D's R_EntityDynamicLight reimplements it). GoldSrc takes one light sample at the
	// entity's origin: the sky's color and direction (light_environment) if a line toward the sun hits sky, or else the
	// lightmap of the surface below, with a direction tilted by how that lightmap changes around it. 90% of the sample
	// is directional and 10% ambient, and each vertex gets the ambient part plus the directional part scaled by a
	// wrapped Lambert term (R_StudioLighting). Each leaf gets ambient cubes holding what that lighting gives the six
	// axis directions, sampled at a few points in the leaf.
	public class GoldSrcModelLighting
	{
		private const int MaxSamplesPerAxis = 4;
		private const int MaxSamplesPerLeaf = 16;
		// Spacing of the samples in a leaf, before the limits above
		private const float SampleSpacing = 96f;

		private const int LightmapLuxelSize = 16;
		private const int MaxLightmaps = 4;
		// Lightstyle value of 'm', a light at its normal brightness ('a' is 0 and each letter adds 22)
		private const int NormalLightStyleValue = 264;
		private const int SF_LIGHT_START_OFF = 1;

		// v_direct: the share of the sample that's directional
		private const float DirectScale = 0.9f;
		// lambert: how far the directional light wraps around (1 is plain Lambert)
		private const float Lambert = 1.5f;
		private const float MaxAmbient = 128f;
		private const float MaxLight = 255f;

		// Where the light sample is taken from, above the origin, and how far it looks for the floor and the sky
		private const float SampleHeight = 8f;
		private const float FloorDistance = 2048f;
		private const float SkyDistance = 8192f;
		// Spacing of the samples the direction of light from the floor is taken from
		private const float GradientOffset = 16f;

		// Ambient cube sides, in the order Source stores them
		private static readonly Vector3[] BoxDirections =
		{
			Vector3.UnitX, -Vector3.UnitX,
			Vector3.UnitY, -Vector3.UnitY,
			Vector3.UnitZ, -Vector3.UnitZ
		};

		private readonly GoldSrcBsp gs;
		private readonly BSP sourceBsp;
		private readonly int[] sourceLeafForGoldSrcLeaf;
		private readonly int[] lightStyleValues = new int[256];
		private readonly int worldHeadNode;

		// light_environment's color (scaled as the game sets sv_skycolor) and the direction its light travels
		private Vector3 skyColor;
		private Vector3 skyDirection;

		public GoldSrcModelLighting(GoldSrcBsp gs, BSP sourceBsp, int[] sourceLeafForGoldSrcLeaf)
		{
			this.gs = gs;
			this.sourceBsp = sourceBsp;
			this.sourceLeafForGoldSrcLeaf = sourceLeafForGoldSrcLeaf;
			worldHeadNode = gs.Models.Length > 0 ? gs.Models[0].headNodes[0] : -1;
		}

		public void Convert()
		{
			if (worldHeadNode < 0 || gs.Lighting == null || gs.Lighting.Length == 0)
				return;

			SetUpLightStyles();
			SetUpSkyLight();

			LeafAmbientLightingWriter.SetLumpVersions(sourceBsp);
			var leafSamples = new List<List<LeafAmbientLighting>>(sourceBsp.Leaves.Count);
			for (var i = 0; i < sourceBsp.Leaves.Count; i++)
				leafSamples.Add(new List<LeafAmbientLighting>());

			for (var gsLeafIndex = 1; gsLeafIndex < sourceLeafForGoldSrcLeaf.Length; gsLeafIndex++)
			{
				var sourceLeafIndex = sourceLeafForGoldSrcLeaf[gsLeafIndex];
				if (sourceLeafIndex > 0 && sourceLeafIndex < leafSamples.Count)
					AddLeafSamples(gsLeafIndex, sourceBsp.Leaves[sourceLeafIndex], leafSamples[sourceLeafIndex]);
			}

			if (leafSamples.Any(samples => samples.Count > 0))
				LeafAmbientLightingWriter.Write(sourceBsp, leafSamples);
		}

		// Lightstyles 0-31 are the game's patterns, which are about normal brightness on average. Styles from 32 belong
		// to lights with a name, which are off if they start off, or play their pattern.
		private void SetUpLightStyles()
		{
			Array.Fill(lightStyleValues, NormalLightStyleValue);
			foreach (var entity in gs.Entities)
			{
				if (!entity.ClassName.StartsWith("light", StringComparison.Ordinal) || !int.TryParse(entity["style"], out var style) || style < 32 || style > 255)
					continue;

				var flags = int.TryParse(entity["spawnflags"], out var parsedFlags) ? parsedFlags : 0;
				var pattern = entity["pattern"];
				if ((flags & SF_LIGHT_START_OFF) != 0)
					lightStyleValues[style] = 0;
				else if (pattern.Length > 0)
					lightStyleValues[style] = (int)pattern.Average(c => Math.Clamp(char.ToLowerInvariant(c) - 'a', 0, 25) * 22);
			}
		}

		// The sky light the game sets from light_environment (CEnvLight)
		private void SetUpSkyLight()
		{
			var lightEnvironment = gs.Entities.FirstOrDefault(entity => entity.ClassName == "light_environment");
			if (lightEnvironment == null)
				return;

			var light = lightEnvironment["_light"].Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f)
				.ToArray();
			if (light.Length == 0)
				return;

			var color = light.Length >= 3 ? new Vector3(light[0], light[1], light[2]) : new Vector3(light[0]);
			if (light.Length >= 4)
				color *= light[3] / 255f;

			// Simulates the compiler's direct, ambient and gamma adjustments and the engine's scaling
			skyColor = new Vector3(SkyLevel(color.X), SkyLevel(color.Y), SkyLevel(color.Z));

			// Its pitch key (or the pitch of its angles) aims it, with negative pitches pointing down
			var angles = lightEnvironment["angles"].Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f)
				.ToArray();
			var pitch = angles.Length >= 1 ? angles[0] : 0f;
			var yaw = angles.Length >= 2 ? angles[1] : 0f;
			if (float.TryParse(lightEnvironment["pitch"], NumberStyles.Float, CultureInfo.InvariantCulture, out var pitchKey))
				pitch = pitchKey;
			else if (float.TryParse(lightEnvironment["angle"], NumberStyles.Float, CultureInfo.InvariantCulture, out var angle))
				yaw = angle;

			var p = pitch * MathF.PI / 180f;
			var y = yaw * MathF.PI / 180f;
			skyDirection = new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p));
		}

		private static float SkyLevel(float value)
		{
			return MathF.Floor(MathF.Pow(MathF.Max(value, 0f) / 114f, 0.6f) * 264f);
		}

		private void AddLeafSamples(int gsLeafIndex, Leaf sourceLeaf, List<LeafAmbientLighting> samples)
		{
			var mins = sourceLeaf.Minimums;
			var maxs = sourceLeaf.Maximums;
			if (mins.X > maxs.X || mins.Y > maxs.Y || mins.Z > maxs.Z)
				return;

			var size = maxs - mins;
			var counts = new int[3];
			for (var i = 0; i < 3; i++)
				counts[i] = Math.Clamp((int)MathF.Ceiling(size[i] / SampleSpacing), 1, MaxSamplesPerAxis);
			while (counts[0] * counts[1] * counts[2] > MaxSamplesPerLeaf)
				counts[Array.IndexOf(counts, counts.Max())]--;

			for (var xi = 0; xi < counts[0]; xi++)
			{
				for (var yi = 0; yi < counts[1]; yi++)
				{
					for (var zi = 0; zi < counts[2]; zi++)
					{
						var fraction = new Vector3((xi + 0.5f) / counts[0], (yi + 0.5f) / counts[1], (zi + 0.5f) / counts[2]);
						var position = mins + size * fraction;

						// Leaves aren't boxes, so positions in their bounds can be in another leaf
						if (FindLeaf(position) != gsLeafIndex)
							continue;

						var sample = LeafAmbientLightingWriter.CreateSample(sourceBsp, fraction.X, fraction.Y, fraction.Z);
						SetAmbientCube(sample, position);
						samples.Add(sample);
					}
				}
			}
		}

		private void SetAmbientCube(LeafAmbientLighting sample, Vector3 origin)
		{
			var (light, lightDirection) = GetLight(origin);

			var total = MathF.Max(light.X, MathF.Max(light.Y, light.Z));
			var color = total > 0f ? light / total : Vector3.One;
			if (total == 0f)
				total = 1f;

			var shade = total * DirectScale;
			var ambient = MathF.Min(total - shade, MaxAmbient);
			shade = MathF.Min(shade, MaxLight - ambient);

			for (var i = 0; i < LeafAmbientLighting.NumCubeSides; i++)
			{
				// 1 where the normal faces away from the light, -1 where it faces it
				var lightCos = Vector3.Dot(BoxDirections[i], lightDirection);
				var wrapped = (lightCos + (Lambert - 1f)) / Lambert;
				var illum = Math.Clamp(ambient + shade - shade * MathF.Max(wrapped, 0f), 0f, MaxLight);

				var cubeColor = ColorUtil.ConvertGoldSrcLightToAmbientColorRGBExp32(illum * color.X, illum * color.Y, illum * color.Z);
				sample.SetColor(i, Color.FromArgb(cubeColor.r, cubeColor.g, cubeColor.b));
				sample.SetExponent(i, cubeColor.exponent);
			}
		}

		// The light a model with its origin here gets, and the direction it travels
		private (Vector3 light, Vector3 direction) GetLight(Vector3 origin)
		{
			var start = origin + new Vector3(0f, 0f, SampleHeight);

			if (skyColor != Vector3.Zero && TraceHitsSky(start, origin - skyDirection * SkyDistance))
				return (skyColor, skyDirection);

			var down = new Vector3(0f, 0f, -FloorDistance);
			var light = LightPoint(start, start + down);

			// The direction leans away from where the floor is brighter
			float Gradient(float dx, float dy)
			{
				var offset = new Vector3(dx, dy, 0f);
				var sample = LightPoint(start + offset, start + offset + down);
				return (sample.X + sample.Y + sample.Z) / 768f;
			}

			var g0 = Gradient(-GradientOffset, -GradientOffset);
			var g1 = Gradient(GradientOffset, -GradientOffset);
			var g2 = Gradient(GradientOffset, GradientOffset);
			var g3 = Gradient(-GradientOffset, GradientOffset);
			var direction = Vector3.Normalize(new Vector3(g0 - g1 - g2 + g3, g1 + g0 - g2 - g3, -1f));

			return (light, direction);
		}

		// The lightmap color where a line first crosses a lit face (R_RecursiveLightPoint), black if it crosses none
		private Vector3 LightPoint(Vector3 start, Vector3 end)
		{
			return RecursiveLightPoint(worldHeadNode, start, end, out var color) ? color : Vector3.Zero;
		}

		private bool RecursiveLightPoint(int nodeIndex, Vector3 start, Vector3 end, out Vector3 color)
		{
			color = Vector3.Zero;
			while (true)
			{
				if (nodeIndex < 0)
					return false;

				var node = gs.Nodes[nodeIndex];
				var plane = gs.Planes[node.planeIndex];
				var front = Vector3.Dot(start, plane.normal) - plane.dist;
				var back = Vector3.Dot(end, plane.normal) - plane.dist;
				var side = front < 0f ? 1 : 0;
				if ((back < 0f ? 1 : 0) == side)
				{
					nodeIndex = side == 0 ? node.child0 : node.child1;
					continue;
				}

				var mid = Vector3.Lerp(start, end, front / (front - back));

				// The near side first
				if (RecursiveLightPoint(side == 0 ? node.child0 : node.child1, start, mid, out color))
					return true;

				for (var i = 0; i < node.numFaces; i++)
				{
					if (SampleFace(node.firstFace + i, mid, out color))
						return true;
				}

				// Then the far side
				nodeIndex = side == 0 ? node.child1 : node.child0;
				start = mid;
			}
		}

		// The lightmap color of a face at a point on its plane, if the point is within its lightmap
		private bool SampleFace(int faceIndex, Vector3 point, out Vector3 color)
		{
			color = Vector3.Zero;
			var face = gs.Faces[faceIndex];
			var texInfo = gs.TexInfos[face.texInfo];
			if ((texInfo.flags & GoldSrcBsp.TEX_SPECIAL) != 0)
				return false;

			var s = point.X * texInfo.s.X + point.Y * texInfo.s.Y + point.Z * texInfo.s.Z + texInfo.s.W;
			var t = point.X * texInfo.t.X + point.Y * texInfo.t.Y + point.Z * texInfo.t.Z + texInfo.t.W;
			var (minS, minT, extentS, extentT) = GetLightmapExtents(face, texInfo);
			var ds = s - minS;
			var dt = t - minT;
			if (ds < 0f || dt < 0f || ds > extentS || dt > extentT)
				return false;

			// A face without a lightmap is black
			if (face.lightOffset < 0)
				return true;

			var smax = extentS / LightmapLuxelSize + 1;
			var tmax = extentT / LightmapLuxelSize + 1;
			var luxel = (int)MathF.Round(dt / LightmapLuxelSize, MidpointRounding.AwayFromZero) * smax +
				(int)MathF.Round(ds / LightmapLuxelSize, MidpointRounding.AwayFromZero);
			for (var map = 0; map < MaxLightmaps && face.styles[map] != 255; map++)
			{
				var offset = face.lightOffset + (map * smax * tmax + luxel) * 3;
				if (offset + 2 >= gs.Lighting.Length)
					break;

				var scale = lightStyleValues[face.styles[map]] / 256f;
				color += new Vector3(gs.Lighting[offset], gs.Lighting[offset + 1], gs.Lighting[offset + 2]) * scale;
			}

			color = Vector3.Min(color, new Vector3(255f));
			return true;
		}

		// A face's lightmap mins and extents in texels, on the luxel grid
		private (int minS, int minT, int extentS, int extentT) GetLightmapExtents(GoldSrcBsp.Face face, GoldSrcBsp.TexInfo texInfo)
		{
			var minS = double.MaxValue;
			var minT = double.MaxValue;
			var maxS = double.MinValue;
			var maxT = double.MinValue;
			for (var i = 0; i < face.numEdges; i++)
			{
				var surfEdge = gs.SurfEdges[face.firstEdge + i];
				var edge = gs.Edges[Math.Abs(surfEdge)];
				var v = gs.Vertices[surfEdge >= 0 ? edge.v0 : edge.v1];
				var s = GoldSrcConverter.GetTexCoordForExtents(v, texInfo.s);
				var t = GoldSrcConverter.GetTexCoordForExtents(v, texInfo.t);
				minS = Math.Min(minS, s);
				minT = Math.Min(minT, t);
				maxS = Math.Max(maxS, s);
				maxT = Math.Max(maxT, t);
			}

			var lumMinS = (int)Math.Floor(minS / LightmapLuxelSize);
			var lumMinT = (int)Math.Floor(minT / LightmapLuxelSize);
			var lumMaxS = (int)Math.Ceiling(maxS / LightmapLuxelSize);
			var lumMaxT = (int)Math.Ceiling(maxT / LightmapLuxelSize);
			return (lumMinS * LightmapLuxelSize, lumMinT * LightmapLuxelSize, (lumMaxS - lumMinS) * LightmapLuxelSize, (lumMaxT - lumMinT) * LightmapLuxelSize);
		}

		// Whether a line through the world first stops in sky, as the game's world trace does (it passes through
		// empty space and liquids)
		private bool TraceHitsSky(Vector3 start, Vector3 end)
		{
			return FirstBlockingContents(worldHeadNode, start, end) == GoldSrcBsp.CONTENTS_SKY;
		}

		private int? FirstBlockingContents(int nodeIndex, Vector3 start, Vector3 end)
		{
			while (nodeIndex >= 0)
			{
				var node = gs.Nodes[nodeIndex];
				var plane = gs.Planes[node.planeIndex];
				var front = Vector3.Dot(start, plane.normal) - plane.dist;
				var back = Vector3.Dot(end, plane.normal) - plane.dist;
				if (front >= 0f && back >= 0f)
				{
					nodeIndex = node.child0;
					continue;
				}
				if (front < 0f && back < 0f)
				{
					nodeIndex = node.child1;
					continue;
				}

				var side = front < 0f ? 1 : 0;
				var mid = Vector3.Lerp(start, end, front / (front - back));
				if (FirstBlockingContents(side == 0 ? node.child0 : node.child1, start, mid) is int contents)
					return contents;

				nodeIndex = side == 0 ? node.child1 : node.child0;
				start = mid;
			}

			var leafContents = gs.Leaves[-nodeIndex - 1].contents;
			return leafContents is GoldSrcBsp.CONTENTS_EMPTY or GoldSrcBsp.CONTENTS_WATER or GoldSrcBsp.CONTENTS_SLIME or GoldSrcBsp.CONTENTS_LAVA
				? null
				: leafContents;
		}

		private int FindLeaf(Vector3 position)
		{
			var nodeIndex = worldHeadNode;
			while (nodeIndex >= 0)
			{
				var node = gs.Nodes[nodeIndex];
				var plane = gs.Planes[node.planeIndex];
				nodeIndex = Vector3.Dot(plane.normal, position) - plane.dist >= 0f ? node.child0 : node.child1;
			}

			return -nodeIndex - 1;
		}
	}
}
