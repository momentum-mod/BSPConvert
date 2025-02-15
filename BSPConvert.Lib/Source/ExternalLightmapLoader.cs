namespace BSPConvert.Lib;
using System;
using System.Collections.Generic;
using System.Linq;

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
	}
	
	public class ExternalLightmapLoader(Dictionary<string, Shader> shaderDict, string contentDir)
{
		private readonly Dictionary<string, Shader> shaderDict = shaderDict;
		private readonly string contentDir = contentDir;

		private readonly HashSet<string> validLightmapFormats =
        [
            ".tga",
			".jpg",
			".jpeg",
			".png",
			".bmp"
		];

    public Dictionary<string, LightmapData> LoadLightmaps()
		{
			var lightmapDict = new Dictionary<string, LightmapData>();
        int curOffset = 0;

			foreach (Shader shader in shaderDict.Values)
			{
            ShaderStage? stage = shader.stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_LIGHTMAP && x.bundles[0].images[0] != "$lightmap");
				if (stage == null)
					continue;

            string lmImage = stage.bundles[0].images[0];
				if (lightmapDict.ContainsKey(lmImage)) // Only add unique lightmaps
					continue;

				try
				{
					(byte[]? data, Vector2 size) = GetExternalLightmapData(stage);

                var lightmapData = new LightmapData
                {
                    data = data,
                    size = size
                };

                curOffset += data.Length;

					lightmapDict.Add(lmImage, lightmapData);
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex.Message);
				}
			}

			return lightmapDict;
		}

		private (byte[] data, Vector2 size) GetExternalLightmapData(ShaderStage stage)
		{
        string lmImage = stage.bundles[0].images[0];
        string lmPath = Path.Combine(contentDir, Path.GetDirectoryName(lmImage));

        // Look for any valid image files with matching name
        string? lmFile = Directory.GetFiles(lmPath, Path.GetFileNameWithoutExtension(lmImage) + ".*").FirstOrDefault();
			if (lmFile == null || !validLightmapFormats.Contains(Path.GetExtension(lmFile)))
				throw new Exception($"Lightmap image {lmImage} not found");

			using var image = Image.Load<Rgba32>(lmFile);

        byte[] data = new byte[image.Height * image.Width * 3];
        int curPixel = 0;

			image.ProcessPixelRows(accessor =>
			{
				for (int y = 0; y < accessor.Height; y++)
				{
					var pixelRow = accessor.GetRowSpan(y);

					// pixelRow.Length has the same value as accessor.Width,
					// but using pixelRow.Length allows the JIT to optimize away bounds checks:
					for (int x = 0; x < pixelRow.Length; x++)
					{
						// Get a reference to the pixel at position x
						ref object pixel = ref pixelRow[x];
						data[(curPixel * 3) + 0] = pixel.R;
						data[(curPixel * 3) + 1] = pixel.G;
						data[(curPixel * 3) + 2] = pixel.B;

						curPixel++;
					}
				}
			});

			return (data, new Vector2(image.Width, image.Height));
		}
	}
