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
						.Replace(Path.DirectorySeparatorChar, '/').Replace(ext, "").ToLower();

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
				return GenerateUnlitVMT(shader);
			else
				return GenerateLitVMT(shader);
		}

		private void WriteVMT(string texture, string vmt)
		{
			var vmtPath = Path.Combine(pk3Dir, $"{texture}.vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath));

			File.WriteAllText(vmtPath, vmt);
		}

		// Copies content from the Q3Content folder if it exists
		private void TryCopyQ3Content(string texturePath)
		{
			if (q3ImageDict.TryGetValue(texturePath, out var q3TexturePath))
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
			var textureStage = stages.FirstOrDefault(x => x.bundles[0].tcGen != TexCoordGen.TCGEN_ENVIRONMENT_MAPPED && x.bundles[0].tcGen != TexCoordGen.TCGEN_LIGHTMAP);
			if (textureStage != null)
			{
				var texture = Path.ChangeExtension(textureStage.bundles[0].images[0], null);
				sb.AppendLine($"\t$basetexture \"{texture}\"");

				if (textureStage.rgbGen.HasFlag(ColorGen.CGEN_CONST))
				{
					var color = textureStage.constantColor;
					var colorStr = $"{color[0]} {color[1]} {color[2]}";
					sb.AppendLine("\t$color \"{" + colorStr + "}\"");
				}

				if (textureStage.alphaGen.HasFlag(AlphaGen.AGEN_CONST))
					sb.AppendLine("\t$alpha 0.25"); // TODO: Convert actual alpha value from AGEN_CONST
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

			var firstImageStage = stages.FirstOrDefault();
			if (firstImageStage != null)
			{
				if (firstImageStage.flags.HasFlag(ShaderStageFlags.GLS_SRCBLEND_ONE | ShaderStageFlags.GLS_DSTBLEND_ONE)) // blendFunc add
					sb.AppendLine("\t$additive 1");

				if (firstImageStage.flags.HasFlag(ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA | ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA)) // blendFunc blend
					sb.AppendLine("\t$translucent 1");
			}

			if (textureStage != null && textureStage.bundles[0].texMods.Any(y => y.type == TexMod.TMOD_SCROLL || y.type == TexMod.TMOD_ROTATE ||
				y.type == TexMod.TMOD_STRETCH || y.type == TexMod.TMOD_SCALE))
				ConvertTexMods(sb, textureStage);
		}

		private void ConvertTexMods(StringBuilder sb, ShaderStage texModStage)
		{
			AppendProxyVars(sb, texModStage);

			foreach (var texModInfo in texModStage.bundles[0].texMods)
			{
				if (texModInfo.type == TexMod.TMOD_ROTATE)
					ConvertTexModRotate(sb, texModInfo);
				else if (texModInfo.type == TexMod.TMOD_SCROLL)
					ConvertTexModScroll(sb, texModInfo);
				else if (texModInfo.type == TexMod.TMOD_STRETCH)
					ConvertTexModStretch(sb, texModInfo);

				if (texModInfo.type == TexMod.TMOD_ROTATE || texModInfo.type == TexMod.TMOD_SCROLL ||
					texModInfo.type == TexMod.TMOD_STRETCH || texModInfo.type == TexMod.TMOD_SCALE)
					AppendTextureTransform(sb, texModStage);
			}
			sb.AppendLine("\t}");
		}

		private static void AppendTextureTransform(StringBuilder sb, ShaderStage texModStage)
		{
			sb.AppendLine("\t\tTextureTransform");
			sb.AppendLine("\t\t{");

			foreach (var texModInfo in texModStage.bundles[0].texMods)
			{
				if (texModInfo.type == TexMod.TMOD_ROTATE)
				{
					sb.AppendLine("\t\t\trotateVar $angle");
					sb.AppendLine("\t\t\tcenterVar $center");
				}
				else if (texModInfo.type == TexMod.TMOD_SCROLL)
					sb.AppendLine("\t\t\ttranslateVar $translate");
				else if (texModInfo.type == TexMod.TMOD_STRETCH || texModInfo.type == TexMod.TMOD_SCALE)
					sb.AppendLine("\t\t\tscaleVar $scale");
			}
			
			sb.AppendLine("\t\t\tinitialValue 0");
			sb.AppendLine("\t\t\tresultVar $basetexturetransform");
			sb.AppendLine("\t\t}");
		}

		private static void AppendProxyVars(StringBuilder sb, ShaderStage texModStage)
		{
			foreach (var texModInfo in texModStage.bundles[0].texMods)
			{
				if (texModInfo.type == TexMod.TMOD_ROTATE)
				{
					sb.AppendLine("\t$angle 0.0");
					sb.AppendLine("\t$center \"[0.5 0.5]\"");
				}
				else if (texModInfo.type == TexMod.TMOD_SCROLL)
					sb.AppendLine("\t$translate \"[0.0 0.0]\"");
				else if (texModInfo.type == TexMod.TMOD_SCALE)
					sb.AppendLine($"\t$scale \"[{texModInfo.scale[0]} {texModInfo.scale[1]}]\"");
				else if (texModInfo.type == TexMod.TMOD_STRETCH)
					sb.AppendLine("\t$scale 1");

				if (texModInfo.wave.func == GenFunc.GF_SQUARE)
				{
					sb.AppendLine($"\t$min {texModInfo.wave.base_}");
					sb.AppendLine($"\t$max {texModInfo.wave.amplitude}");
					sb.AppendLine($"\t$mid {(texModInfo.wave.amplitude + texModInfo.wave.base_) / 2}");
				}
			}

			sb.AppendLine("\tProxies");
			sb.AppendLine("\t{");
		}

		// TODO: Convert other waveforms
		private static void ConvertTexModStretch(StringBuilder sb, TexModInfo texModInfo)
		{
			switch (texModInfo.wave.func)
			{
				case GenFunc.GF_SIN:
					ConvertSineWaveStretch(sb, texModInfo);
					break;
				case GenFunc.GF_SQUARE:
					ConvertSquareWaveStretch(sb, texModInfo);
					break;
				case GenFunc.GF_SAWTOOTH:
				case GenFunc.GF_INVERSE_SAWTOOTH:
					break;
			}
		}

		private static void ConvertSineWaveStretch(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tSine");
			sb.AppendLine("\t\t{");
			sb.AppendLine($"\t\t\tsinemin {texModInfo.wave.base_}");
			sb.AppendLine($"\t\t\tsinemax {texModInfo.wave.amplitude}");
			sb.AppendLine($"\t\t\tsineperiod {1 / texModInfo.wave.frequency}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar $scale");
			sb.AppendLine("\t\t}");
		}

		private static void ConvertSquareWaveStretch(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tSine");
			sb.AppendLine("\t\t{");
			sb.AppendLine($"\t\t\tsinemin {texModInfo.wave.base_}");
			sb.AppendLine($"\t\t\tsinemax {texModInfo.wave.amplitude}");
			sb.AppendLine($"\t\t\tsineperiod {1 / texModInfo.wave.frequency}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar $sineOutput");
			sb.AppendLine("\t\t}");

			sb.AppendLine("\t\tLessOrEqual");
			sb.AppendLine("\t\t{");
			sb.AppendLine($"\t\t\tlessEqualVar $min");
			sb.AppendLine($"\t\t\tgreaterVar $max");
			sb.AppendLine($"\t\t\tsrcVar1 $sineOutput");
			sb.AppendLine($"\t\t\tsrcVar2 $mid");
			sb.AppendLine($"\t\t\tresultVar $scale");
			sb.AppendLine("\t\t}");
		}

		private static void ConvertTexModScroll(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tLinearRamp");
			sb.AppendLine("\t\t{");
			sb.AppendLine($"\t\t\trate {texModInfo.scroll[0]}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar \"$translate[0]\"");
			sb.AppendLine("\t\t}");

			sb.AppendLine("\t\tLinearRamp");
			sb.AppendLine("\t\t{");
			sb.AppendLine($"\t\t\trate {texModInfo.scroll[1]}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar \"$translate[1]\"");
			sb.AppendLine("\t\t}");
		}

		private static void ConvertTexModRotate(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tLinearRamp");
			sb.AppendLine("\t\t{");
			sb.AppendLine($"\t\t\trate {texModInfo.rotateSpeed}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar $angle");
			sb.AppendLine("\t\t}");
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
