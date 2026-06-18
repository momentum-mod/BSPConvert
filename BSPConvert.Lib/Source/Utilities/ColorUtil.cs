namespace BSPConvert.Lib
{
	public static class ColorUtil
	{
		// The Momentum engine re-applies a 4x overbright to lightmapped surfaces at load
		// (map_loadhelper.cpp ColorModulate), matching Quake 3's default r_mapOverBrightBits = 2 (<<2).
		private const int OVERBRIGHT = 4;

		public static ColorRGBExp32 ConvertQ3LightmapToColorRGBExp32(byte r, byte g, byte b, float lightmapMin = 0f, bool clampOverbright = false)
		{
			// The white point (brightest surviving luxel) is 255/OVERBRIGHT when the overbright clamp is
			// active, otherwise the full 8-bit range survives; the black-point lift remaps against it.
			var ceiling = clampOverbright ? 255 / OVERBRIGHT : 255;

			if (clampOverbright)
				(r, g, b) = ApplyOverbrightClamp(r, g, b);
			(r, g, b) = ApplyMinBrightness(r, g, b, lightmapMin, ceiling);

			var color = new ColorRGBExp32();

			var rf = GammaToLinear(r) * 4f; // Multiply by 4 since Source expects lightmap values in 0-4 range
			var gf = GammaToLinear(g) * 4f;
			var bf = GammaToLinear(b) * 4f;

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
		// clipping per channel. Because the engine applies that overbright (x4) itself, we don't amplify
		// here - we only enforce the matching ceiling: the brightest pre-overbright luxel that survives is
		// 255/OVERBRIGHT (= 63), above which the engine's x4 would blow past white. Bytes at or below the
		// ceiling (most of the map, incl. midtones) pass through unchanged, so the look there is preserved;
		// only the over-bright luxels Q3 discards get flattened, fixing the too-bright/blotchy highlights.
		private static (byte r, byte g, byte b) ApplyOverbrightClamp(byte r, byte g, byte b)
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

		// Raises the lightmap black point: linearly remaps each channel's [0, ceiling] range to
		// [min*ceiling, ceiling] (i.e. the rendered [0,1] tonal range to [min,1], white unchanged). Near-
		// black 8-bit luxels have huge RELATIVE gaps (byte 1 vs 2 = 2x) that read as harsh banding once the
		// overbright/display amplify them; lifting the darkest luxels to a dim floor shrinks those relative
		// gaps for smoother shadow gradients, at the cost of shadow depth. min = 0 leaves darks untouched.
		// ceiling is the white point (255/OVERBRIGHT with the overbright clamp, else the full 255).
		private static (byte r, byte g, byte b) ApplyMinBrightness(byte r, byte g, byte b, float min, int ceiling)
		{
			if (min <= 0f)
				return (r, g, b);

			var floor = min * ceiling;

			byte Remap(byte c) => (byte)(floor + (1f - min) * c);
			return (Remap(r), Remap(g), Remap(b));
		}

		private static float GammaToLinear(byte gamma)
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
