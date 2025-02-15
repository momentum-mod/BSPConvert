namespace BSPConvert.Lib;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using LibBSP;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives;

public class TextureConverter
	{
		private readonly string pk3Dir;
		private readonly BSP bsp;
		private readonly string outputDir;

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
        var startInfo = new ProcessStartInfo
        {
            FileName = "Dependencies\\VTFCmd.exe",
            Arguments = $"-folder {pk3Dir}\\*.* -resize -recurse -silent"
        };

        var process = Process.Start(startInfo);
			process.EnableRaisingEvents = true;
			process.Exited += (x, y) => OnFinishedConvertingTextures();

			process.WaitForExit();
		}

		private void OnFinishedConvertingTextures()
		{
        // TODO: Find textures using shader texture paths
        string[] vtfFiles = Directory.GetFiles(pk3Dir, "*.vtf", SearchOption.AllDirectories);
        string[] vmtFiles = Directory.GetFiles(pk3Dir, "*.vmt", SearchOption.AllDirectories);
        IEnumerable<string> textureFiles = vtfFiles.Concat(vmtFiles);

			if (bsp != null)
				EmbedFiles(textureFiles);
			else
				MoveFilesToOutputDir(textureFiles);
		}

		// Embed vtf/vmt files into BSP pak lump
		private void EmbedFiles(IEnumerable<string> textureFiles)
		{
			using (ZipArchive archive = bsp.PakFile.GetZipArchive())
			{
				foreach (string file in textureFiles)
				{
                string newPath = file.Replace(pk3Dir, "materials");
					archive.AddEntry(newPath, new FileInfo(file));
				}

				bsp.PakFile.SetZipArchive(archive, true);
			}
		}

		// Move vtf/vmt files into output directory
		private void MoveFilesToOutputDir(IEnumerable<string> textureFiles)
		{
			foreach (string file in textureFiles)
			{
            string materialDir = Path.Combine(outputDir, "materials");
            string newPath = file.Replace(pk3Dir, materialDir);
				FileUtil.MoveFile(file, newPath);
			}
		}
	}
