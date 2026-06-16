using System;
using System.IO;
using System.Linq;
using LibBSP;

namespace BSPConvert.Lib
{
	// Diagnostic for an ALREADY-CONVERTED Source BSP: reports whether each lightmapped face's
	// lightmap block carries the clamp-to-edge guard band (a duplicated 1-luxel border on every side)
	// that prevents bilinear bleed between adjacent blocks in the atlas page. Point it at the exact BSP
	// in your game directory to confirm which build produced it (the border is invisible to
	// mat_showlightmappage, which only renders luxels inside a surface's sampling range).
	public static class LightmapVerifier
	{
		private const int LUXEL_BYTES = 4; // ColorRGBExp32 (RGBE)

		public static void Verify(string bspPath, ILogger logger)
		{
			if (!File.Exists(bspPath))
			{
				logger.Log($"Error: BSP file does not exist: {bspPath}");
				return;
			}

			logger.Log($"Verifying lightmaps: {bspPath}");
			logger.Log($"  last modified: {File.GetLastWriteTime(bspPath):yyyy-MM-dd HH:mm:ss}");

			var bsp = new BSP(new FileInfo(bspPath));
			var faces = bsp.Faces;
			var data = bsp.Lightmaps.Data;

			logger.Log($"  faces: {faces.Count}");
			logger.Log($"  lighting lump: {data.Length} bytes");

			if (data.Length == 0)
			{
				logger.Log("  No lightmap data in this BSP (LDR lighting lump is empty).");
				return;
			}

			int lightmappedFaces = 0;
			int dispFaces = 0;         // displacement faces - intentionally NOT bordered (engine computes coords)
			int checkedFaces = 0;      // primitive blocks large enough (>= 3x3) to carry a border
			int borderedFaces = 0;     // full border ring present on all four sides
			int partialFaces = 0;      // border on some but not all sides
			int outOfRange = 0;

			foreach (var face in faces)
			{
				var ofs = (int)face.Lightmap;
				if (ofs < 0)
					continue;
				lightmappedFaces++;

				// Only primitive faces carry the converter-baked guard band; displacement faces get their
				// lightCoords from the engine (border-unaware) so they're intentionally left unbordered.
				if ((int)face.NumPrimitives <= 0)
				{
					dispFaces++;
					continue;
				}

				var width = (int)face.LightmapSize.X + 1;
				var height = (int)face.LightmapSize.Y + 1;
				if (width < 3 || height < 3)
					continue; // too small to have an interior + border

				if (ofs + width * height * LUXEL_BYTES > data.Length)
				{
					outOfRange++;
					continue;
				}

				checkedFaces++;

				var top = RowsEqual(data, ofs, width, 0, 1);
				var bottom = RowsEqual(data, ofs, width, height - 1, height - 2);
				var left = ColumnsEqual(data, ofs, width, height, 0, 1);
				var right = ColumnsEqual(data, ofs, width, height, width - 1, width - 2);

				if (top && bottom && left && right)
					borderedFaces++;
				else if (top || bottom || left || right)
					partialFaces++;
			}

			logger.Log($"  lightmapped faces: {lightmappedFaces}  (displacement, unbordered by design: {dispFaces})");
			logger.Log($"  checked primitive blocks (>= 3x3): {checkedFaces}");
			logger.Log($"  full guard-band border: {borderedFaces}");
			logger.Log($"  partial border: {partialFaces}");
			logger.Log($"  no border: {checkedFaces - borderedFaces - partialFaces}");
			if (outOfRange > 0)
				logger.Log($"  WARNING: {outOfRange} faces had lightmap offsets past the end of the lighting lump.");

			if (checkedFaces == 0)
				logger.Log("  VERDICT(data): no primitive blocks large enough to verify.");
			else if (borderedFaces == checkedFaces)
				logger.Log("  VERDICT(data): PASS - every primitive block has the clamp-to-edge guard band.");
			else if (borderedFaces == 0)
				logger.Log("  VERDICT(data): FAIL - no guard-band borders found. This BSP was produced WITHOUT the lightmap-border fix (stale build/old map).");
			else
				logger.Log($"  VERDICT(data): PARTIAL - {borderedFaces}/{checkedFaces} primitive blocks bordered (a few may be uniform-color edges; investigate if many).");

			VerifyPrimCoords(bsp, faces, logger);
		}

