using LibBSP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// GoldSrc (BSP version 30) lumps, read straight from the file's lump bytes. LibBSP detects and loads GoldSrc
	// maps (and parses their entities), but doesn't implement most of the version 30 lump structures.
	public class GoldSrcBsp
	{
		public const int LUMP_ENTITIES = 0;
		public const int LUMP_PLANES = 1;
		public const int LUMP_TEXTURES = 2;
		public const int LUMP_VERTICES = 3;
		public const int LUMP_VISIBILITY = 4;
		public const int LUMP_NODES = 5;
		public const int LUMP_TEXINFO = 6;
		public const int LUMP_FACES = 7;
		public const int LUMP_LIGHTING = 8;
		public const int LUMP_CLIPNODES = 9;
		public const int LUMP_LEAVES = 10;
		public const int LUMP_MARKSURFACES = 11;
		public const int LUMP_EDGES = 12;
		public const int LUMP_SURFEDGES = 13;
		public const int LUMP_MODELS = 14;

		// Leaf and clipnode contents
		public const int CONTENTS_EMPTY = -1;
		public const int CONTENTS_SOLID = -2;
		public const int CONTENTS_WATER = -3;
		public const int CONTENTS_SLIME = -4;
		public const int CONTENTS_LAVA = -5;
		public const int CONTENTS_SKY = -6;
		public const int CONTENTS_ORIGIN = -7;
		public const int CONTENTS_CLIP = -8;
		public const int CONTENTS_CURRENT_0 = -9;
		public const int CONTENTS_CURRENT_DOWN = -14;
		public const int CONTENTS_TRANSLUCENT = -15;
		public const int CONTENTS_LADDER = -16;

		// Texinfo flag for surfaces without lightmaps (sky, water, triggers)
		public const int TEX_SPECIAL = 1;

		public const int MAX_MAP_HULLS = 4;

		// Plane type of a plane whose normal is +Z
		public const int PLANE_Z = 2;

		public struct Plane
		{
			public Vector3 normal;
			public float dist;
			public int type;
		}

		public struct Node
		{
			public int planeIndex;
			// >= 0 is a node index, < 0 is a leaf index encoded as -(leaf + 1)
			public int child0;
			public int child1;
			public Vector3 mins;
			public Vector3 maxs;
			public int firstFace;
			public int numFaces;
		}

		public struct ClipNode
		{
			public int planeIndex;
			// >= 0 is a clipnode index, < 0 is the leaf's contents (CONTENTS_*)
			public int child0;
			public int child1;
		}

		public struct Leaf
		{
			public int contents;
			public int visOffset;
			public Vector3 mins;
			public Vector3 maxs;
			public int firstMarkSurface;
			public int numMarkSurfaces;
		}

		public struct Face
		{
			public int planeIndex;
			public bool planeSide;
			public int firstEdge;
			public int numEdges;
			public int texInfo;
			public byte[] styles;
			public int lightOffset;
		}

		public struct TexInfo
		{
			// xyz = texture axis, w = offset, in texels
			public Vector4 s;
			public Vector4 t;
			public int mipTex;
			public int flags;
		}

		public struct MipTex
		{
			public string name;
			public int width;
			public int height;
			// Offset of the miptex within the textures lump, or -1 if the lump has no entry for it
			public int offset;
		}

		public struct Model
		{
			public Vector3 mins;
			public Vector3 maxs;
			public Vector3 origin;
			// Head node per hull: [0] is a node index (render/point hull), [1..3] are clipnode indices
			public int[] headNodes;
			public int visLeafs;
			public int firstFace;
			public int numFaces;
		}

		public Plane[] Planes { get; private set; }
		public Vector3[] Vertices { get; private set; }
		public (int v0, int v1)[] Edges { get; private set; }
		public int[] SurfEdges { get; private set; }
		public Face[] Faces { get; private set; }
		public TexInfo[] TexInfos { get; private set; }
		public MipTex[] MipTextures { get; private set; }
		public byte[] TexturesLump { get; private set; }
		public Node[] Nodes { get; private set; }
		public ClipNode[] ClipNodes { get; private set; }
		public Leaf[] Leaves { get; private set; }
		public int[] MarkSurfaces { get; private set; }
		public Model[] Models { get; private set; }
		public byte[] Lighting { get; private set; }
		public byte[] Visibility { get; private set; }
		public Entities Entities { get; private set; }

		public static GoldSrcBsp Read(BSP bsp)
		{
			byte[] Lump(int index) => bsp.Reader.ReadLump(bsp[index]);

			var gs = new GoldSrcBsp();
			gs.Entities = bsp.Entities;
			gs.Planes = ReadArray(Lump(LUMP_PLANES), 20, (r) => new Plane
			{
				normal = ReadVector3(r),
				dist = r.ReadSingle(),
				type = r.ReadInt32()
			});
			gs.Vertices = ReadArray(Lump(LUMP_VERTICES), 12, ReadVector3);
			gs.Edges = ReadArray(Lump(LUMP_EDGES), 4, (r) => ((int)r.ReadUInt16(), (int)r.ReadUInt16()));
			gs.SurfEdges = ReadArray(Lump(LUMP_SURFEDGES), 4, (r) => r.ReadInt32());
			gs.Faces = ReadArray(Lump(LUMP_FACES), 20, (r) => new Face
			{
				planeIndex = r.ReadUInt16(),
				planeSide = r.ReadUInt16() != 0,
				firstEdge = r.ReadInt32(),
				numEdges = r.ReadInt16(),
				texInfo = r.ReadInt16(),
				styles = r.ReadBytes(4),
				lightOffset = r.ReadInt32()
			});
			gs.TexInfos = ReadArray(Lump(LUMP_TEXINFO), 40, (r) => new TexInfo
			{
				s = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
				t = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
				mipTex = r.ReadInt32(),
				flags = r.ReadInt32()
			});
			gs.Nodes = ReadArray(Lump(LUMP_NODES), 24, (r) => new Node
			{
				planeIndex = r.ReadInt32(),
				child0 = r.ReadInt16(),
				child1 = r.ReadInt16(),
				mins = ReadShortVector3(r),
				maxs = ReadShortVector3(r),
				firstFace = r.ReadUInt16(),
				numFaces = r.ReadUInt16()
			});
			gs.ClipNodes = ReadArray(Lump(LUMP_CLIPNODES), 8, (r) => new ClipNode
			{
				planeIndex = r.ReadInt32(),
				child0 = r.ReadInt16(),
				child1 = r.ReadInt16()
			});
			gs.Leaves = ReadArray(Lump(LUMP_LEAVES), 28, (r) =>
			{
				var leaf = new Leaf
				{
					contents = r.ReadInt32(),
					visOffset = r.ReadInt32(),
					mins = ReadShortVector3(r),
					maxs = ReadShortVector3(r),
					firstMarkSurface = r.ReadUInt16(),
					numMarkSurfaces = r.ReadUInt16()
				};
				r.ReadBytes(4); // ambient sound levels
				return leaf;
			});
			gs.MarkSurfaces = ReadArray(Lump(LUMP_MARKSURFACES), 2, (r) => (int)r.ReadUInt16());
			gs.Models = ReadArray(Lump(LUMP_MODELS), 64, (r) => new Model
			{
				mins = ReadVector3(r),
				maxs = ReadVector3(r),
				origin = ReadVector3(r),
				headNodes = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() },
				visLeafs = r.ReadInt32(),
				firstFace = r.ReadInt32(),
				numFaces = r.ReadInt32()
			});
			gs.Lighting = Lump(LUMP_LIGHTING);
			gs.Visibility = Lump(LUMP_VISIBILITY);
			gs.TexturesLump = Lump(LUMP_TEXTURES);
			gs.MipTextures = ReadMipTextures(gs.TexturesLump);

			return gs;
		}

		private static MipTex[] ReadMipTextures(byte[] lump)
		{
			if (lump.Length < 4)
				return Array.Empty<MipTex>();

			using var reader = new BinaryReader(new MemoryStream(lump));
			var count = reader.ReadInt32();
			var offsets = new int[count];
			for (var i = 0; i < count; i++)
				offsets[i] = reader.ReadInt32();

			var mipTextures = new MipTex[count];
			for (var i = 0; i < count; i++)
			{
				if (offsets[i] < 0 || offsets[i] + MipTexture.HeaderSize > lump.Length)
				{
					mipTextures[i] = new MipTex { name = "", width = 16, height = 16, offset = -1 };
					continue;
				}

				reader.BaseStream.Position = offsets[i];
				mipTextures[i] = new MipTex
				{
					name = MipTexture.ReadName(reader.ReadBytes(16)),
					width = reader.ReadInt32(),
					height = reader.ReadInt32(),
					offset = offsets[i]
				};
			}

			return mipTextures;
		}

		// The texture's pixels if they're embedded in the BSP, or null if it lives in an external WAD
		public MipTexture? GetEmbeddedMipTexture(int mipTexIndex)
		{
			var offset = MipTextures[mipTexIndex].offset;
			return offset < 0 ? null : MipTexture.Read(TexturesLump.AsSpan(offset));
		}

		private static T[] ReadArray<T>(byte[] lump, int structSize, Func<BinaryReader, T> readFunc)
		{
			var count = lump.Length / structSize;
			var items = new T[count];
			using var reader = new BinaryReader(new MemoryStream(lump));
			for (var i = 0; i < count; i++)
			{
				reader.BaseStream.Position = (long)i * structSize;
				items[i] = readFunc(reader);
			}

			return items;
		}

		private static Vector3 ReadVector3(BinaryReader reader)
		{
			return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
		}

		private static Vector3 ReadShortVector3(BinaryReader reader)
		{
			return new Vector3(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
		}
	}
}
