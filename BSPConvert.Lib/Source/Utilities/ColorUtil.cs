using BSPConvert.Lib.GoldSrc;

namespace BSPConvert.Lib
{
	public static class ColorUtil
	{
		// Quake 3 draws lightmapped surfaces overbright (R_ColorShiftLightingBytes in tr_bsp.c, 4x with the default
		// r_mapOverBrightBits 2). We bake that, and the rest of the brightness its video settings give the light (see
		// Q3Brightness), into the stored RGBExp32 luxels so the lighting is display-ready and renders identically on
		// world geometry and brush entities. External lightmaps are drawn like textures, without the overbright.
		public static ColorRGBExp32 ConvertQ3LightmapToColorRGBExp32(byte r, byte g, byte b, Q3Brightness brightness, bool clampOverbright = false, bool external = false)
		{
			if (clampOverbright)
				(r, g, b) = ApplyOverbrightClamp(r, g, b, (int)brightness.OverbrightCeiling);

			// Source's lightmaps store linear light 255 times over (TexLightToLinear)
			return PackColorRGBExp32(
				brightness.LightToLinear(r, external) * 255f,
				brightness.LightToLinear(g, external) * 255f,
				brightness.LightToLinear(b, external) * 255f);
		}

		// Light grid light as an ambient cube color, which the engine reads 255 times brighter than lightmaps, so as
		// linear light
		public static ColorRGBExp32 ConvertQ3LightGridColorToColorRGBExp32(float r, float g, float b, Q3Brightness brightness, bool clampOverbright = false)
		{
			var ceiling = clampOverbright ? brightness.OverbrightCeiling : 255f;
			r = Math.Min(r, ceiling);
			g = Math.Min(g, ceiling);
			b = Math.Min(b, ceiling);

			return PackColorRGBExp32(brightness.LightToLinear(r), brightness.LightToLinear(g), brightness.LightToLinear(b));
		}

		// A GoldSrc lightmap luxel as a Source one, which stores linear light 255 times over (TexLightToLinear), as bright
		// as GoldSrc draws it with the given settings
		public static ColorRGBExp32 ConvertGoldSrcLightmapToColorRGBExp32(byte r, byte g, byte b, GoldSrcBrightness brightness)
		{
			return PackColorRGBExp32(brightness.LightmapToLinear(r) * 255f, brightness.LightmapToLinear(g) * 255f, brightness.LightmapToLinear(b) * 255f);
		}

		// The average of a run of GoldSrc lightmap luxels (RGB triplets), taken in linear space like VRAD's
		public static ColorRGBExp32 AverageGoldSrcLightmapColor(ReadOnlySpan<byte> rgb, GoldSrcBrightness brightness)
		{
			var count = rgb.Length / 3;
			if (count == 0)
				return PackColorRGBExp32(0f, 0f, 0f);

			float r = 0f, g = 0f, b = 0f;
			for (var i = 0; i < count * 3; i += 3)
			{
				r += brightness.LightmapToLinear(rgb[i]);
				g += brightness.LightmapToLinear(rgb[i + 1]);
				b += brightness.LightmapToLinear(rgb[i + 2]);
			}

			return PackColorRGBExp32(r * 255f / count, g * 255f / count, b * 255f / count);
		}

		// Light on a GoldSrc model (0-255) as an ambient cube color, which the engine reads 255 times brighter than
		// lightmaps, so as linear light
		public static ColorRGBExp32 ConvertGoldSrcLightToAmbientColorRGBExp32(float r, float g, float b, GoldSrcBrightness brightness)
		{
			return PackColorRGBExp32(brightness.ModelLightToLinear(r), brightness.ModelLightToLinear(g), brightness.ModelLightToLinear(b));
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
		// clipping per channel. Because we bake that overbright into the encoded luxels (not in this
		// clamp step), here we only enforce the matching ceiling: the brightest pre-overbright luxel that survives is
		// 255 / the overbright (Q3Brightness.OverbrightCeiling, 63 for the default x4), above which it would blow past
		// white. Bytes at or below the ceiling (most of the map, incl. midtones) pass through unchanged, so the look
		// there is preserved; only the over-bright luxels Q3 discards get flattened, fixing the too-bright/blotchy highlights.
		public static (byte r, byte g, byte b) ApplyOverbrightClamp(byte r, byte g, byte b, int ceiling)
		{
			var max = Math.Max(r, Math.Max(g, b));
			if (max <= ceiling)
				return (r, g, b);

			return (
				(byte)(r * ceiling / max),
				(byte)(g * ceiling / max),
				(byte)(b * ceiling / max));
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
