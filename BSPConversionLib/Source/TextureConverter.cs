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
using BSPConversionLib.Source;
using SharpCompress.Archives;

namespace BSPConversionLib
{
	// TODO: Add support for shader conversion
	public class TextureConverter
	{
		private string pk3Dir;
		private BSP bsp;
		private string outputDir;
		private MaterialConverter materialConverter;
		private ILogger logger;

		public TextureConverter(string pk3Dir, BSP bsp, Dictionary<string, Shader> shaderDict, ILogger logger)
		{
			this.pk3Dir = pk3Dir;
			this.bsp = bsp;
			materialConverter = new MaterialConverter(pk3Dir, shaderDict);
			this.logger = logger;
		}

		public TextureConverter(string pk3Dir, string outputDir, Dictionary<string, Shader> shaderDict, ILogger logger)
		{
			this.pk3Dir = pk3Dir;
			this.outputDir = outputDir;
			materialConverter = new MaterialConverter(pk3Dir, shaderDict);
			this.logger = logger;
		}

		public void Convert()
		{
			var startInfo = new ProcessStartInfo();
			startInfo.FileName = "Dependencies\\VTFCmd.exe";
			startInfo.Arguments = $"-folder {pk3Dir}\\*.* -recurse -silent";

			var process = Process.Start(startInfo);
			process.EnableRaisingEvents = true;
			process.Exited += (x, y) => OnFinishedConvertingTextures();

			process.WaitForExit();
		}

		private void OnFinishedConvertingTextures()
		{
			// TODO: Skip skies textures, and only use the textures referenced by the skybox shader

			// Move env textures into skybox folder since Source engine only loads skybox textures from there
			// TODO: Are skybox textures always in the env folder?
			MoveEnvTexturesToSkyboxFolder();

			var vtfFiles = Directory.GetFiles(pk3Dir, "*.vtf", SearchOption.AllDirectories);
			var vmtFiles = ConvertVMTFiles(vtfFiles);

			if (bsp != null)
				EmbedFiles(vtfFiles, vmtFiles);
			else
				MoveFilesToOutputDir(vtfFiles, vmtFiles);
		}

		private void MoveEnvTexturesToSkyboxFolder()
		{
			var envDir = Path.Combine(pk3Dir, "env");
			if (!Directory.Exists(envDir))
				return;
			
			var skyboxDir = Path.Combine(pk3Dir, "skybox", "env");
			foreach (var file in Directory.GetFiles(envDir, "*.vtf", SearchOption.AllDirectories))
			{
				var newPath = file.Replace(envDir, skyboxDir);
				var destFile = newPath.Remove(newPath.LastIndexOf('_'), 1); // Remove underscore from skybox suffix

				Directory.CreateDirectory(Path.GetDirectoryName(destFile));

				File.Move(file, destFile);
			}
		}

		private string[] ConvertVMTFiles(string[] vtfFiles)
		{
			var vmtFiles = new string[vtfFiles.Length];
			for (var i = 0; i < vtfFiles.Length; i++)
			{
				var vtfFile = vtfFiles[i];

				var vmtPath = Path.ChangeExtension(vtfFile, ".vmt");
				var vmt = materialConverter.ConvertVMT(vtfFile);
				File.WriteAllText(vmtPath, vmt);

				vmtFiles[i] = vmtPath;
			}

			return vmtFiles;
		}

		// Embed vtf/vmt files into BSP pak lump
		private void EmbedFiles(string[] vtfFiles, string[] vmtFiles)
		{
			// TODO: Only create a zip archive if one doesn't exist
			using (var archive = ZipArchive.Create())
			{
				for (var i = 0; i < vtfFiles.Length; i++)
				{
					var vtfFile = vtfFiles[i];
					var vmtFile = vmtFiles[i];

					var pakVtfPath = vtfFile.Replace(pk3Dir, "materials");
					archive.AddEntry(pakVtfPath, new FileInfo(vtfFile));

					var pakVmtPath = vmtFile.Replace(pk3Dir, "materials");
					archive.AddEntry(pakVmtPath, new FileInfo(vmtFile));
				}

				bsp.PakFile.SetZipArchive(archive, true);
			}
		}

		// Move vtf/vmt files into output directory
		private void MoveFilesToOutputDir(string[] vtfFiles, string[] vmtFiles)
		{
			for (var i = 0; i < vtfFiles.Length; i++)
			{
				var vtfFile = vtfFiles[i];
				var vmtFile = vmtFiles[i];

				var materialDir = Path.Combine(outputDir, "materials");

				var destVtfFile = vtfFile.Replace(pk3Dir, materialDir);
				MoveFile(vtfFile, destVtfFile);

				var destVmtFile = vmtFile.Replace(pk3Dir, materialDir);
				MoveFile(vmtFile, destVmtFile);
			}
		}

		private void MoveFile(string sourceFileName, string destFileName)
		{
			// Create directory if it doesn't exist
			Directory.CreateDirectory(Path.GetDirectoryName(destFileName));

			// Delete file if it already exists
			if (File.Exists(destFileName))
				File.Delete(destFileName);
			
			File.Move(sourceFileName, destFileName);
		}
	}
}
