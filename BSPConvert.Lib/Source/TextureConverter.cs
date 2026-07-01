using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using LibBSP;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives;
using sourcepp.vtfpp;

namespace BSPConvert.Lib
{
	public class TextureConverter
	{
		private string pk3Dir;
		private BSP bsp;
		private string outputDir;
		private Dictionary<string, Shader> shaderDict;

		public TextureConverter(string pk3Dir, BSP bsp, Dictionary<string, Shader> shaderDict)
		{
			this.pk3Dir = pk3Dir;
			this.bsp = bsp;
			this.shaderDict = shaderDict;
		}

		public TextureConverter(string pk3Dir, string outputDir, Dictionary<string, Shader> shaderDict)
		{
			this.pk3Dir = pk3Dir;
			this.outputDir = outputDir;
			this.shaderDict = shaderDict;
		}

		public void Convert()
        {
            var options = new VTF.CreationOptions
            {
                Version = 6,
                CompressionLevel = 0,
                OutputFormat = ImageFormat.STRATA_BC7,
                WidthResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_SMALLER,
                HeightResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_SMALLER,
                ComputeMips = 1,
                ComputeThumbnail = 1,
                ComputeReflectivity = 1,
                ComputeTransparencyFlags = 1,
            };

            string[] supportedExtensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp", ".exr", ".hdr"];

            var clampedTextures = GetClampedTextures();
            var reverseAlphaChrome = ReverseAlphaChromeTextures.Analyze(shaderDict.Values);

            foreach (var inputPath in Directory.EnumerateFiles(pk3Dir, "*", SearchOption.AllDirectories))
            {
                if (!supportedExtensions.Contains(Path.GetExtension(inputPath), StringComparer.OrdinalIgnoreCase))
                    continue;

                var outputPath = Path.Combine
                (
                    Path.GetDirectoryName(inputPath)!,
                    Path.GetFileNameWithoutExtension(inputPath) + ".vtf"
                );

                // CreationOptions is a struct, so this copy lets us bake per-texture flags without
                // mutating the shared base options. "clampmap" shader stages need clamped (non-repeating)
                // texture coordinates, which in Source is a VTF flag (TEXTUREFLAGS_CLAMPS/T) rather than a VMT parameter.
                // Skybox faces (moved under skybox/ by MaterialConverter) must also be clamped, otherwise
                // bilinear filtering samples the wrapped-around opposite edge and produces seams between faces.
                var relativeTexturePath = GetRelativeTexturePath(inputPath);
                var textureOptions = options;
                if (clampedTextures.Contains(relativeTexturePath) || IsSkyboxTexture(relativeTexturePath))
                    textureOptions.VTFFlags |= VTF.Flags.V0_CLAMP_S | VTF.Flags.V0_CLAMP_T;

                // Reverse-alpha Q3 chrome diffuse textures (drawn "blendFunc GL_ONE_MINUS_SRC_ALPHA GL_SRC_ALPHA"
                // over a reflection) reveal the reflection where alpha is high. Source's $basealphaenvmapmask only
                // masks by (1 - alpha), so bake the alpha inverted - then (1 - invertedAlpha) == alpha gives the
                // correct mask without a custom shader param. When the texture is used only as a chrome base the
                // inversion goes in place (its own VTF, no orphan); when it's also used non-inverted elsewhere the
                // normal VTF is kept and the inverted copy gets a distinct suffixed name.
                bool success;
                if (reverseAlphaChrome.NeedsInversion(relativeTexturePath))
                {
                    if (reverseAlphaChrome.IsSharedWithNormalUse(relativeTexturePath))
                    {
                        success = VTF.Create(inputPath, outputPath, textureOptions);
                        var invertedOutputPath = Path.Combine
                        (
                            Path.GetDirectoryName(inputPath)!,
                            Path.GetFileNameWithoutExtension(inputPath) + ReverseAlphaChromeTextures.InvertedAlphaSuffix + ".vtf"
                        );
                        success &= CreateAlphaInvertedVTF(inputPath, invertedOutputPath, textureOptions);
                    }
                    else
                    {
                        success = CreateAlphaInvertedVTF(inputPath, outputPath, textureOptions);
                    }
                }
                else
                {
                    success = VTF.Create(inputPath, outputPath, textureOptions);
                }

                if (!success)
                    Console.WriteLine($"Failed to convert: {inputPath}");
            }

            OnFinishedConvertingTextures();
        }

