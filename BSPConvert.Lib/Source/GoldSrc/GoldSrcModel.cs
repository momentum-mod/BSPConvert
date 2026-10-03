using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// A GoldSrc studio model (.mdl, version 10; studio.h), decoded into what an SMD holds: a skeleton, triangles in
	// model space and an animation per sequence. Models without textures keep them in <name>T.mdl, and sequences
	// can be stored in <name>01.mdl, <name>02.mdl, ...
	public class GoldSrcModel
	{
		private const int Version = 10;

		// Texture flags (studio.h)
		public const int STUDIO_NF_FULLBRIGHT = 0x0004;
		public const int STUDIO_NF_ADDITIVE = 0x0020;
		// Palette index 255 is transparent
		public const int STUDIO_NF_MASKED = 0x0040;

		// Sequence flags and motion types (studio.h)
		private const int STUDIO_LOOPING = 0x0001;
		private const int STUDIO_X = 0x0001;
		private const int STUDIO_Y = 0x0002;
		private const int STUDIO_Z = 0x0004;
		private const int STUDIO_XR = 0x0008;
		private const int STUDIO_YR = 0x0010;
		private const int STUDIO_ZR = 0x0020;
		private const int STUDIO_TYPES = 0x7FFF;

		private const int TransparentIndex = 255;

		// Position and rotation (radians, applied about x then y then z) relative to the parent bone
		public record Bone(string Name, int Parent, Vector3 Position, Vector3 Rotation);
		// RGBA pixels
		public record Texture(string Name, int Flags, int Width, int Height, byte[] Pixels);
		// Model space position and normal, and texture coordinates with v up like an SMD's
		public record struct Vertex(int Bone, Vector3 Position, Vector3 Normal, Vector2 TexCoord);
		// SkinRef indexes the skin families' textures
		public record Triangle(int SkinRef, Vertex A, Vertex B, Vertex C);
		public record SubModel(string Name, List<Triangle> Triangles);
		public record BodyPart(string Name, List<SubModel> Models);
		// Each frame's bone positions and rotations, like Bone's
		public record Sequence(string Name, float Fps, bool Looping, Vector3[][] Positions, Vector3[][] Rotations)
		{
			public int FrameCount => Positions.Length;
		}

		public List<Bone> Bones { get; } = new List<Bone>();
		public List<Texture> Textures { get; } = new List<Texture>();
		// The texture each skin reference uses, per skin family
		public short[][] SkinFamilies { get; private set; } = Array.Empty<short[]>();
		public List<BodyPart> BodyParts { get; } = new List<BodyPart>();
		public List<Sequence> Sequences { get; } = new List<Sequence>();

		private struct BoneInfo
		{
			public int[] Controllers;
			public float[] Values;
			public float[] Scales;
		}

		// Returns null if the file isn't a model this can read. findFile finds the model's texture and sequence group
		// files by their file name.
		public static GoldSrcModel? Read(string path, Func<string, string?> findFile, out string? error)
		{
			error = null;
			try
			{
				var data = File.ReadAllBytes(path);
				if (!IsStudioModel(data))
				{
					error = "not a version 10 studio model";
					return null;
				}

				var model = new GoldSrcModel();
				var bones = model.ReadBones(data);
				var baseName = Path.GetFileNameWithoutExtension(path);

				var textureData = data;
				if (ReadInt(data, 180) == 0)
				{
					var textureFile = findFile(baseName + "T.mdl");
					if (textureFile == null)
					{
						error = $"its textures ({baseName}T.mdl) weren't found";
						return null;
					}

					textureData = File.ReadAllBytes(textureFile);
					if (!IsStudioModel(textureData))
					{
						error = $"{baseName}T.mdl isn't a version 10 studio model";
						return null;
					}
				}

				model.ReadTextures(textureData);
				model.ReadBodyParts(data);
				if (!model.ReadSequences(data, bones, baseName, findFile, out error))
					return null;

				return model;
			}
			catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is IndexOutOfRangeException)
			{
				error = ex.Message;
				return null;
			}
		}

		private static bool IsStudioModel(byte[] data)
		{
			return data.Length >= 244 && Encoding.ASCII.GetString(data, 0, 4) == "IDST" && ReadInt(data, 4) == Version;
		}

		private BoneInfo[] ReadBones(byte[] data)
		{
			var numBones = ReadInt(data, 140);
			var boneIndex = ReadInt(data, 144);
			var bones = new BoneInfo[numBones];
			for (var i = 0; i < numBones; i++)
			{
				var offset = boneIndex + i * 112;
				var info = new BoneInfo { Controllers = new int[6], Values = new float[6], Scales = new float[6] };
				for (var j = 0; j < 6; j++)
				{
					info.Controllers[j] = ReadInt(data, offset + 40 + j * 4);
					info.Values[j] = ReadFloat(data, offset + 64 + j * 4);
					info.Scales[j] = ReadFloat(data, offset + 88 + j * 4);
				}

				bones[i] = info;
				Bones.Add(new Bone(ReadString(data, offset, 32), ReadInt(data, offset + 32),
					new Vector3(info.Values[0], info.Values[1], info.Values[2]), new Vector3(info.Values[3], info.Values[4], info.Values[5])));
			}

			return bones;
		}

		private void ReadTextures(byte[] data)
		{
			var numTextures = ReadInt(data, 180);
			var textureIndex = ReadInt(data, 184);
			for (var i = 0; i < numTextures; i++)
			{
				var offset = textureIndex + i * 80;
				var flags = ReadInt(data, offset + 64);
				var width = ReadInt(data, offset + 68);
				var height = ReadInt(data, offset + 72);
				var index = ReadInt(data, offset + 76);
				var masked = (flags & STUDIO_NF_MASKED) != 0;

				// Indices, followed by the texture's palette
				var paletteOffset = index + width * height;
				var pixels = new byte[width * height * 4];
				for (var p = 0; p < width * height; p++)
				{
					var color = data[index + p];
					pixels[p * 4] = data[paletteOffset + color * 3];
					pixels[p * 4 + 1] = data[paletteOffset + color * 3 + 1];
					pixels[p * 4 + 2] = data[paletteOffset + color * 3 + 2];
					pixels[p * 4 + 3] = masked && color == TransparentIndex ? (byte)0 : (byte)255;
				}

				Textures.Add(new Texture(ReadString(data, offset, 64), flags, width, height, pixels));
			}

			var numSkinRefs = ReadInt(data, 192);
			var numSkinFamilies = ReadInt(data, 196);
			var skinIndex = ReadInt(data, 200);
			SkinFamilies = new short[numSkinFamilies][];
			for (var family = 0; family < numSkinFamilies; family++)
			{
				SkinFamilies[family] = new short[numSkinRefs];
				for (var skinRef = 0; skinRef < numSkinRefs; skinRef++)
					SkinFamilies[family][skinRef] = BitConverter.ToInt16(data, skinIndex + (family * numSkinRefs + skinRef) * 2);
			}
		}

		// Vertices and normals are stored relative to the bone they're attached to, in the pose of the bones' default
		// values, which the reference skeleton has too
		private void ReadBodyParts(byte[] data)
		{
			var boneTransforms = GetBoneTransforms();
			var numBodyParts = ReadInt(data, 204);
			var bodyPartIndex = ReadInt(data, 208);
			for (var i = 0; i < numBodyParts; i++)
			{
				var bodyPartOffset = bodyPartIndex + i * 76;
				var numModels = ReadInt(data, bodyPartOffset + 64);
				var modelIndex = ReadInt(data, bodyPartOffset + 72);
				var bodyPart = new BodyPart(ReadString(data, bodyPartOffset, 64), new List<SubModel>());
				for (var j = 0; j < numModels; j++)
					bodyPart.Models.Add(ReadSubModel(data, modelIndex + j * 112, boneTransforms));

				BodyParts.Add(bodyPart);
			}
		}

		private SubModel ReadSubModel(byte[] data, int offset, Matrix4x4[] boneTransforms)
		{
			var numMeshes = ReadInt(data, offset + 72);
			var meshIndex = ReadInt(data, offset + 76);
			var vertInfoIndex = ReadInt(data, offset + 84);
			var vertIndex = ReadInt(data, offset + 88);
			var normInfoIndex = ReadInt(data, offset + 96);
			var normIndex = ReadInt(data, offset + 100);

			var triangles = new List<Triangle>();
			for (var i = 0; i < numMeshes; i++)
			{
				var meshOffset = meshIndex + i * 20;
				var triIndex = ReadInt(data, meshOffset + 4);
				var skinRef = ReadInt(data, meshOffset + 8);
				var texture = GetTexture(0, skinRef);

				Vertex ReadVertex(int cmdOffset)
				{
					var vert = BitConverter.ToInt16(data, cmdOffset);
					var norm = BitConverter.ToInt16(data, cmdOffset + 2);
					var s = BitConverter.ToInt16(data, cmdOffset + 4);
					var t = BitConverter.ToInt16(data, cmdOffset + 6);

					var bone = data[vertInfoIndex + vert];
					var position = Vector3.Transform(ReadVector(data, vertIndex + vert * 12), boneTransforms[bone]);
					var normalBone = data[normInfoIndex + norm];
					var normal = Vector3.Normalize(Vector3.TransformNormal(ReadVector(data, normIndex + norm * 12), boneTransforms[normalBone]));

					// Texture coordinates are in texels, from the top left
					var texCoord = texture != null ? new Vector2(s / (float)texture.Width, 1f - t / (float)texture.Height) : Vector2.Zero;
					return new Vertex(bone, position, normal, texCoord);
				}

				// Triangle strips (a positive vertex count) and fans (a negative one), until a count of 0. Each vertex is
				// 4 shorts: vertex, normal, s, t.
				var cmd = triIndex;
				while (true)
				{
					int count = BitConverter.ToInt16(data, cmd);
					cmd += 2;
					if (count == 0)
						break;

					var isFan = count < 0;
					count = Math.Abs(count);
					var vertices = new Vertex[count];
					for (var v = 0; v < count; v++, cmd += 8)
						vertices[v] = ReadVertex(cmd);

					// GoldSrc's triangles wind clockwise around their normals, and an SMD's counterclockwise. Every other
					// triangle of a strip has its first two vertices swapped to keep its winding.
					for (var v = 2; v < count; v++)
					{
						if (isFan)
							triangles.Add(new Triangle(skinRef, vertices[0], vertices[v], vertices[v - 1]));
						else if (v % 2 == 0)
							triangles.Add(new Triangle(skinRef, vertices[v - 2], vertices[v], vertices[v - 1]));
						else
							triangles.Add(new Triangle(skinRef, vertices[v - 1], vertices[v], vertices[v - 2]));
					}
				}
			}

			return new SubModel(ReadString(data, offset, 64), triangles);
		}

		// The texture a skin reference uses in a skin family
		public Texture? GetTexture(int family, int skinRef)
		{
			if (family >= SkinFamilies.Length || skinRef < 0 || skinRef >= SkinFamilies[family].Length)
				return skinRef >= 0 && skinRef < Textures.Count ? Textures[skinRef] : null;

			var index = SkinFamilies[family][skinRef];
			return index >= 0 && index < Textures.Count ? Textures[index] : null;
		}

		// Each bone's model space transform in the default pose
		private Matrix4x4[] GetBoneTransforms()
		{
			var transforms = new Matrix4x4[Bones.Count];
			for (var i = 0; i < Bones.Count; i++)
			{
				var bone = Bones[i];
				var local = GetBoneMatrix(bone.Position, bone.Rotation);
				// Parents come before their children
				transforms[i] = bone.Parent >= 0 && bone.Parent < i ? local * transforms[bone.Parent] : local;
			}

			return transforms;
		}

		// Rotates about x, then y, then z (AngleQuaternion), then translates. System.Numerics transforms row vectors, so
		// the first transform comes first.
		private static Matrix4x4 GetBoneMatrix(Vector3 position, Vector3 rotation)
		{
			return Matrix4x4.CreateRotationX(rotation.X) * Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationZ(rotation.Z) *
				Matrix4x4.CreateTranslation(position);
		}

		private bool ReadSequences(byte[] data, BoneInfo[] bones, string baseName, Func<string, string?> findFile, out string? error)
		{
			error = null;
			var adjustments = GetControllerAdjustments(data);
			var groupData = new Dictionary<int, byte[]> { [0] = data };
			var numSequences = ReadInt(data, 164);
			var sequenceIndex = ReadInt(data, 168);
			for (var i = 0; i < numSequences; i++)
			{
				var offset = sequenceIndex + i * 176;
				var name = ReadString(data, offset, 32);
				var fps = ReadFloat(data, offset + 32);
				var flags = ReadInt(data, offset + 36);
				var numFrames = Math.Max(ReadInt(data, offset + 56), 1);
				var motionType = ReadInt(data, offset + 68);
				var motionBone = ReadInt(data, offset + 72);
				var animIndex = ReadInt(data, offset + 124);
				var group = ReadInt(data, offset + 156);

				if (!groupData.TryGetValue(group, out var animData))
				{
					var groupFileName = $"{baseName}{group:00}.mdl";
					var groupFile = findFile(groupFileName);
					if (groupFile == null)
					{
						error = $"its sequence group {groupFileName} wasn't found";
						return false;
					}

					animData = File.ReadAllBytes(groupFile);
					if (Encoding.ASCII.GetString(animData, 0, 4) != "IDSQ")
					{
						error = $"{groupFileName} isn't a sequence group";
						return false;
					}

					groupData[group] = animData;
				}

				// Only the first blend of blended sequences
				var positions = new Vector3[numFrames][];
				var rotations = new Vector3[numFrames][];
				for (var frame = 0; frame < numFrames; frame++)
				{
					positions[frame] = new Vector3[bones.Length];
					rotations[frame] = new Vector3[bones.Length];
					for (var b = 0; b < bones.Length; b++)
					{
						var values = new float[6];
						var animOffset = animIndex + b * 12;
						for (var j = 0; j < 6; j++)
						{
							var valueOffset = BitConverter.ToUInt16(animData, animOffset + j * 2);
							values[j] = bones[b].Values[j];
							if (valueOffset != 0)
								values[j] += DecodeAnimValue(animData, animOffset + valueOffset, frame) * bones[b].Scales[j];

							if (bones[b].Controllers[j] >= 0 && bones[b].Controllers[j] < adjustments.Length)
								values[j] += adjustments[bones[b].Controllers[j]];
						}

						positions[frame][b] = new Vector3(values[0], values[1], values[2]);
						rotations[frame][b] = new Vector3(values[3], values[4], values[5]);
					}

					// The renderer keeps the motion bone from moving along the axes the sequence moves the entity along
					if (motionBone >= 0 && motionBone < bones.Length)
					{
						var position = positions[frame][motionBone];
						if ((motionType & STUDIO_X) != 0)
							position.X = 0f;
						if ((motionType & STUDIO_Y) != 0)
							position.Y = 0f;
						if ((motionType & STUDIO_Z) != 0)
							position.Z = 0f;
						positions[frame][motionBone] = position;
					}
				}

				Sequences.Add(new Sequence(name, fps, (flags & STUDIO_LOOPING) != 0, positions, rotations));
			}

			return true;
		}

		// A frame's value of a bone's position or rotation component: runs of values, each a header (how many values it
		// stores and how many frames it spans) followed by its values, the last repeating for the rest of the run
		private static short DecodeAnimValue(byte[] data, int offset, int frame)
		{
			var k = frame;
			while (data[offset + 1] <= k)
			{
				var total = data[offset + 1];
				if (total == 0)
					return 0;

				k -= total;
				offset += (data[offset] + 1) * 2;
			}

			var valid = data[offset];
			return BitConverter.ToInt16(data, offset + (valid > k ? k + 1 : valid) * 2);
		}

		// What each bone controller adds to its bone: entities that don't set their controllers leave them at 0, which is
		// the start of the controller's range
		private static float[] GetControllerAdjustments(byte[] data)
		{
			var numControllers = ReadInt(data, 148);
			var controllerIndex = ReadInt(data, 152);
			var adjustments = new float[numControllers];
			for (var i = 0; i < numControllers; i++)
			{
				var offset = controllerIndex + i * 24;
				var type = ReadInt(data, offset + 4) & STUDIO_TYPES;
				var start = ReadFloat(data, offset + 8);
				adjustments[i] = (type & (STUDIO_XR | STUDIO_YR | STUDIO_ZR)) != 0 ? start * MathF.PI / 180f : start;
			}

			return adjustments;
		}

		private static int ReadInt(byte[] data, int offset) => BitConverter.ToInt32(data, offset);

		private static float ReadFloat(byte[] data, int offset) => BitConverter.ToSingle(data, offset);

		private static Vector3 ReadVector(byte[] data, int offset)
		{
			return new Vector3(ReadFloat(data, offset), ReadFloat(data, offset + 4), ReadFloat(data, offset + 8));
		}

		private static string ReadString(byte[] data, int offset, int length)
		{
			var end = Array.IndexOf(data, (byte)0, offset, length);
			return Encoding.ASCII.GetString(data, offset, (end >= 0 ? end : offset + length) - offset);
		}
	}
}
