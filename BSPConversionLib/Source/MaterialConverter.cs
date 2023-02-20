using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class MaterialConverter
	{
		private string pk3Dir;
		private Dictionary<string, Shader> shaderDict;
		private Dictionary<string, string> pk3ImageDict;
		private Dictionary<string, string> q3ImageDict;

		private string[] skySuffixes =
		{
			"bk",
			"dn",
			"ft",
			"lf",
			"rt",
			"up"
		};

		public MaterialConverter(string pk3Dir, Dictionary<string, Shader> shaderDict)
		{
			this.pk3Dir = pk3Dir;
			this.shaderDict = shaderDict;
			pk3ImageDict = GetImageLookupDictionary(pk3Dir);
			q3ImageDict = GetImageLookupDictionary(ContentManager.GetQ3ContentDir());
		}

		// Create a dictionary that maps relative texture paths to the full file paths in the content folder
		private Dictionary<string, string> GetImageLookupDictionary(string contentDir)
		{
			var imageDict = new Dictionary<string, string>();

			foreach (var file in Directory.GetFiles(contentDir, "*.*", SearchOption.AllDirectories))
			{
				var ext = Path.GetExtension(file);
				if (ext == ".tga" || ext == ".jpg")
				{
					var texturePath = file.Replace(contentDir + Path.DirectorySeparatorChar, "")
						.Replace(Path.DirectorySeparatorChar, '/').Replace(ext, "");

					if (!imageDict.ContainsKey(texturePath))
						imageDict.Add(texturePath, file);
				}
			}

			return imageDict;
		}

		public void Convert(string texture)
		{
			if (shaderDict.TryGetValue(texture, out var shader))
				CreateShaderVMT(texture, shader);
			else
				CreateDefaultVMT(texture);
		}

		private void CreateShaderVMT(string texture, Shader shader)
		{
			if (shader.skyParms != null && !string.IsNullOrEmpty(shader.skyParms.outerBox))
				CreateSkyboxVMT(shader);
			else if (!string.IsNullOrEmpty(shader.map))
				CreateBaseShaderVMT(texture, shader);
		}

		private void CreateSkyboxVMT(Shader shader)
		{
			foreach (var suffix in skySuffixes)
			{
				var skyTexture = $"{shader.skyParms.outerBox}_{suffix}";
				if (!PrepareSkyboxImage(skyTexture))
					continue;

				var baseTexture = $"skybox/{shader.skyParms.outerBox}{suffix}";
				var skyboxVmt = GenerateSkyboxVMT(baseTexture);
				WriteVMT(baseTexture, skyboxVmt);
			}
		}

		// Try to find the sky image file and move it to skybox folder in order for Source engine to detect it properly
		private bool PrepareSkyboxImage(string skyTexture)
		{
			var skyboxDir = Path.Combine(pk3Dir, "skybox");
			if (pk3ImageDict.TryGetValue(skyTexture, out var pk3Path))
			{
				var newPath = pk3Path.Replace(pk3Dir, skyboxDir);
				var destFile = newPath.Remove(newPath.LastIndexOf('_'), 1); // Remove underscore from skybox suffix
				
				FileUtil.MoveFile(pk3Path, destFile);

				return true;
			}
			else if (q3ImageDict.TryGetValue(skyTexture, out var q3Path))
			{
				var q3ContentDir = ContentManager.GetQ3ContentDir();
				var newPath = q3Path.Replace(q3ContentDir, skyboxDir);
				var destFile = newPath.Remove(newPath.LastIndexOf('_'), 1); // Remove underscore from skybox suffix
				
				FileUtil.CopyFile(q3Path, destFile);

				return true;
			}

			return false; // No sky image found
		}

		private void CreateBaseShaderVMT(string texture, Shader shader)
		{
			var baseTexture = Path.ChangeExtension(shader.map, null);
			TryCopyQ3Content(baseTexture);

			var shaderVmt = GenerateVMT(shader, baseTexture);
			WriteVMT(texture, shaderVmt);
		}

		private string GenerateVMT(Shader shader, string baseTexture)
		{
			if (shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP))
				return GenerateUnlitVMT(baseTexture, shader);
			
			return GenerateLitVMT(baseTexture, shader);
		}

		private void CreateDefaultVMT(string texture)
		{
			TryCopyQ3Content(texture);

			var vmt = GenerateLitVMT(texture, null);
			WriteVMT(texture, vmt);
		}

		private void WriteVMT(string texture, string vmt)
		{
			var vmtPath = Path.Combine(pk3Dir, $"{texture}.vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath));

			File.WriteAllText(vmtPath, vmt);
		}

		// Copies content from the Q3Content folder if it's referenced by this shader
		private void TryCopyQ3Content(string shaderTexturePath)
		{
			if (q3ImageDict.TryGetValue(shaderTexturePath, out var q3TexturePath))
			{
				var q3ContentDir = ContentManager.GetQ3ContentDir();
				var newPath = q3TexturePath.Replace(q3ContentDir, pk3Dir);
				FileUtil.CopyFile(q3TexturePath, newPath);
			}
		}

		private string GenerateLitVMT(string baseTexture, Shader shader)
		{
			var sb = new StringBuilder();
			sb.AppendLine("LightmappedGeneric");
			sb.AppendLine("{");

			sb.AppendLine($"\t$basetexture \"{baseTexture}\"");
			if (shader != null)
				AppendShaderParameters(sb, shader);

			sb.AppendLine("}");

			return sb.ToString();
		}

		private string GenerateUnlitVMT(string baseTexture, Shader shader)
		{
			var sb = new StringBuilder();
			sb.AppendLine("UnlitGeneric");
			sb.AppendLine("{");

			sb.AppendLine($"\t$basetexture \"{baseTexture}\"");
			if (shader != null)
				AppendShaderParameters(sb, shader);

			sb.AppendLine("}");

			return sb.ToString();
		}
		
		private void AppendShaderParameters(StringBuilder sb, Shader shader)
		{
			if (shader.alphaFunc == AlphaFunc.GLS_ATEST_GE_80)
			{
				sb.AppendLine("\t$alphatest 1");
				sb.AppendLine("\t$alphatestreference 0.5");
			}
			else if (shader.contents.HasFlag(Q3ContentsFlags.CONTENTS_TRANSLUCENT))
				sb.AppendLine("\t$translucent 1");
		}

		private string GenerateSkyboxVMT(string baseTexture)
		{
			var sb = new StringBuilder();
			sb.AppendLine("UnlitGeneric");
			sb.AppendLine("{");

			sb.AppendLine($"\t\"$basetexture\" \"{baseTexture}\"");
			sb.AppendLine("\t\"$nofog\" 1");
			sb.AppendLine("\t\"$ignorez\" 1");

			sb.AppendLine("}");

			return sb.ToString();
		}
	}
}