        // Collects the texture paths used by "clampmap" shader stages, so their VTFs can be baked with
        // clamped (non-repeating) wrap flags. Paths are normalized (no extension, forward slashes) to
        // match the relative paths derived from files on disk in GetRelativeTexturePath.
        private HashSet<string> GetClampedTextures()
        {
            var clampedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shader in shaderDict.Values)
            {
                if (shader.stages == null)
                    continue;

                foreach (var stage in shader.stages)
                {
                    foreach (var bundle in stage.bundles)
                    {
                        if (!bundle.clamp)
                            continue;

                        foreach (var image in bundle.images)
                        {
                            if (!string.IsNullOrEmpty(image))
                                clampedTextures.Add(Path.ChangeExtension(image, null).Replace('\\', '/'));
                        }
                    }
                }
            }

            return clampedTextures;
        }

        // Bakes a VTF from an image file with its alpha channel inverted. Decodes the source to raw pixels
        // once (so BC7 compression only happens at bake time, avoiding a double-compression quality loss),
        // normalizes to RGBA8888 so alpha sits at a known byte offset, flips it, then bakes with the same
        // options as a normal conversion.
        private static bool CreateAlphaInvertedVTF(string inputPath, string outputPath, VTF.CreationOptions options)
        {
            var fileBytes = File.ReadAllBytes(inputPath);

            var format = ImageFormat.RGBA8888;
            int width = 0, height = 0, frameCount = 0;
            var pixels = ImageConversion.ConvertImageDataToFile(fileBytes, ref format, ref width, ref height, ref frameCount);
            if (pixels == null || pixels.Length == 0)
                return false;

            if (format != ImageFormat.RGBA8888)
            {
                pixels = ImageConversion.ConvertImageDataToFormat(pixels, format, ImageFormat.RGBA8888, (ushort)width, (ushort)height);
                format = ImageFormat.RGBA8888;
            }

            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = (byte)(255 - pixels[i]);

            return VTF.Create(pixels, ImageFormat.RGBA8888, (ushort)width, (ushort)height, outputPath, options);
        }

        // Converts a file path under pk3Dir into the shader-relative texture path (no extension,
        // forward slashes) so it can be matched against shader-referenced texture names.
        private string GetRelativeTexturePath(string filePath)
        {
            var relative = filePath
                .Replace(pk3Dir + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
                .Replace(Path.DirectorySeparatorChar, '/');

            return Path.ChangeExtension(relative, null);
        }

        // Skybox face images are relocated under skybox/ by MaterialConverter (image-box skies) and
        // CloudSkyboxBaker (baked cloud skies); both need clamped wrap flags to avoid edge seams.
        private static bool IsSkyboxTexture(string relativeTexturePath)
        {
            return relativeTexturePath.StartsWith("skybox/", StringComparison.OrdinalIgnoreCase);
        }

		private void OnFinishedConvertingTextures()
		{
			// TODO: Find textures using shader texture paths
			var vtfFiles = Directory.GetFiles(pk3Dir, "*.vtf", SearchOption.AllDirectories);
			var vmtFiles = Directory.GetFiles(pk3Dir, "*.vmt", SearchOption.AllDirectories);
			var textureFiles = vtfFiles.Concat(vmtFiles);

			if (bsp != null)
				EmbedFiles(textureFiles);
			else
				MoveFilesToOutputDir(textureFiles);
		}

		// Embed vtf/vmt files into BSP pak lump
		private void EmbedFiles(IEnumerable<string> textureFiles)
		{
			using (var archive = bsp.PakFile.GetZipArchive())
			{
				foreach (var file in textureFiles)
				{
					var newPath = file.Replace(pk3Dir, "materials", StringComparison.OrdinalIgnoreCase);
					archive.AddEntry(newPath, new FileInfo(file));
				}

				bsp.PakFile.SetZipArchive(archive, true);
			}
		}

		// Move vtf/vmt files into output directory
		private void MoveFilesToOutputDir(IEnumerable<string> textureFiles)
		{
			foreach (var file in textureFiles)
			{
				var materialDir = Path.Combine(outputDir, "materials");
				var newPath = file.Replace(pk3Dir, materialDir, StringComparison.OrdinalIgnoreCase);
				FileUtil.MoveFile(file, newPath);
			}
		}
	}
}
