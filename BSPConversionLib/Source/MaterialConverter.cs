using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib.Source
{
	public class MaterialConverter
	{
		private string pk3Dir;
		private Dictionary<string, Shader> shaderDict;
		private HashSet<string> skyboxTextures;

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
			skyboxTextures = FindSkyboxTextures();
		}

		private HashSet<string> FindSkyboxTextures()
		{
			var skyboxTextures = new HashSet<string>();

			foreach (var shader in shaderDict.Values)
			{
				if (shader.skyParms != null && !string.IsNullOrEmpty(shader.skyParms.outerBox))
				{
					foreach (var suffix in skySuffixes)
					{
						var texture = $"skybox/{shader.skyParms.outerBox}{suffix}";
						skyboxTextures.Add(texture);
					}

					break;
				}
			}

			return skyboxTextures;
		}

		public string ConvertVMT(string vtfFile)
		{
			var relativePath = GetRelativePath(vtfFile);
			if (skyboxTextures.Contains(relativePath))
				return GenerateSkyboxVMT(vtfFile);

			return GenerateLitVMT(vtfFile);
		}

		private string GenerateLitVMT(string vtfFile)
		{
			var sb = new StringBuilder();
			sb.AppendLine("LightmappedGeneric");
			sb.AppendLine("{");

			var relativePath = GetRelativePath(vtfFile);
			sb.AppendLine($"\t\"$basetexture\" \"{relativePath}\"");

			sb.AppendLine("}");

			return sb.ToString();
		}

		private string GenerateSkyboxVMT(string vtfFile)
		{
			var sb = new StringBuilder();
			sb.AppendLine("UnlitGeneric");
			sb.AppendLine("{");

			var relativePath = GetRelativePath(vtfFile);
			sb.AppendLine($"\t\"$basetexture\" \"{relativePath}\"");
			sb.AppendLine("\t\"$nofog\" 1");
			sb.AppendLine("\t\"$ignorez\" 1");

			sb.AppendLine("}");

			return sb.ToString();
		}

		private string GetRelativePath(string vtfPath)
		{
			return vtfPath.Replace(pk3Dir + Path.DirectorySeparatorChar, "")
				.Replace('\\', '/').Replace(".vtf", "");
		}
	}
}
