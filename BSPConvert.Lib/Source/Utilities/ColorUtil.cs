namespace BSPConvert.Lib
{
	public static class ColorUtil
	{
		public static ColorRGBExp32 ConvertQ3LightmapToColorRGBExp32(byte r, byte g, byte b, float brightness)
		{
			var color = new ColorRGBExp32();

			var rf = GammaToLinear(r) * brightness;
			var gf = GammaToLinear(g) * brightness;
			var bf = GammaToLinear(b) * brightness;

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

		public static float GammaToLinear(byte gamma)
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
