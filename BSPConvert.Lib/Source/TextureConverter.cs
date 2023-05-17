using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using LibBSP;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives;

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
			var startInfo = new ProcessStartInfo();
			startInfo.FileName = "Dependencies\\VTFCmd.exe";
			startInfo.Arguments = $"-folder {pk3Dir}\\*.* -resize -recurse -silent";

			var process = Process.Start(startInfo);
			process.EnableRaisingEvents = true;
			process.Exited += (x, y) => OnFinishedConvertingTextures();

			process.WaitForExit();
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
					var newPath = file.Replace(pk3Dir, "materials");
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
				var newPath = file.Replace(pk3Dir, materialDir);
				FileUtil.MoveFile(file, newPath);
			}
		}
	}
}
