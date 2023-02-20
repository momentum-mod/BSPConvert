using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class Shader
	{
		public class SkyParms
		{
			public string outerBox;
			public string cloudHeight;
			public string innerBox;
		}

		public string map; // Path to image file
		public SkyParms skyParms;
		public Q3SurfaceFlags surfaceFlags;
		public Q3ContentsFlags contents;

		// Shader stage parameters (TODO: Needs to be moved to separate class for handling stages)
		public AlphaFunc alphaFunc;
	}

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
						ParseMap(shader, split);
						break;
					case "surfaceparm":
						ParseSurfaceParm(shader, split);
						break;
					case "skyparms":
						ParseSkyParms(shader, split);
						break;
					case "alphafunc":
						ParseAlphaFunc(shader, split);
						break;
				}
			}

			return shader;
		}

		private static void ParseMap(Shader shader, string[] split)
		{
			if (!split[1].StartsWith('$') && string.IsNullOrEmpty(shader.map))
				shader.map = split[1];
		}

		private void ParseSurfaceParm(Shader shader, string[] split)
		{
			foreach (var infoParm in Constants.infoParms)
			{
				if (split[1] == infoParm.name)
				{
					shader.surfaceFlags |= infoParm.surfaceFlags;
					shader.contents |= infoParm.contents;
					break;
				}
			}
		}

		private void ParseSkyParms(Shader shader, string[] split)
		{
			shader.skyParms = new Shader.SkyParms()
			{
				outerBox = split[1],
				cloudHeight = split[2],
				innerBox = split[3]
			};
		}

		private void ParseAlphaFunc(Shader shader, string[] split)
		{
			switch (split[1].ToLower())
			{
				case "gt0":
					shader.alphaFunc = AlphaFunc.GLS_ATEST_GT_0;
					break;
				case "lt128":
					shader.alphaFunc = AlphaFunc.GLS_ATEST_LT_80;
					break;
				case "ge128":
					shader.alphaFunc = AlphaFunc.GLS_ATEST_GE_80;
					break;
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
