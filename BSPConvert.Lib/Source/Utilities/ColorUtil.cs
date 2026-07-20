namespace BSPConvert.Lib
{
	public static class ColorUtil
	{
		// Quake 3 applies a 4x overbright to lightmapped surfaces at display time (R_ColorShiftLightingBytes
		// in tr_bsp.c, with the default r_mapOverBrightBits = 2, i.e. << 2). We bake that factor directly
		// into the stored RGBExp32 luxels (below) so the lighting is display-ready and renders identically on
		// world geometry and brush entities.
		private const int OVERBRIGHT = 4;

		public static ColorRGBExp32 ConvertQ3LightmapToColorRGBExp32(byte r, byte g, byte b, bool clampOverbright = false, bool applyOverbright = true)
		{
			// The white point (brightest surviving luxel) is 255/OVERBRIGHT when the overbright clamp is
			// active, otherwise the full 8-bit range survives; the black-point lift remaps against it.
			var ceiling = clampOverbright ? 255 / OVERBRIGHT : 255;

			if (clampOverbright)
				(r, g, b) = ApplyOverbrightClamp(r, g, b);

			var overbright = applyOverbright ? OVERBRIGHT : 1; // External lightmaps don't need the 4x overbright, only apply to internal lightmaps.

			// The * 4f maps into the 0-4 HDR range Source's lightmap format expects; the * OVERBRIGHT bakes
			// in Quake 3's display overbright so the renderer needs no extra per-material scaling.
			var rf = GammaToLinear(r) * 4f * overbright;
			var gf = GammaToLinear(g) * 4f * overbright;
			var bf = GammaToLinear(b) * 4f * overbright;

			return PackColorRGBExp32(rf, gf, bf);
		}

		public static ColorRGBExp32 ConvertQ3LightGridColorToColorRGBExp32(float r, float g, float b, bool clampOverbright = false)
		{
			var ceiling = clampOverbright ? 255f / OVERBRIGHT : 255f;
			r = Math.Min(r, ceiling);
			g = Math.Min(g, ceiling);
			b = Math.Min(b, ceiling);

			var rf = GammaToLinear(r) * 4f * OVERBRIGHT / 255f;
			var gf = GammaToLinear(g) * 4f * OVERBRIGHT / 255f;
			var bf = GammaToLinear(b) * 4f * OVERBRIGHT / 255f;

			return PackColorRGBExp32(rf, gf, bf);
		}

		private static ColorRGBExp32 PackColorRGBExp32(float rf, float gf, float bf)
		{
			var color = new ColorRGBExp32();

			var max = Math.Max(rf, Math.Max(gf, bf));
			var exp = CalcExponent(max);

			var fbits = (uint)((127 - exp) << 23);
			var scalar = BitConverter.UInt32BitsToSingle(fbits);

			color.r = (byte)(rf * scalar);
			color.g = (byte)(gf * scalar);
			color.b = (byte)(bf * scalar);
			color.exponent = (sbyte)exp;

			return color;
		}

		// Replicates the saturation behaviour of Quake 3's R_ColorShiftLightingBytes (tr_bsp.c). Q3
		// multiplies lightmap colors by the overbright factor and, when a channel exceeds 255, scales ALL
		// channels down together (hue-preserving) so a bright luxel flattens toward white instead of
		// clipping per channel. Because we bake that overbright (x4) into the encoded luxels (not in this
		// clamp step), here we only enforce the matching ceiling: the brightest pre-overbright luxel that survives is
		// 255/OVERBRIGHT (= 63), above which the engine's x4 would blow past white. Bytes at or below the
		// ceiling (most of the map, incl. midtones) pass through unchanged, so the look there is preserved;
		// only the over-bright luxels Q3 discards get flattened, fixing the too-bright/blotchy highlights.
		public static (byte r, byte g, byte b) ApplyOverbrightClamp(byte r, byte g, byte b)
		{
			const int ceiling = 255 / OVERBRIGHT; // 63

			var max = Math.Max(r, Math.Max(g, b));
			if (max <= ceiling)
				return (r, g, b);

			return (
				(byte)(r * ceiling / max),
				(byte)(g * ceiling / max),
				(byte)(b * ceiling / max));
		}

		private static float GammaToLinear(float gamma)
		{
			return (float)(255.0 * Math.Pow(gamma / 255.0, 2.2));
		}

		private static int CalcExponent(float max)
		{
			if (max == 0f)
				return 0;

			var fbits = BitConverter.SingleToUInt32Bits(max);

			// Extract the exponent component from the floating point bits (bits 23 - 30)
			var expComponent = (int)((fbits & 0x7F800000) >> 23);

			const int biasedSeven = 7 + 127;
			expComponent -= biasedSeven;

			return expComponent;
		}
	}
}
