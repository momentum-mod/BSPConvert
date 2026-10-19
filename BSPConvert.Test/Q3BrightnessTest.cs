using BSPConvert.Lib;

namespace BSPConvert.Test
{
	public class Q3BrightnessTests
	{
		[Test]
		public void DefaultsConvertAsBefore()
		{
			// Before its brightness was a setting, BSPConvert converted Quake 3 light to 16 * (c / 255)^2.2, and external
			// lightmaps to 4 * (c / 255)^2.2
			var brightness = new Q3Brightness();
			for (var i = 0; i < 256; i++)
			{
				var old = (float)Math.Pow(i / 255.0, 2.2);
				Assert.That(brightness.LightToLinear(i), Is.EqualTo(16f * old).Within(1e-5).Percent);
				Assert.That(brightness.LightToLinear(i, external: true), Is.EqualTo(4f * old).Within(1e-5).Percent);
			}
		}

		[Test]
		public void MapOverBrightBitsScaleLightBeforeTheDisplaysGamma()
		{
			var brightness = new Q3Brightness { mapOverBrightBits = 3 };
			var defaults = new Q3Brightness();

			Assert.That(brightness.LightToLinear(50f) / defaults.LightToLinear(50f), Is.EqualTo(MathF.Pow(2f, 2.2f)).Within(1e-4));
			// External lightmaps aren't drawn overbright
			Assert.That(brightness.LightToLinear(50f, external: true), Is.EqualTo(defaults.LightToLinear(50f, external: true)));
			Assert.That(brightness.OverbrightCeiling, Is.EqualTo(255f / 8f));
		}

		[Test]
		public void GammaBrightensDarkLightTheMost()
		{
			var brightness = new Q3Brightness { gamma = 1.5f };
			var defaults = new Q3Brightness();

			var dark = brightness.LightToLinear(10f) / defaults.LightToLinear(10f);
			var bright = brightness.LightToLinear(60f) / defaults.LightToLinear(60f);
			Assert.That(dark, Is.GreaterThan(bright));
			Assert.That(bright, Is.GreaterThan(1f));
			Assert.That(brightness.LightToLinear(0f), Is.EqualTo(0f));
		}
	}
}
