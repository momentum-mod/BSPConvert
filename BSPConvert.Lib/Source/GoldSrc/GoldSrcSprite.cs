using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// How a sprite faces the viewer (spritegn.h). Source's $spriteorientation values are the same.
	public enum SpriteType
	{
		ParallelUpright = 0,
		FacingUpright = 1,
		Parallel = 2,
		Oriented = 3,
		ParallelOriented = 4
	}

	// How a sprite's palette indices become colors (spritegn.h)
	public enum SpriteTextureFormat
	{
		Normal = 0,
		Additive = 1,
		// The last palette color, with the index as its alpha
		IndexAlpha = 2,
		// Index 255 is transparent
		AlphaTest = 3
	}

	// A GoldSrc sprite (.spr, version 2): 8-bit frames sharing a palette. Its frames are laid out on one canvas big
	// enough for all of them, placed by their offsets from the sprite's origin, since Source sprites need every
	// frame the same size.
	public class GoldSrcSprite
	{
		private const int Version = 2;
		private const int TransparentIndex = 255;

		public SpriteType Type { get; private set; }
		public SpriteTextureFormat TextureFormat { get; private set; }
		public int Width { get; private set; }
		public int Height { get; private set; }
		// Where the sprite's origin is on the canvas, as a fraction of its size from the top left
		public float OriginX { get; private set; }
		public float OriginY { get; private set; }
		// Each frame's RGBA pixels on the canvas
		public List<byte[]> Frames { get; } = new List<byte[]>();

		private record Frame(int OriginX, int OriginY, int Width, int Height, byte[] Pixels);

		// Returns null if the file isn't a sprite this can read
		public static GoldSrcSprite? Read(string path)
		{
			try
			{
				using var reader = new BinaryReader(File.OpenRead(path));
				if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "IDSP" || reader.ReadInt32() != Version)
					return null;

				var sprite = new GoldSrcSprite
				{
					Type = (SpriteType)reader.ReadInt32(),
					TextureFormat = (SpriteTextureFormat)reader.ReadInt32(),
				};
				reader.ReadSingle(); // bounding radius
				reader.ReadInt32(); // max width
				reader.ReadInt32(); // max height
				var numFrames = reader.ReadInt32();
				reader.ReadSingle(); // beam length
				reader.ReadInt32(); // sync type

				var paletteColors = reader.ReadInt16();
				var palette = new byte[256 * 3];
				var paletteBytes = reader.ReadBytes(paletteColors * 3);
				Array.Copy(paletteBytes, palette, Math.Min(paletteBytes.Length, palette.Length));

				// Group frames are flattened into their subframes
				var frames = new List<Frame>();
				for (var i = 0; i < numFrames; i++)
				{
					var frameType = reader.ReadInt32();
					if (frameType == 0)
					{
						frames.Add(ReadFrame(reader));
						continue;
					}

					var groupFrames = reader.ReadInt32();
					reader.ReadBytes(groupFrames * 4); // intervals
					for (var j = 0; j < groupFrames; j++)
						frames.Add(ReadFrame(reader));
				}

				if (frames.Count == 0)
					return null;

				sprite.LayOutFrames(frames, palette);
				return sprite;
			}
			catch (Exception ex) when (ex is IOException || ex is EndOfStreamException || ex is ArgumentException)
			{
				return null;
			}
		}

		private static Frame ReadFrame(BinaryReader reader)
		{
			var originX = reader.ReadInt32();
			var originY = reader.ReadInt32();
			var width = reader.ReadInt32();
			var height = reader.ReadInt32();
			var pixels = reader.ReadBytes(width * height);
			if (width <= 0 || height <= 0 || pixels.Length != width * height)
				throw new EndOfStreamException();

			return new Frame(originX, originY, width, height, pixels);
		}

		// A frame's origin is its top left corner's offset from the sprite's origin, with y up
		private void LayOutFrames(List<Frame> frames, byte[] palette)
		{
			var left = int.MaxValue;
			var right = int.MinValue;
			var top = int.MinValue;
			var bottom = int.MaxValue;
			foreach (var frame in frames)
			{
				left = Math.Min(left, frame.OriginX);
				right = Math.Max(right, frame.OriginX + frame.Width);
				top = Math.Max(top, frame.OriginY);
				bottom = Math.Min(bottom, frame.OriginY - frame.Height);
			}

			// Block compressed textures need sizes that are multiples of 4
			Width = (right - left + 3) & ~3;
			Height = (top - bottom + 3) & ~3;
			OriginX = -left / (float)Width;
			OriginY = top / (float)Height;

			foreach (var frame in frames)
			{
				// Transparent outside the frame
				var canvas = new byte[Width * Height * 4];
				var startX = frame.OriginX - left;
				var startY = top - frame.OriginY;
				for (var y = 0; y < frame.Height; y++)
				{
					for (var x = 0; x < frame.Width; x++)
					{
						var index = frame.Pixels[y * frame.Width + x];
						var i = ((startY + y) * Width + startX + x) * 4;
						var color = TextureFormat == SpriteTextureFormat.IndexAlpha ? TransparentIndex : index;
						canvas[i] = palette[color * 3];
						canvas[i + 1] = palette[color * 3 + 1];
						canvas[i + 2] = palette[color * 3 + 2];
						canvas[i + 3] = TextureFormat switch
						{
							SpriteTextureFormat.IndexAlpha => index,
							SpriteTextureFormat.AlphaTest => index == TransparentIndex ? (byte)0 : (byte)255,
							_ => 255,
						};
					}
				}

				Frames.Add(canvas);
			}
		}
	}
}
