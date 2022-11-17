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
				switch (split[0])
				{
					case "map":
						if (!split[1].StartsWith('$') && string.IsNullOrEmpty(shader.map))
							shader.map = split[1];
						break;
					case "skyparms":
						shader.skyParms = new Shader.SkyParms()
						{
							outerBox = split[1],
							cloudHeight = split[2],
							innerBox = split[3]
						};
						break;
				}
			}

			return shader;
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
