using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BSPConvert.Lib.Zones
{
	public class ZoneWriter
	{
		private static readonly JsonSerializerOptions serializerOptions = new()
		{
			WriteIndented = false,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		/// <summary>
		/// Serializes a ZoneDefsBase object and writes it to a JSON file.
		/// </summary>
		public static void WriteToFile(ZoneDefsBase zoneDefs, string path)
		{
			ArgumentNullException.ThrowIfNull(zoneDefs);

			Directory.CreateDirectory(Path.GetDirectoryName(path));

			using (var stream = File.Open(path, FileMode.Create, FileAccess.Write))
			{
				JsonSerializer.Serialize(stream, zoneDefs, serializerOptions);
			}
		}

		/// <summary>
		/// Asynchronously serializes a ZoneDefsBase object and writes it to a JSON file.
		/// </summary>
		public static async Task WriteToFileAsync(ZoneDefsBase zoneDefs, string path)
		{
			ArgumentNullException.ThrowIfNull(zoneDefs);

			Directory.CreateDirectory(Path.GetDirectoryName(path));

			using (var stream = File.Open(path, FileMode.Create, FileAccess.Write))
			{
				await JsonSerializer.SerializeAsync(stream, zoneDefs, serializerOptions);
			}
		}

		/// <summary>
		/// Serializes a ZoneDefsBase object to a JSON string.
		/// </summary>
		public static string WriteToString(ZoneDefsBase zoneDefs)
		{
			ArgumentNullException.ThrowIfNull(zoneDefs);

			return JsonSerializer.Serialize(zoneDefs, serializerOptions);
		}
	}
}