		// Checks the OTHER half of the fix: the primitives' stored lightCoords must be inset away from the
		// block edge so a surface never samples the border ring (let alone the neighbouring block). The
		// engine maps a prim vertex to block-local texel position (lightCoord * LightmapExtents); with the
		// inset every vertex should sit >= ~1.5 luxels from both block edges {0, Extents+1}. Without it the
		// minimum distance collapses to ~0.5 and surfaces sample the boundary -> bleed.
		private static void VerifyPrimCoords(dynamic bsp, dynamic faces, ILogger logger)
		{
			dynamic prims = bsp.Primitives;
			dynamic primTexInfo = bsp.PrimitiveTextureInfo;
			int primCount = prims.Count;
			int texInfoCount = primTexInfo.Count;
			if (primCount == 0 || texInfoCount == 0)
			{
				logger.Log("  prim-coord check: SKIPPED (no primitives / prim texinfo parsed on load).");
				return;
			}

			float globalMinEdgeDist = float.MaxValue;
			float sumMinEdgeDist = 0f;
			int samples = 0;
			foreach (var face in faces)
			{
				if ((int)face.Lightmap < 0)
					continue;
				int extX = (int)face.LightmapSize.X;
				int extY = (int)face.LightmapSize.Y;
				if (extX < 2 || extY < 2)
					continue;

				int firstPrim = (int)face.FirstPrimitive;
				int numPrims = (int)face.NumPrimitives;
				for (int p = 0; p < numPrims; p++)
				{
					dynamic prim = prims[firstPrim + p];
					int firstVert = (int)prim.FirstVertex;
					int vertCount = (int)prim.VertexCount;
					float faceMin = float.MaxValue;
					for (int v = 0; v < vertCount; v++)
					{
						dynamic ti = primTexInfo[firstVert + v];
						float lx = (float)ti.LightmapCoord.X;
						float ly = (float)ti.LightmapCoord.Y;
						float px = lx * extX; // block-local texel position
						float py = ly * extY;
						float dEdge = Math.Min(Math.Min(px, (extX + 1) - px), Math.Min(py, (extY + 1) - py));
						if (dEdge < faceMin)
							faceMin = dEdge;
					}
					if (faceMin < globalMinEdgeDist)
						globalMinEdgeDist = faceMin;
					sumMinEdgeDist += faceMin;
					samples++;
				}
			}

			if (samples == 0)
			{
				logger.Log("  prim-coord check: no eligible primitives.");
				return;
			}

			float avg = sumMinEdgeDist / samples;
			logger.Log($"  prim-coord inset: minEdgeDist={globalMinEdgeDist:0.000} avgEdgeDist={avg:0.000} (luxels from block edge; expect ~1.0+ with the inset, ~0.5 without)");
			// Inset coords land ~1.0+ luxels from the edge; un-inset ones land ~0.5. Use a midpoint
			// threshold so float round-trip on an exactly-1.0 inset doesn't trip a false FAIL.
			if (globalMinEdgeDist >= 0.9f)
				logger.Log("  VERDICT(coords): PASS - prim lightCoords are inset; surfaces sample interior luxels only.");
			else
				logger.Log("  VERDICT(coords): FAIL - prim lightCoords reach the block edge; surfaces will sample the border/neighbour -> bleed.");
		}

		private static bool RowsEqual(byte[] data, int blockOfs, int width, int rowA, int rowB)
		{
			var a = blockOfs + rowA * width * LUXEL_BYTES;
			var b = blockOfs + rowB * width * LUXEL_BYTES;
			var count = width * LUXEL_BYTES;
			for (var i = 0; i < count; i++)
				if (data[a + i] != data[b + i])
					return false;
			return true;
		}

		private static bool ColumnsEqual(byte[] data, int blockOfs, int width, int height, int colA, int colB)
		{
			for (var y = 0; y < height; y++)
			{
				var a = blockOfs + (y * width + colA) * LUXEL_BYTES;
				var b = blockOfs + (y * width + colB) * LUXEL_BYTES;
				for (var c = 0; c < LUXEL_BYTES; c++)
					if (data[a + c] != data[b + c])
						return false;
			}
			return true;
		}
	}
}
