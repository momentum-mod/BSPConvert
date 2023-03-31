using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class ShaderParser
	{
		private string shaderFile;

		public ShaderParser(string shaderFile)
		{
			this.shaderFile = shaderFile;
		}

		public Dictionary<string, Shader> ParseShaders()
		{
			var shaderDict = new Dictionary<string, Shader>();

			var fileEnumerator = File.ReadLines(shaderFile).GetEnumerator();
			while (fileEnumerator.MoveNext())
			{
				var line = TrimLine(fileEnumerator.Current);
				if (string.IsNullOrEmpty(line))
					continue;

				var textureName = line;
				var shader = ParseShader(fileEnumerator);

				shaderDict[textureName] = shader;
			}

			return shaderDict;
		}

		private Shader ParseShader(IEnumerator<string> fileEnumerator)
		{
			var shader = new Shader();

			var nesting = 0;
			while (fileEnumerator.MoveNext())
			{
				var line = TrimLine(fileEnumerator.Current);
				if (string.IsNullOrEmpty(line))
					continue;

				// TODO: Parse shader passes
				// TODO: Handle nesting with recursion?
				if (line == "{")
				{
					nesting++;
					continue;
				}

				if (line == "}")
				{
					nesting--;
					if (nesting <= 0)
						break;
				}

				// Parse shader parameter
				var split = line.Split(' ');
				switch (split[0].ToLower())
				{
					case "map":
						if (string.IsNullOrEmpty(shader.map)) // Don't overwrite existing map
							shader.map = ParseMap(split[1]);
						break;
					case "surfaceparm":
						{
							var infoParm = ParseSurfaceParm(split[1]);
							shader.surfaceFlags |= infoParm.surfaceFlags;
							shader.contents |= infoParm.contents;
							break;
						}
					case "skyparms":
						shader.skyParms = ParseSkyParms(split);
						break;
					case "cull":
						shader.cullType = ParseCullType(split[1]);
						break;
					case "alphafunc":
						shader.alphaFunc = ParseAlphaFunc(split[1]);
						break;
					case "blendfunc":
						if (shader.blendFunc == 0)  // Don't overwrite existing blendFunc
							shader.blendFunc = ParseBlendFunc(split);
						break;
				}
			}

			return shader;
		}

		private static string ParseMap(string map)
		{
			if (map.StartsWith('$'))
				return string.Empty;

			return map;
		}

		private InfoParm ParseSurfaceParm(string surfaceParm)
		{
			foreach (var infoParm in Constants.infoParms)
			{
				if (surfaceParm == infoParm.name)
					return infoParm;
			}

			return default(InfoParm);
		}

		private Shader.SkyParms ParseSkyParms(string[] split)
		{
			return new Shader.SkyParms()
			{
				outerBox = split[1],
				cloudHeight = split[2],
				innerBox = split[3]
			};
		}

		private CullType ParseCullType(string cullType)
		{
			switch (cullType.ToLower())
			{
				case "none":
				case "twosided":
				case "disable":
					return CullType.TWO_SIDED;
				case "back":
				case "backside":
				case "backsided":
					return CullType.BACK_SIDED;
				default:
					return CullType.FRONT_SIDED;
			}
		}

		private AlphaFunc ParseAlphaFunc(string func)
		{
			switch (func.ToLower())
			{
				case "gt0":
					return AlphaFunc.GLS_ATEST_GT_0;
				case "lt128":
					return AlphaFunc.GLS_ATEST_LT_80;
				case "ge128":
					return AlphaFunc.GLS_ATEST_GE_80;
			}

			// Invalid alphaFunc
			return 0;
		}

		private BlendFuncFlags ParseBlendFunc(string[] split)
		{
			switch (split[1].ToLower())
			{
				case "add":
					return BlendFuncFlags.GLS_SRCBLEND_ONE | BlendFuncFlags.GLS_DSTBLEND_ONE;
				case "filter":
					return BlendFuncFlags.GLS_SRCBLEND_DST_COLOR | BlendFuncFlags.GLS_DSTBLEND_ZERO;
				case "blend":
					return BlendFuncFlags.GLS_SRCBLEND_SRC_ALPHA | BlendFuncFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;
				default:
					if (split.Length == 3)
						return ParseSrcBlendMode(split[1]) | ParseDestBlendMode(split[2]);
					else
						return 0;
			}
		}

		private BlendFuncFlags ParseSrcBlendMode(string src)
		{
			switch (src.ToUpper())
			{
				case "GL_ONE":
					return BlendFuncFlags.GLS_SRCBLEND_ONE;
				case "GL_ZERO":
					return BlendFuncFlags.GLS_SRCBLEND_ZERO;
				case "GL_DST_COLOR":
					return BlendFuncFlags.GLS_SRCBLEND_DST_COLOR;
				case "GL_ONE_MINUS_DST_COLOR":
					return BlendFuncFlags.GLS_SRCBLEND_ONE_MINUS_DST_COLOR;
				case "GL_SRC_ALPHA":
					return BlendFuncFlags.GLS_SRCBLEND_SRC_ALPHA;
				case "GL_ONE_MINUS_SRC_ALPHA":
					return BlendFuncFlags.GLS_SRCBLEND_ONE_MINUS_SRC_ALPHA;
				case "GL_DST_ALPHA":
					return BlendFuncFlags.GLS_SRCBLEND_DST_ALPHA;
				case "GL_ONE_MINUS_DST_ALPHA":
					return BlendFuncFlags.GLS_SRCBLEND_ONE_MINUS_DST_ALPHA;
				case "GL_SRC_ALPHA_SATURATE":
					return BlendFuncFlags.GLS_SRCBLEND_ALPHA_SATURATE;
				default:
					Debug.WriteLine($"Warning: Unknown blend mode: {src}");
					return BlendFuncFlags.GLS_SRCBLEND_ONE;
			}
		}

		private BlendFuncFlags ParseDestBlendMode(string dest)
		{
			switch (dest.ToUpper())
			{
				case "GL_ONE":
					return BlendFuncFlags.GLS_DSTBLEND_ONE;
				case "GL_ZERO":
					return BlendFuncFlags.GLS_DSTBLEND_ZERO;
				case "GL_SRC_ALPHA":
					return BlendFuncFlags.GLS_DSTBLEND_SRC_ALPHA;
				case "GL_ONE_MINUS_SRC_ALPHA":
					return BlendFuncFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;
				case "GL_DST_ALPHA":
					return BlendFuncFlags.GLS_DSTBLEND_DST_ALPHA;
				case "GL_ONE_MINUS_DST_ALPHA":
					return BlendFuncFlags.GLS_DSTBLEND_ONE_MINUS_DST_ALPHA;
				case "GL_SRC_COLOR":
					return BlendFuncFlags.GLS_DSTBLEND_SRC_COLOR;
				case "GL_ONE_MINUS_SRC_COLOR":
					return BlendFuncFlags.GLS_DSTBLEND_ONE_MINUS_SRC_COLOR;
				default:
					Debug.WriteLine($"Warning: Unknown blend mode: {dest}");
					return BlendFuncFlags.GLS_DSTBLEND_ONE;
			}
		}

		private string TrimLine(string line)
		{
			var trimmed = line.Trim();

			// Remove comments from line
			if (trimmed.Contains("//"))
				trimmed = trimmed.Substring(0, trimmed.IndexOf("//"));

			// TODO: Handle multi-line comments
			
			return trimmed;
		}
	}
}
