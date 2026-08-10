using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BSPConvert.Lib
{
#if UNITY
	using Vector2 = UnityEngine.Vector2;
#elif GODOT
	using Vector2 = Godot.Vector2;
#elif NEOAXIS
	using Vector2 = NeoAxis.Vector2F;
#else
	using Vector2 = System.Numerics.Vector2;
#endif

	public class LightmapData
	{
		public Vector2 size;
		public byte[] data;

		// True when no luxel in the map exceeds 255/OVERBRIGHT, i.e. q3map2 wrote these images in Quake 3's
		// pre-overbright range (like internal lightmaps) and the engine's 4x display overbright is expected to
		// be applied. Other q3map2 configurations export external lightmaps already at full display range;
		// those have luxels above the ceiling and must be taken as-is. See ConvertExternalLightmaps.
		public bool preOverbright;
	}
	
	public class ExternalLightmapLoader
	{
		private Dictionary<string, Shader> shaderDict;
		private string contentDir;

		private readonly HashSet<string> validLightmapFormats = new HashSet<string>()
		{
			".tga",
			".jpg",
			".jpeg",
			".png",
			".bmp"
		};

		public ExternalLightmapLoader(Dictionary<string, Shader> shaderDict, string contentDir)
        {
			this.shaderDict = shaderDict;
			this.contentDir = contentDir;
        }

		public Dictionary<string, LightmapData> LoadLightmaps()
		{
			var lightmapDict = new Dictionary<string, LightmapData>();
			var curOffset = 0;

			foreach (var shader in shaderDict.Values)
			{
				var stage = shader.stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_LIGHTMAP && x.bundles[0].images[0] != "$lightmap");
				if (stage == null)
					continue;

				var lmImage = stage.bundles[0].images[0];
				if (lightmapDict.ContainsKey(lmImage)) // Only add unique lightmaps
					continue;

				try
				{
					(var data, var size) = GetExternalLightmapData(stage);

					var lightmapData = new LightmapData();
					lightmapData.data = data;
					lightmapData.size = size;

					curOffset += data.Length;

					lightmapDict.Add(lmImage, lightmapData);
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex.Message);
				}
			}

			// How q3map2 exported the lightmaps is a property of the map, not of one page, so decide it across
			// all of them: a single dark page that happens to stay under the ceiling must not end up 4x
			// brighter than its neighbours.
			var preOverbright = lightmapDict.Values.All(x => IsPreOverbright(x.data));
			foreach (var lightmapData in lightmapDict.Values)
				lightmapData.preOverbright = preOverbright;

			return lightmapDict;
		}

		// Whether the image's luxels all fit under Quake 3's pre-overbright ceiling (255/OVERBRIGHT). Testing
		// the ceiling rather than guessing is safe in both directions: the 4x is only ever applied when it
		// cannot clip, and images that already reach full display range are left alone.
		private static bool IsPreOverbright(byte[] data)
		{
			const int ceiling = 255 / ColorUtil.OVERBRIGHT;

			foreach (var b in data)
			{
				if (b > ceiling)
					return false;
			}

			return true;
		}

		private (byte[] data, Vector2 size) GetExternalLightmapData(ShaderStage stage)
		{
			var lmImage = stage.bundles[0].images[0];
			var lmPath = Path.Combine(contentDir, Path.GetDirectoryName(lmImage));
			
			// Look for any valid image files with matching name
			var lmFile = Directory.GetFiles(lmPath, Path.GetFileNameWithoutExtension(lmImage) + ".*").FirstOrDefault();
			if (lmFile == null || !validLightmapFormats.Contains(Path.GetExtension(lmFile)))
				throw new Exception($"Lightmap image {lmImage} not found");

			using var image = Image.Load<Rgba32>(lmFile);

			var data = new byte[image.Height * image.Width * 3];
			var curPixel = 0;

			image.ProcessPixelRows(accessor =>
			{
				for (var y = 0; y < accessor.Height; y++)
				{
					var pixelRow = accessor.GetRowSpan(y);

					// pixelRow.Length has the same value as accessor.Width,
					// but using pixelRow.Length allows the JIT to optimize away bounds checks:
					for (var x = 0; x < pixelRow.Length; x++)
					{
						// Get a reference to the pixel at position x
						ref var pixel = ref pixelRow[x];
						data[curPixel * 3 + 0] = pixel.R;
						data[curPixel * 3 + 1] = pixel.G;
						data[curPixel * 3 + 2] = pixel.B;

						curPixel++;
					}
				}
			});

			return (data, new Vector2(image.Width, image.Height));
		}
	}
}
