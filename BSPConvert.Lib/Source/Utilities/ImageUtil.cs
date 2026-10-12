using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace BSPConvert.Lib
{
	// Loads the images maps and their content packs ship. ImageSharp picks the decoder from the file's contents, not
	// its extension, so only the formats maps use are enabled: an image in a downloaded pk3 can't reach the other
	// decoders (e.g. TIFF, which has known denial of service bugs in ImageSharp 3).
	public static class ImageUtil
	{
		private static readonly DecoderOptions decoderOptions = new DecoderOptions
		{
			Configuration = new Configuration(
				new PngConfigurationModule(),
				new JpegConfigurationModule(),
				new TgaConfigurationModule(),
				new BmpConfigurationModule(),
				new WebpConfigurationModule())
		};

		public static Image<Rgba32> Load(string path)
		{
			return Image.Load<Rgba32>(decoderOptions, path);
		}

		public static ImageInfo Identify(string path)
		{
			return Image.Identify(decoderOptions, path);
		}
	}
}
