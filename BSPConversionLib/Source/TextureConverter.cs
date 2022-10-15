using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class TextureConverter
	{
		private string textureDir;
		private string outputDir;
		private ILogger logger;

		public TextureConverter(string pk3Dir, string outputDir, ILogger logger)
		{
			textureDir = Path.Combine(pk3Dir, "textures");
			this.outputDir = outputDir;
			this.logger = logger;
		}

		public void Convert()
		{
			var startInfo = new ProcessStartInfo();
			startInfo.FileName = "Dependencies\\VTFCmd.exe";
			startInfo.Arguments = $"-folder {textureDir}\\*.* -recurse -silent";

			var process = Process.Start(startInfo);
			process.EnableRaisingEvents = true;
			process.Exited += (x, y) => OnFinishedConvertingTextures();
			
			process.WaitForExit();
		}

		private void OnFinishedConvertingTextures()
		{
			// TODO: Add an option to pack vtfs/vmts into bsp file

			// Move vtf files into output directory and generate vmts
			var vtfFiles = Directory.GetFiles(textureDir, "*.vtf", SearchOption.AllDirectories);
			foreach (var vtfFile in vtfFiles)
			{
				var materialDir = Path.Combine(outputDir, "materials");
				var destPath = vtfFile.Replace(textureDir, materialDir);
				ConvertVTFFile(vtfFile, destPath);

				var vmtPath = Path.ChangeExtension(destPath, ".vmt");
				ConvertVMTFile(vmtPath);
			}
		}

		private void ConvertVTFFile(string vtfFile, string destPath)
		{
			var vtfDir = Path.GetDirectoryName(destPath);
			if (!Directory.Exists(vtfDir))
				Directory.CreateDirectory(vtfDir);

			// Delete existing file if it exists
			if (File.Exists(destPath))
				File.Delete(destPath);
			
			File.Move(vtfFile, destPath);

			logger.Log("Converted VTF file: " + destPath);
		}

		private void ConvertVMTFile(string vmtPath)
		{
			var vmtFile = File.CreateText(vmtPath);
			vmtFile.WriteLine("LightmappedGeneric");
			vmtFile.WriteLine("{");

			var relativePath = GetRelativePath(vmtPath);
			vmtFile.WriteLine($"\t\"$basetexture\" \"{relativePath}\"");

			vmtFile.WriteLine("}");
			vmtFile.Close();

			logger.Log("Converted VMT file: " + vmtPath);
		}

		private string GetRelativePath(string vmtPath)
		{
			var materialFolder = "materials" + Path.DirectorySeparatorChar;
			return vmtPath.Substring(vmtPath.LastIndexOf(materialFolder) + materialFolder.Length);
		}
	}
}
