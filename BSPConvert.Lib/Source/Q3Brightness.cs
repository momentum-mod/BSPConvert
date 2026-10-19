using System;

namespace BSPConvert.Lib
{
	// The Quake 3 video settings a converted map's lighting matches: how bright Quake 3 draws its lightmaps and light
	// grid with them (R_SetColorMappings and R_ColorShiftLightingBytes), as the linear light Strata draws. Defaults are
	// the game's own (its cvars' defaults), and convert maps as bright as BSPConvert always has. Maps made for other
	// settings, e.g. a raised r_gamma or r_mapOverBrightBits, look like they did for their author with those.
	// Textures aren't touched, though Quake 3's gamma brightens them too.
	public class Q3Brightness
	{
		// "r_gamma": the gamma ramp Quake 3 draws the screen with (out = in^(1 / gamma)), which brightens dark light
		public float gamma = 1f;
		// "r_mapOverBrightBits": lightmaps and the light grid are drawn 2^n as bright (2, 4x, by default)
		public int mapOverBrightBits = 2;
		// Not a video setting: the least light (0-255 per channel, like q3map2's _minlight) lightmaps and the light grid
		// get, before the settings above. Evens out dark, splotchy lighting, which a high gamma brings out. 0 leaves the
		// light alone.
		public int minLight = 0;

		// Quake 3's r_gamma range, and the most map overbright bits taken (16x, which already whitens all but the
		// darkest light)
		public const float MinGamma = 0.5f;
		public const float MaxGamma = 3f;
		public const int MaxMapOverBrightBits = 4;

		// Strata draws linear light on a gamma 2.2 display
		private const double DisplayGamma = 2.2;
		// "r_overBrightBits": how much of the overbright the gamma ramp does instead of the light (1, Quake 3's
		// default, in fullscreen and in Quake3e). It only matters with a gamma other than 1.
		private const int OverBrightBits = 1;

		// The linear light Strata draws for the defaults' overbright (4x lightmaps and light grid, 1x external
		// lightmaps), which BSPConvert has always converted them to: 16x and 4x
		private const double DefaultLinear = 16.0;
		private const double DefaultExternalLinear = 4.0;

		// Light (0-255, at least minLight) of a lightmap luxel or the light grid as linear light, where 1 lights a texture
		// at its full color. External lightmaps are images a shader draws like a texture, which r_mapOverBrightBits
		// doesn't touch.
		public float LightToLinear(float value, bool external = false)
		{
			var defaultLight = external ? 1.0 : 4.0;
			var defaultLinear = external ? DefaultExternalLinear : DefaultLinear;
			var light = Math.Max(value, Math.Max(minLight, 0)) / 255.0;
			return (float)(defaultLinear * Math.Pow(DrawnLight(light, external) / defaultLight, DisplayGamma));
		}

		// The largest light (0-255) R_ColorShiftLightingBytes keeps a luxel's hue under with these settings, if
		// Quake 3 did all of the overbright on the light (r_overBrightBits 0, as in a window)
		public float OverbrightCeiling => 255f / (1 << Math.Clamp(mapOverBrightBits, 0, MaxMapOverBrightBits));

		// How bright Quake 3 draws light (0-1), on a texture of full color, before the screen's own gamma.
		// R_ColorShiftLightingBytes shifts lightmaps and the light grid by r_mapOverBrightBits - r_overBrightBits,
		// shaders draw external lightmaps with identityLight (1 / 2^r_overBrightBits), and the gamma ramp raises the
		// screen to 1 / r_gamma, then shifts it by r_overBrightBits (never more than r_mapOverBrightBits).
		private double DrawnLight(double light, bool external)
		{
			var overBrightBits = Math.Min(OverBrightBits, Math.Max(mapOverBrightBits, 0));
			var shift = external ? -overBrightBits : mapOverBrightBits - overBrightBits;
			return (1 << overBrightBits) * Math.Pow(light * Math.Pow(2.0, shift), 1.0 / gamma);
		}
	}
}
