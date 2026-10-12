using BSPConvert.Lib;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;

namespace BSPConvert.Test
{
	public class ImageUtilTests
	{
		private string tempDir;

		[SetUp]
		public void Setup()
		{
			tempDir = Path.Combine(Path.GetTempPath(), "BSPConvert.ImageUtilTests." + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempDir);
		}

		[TearDown]
		public void TearDown()
		{
			Directory.Delete(tempDir, true);
		}

		[Test]
		public void LoadsTheFormatsMapsUse()
		{
			var png = Save("image.png", new PngEncoder());
			var tga = Save("image.tga", new TgaEncoder());

			using (var image = ImageUtil.Load(png))
				Assert.That(image[1, 0], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
			using (var image = ImageUtil.Load(tga))
				Assert.That(image.Width, Is.EqualTo(4));
			Assert.That(ImageUtil.Identify(png).Height, Is.EqualTo(2));
		}

		[Test]
		public void RefusesOtherFormatsWhateverTheirExtension()
		{
			// ImageSharp picks the decoder from the contents, so a TIFF named .tga would still reach its TIFF decoder
			var tiff = Save("image.tga", new TiffEncoder());

			Assert.Throws<UnknownImageFormatException>(() => ImageUtil.Load(tiff));
			Assert.Throws<UnknownImageFormatException>(() => ImageUtil.Identify(tiff));
		}

		private string Save(string name, SixLabors.ImageSharp.Formats.IImageEncoder encoder)
		{
			var path = Path.Combine(tempDir, name);
			using var image = new Image<Rgba32>(4, 2);
			image[1, 0] = new Rgba32(255, 0, 0, 255);
			image.Save(path, encoder);
			return path;
		}
	}
}
