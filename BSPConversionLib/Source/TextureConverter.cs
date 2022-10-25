using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using LibBSP;
using SharpCompress.Archives.Zip;

namespace BSPConversionLib
{
	public class TextureConverter
	{
		private string textureDir;
		private BSP bsp;
		private string outputDir;
		private ILogger logger;

		public TextureConverter(string pk3Dir, BSP bsp, ILogger logger)
		{
			textureDir = Path.Combine(pk3Dir, "textures");
			this.bsp = bsp;
			this.logger = logger;
		}

		public TextureConverter(string pk3Dir, string outputDir, ILogger logger)
		{
			textureDir = Path.Combine(pk3Dir, "textures");
			this.outputDir = outputDir;
			this.logger = logger;
		}

		public void Convert()
		{
			if (!Directory.Exists(textureDir))
			{
				logger.Log("No textures directory found, skipping texture conversion.");
				return;
			}

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
			var vtfFiles = Directory.GetFiles(textureDir, "*.vtf", SearchOption.AllDirectories);

			if (bsp != null)
			{
				var archive = ZipArchive.Create();

				// Generate vtf/vmt files in BSP pak lump
				foreach (var vtfFile in vtfFiles)
				{
					var pakVtfPath = vtfFile.Replace(textureDir, "materials");
					archive.AddEntry(pakVtfPath, File.OpenRead(vtfFile));

					var pakVmtPath = pakVtfPath.Replace(".vtf", ".vmt");
					var vmt = GenerateVMT(pakVtfPath);
					var vmtBytes = Encoding.UTF8.GetBytes(vmt);
					archive.AddEntry(pakVmtPath, new MemoryStream(vmtBytes));
				}

				bsp.PakFile.SetZipArchive(archive, true);
			}
			else
			{
				// Move vtf files into output directory and generate vmts
				foreach (var vtfFile in vtfFiles)
				{
					var materialDir = Path.Combine(outputDir, "materials");
					var destPath = vtfFile.Replace(textureDir, materialDir);
					ConvertVTFFile(vtfFile, destPath);

					var vmtPath = Path.ChangeExtension(destPath, ".vmt");
					ConvertVMTFile(vmtPath);
				}
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
			var vmt = GenerateVMT(vmtPath);
			File.WriteAllText(vmtPath, vmt);

			logger.Log("Converted VMT file: " + vmtPath);
		}

		private string GenerateVMT(string vmtPath)
		{
			var sb = new StringBuilder();
			sb.AppendLine("LightmappedGeneric");
			sb.AppendLine("{");

			var relativePath = GetRelativePath(vmtPath);
			sb.AppendLine($"\t\"$basetexture\" \"{relativePath}\"");

			sb.AppendLine("}");

			return sb.ToString();
		}

		private string GetRelativePath(string vmtPath)
		{
			var materialFolder = "materials" + Path.DirectorySeparatorChar;
			return vmtPath.Substring(vmtPath.LastIndexOf(materialFolder) + materialFolder.Length);
		}
	}
}
