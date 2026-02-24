using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BSPConvert.Lib.Zones
{
	public class ZoneReader
	{
		/// <summary>
		/// Reads a JSON file and deserializes it into a ZoneDefsBase object.
		/// </summary>
		public static ZoneDefsBase ReadFromFile(string path)
		{
			if (!File.Exists(path))
				throw new FileNotFoundException($"Zone definition file not found: {path}");

			using (var stream = File.OpenRead(path))
			{
				return JsonSerializer.Deserialize<ZoneDefsBase>(stream);
			}
		}

		/// <summary>
		/// Asynchronously reads a JSON file and deserializes it into a ZoneDefsBase object.
		/// </summary>
		public static async Task<ZoneDefsBase> ReadFromFileAsync(string path)
		{
			if (!File.Exists(path))
				throw new FileNotFoundException($"Zone definition file not found: {path}");

			using (var stream = File.OpenRead(path))
			{
				return await JsonSerializer.DeserializeAsync<ZoneDefsBase>(stream);
			}
		}

		/// <summary>
		/// Reads a JSON string and deserializes it into a ZoneDefsBase object.
		/// </summary>
		public static ZoneDefsBase ReadFromString(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
				throw new ArgumentException("JSON string cannot be null or empty", nameof(json));

			return JsonSerializer.Deserialize<ZoneDefsBase>(json);
		}
	}
}
