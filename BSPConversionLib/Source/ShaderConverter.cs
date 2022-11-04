using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class ShaderConverter
	{
		private string pk3Dir;
		
		public ShaderConverter(string pk3Dir)
		{
			this.pk3Dir = pk3Dir;
		}

		public Dictionary<string, Shader> Convert()
		{
			var shaderDict = new Dictionary<string, Shader>();

			var shaders = Directory.GetFiles(pk3Dir, "*.shader", SearchOption.AllDirectories);
			foreach (var shader in shaders)
			{
				var shaderParser = new ShaderParser(shader);
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
