using BSPConvert.Lib;
using BSPConvert.Lib.GoldSrc;

namespace BSPConvert.Test
{
	public class MinLightTests
	{
		[Test]
		public void Q3LightIsAtLeastMinLight()
		{
			var brightness = new Q3Brightness { minLight = 20 };
			var defaults = new Q3Brightness();

			for (var i = 0; i <= 20; i++)
			{
				Assert.That(brightness.LightToLinear(i), Is.EqualTo(defaults.LightToLinear(20)));
				Assert.That(brightness.LightToLinear(i, external: true), Is.EqualTo(defaults.LightToLinear(20, external: true)));
			}

			Assert.That(brightness.LightToLinear(21), Is.EqualTo(defaults.LightToLinear(21)));
			Assert.That(brightness.LightToLinear(255), Is.EqualTo(defaults.LightToLinear(255)));
		}

		[Test]
		public void GoldSrcBaseStyleAndModelLightAreAtLeastMinLight()
		{
			var brightness = new GoldSrcBrightness { minLight = 20 };
			var defaults = new GoldSrcBrightness();

			for (byte i = 0; i <= 20; i++)
			{
				Assert.That(brightness.LightmapToLinear(i), Is.EqualTo(defaults.LightmapToLinear(20)));
				Assert.That(brightness.ModelLightToLinear(i), Is.EqualTo(defaults.ModelLightToLinear(20)));
				// Switchable light styles add to the base style, so they don't get it
				Assert.That(brightness.LightmapToLinear(i, baseStyle: false), Is.EqualTo(defaults.LightmapToLinear(i)));
			}

			Assert.That(brightness.LightmapToLinear(200), Is.EqualTo(defaults.LightmapToLinear(200)));
			Assert.That(brightness.ModelLightToLinear(200f), Is.EqualTo(defaults.ModelLightToLinear(200f)));
		}

		[Test]
		public void NoMinLightByDefault()
		{
			Assert.That(new Q3Brightness().LightToLinear(0), Is.EqualTo(0f));
			Assert.That(new GoldSrcBrightness().LightmapToLinear(0), Is.EqualTo(0f));
		}
	}
}
