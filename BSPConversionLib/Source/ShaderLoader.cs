using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class ShaderLoader
	{
		private IEnumerable<string> shaderFiles;
		
		public ShaderLoader(IEnumerable<string> shaderFiles)
		{
			this.shaderFiles = shaderFiles;
		}

		public Dictionary<string, Shader> LoadShaders()
		{
			var shaderDict = new Dictionary<string, Shader>();

			foreach (var file in shaderFiles)
			{
				var shaderParser = new ShaderParser(file);
				var newShaderDict = shaderParser.ParseShaders();
				foreach (var kv in newShaderDict)
				{
					if (!shaderDict.ContainsKey(kv.Key))
						shaderDict.Add(kv.Key, kv.Value);
				}
			}

			return shaderDict;
		}
	}
}
