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

		public TextureConverter(string pk3Dir, BSP bsp)
		{
			this.pk3Dir = pk3Dir;
			this.bsp = bsp;
		}

		public TextureConverter(string pk3Dir, string outputDir)
		{
			this.pk3Dir = pk3Dir;
			this.outputDir = outputDir;
		}

		public void Convert()
        {
            var options = new VTF.CreationOptions
            {
                OutputFormat = ImageFormat.STRATA_BC7,
                WidthResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_SMALLER,
                HeightResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_SMALLER,
                ComputeMips = 1,
                ComputeThumbnail = 1,
                ComputeReflectivity = 1,
                ComputeTransparencyFlags = 1,
            };

            string[] supportedExtensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp", ".exr", ".hdr"];

            foreach (var inputPath in Directory.EnumerateFiles(pk3Dir, "*", SearchOption.AllDirectories))
            {
                if (!supportedExtensions.Contains(Path.GetExtension(inputPath), StringComparer.OrdinalIgnoreCase))
                    continue;

                var outputPath = Path.Combine
                (
                    Path.GetDirectoryName(inputPath)!,
                    Path.GetFileNameWithoutExtension(inputPath) + ".vtf"
                );

                bool success = VTF.Create(inputPath, outputPath, options);
                if (!success)
                    Console.WriteLine($"Failed to convert: {inputPath}");
            }

            OnFinishedConvertingTextures();
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
