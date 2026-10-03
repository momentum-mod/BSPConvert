using System;
using System.IO;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// Resamples GoldSrc WAVs whose sample rate Source can't play. GoldSrc plays PCM WAVs at any rate, but Source only
	// mixes 11025, 22050 and 44100 Hz sounds (MIX_MixChannelsToPaintbuffer's passes) and never mixes the rest, so they
	// play nothing.
	public static class GoldSrcWave
	{
		private const int WAVE_FORMAT_PCM = 1;
		private static readonly int[] SourceRates = { 11025, 22050, 44100 };

		private class Wave
		{
			public int Channels;
			public int Rate;
			public int BitsPerSample;
			public byte[] Data = Array.Empty<byte>();
			// The sample the sound loops back to, or -1 if it doesn't loop
			public int LoopStart = -1;
			// GoldSrc's end of the loop, or -1 for the end of the data
			public int LoopLength = -1;
		}

		// Whether Source can't play the WAV at its sample rate, and it's PCM that can be resampled
		public static bool NeedsResampling(string path)
		{
			var wave = Read(path);
			return wave != null && Array.IndexOf(SourceRates, wave.Rate) < 0;
		}

		// Writes the WAV at the next rate Source plays, keeping where it loops. Returns false if it can't be read.
		public static bool Resample(string path, string outputPath)
		{
			var wave = Read(path);
			if (wave == null || wave.Rate <= 0)
				return false;

			var bytesPerSample = wave.BitsPerSample / 8;
			var frameSize = bytesPerSample * wave.Channels;
			var frames = wave.Data.Length / frameSize;

			// GoldSrc stops a looping sound at the end of its loop, where Source plays to the end of the data
			if (wave.LoopStart >= 0 && wave.LoopLength > 0)
				frames = Math.Min(frames, wave.LoopStart + wave.LoopLength);

			var rate = Array.Find(SourceRates, sourceRate => sourceRate >= wave.Rate);
			if (rate == 0)
				rate = SourceRates[^1];

			var scale = (double)wave.Rate / rate;
			var newFrames = Math.Max((int)Math.Round(frames / scale), 1);
			var data = new byte[newFrames * frameSize];
			for (var i = 0; i < newFrames; i++)
			{
				var position = i * scale;
				var first = Math.Min((int)position, frames - 1);
				var second = Math.Min(first + 1, frames - 1);
				var fraction = position - first;
				for (var channel = 0; channel < wave.Channels; channel++)
				{
					var a = ReadSample(wave.Data, (first * wave.Channels + channel) * bytesPerSample, bytesPerSample);
					var b = ReadSample(wave.Data, (second * wave.Channels + channel) * bytesPerSample, bytesPerSample);
					WriteSample(data, (i * wave.Channels + channel) * bytesPerSample, bytesPerSample, a + (b - a) * fraction);
				}
			}

			var loopStart = wave.LoopStart >= 0 ? Math.Min((int)Math.Round(wave.LoopStart / scale), newFrames - 1) : -1;
			Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
			using var writer = new BinaryWriter(File.Create(outputPath));
			var cueSize = loopStart >= 0 ? 8 + 4 + 24 : 0;
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(4 + 8 + 16 + cueSize + 8 + data.Length + (data.Length & 1));
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));

			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((short)WAVE_FORMAT_PCM);
			writer.Write((short)wave.Channels);
			writer.Write(rate);
			writer.Write(rate * frameSize);
			writer.Write((short)frameSize);
			writer.Write((short)wave.BitsPerSample);

			if (loopStart >= 0)
			{
				// One cue point, at the start of the loop
				writer.Write(Encoding.ASCII.GetBytes("cue "));
				writer.Write(4 + 24);
				writer.Write(1);
				writer.Write(1);
				writer.Write(loopStart);
				writer.Write(Encoding.ASCII.GetBytes("data"));
				writer.Write(0);
				writer.Write(0);
				writer.Write(loopStart);
			}

			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(data.Length);
			writer.Write(data);
			if ((data.Length & 1) != 0)
				writer.Write((byte)0);

			return true;
		}

		// 8-bit samples are unsigned and 16-bit ones signed
		private static double ReadSample(byte[] data, int offset, int bytesPerSample)
		{
			return bytesPerSample == 1 ? data[offset] - 128 : BitConverter.ToInt16(data, offset);
		}

		private static void WriteSample(byte[] data, int offset, int bytesPerSample, double value)
		{
			if (bytesPerSample == 1)
			{
				data[offset] = (byte)(Math.Clamp((int)Math.Round(value), -128, 127) + 128);
				return;
			}

			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue));
		}

		// Reads an 8 or 16-bit PCM WAV's format, data and loop the way GoldSrc does (GetWavinfo): the loop starts at
		// the first cue point, and a "mark" label in a following LIST chunk gives its length. Returns null for anything
		// else.
		private static Wave? Read(string path)
		{
			try
			{
				var bytes = File.ReadAllBytes(path);
				if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
					return null;

				var wave = new Wave();
				var hasFormat = false;
				var hasData = false;
				var afterCue = false;
				for (var offset = 12; offset + 8 <= bytes.Length;)
				{
					var id = Encoding.ASCII.GetString(bytes, offset, 4);
					var size = BitConverter.ToInt32(bytes, offset + 4);
					var start = offset + 8;
					if (size < 0 || start + size > bytes.Length)
						size = bytes.Length - start;

					switch (id)
					{
						case "fmt ":
							if (size < 16 || BitConverter.ToInt16(bytes, start) != WAVE_FORMAT_PCM)
								return null;

							wave.Channels = BitConverter.ToInt16(bytes, start + 2);
							wave.Rate = BitConverter.ToInt32(bytes, start + 4);
							wave.BitsPerSample = BitConverter.ToInt16(bytes, start + 14);
							hasFormat = true;
							break;
						case "data":
							wave.Data = bytes.AsSpan(start, size).ToArray();
							hasData = true;
							break;
						case "cue ":
							if (size >= 28 && BitConverter.ToInt32(bytes, start) > 0)
								wave.LoopStart = BitConverter.ToInt32(bytes, start + 24);
							break;
						case "LIST":
							if (afterCue && size >= 24 && Encoding.ASCII.GetString(bytes, start + 20, 4) == "mark")
								wave.LoopLength = BitConverter.ToInt32(bytes, start + 16);
							break;
					}

					afterCue |= id == "cue ";
					offset = start + size + (size & 1);
				}

				if (!hasFormat || !hasData || wave.Channels < 1 || (wave.BitsPerSample != 8 && wave.BitsPerSample != 16))
					return null;

				return wave;
			}
			catch (IOException)
			{
				return null;
			}
		}
	}
}
