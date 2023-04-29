using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConvert.Lib
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
			if (shader.fogParms != null)
				CreateFogVMT(texture, shader);
			else if (shader.skyParms != null && !string.IsNullOrEmpty(shader.skyParms.outerBox))
				CreateSkyboxVMT(shader);
			else if (shader.GetImageStages().Any(x => !string.IsNullOrEmpty(x.bundles[0].images[0])))
				CreateBaseShaderVMT(texture, shader);
		}

		private void CreateFogVMT(string texture, Shader shader)
		{
			var fogVmt = GenerateFogVMT(shader);
			WriteVMT(texture, fogVmt);
		}

		private string GenerateFogVMT(Shader shader)
		{
			var fogParms = shader.fogParms;
			var fogColor = $"{fogParms.color.X * 255} {fogParms.color.Y * 255} {fogParms.color.Z * 255}";

			return $$"""
					Water
					{
						$forceexpensive 1

						%tooltexture "dev/water_normal"

						$refracttexture "_rt_WaterRefraction"
						$refractamount 0

						$scale "[1 1]"
	
						$bottommaterial "dev/dev_water3_beneath"

						$normalmap "dev/bump_normal"

						%compilewater 1
						$surfaceprop "water"

						$fogenable 1
						$fogcolor "{{fogColor}}"

						$fogstart 0
						$fogend {{fogParms.depthForOpaque}}

						$abovewater 1	
					}
					""";
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
			var images = shader.GetImageStages().SelectMany(x => x.bundles[0].images);
			foreach (var image in images)
			{
				if (string.IsNullOrEmpty(image))
					continue;
				
				var baseTexture = Path.ChangeExtension(image, null);
				TryCopyQ3Content(baseTexture);
			}

			var shaderVmt = GenerateVMT(shader);
			WriteVMT(texture, shaderVmt);
		}

		private string GenerateVMT(Shader shader)
		{
			if (shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP))
			{
				if (shader.GetImageStages().Count() <= 1)
					return GenerateUnlitVMT(shader);
				else
					return GenerateUnlitTwoTextureVMT(shader);
			}
			
			return GenerateLitVMT(shader);
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

		private string GenerateUnlitVMT(Shader shader)
		{
			var sb = new StringBuilder();
			sb.AppendLine("UnlitGeneric");
			sb.AppendLine("{");

			AppendShaderParameters(sb, shader);

			sb.AppendLine("}");

			return sb.ToString();
		}

		private string GenerateLitVMT(Shader shader)
		{
			var sb = new StringBuilder();
			sb.AppendLine("LightmappedGeneric");
			sb.AppendLine("{");

			AppendShaderParameters(sb, shader);

			sb.AppendLine("}");

			return sb.ToString();
		}

		private void AppendShaderParameters(StringBuilder sb, Shader shader)
		{
			var stages = shader.GetImageStages();
			var textureStage = stages.FirstOrDefault(x => x.bundles[0].tcGen != TexCoordGen.TCGEN_ENVIRONMENT_MAPPED);
			if (textureStage != null)
			{
				var texture = Path.ChangeExtension(textureStage.bundles[0].images[0], null);
				sb.AppendLine($"\t$basetexture \"{texture}\"");
			}
			
			var envMapStage = stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED);
			if (envMapStage != null)
				sb.AppendLine($"\t$envmap \"engine/defaultcubemap\"");

			if (shader.cullType == CullType.TWO_SIDED)
				sb.AppendLine("\t$nocull 1");
			
			var flags = (textureStage?.flags ?? 0) | (envMapStage?.flags ?? 0);
			if (flags.HasFlag(ShaderStageFlags.GLS_ATEST_GE_80))
			{
				sb.AppendLine("\t$alphatest 1");
				sb.AppendLine("\t$alphatestreference 0.5");
			}
			else if (shader.contents.HasFlag(Q3ContentsFlags.CONTENTS_TRANSLUCENT))
				sb.AppendLine("\t$translucent 1");

			if (flags.HasFlag(ShaderStageFlags.GLS_SRCBLEND_ONE | ShaderStageFlags.GLS_DSTBLEND_ONE))
				sb.AppendLine("\t$additive 1");
		}

		private string GenerateUnlitTwoTextureVMT(Shader shader)
		{
			var sb = new StringBuilder();
			sb.AppendLine("UnlitTwoTexture");
			sb.AppendLine("{");

			AppendShaderParametersTwoTexture(shader, sb);

			sb.AppendLine("}");

			return sb.ToString();
		}

		private void AppendShaderParametersTwoTexture(Shader shader, StringBuilder sb)
		{
			var stages = shader.GetImageStages();
			var textureStages = stages.Where(x => x.bundles[0].tcGen != TexCoordGen.TCGEN_ENVIRONMENT_MAPPED).ToList();
			if (textureStages.Count > 0)
			{
				var texture = Path.ChangeExtension(textureStages[0].bundles[0].images[0], null);
				sb.AppendLine($"\t$basetexture \"{texture}\"");
			}

			if (textureStages.Count > 1)
			{
				var texture = Path.ChangeExtension(textureStages[1].bundles[0].images[0], null);
				sb.AppendLine($"\t$texture2 \"{texture}\"");
			}

			var envMapStage = stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED);
			if (envMapStage != null)
				sb.AppendLine($"\t$envmap \"engine/defaultcubemap\"");

			if (shader.cullType == CullType.TWO_SIDED)
				sb.AppendLine("\t$nocull 1");

			if (stages.Any(x => x.flags.HasFlag(ShaderStageFlags.GLS_ATEST_GE_80)))
			{
				sb.AppendLine("\t$alphatest 1");
				sb.AppendLine("\t$alphatestreference 0.5");
			}
			else if (shader.contents.HasFlag(Q3ContentsFlags.CONTENTS_TRANSLUCENT))
				sb.AppendLine("\t$translucent 1");

			if (stages.Any(x => x.flags.HasFlag(ShaderStageFlags.GLS_SRCBLEND_ONE | ShaderStageFlags.GLS_DSTBLEND_ONE)))
				sb.AppendLine("\t$additive 1");
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

		private void CreateDefaultVMT(string texture)
		{
			TryCopyQ3Content(texture);

			var vmt = GenerateDefaultLitVMT(texture);
			WriteVMT(texture, vmt);
		}

		private string GenerateDefaultLitVMT(string texture)
		{
			return $$"""
				LightmappedGeneric
				{
					$basetexture "{{texture}}"
				}
				""";
		}
	}
}
