namespace BSPConvert.Lib;
using System.Collections.Generic;

public class ShaderLoader(IEnumerable<string> shaderFiles)
{
		private readonly IEnumerable<string> shaderFiles = shaderFiles;

    public Dictionary<string, Shader> LoadShaders()
		{
			var shaderDict = new Dictionary<string, Shader>();

			foreach (string file in shaderFiles)
			{
				var shaderParser = new ShaderParser(file);
            Dictionary<string, Shader> newShaderDict = shaderParser.ParseShaders();
				foreach (KeyValuePair<string, Shader> kv in newShaderDict)
				{
					if (!shaderDict.ContainsKey(kv.Key))
						shaderDict.Add(kv.Key, kv.Value);
				}
			}

			return shaderDict;
		}
	}
