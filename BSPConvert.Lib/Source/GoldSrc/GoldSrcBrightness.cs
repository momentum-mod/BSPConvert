using System;
using System.Linq;

namespace BSPConvert.Lib.GoldSrc
{
	// The GoldSrc video settings a converted map's brightness matches: how bright GoldSrc (Half-Life's 25th anniversary
	// update's hw.dll) draws its lightmaps, model lighting and textures with them, as the linear light and sRGB texture
	// values Strata draws. Defaults are the game's own (its cvars' defaults). Maps made for other settings, e.g. an
	// older config's gamma 2.5 and brightness 1, look like they did for their author with those.
	public class GoldSrcBrightness
	{
		// "gamma": the display gamma GoldSrc corrects lighting and textures for
		public float gamma = 2.2f;
		// "brightness": brightens dark lighting (0 leaves it alone, 1 is the old default config's)
		public float brightness = 0f;
		// "lightgamma": the gamma lightmaps and model lighting are converted to linear light with
		public float lightGamma = 2.5f;
		// "texgamma": textures are brightened to (c / 255)^(texgamma / gamma)
		public float texGamma = 2f;
		// Draws the world's lightmaps 4/3 as bright. The 25th anniversary update's world shader (fs_world.frag) always
		// does, whatever gl_overbright says; without it is GoldSrc's fixed function path (gl_use_shaders 0) with
		// gl_overbright 0.
		public bool overbright = true;

		// Strata draws sRGB textures and linear light on a gamma 2.2 display
		private const float DisplayGamma = 2.2f;
		private const int LightTableSize = 1024;
		// R_BuildLightMap scales a luxel by its lightstyle's value, 264 for the normal 'm'
		private const int NormalLightStyle = 264;

		private byte[]? texGammaTable;
		private int[]? lightGammaTable;

		// GoldSrc brightens the textures of BSPs, WADs and studio models as it loads them, apart from masked ones ('{'
		// textures and masked model textures), to (c / 255)^(texgamma / gamma) (BuildGammaTable, and Image_SetPalette's
		// LUMP_TEXGAMMA in Xash)
		public byte[] TexGammaTable => texGammaTable ??= Enumerable.Range(0, 256)
			.Select(i => (byte)Math.Clamp((int)(Math.Pow(i / 255.0, texGamma / gamma) * 255.0), 0, 255))
			.ToArray();

		// BuildGammaTable's light table: light to linear with lightgamma, the brightness curve, and back with 1 / gamma
		private int[] LightGammaTable => lightGammaTable ??= BuildLightGammaTable();

		private int[] BuildLightGammaTable()
		{
			// The light below which brightness lifts dark light to 0.125 (none at 0, where g3 is 0.125 too)
			double g3;
			if (brightness <= 0f)
				g3 = 0.125;
			else if (brightness > 1f)
				g3 = 0.05;
			else
				g3 = 0.125 - brightness * brightness * 0.075;

			var table = new int[LightTableSize];
			for (var i = 0; i < LightTableSize; i++)
			{
				var f = Math.Pow(i / (double)(LightTableSize - 1), lightGamma);
				if (brightness > 1f)
					f *= brightness;

				if (f <= g3)
					f = f / g3 * 0.125;
				else
					f = 0.125 + (f - g3) / (1.0 - g3) * 0.875;

				table[i] = Math.Clamp((int)((LightTableSize - 1) * Math.Pow(f, 1.0 / gamma)), 0, LightTableSize - 1);
			}

			return table;
		}

		// A lightmap luxel (0-255) of a normal lightstyle as linear light, where 1 lights a texture at its full color.
		// R_BuildLightMap indexes the light table at luxel * 264 * scale >> 14, where the scale is 256 with overbright
		// and 2^(1 / lightgamma) * 256 without it, and stores the table's value / 4. The world shader draws that 4/3 as
		// bright (2 * albedo * lightmap * 128 / 192), and so does R_BlendLightmaps' 2x blend of 2/3 of it with
		// gl_overbright. Matched against screenshots of Half-Life 25 within about 5% (10-20% darker in deep shadow).
		public float LightmapToLinear(byte value)
		{
			var scale = overbright ? 256 : (int)(Math.Pow(2.0, 1.0 / lightGamma) * 256.0 + 0.5);
			var index = Math.Min(value * NormalLightStyle * scale >> 14, LightTableSize - 1);
			var light = (LightGammaTable[index] >> 2) / 255f;
			if (overbright)
				light *= 4f / 3f;

			return MathF.Pow(light, DisplayGamma);
		}

		// Light on a model (0-255) as linear light. R_StudioLighting indexes the light table at 4 times the light;
		// overbright doesn't touch models.
		public float ModelLightToLinear(float value)
		{
			var index = Math.Min((int)(Math.Clamp(value, 0f, 255f) * 4f), LightTableSize - 1);
			return MathF.Pow(LightGammaTable[index] / (float)(LightTableSize - 1), DisplayGamma);
		}
	}
}
